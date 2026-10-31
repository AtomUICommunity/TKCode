using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace TKCode.Desktop.Services.Notifications;

/// <summary>
/// Linux 的系统通知（docs/design/notifications.md §5.3）：freedesktop <c>org.freedesktop.Notifications</c>，经 <c>gdbus</c> 调用，
/// 不自己实现 D-Bus 协议。没有 gdbus 时用 notify-send（不能点击、不能替换），两者都没有时静默跳过。
/// 点击：常驻一个 <c>gdbus monitor</c> 解析 ActionInvoked 信号（GNOME Shell、KDE、mako 广播该信号；dunst 只发给原连接，点击无效）。
/// 未读数只显示在托盘（<see cref="AppTray"/>），这里的 <see cref="SetBadge"/> 什么也不做。
/// </summary>
[SupportedOSPlatform("linux")]
internal sealed partial class LinuxNotifier : ISystemNotifier
{
    private const string Destination = "org.freedesktop.Notifications";
    private const string ObjectPath = "/org/freedesktop/Notifications";
    private const string SoundName = "message-new-instant";
    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(5);

    private readonly ILogger _logger;
    private readonly string? _iconPath;
    private readonly string? _gdbus;
    private readonly string? _notifySend;
    private readonly object _lock = new();
    /// <summary>会话 → 通知中心里它那一条的 id（下一条用作 replaces_id）。</summary>
    private readonly Dictionary<string, uint> _ids = new(StringComparer.Ordinal);
    private readonly Dictionary<uint, string> _sessions = [];
    private Task<IReadOnlySet<string>>? _capabilities;
    private Process? _monitor;
    private bool _disposed;

    public LinuxNotifier(ILogger logger, string? iconPath)
    {
        _logger = logger;
        _iconPath = iconPath;
        _gdbus = FindProgram("gdbus");
        _notifySend = _gdbus is null ? FindProgram("notify-send") : null;
        if (_gdbus is null && _notifySend is null)
        {
            _logger.LogInformation("Neither gdbus nor notify-send was found; system notifications are disabled");
        }
    }

    public event Action<string>? Activated;

    public void Show(SystemNotification notification) => _ = Task.Run(async () =>
    {
        try
        {
            if (_gdbus is not null)
            {
                await NotifyAsync(notification);
            }
            else if (_notifySend is not null)
            {
                var arguments = new List<string> { "--app-name=TKCode" };
                if (_iconPath is not null)
                {
                    arguments.Add($"--icon={_iconPath}");
                }
                arguments.AddRange(["--", notification.Title, notification.Body]);
                await RunAsync(_notifySend, arguments);
                if (notification.Sound)
                {
                    PlaySound();
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Showing a notification failed");
        }
    });

    public void Withdraw(string sessionId)
    {
        uint id;
        lock (_lock)
        {
            if (_gdbus is null || !_ids.Remove(sessionId, out id))
            {
                return;
            }
            _sessions.Remove(id);
        }
        _ = Task.Run(async () =>
        {
            try
            {
                await CallAsync("CloseNotification", [$"uint32 {id}"]);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Closing a notification failed");
            }
        });
    }

    public void SetBadge(int count)
    {
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _disposed = true;
            try
            {
                if (_monitor is { HasExited: false })
                {
                    _monitor.Kill();
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
            }
            _monitor?.Dispose();
            _monitor = null;
        }
    }

    private async Task NotifyAsync(SystemNotification notification)
    {
        var capabilities = await Capabilities();
        uint replaces;
        lock (_lock)
        {
            replaces = _ids.GetValueOrDefault(notification.SessionId);
        }
        var serverSound = capabilities.Contains("sound");
        var hints = new List<string> { "'urgency': <byte 1>" };
        if (!notification.Sound)
        {
            hints.Add("'suppress-sound': <true>");
        }
        else if (serverSound)
        {
            hints.Add($"'sound-name': <{Quote(SoundName)}>");
        }
        var body = capabilities.Contains("body-markup") ? EscapeMarkup(notification.Body) : notification.Body;
        var actions = capabilities.Contains("actions") ? "['default', 'Open']" : "@as []";
        var output = await CallAsync("Notify",
        [
            Quote("TKCode"),
            $"uint32 {replaces}",
            Quote(_iconPath ?? ""),
            Quote(notification.Title),
            Quote(body),
            actions,
            "{" + string.Join(", ", hints) + "}",
            "int32 -1",
        ]);
        if (notification.Sound && !serverSound)
        {
            PlaySound();
        }
        if (NotificationId().Match(output) is { Success: true } match && uint.TryParse(match.Groups[1].Value, CultureInfo.InvariantCulture, out var id))
        {
            lock (_lock)
            {
                if (_ids.Remove(notification.SessionId, out var old))
                {
                    _sessions.Remove(old);
                }
                _ids[notification.SessionId] = id;
                _sessions[id] = notification.SessionId;
            }
            if (capabilities.Contains("actions"))
            {
                EnsureMonitor();
            }
        }
    }

    private Task<IReadOnlySet<string>> Capabilities()
    {
        lock (_lock)
        {
            return _capabilities ??= LoadCapabilitiesAsync();
        }
    }

    private async Task<IReadOnlySet<string>> LoadCapabilitiesAsync()
    {
        try
        {
            var output = await CallAsync("GetCapabilities", []);
            return QuotedString().Matches(output).Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "GetCapabilities failed");
            return new HashSet<string>();
        }
    }

    /// <summary>常驻的 <c>gdbus monitor</c>：解析 ActionInvoked / NotificationClosed 信号。</summary>
    private void EnsureMonitor()
    {
        lock (_lock)
        {
            if (_disposed || _monitor is { HasExited: false })
            {
                return;
            }
            _monitor?.Dispose();
            var start = new ProcessStartInfo(_gdbus!)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                ArgumentList = { "monitor", "--session", "--dest", Destination },
            };
            try
            {
                var monitor = new Process { StartInfo = start, EnableRaisingEvents = true };
                monitor.OutputDataReceived += (_, e) => OnMonitorLine(e.Data);
                monitor.ErrorDataReceived += (_, _) => { };
                monitor.Start();
                monitor.BeginOutputReadLine();
                monitor.BeginErrorReadLine();
                _monitor = monitor;
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                _logger.LogDebug(ex, "Starting gdbus monitor failed");
            }
        }
    }

