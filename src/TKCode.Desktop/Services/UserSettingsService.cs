using System.Text.Json.Nodes;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using TKCode.Config;

namespace TKCode.Desktop.Services;

/// <summary>
/// 桌面端对 ~/.tkcode/settings.json 的读写：语言、主题、默认模型，以及设置页对厂商的修改。
/// 文件被外部编辑时（500ms 防抖）重新读取并在 UI 线程触发 <see cref="Changed"/>；厂商与模型的生效由 Agent Host 自己的监听负责。
/// </summary>
public sealed class UserSettingsService : IDisposable
{
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(500);
    private readonly ILogger<UserSettingsService> _logger;
    private readonly string _path;
    private FileSystemWatcher? _watcher;
    private Timer? _timer;

    /// <summary>上次读取时文件的修改时间与长度：监视器在应用自己写完后也会触发，文件没变就不再重新读取和通知。</summary>
    private (DateTime WriteTime, long Length)? _loadedStamp;

    public UserSettingsService(ILogger<UserSettingsService> logger)
        : this(logger, ConfigPaths.SettingsFile)
    {
    }

    public UserSettingsService(ILogger<UserSettingsService> logger, string path)
    {
        _logger = logger;
        _path = path;
        Reload();
        StartWatching();
    }

    public string FilePath => _path;

    /// <summary>最近一次成功解析的配置。</summary>
    public TKCodeSettings Current { get; private set; } = new();

    public IReadOnlyList<string> Errors { get; private set; } = [];

    /// <summary>设置文件内容变化（外部编辑或本服务写入）后在 UI 线程触发。</summary>
    public event Action? Changed;

    public ThemeMode Theme => Current.Theme?.ToLowerInvariant() switch
    {
        "dark" => ThemeMode.Dark,
        "light" => ThemeMode.Light,
        _ => ThemeMode.System,
    };

    public void SetLanguage(string language) => Update(root => root["language"] = language);

    public void SetTheme(ThemeMode mode) => Update(root => root["theme"] = mode.ToString().ToLowerInvariant());

    /// <summary>当前界面缩放比例（已限制在合法范围内）。</summary>
    public double UiScale => UiScaleLevels.Clamp(Current.UiScale ?? UiScaleLevels.Default);

    /// <summary>界面缩放：默认值（1）写回时删除该字段。</summary>
    public void SetUiScale(double scale) =>
        Update(root => SettingsFile.Set(root, "uiScale", UiScaleLevels.IsDefault(scale) ? null : JsonValue.Create(Math.Round(UiScaleLevels.Clamp(scale), 2))));

    /// <summary>沿档位放大（正）或缩小（负）一档；已到头时不写文件。</summary>
    public void StepUiScale(int direction)
    {
        var next = UiScaleLevels.Step(UiScale, direction);
        if (!UiScaleLevels.Same(next, UiScale))
        {
            SetUiScale(next);
        }
    }

    public void SetReduceMotion(bool value) => Update(root => SettingsFile.Set(root, "reduceMotion", value ? JsonValue.Create(true) : null));

    public void SetOriginalImages(bool value) => Update(root => SettingsFile.Set(root, "originalImages", value ? JsonValue.Create(true) : null));

    public void SetCloseToTray(bool value) => Update(root => SettingsFile.Set(root, "closeToTray", value ? null : JsonValue.Create(false)));

    /// <summary>提示缓存有效期："5m"（默认，写回时删除该字段）或 "1h"。</summary>
    public void SetPromptCacheTtl(string ttl) => Update(root => SettingsFile.Set(root, "promptCacheTtl", ttl == "1h" ? JsonValue.Create("1h") : null));

    /// <summary>bash 工具使用的 shell（agent.shell.profile）：null 为 auto，写回时删除该字段（空的 agent / shell 节一并删除）。</summary>
    public void SetAgentShell(string? profile) =>
        Update(root => SetPath(root, ["agent", "shell", "profile"], profile is null ? null : JsonValue.Create(profile)));

    /// <summary>新建终端默认使用的 shell（terminal.defaultProfile）：null 为 auto，写回时删除该字段。</summary>
    public void SetTerminalShell(string? profile) =>
        Update(root => SetPath(root, ["terminal", "defaultProfile"], profile is null ? null : JsonValue.Create(profile)));

    /// <summary>回合结束的通知档位（notifications.turnCompleted）：never / background / always；默认的 background 写回时删除该字段。</summary>
    public void SetNotifyTurnCompleted(string mode) => SetNotificationMode("turnCompleted", mode);

    /// <summary>需要处理时的通知档位（notifications.needsInput），写法同 <see cref="SetNotifyTurnCompleted"/>。</summary>
    public void SetNotifyNeedsInput(string mode) => SetNotificationMode("needsInput", mode);

    /// <summary>通知提示音（notifications.sound）：true 为默认值，写回时删除该字段（空的 notifications 节一并删除）。</summary>
    public void SetNotificationSound(bool value) =>
        Update(root => SetPath(root, ["notifications", "sound"], value ? null : JsonValue.Create(false)));

    private void SetNotificationMode(string key, string mode) =>
        Update(root => SetPath(root, ["notifications", key], mode is "never" or "always" ? JsonValue.Create(mode) : null));

    public void SetDefaultRole(string? role) => Update(root => SettingsFile.Set(root, "defaultRole", role is null or "agent" ? null : JsonValue.Create(role)));

    /// <summary>接收预发布版本：true 写回时删除该字段（默认值）。</summary>
    public void SetReceivePrereleases(bool value) => Update(root => SetUpdateField(root, "prerelease", value ? null : JsonValue.Create(false)));

    /// <summary>自动检查更新：true 写回时删除该字段（默认值）。</summary>
    public void SetAutoCheckUpdates(bool value) => Update(root => SetUpdateField(root, "autoCheck", value ? null : JsonValue.Create(false)));

    /// <summary>设置或删除（value 为 null）嵌套路径上的字段；沿途缺失的节在设置时创建、删除后变空时一并删除。</summary>
    private static void SetPath(JsonObject root, string[] path, JsonNode? value)
    {
        var chain = new List<JsonObject> { root };
        for (var i = 0; i < path.Length - 1; i++)
        {
            if (chain[^1][path[i]] is not JsonObject child)
            {
                if (value is null)
                {
                    return;
                }
                child = [];
                chain[^1][path[i]] = child;
            }
            chain.Add(child);
        }
        SettingsFile.Set(chain[^1], path[^1], value);
        for (var i = chain.Count - 1; i > 0 && chain[i].Count == 0; i--)
        {
            chain[i - 1].Remove(path[i - 1]);
        }
    }

    private static void SetUpdateField(JsonObject root, string key, JsonNode? value)
    {
        var section = root["update"] as JsonObject;
        if (section is null)
        {
            if (value is null)
            {
                return;
            }
            section = [];
            root["update"] = section;
        }
        SettingsFile.Set(section, key, value);
        if (section.Count == 0)
        {
            root.Remove("update");
        }
    }

    public void SetDefaultModel(string? modelRef) => Update(root => SettingsFile.Set(root, "model", modelRef is null ? null : JsonValue.Create(modelRef)));

    /// <summary>快速模型；null 表示跟随会话，写回时删除该字段。</summary>
    public void SetFastModel(string? modelRef) => Update(root => SettingsFile.Set(root, "fastModel", string.IsNullOrWhiteSpace(modelRef) ? null : JsonValue.Create(modelRef.Trim())));

    /// <summary>修改 settings.json：只改动涉及的节点，写完立即重新读取并通知。</summary>
    public void Update(Action<JsonObject> mutate)
    {
        try
        {
            SettingsFile.Update(mutate, _path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            _logger.LogError(ex, "Failed to write {Path}", _path);
            Errors = [ex.Message];
            Changed?.Invoke();
            return;
        }
        Reload();
        Changed?.Invoke();
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        _timer?.Dispose();
    }

    private void Reload()
    {
        _loadedStamp = Stamp();
        var result = SettingsFile.Load(_path);
        Errors = result.Errors;
        if (result.Settings is { } settings)
        {
            Current = settings;
        }
    }

    private (DateTime WriteTime, long Length)? Stamp()
    {
        try
        {
            var info = new FileInfo(_path);
            return info.Exists ? (info.LastWriteTimeUtc, info.Length) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private void StartWatching()
    {
        try
        {
            var dir = Path.GetDirectoryName(_path)!;
            Directory.CreateDirectory(dir);
            _timer = new Timer(_ => Dispatcher.UIThread.Post(() =>
            {
                // 自己刚写的那次已经在 Update 里读过并通知过了：整个设置页会因此再刷新一遍
                if (_loadedStamp is { } loaded && Stamp() == loaded)
                {
                    return;
                }
                Reload();
                Changed?.Invoke();
            }), null, Timeout.Infinite, Timeout.Infinite);
            _watcher = new FileSystemWatcher(dir, Path.GetFileName(_path))
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.CreationTime,
            };
            FileSystemEventHandler schedule = (_, _) => _timer?.Change(Debounce, Timeout.InfiniteTimeSpan);
            _watcher.Changed += schedule;
            _watcher.Created += schedule;
            _watcher.Deleted += schedule;
            _watcher.Renamed += (_, _) => _timer?.Change(Debounce, Timeout.InfiniteTimeSpan);
            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or PlatformNotSupportedException)
        {
            _logger.LogWarning(ex, "Cannot watch {Path}", _path);
        }
    }
}