    private void OnMonitorLine(string? line)
    {
        if (line is null)
        {
            return;
        }
        string? sessionId = null;
        if (ActionInvoked().Match(line) is { Success: true } action && uint.TryParse(action.Groups[1].Value, CultureInfo.InvariantCulture, out var id))
        {
            lock (_lock)
            {
                if (action.Groups[2].Value == "default" && _sessions.TryGetValue(id, out var session))
                {
                    sessionId = session;
                }
            }
        }
        else if (NotificationClosed().Match(line) is { Success: true } closed && uint.TryParse(closed.Groups[1].Value, CultureInfo.InvariantCulture, out var closedId))
        {
            lock (_lock)
            {
                if (_sessions.Remove(closedId, out var session))
                {
                    _ids.Remove(session);
                }
            }
        }
        if (sessionId is not null)
        {
            Activated?.Invoke(sessionId);
        }
    }

    private Task<string> CallAsync(string method, IReadOnlyList<string> arguments) =>
        RunAsync(_gdbus!, ["call", "--session", "--dest", Destination, "--object-path", ObjectPath, "--method", $"{Destination}.{method}", .. arguments]);

    private static async Task<string> RunAsync(string program, IReadOnlyList<string> arguments)
    {
        var start = new ProcessStartInfo(program) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }
        using var process = Process.Start(start) ?? throw new InvalidOperationException($"{program} did not start.");
        using var timeout = new CancellationTokenSource(CallTimeout);
        var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var error = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill();
            throw new TimeoutException($"{Path.GetFileName(program)} timed out.");
        }
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"{Path.GetFileName(program)} exited with {process.ExitCode}: {(await error).Trim()}");
        }
        return await output;
    }

    /// <summary>服务器不支持 sound 能力时自己放一声：canberra-gtk-play（声音主题），否则 pw-play / paplay 播放 freedesktop 主题里的文件。</summary>
    private void PlaySound()
    {
        try
        {
            if (FindProgram("canberra-gtk-play") is { } canberra)
            {
                _ = RunAsync(canberra, ["-i", SoundName]);
                return;
            }
            const string file = "/usr/share/sounds/freedesktop/stereo/message-new-instant.oga";
            if (File.Exists(file) && (FindProgram("pw-play") ?? FindProgram("paplay")) is { } player)
            {
                _ = RunAsync(player, [file]);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Playing the notification sound failed");
        }
    }

    /// <summary>GVariant 文本格式的字符串字面量。</summary>
    internal static string Quote(string value)
    {
        var text = new StringBuilder(value.Length + 2).Append('\'');
        foreach (var c in value)
        {
            switch (c)
            {
                case '\\':
                    text.Append(@"\\");
                    break;
                case '\'':
                    text.Append(@"\'");
                    break;
                case '\n':
                    text.Append(@"\n");
                    break;
                case '\t':
                    text.Append(@"\t");
                    break;
                case '\r':
                    break;
                default:
                    if (char.IsControl(c))
                    {
                        text.Append(@"\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        text.Append(c);
                    }
                    break;
            }
        }
        return text.Append('\'').ToString();
    }

    /// <summary>服务器支持 body-markup 时正文按（受限的）标记解析，需要转义。</summary>
    private static string EscapeMarkup(string text) => text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    private static string? FindProgram(string name)
    {
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(directory, name);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }
        return null;
    }

    [GeneratedRegex(@"uint32 (\d+)")]
    private static partial Regex NotificationId();

    [GeneratedRegex(@"'([^']*)'")]
    private static partial Regex QuotedString();

    [GeneratedRegex(@"\.ActionInvoked \(uint32 (\d+), '([^']*)'\)")]
    private static partial Regex ActionInvoked();

    [GeneratedRegex(@"\.NotificationClosed \(uint32 (\d+),")]
    private static partial Regex NotificationClosed();
}
