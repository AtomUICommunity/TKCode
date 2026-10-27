using System.Diagnostics;
using Microsoft.Extensions.Logging;
using TKCode.Config;
using TKCode.Terminal.Core;
using TKCode.Terminal.Protocol;

namespace TKCode.Desktop.Services;

/// <summary>
/// 桌面端对 pty host 的连接（04 §1.3–§1.4、§13.1）：按需发现或启动当前构建的 host，连接后下发设置，断开后下次使用时重连。
/// host 不随 Desktop 退出；Desktop 崩溃后的会话由新的 Desktop 重新 attach（§10.5）。
/// </summary>
public sealed class PtyHostService(ILogger<PtyHostService> logger, UserSettingsService? settings = null) : IDisposable
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private PtyHostClient? _client;

    /// <summary>已经下发给 host 的默认 shell；设置改变后，下一次取连接（新建终端）时重新下发。</summary>
    private string? _pushedShell;

    /// <summary>当前构建的 ID（与 host 的 --build 比较，见 04 §1.2）。</summary>
    public static string Build => PtyHostPaths.ComputeBuild(AppVersion, GhosttyBuildId);

    /// <summary>用户态数据目录（TKCODE_HOME），revive 等数据放在它下面。</summary>
    public static string Home => ConfigPaths.Home;

    /// <summary>host 所在目录。TKCODE_PTYHOST_DIR 用于测试（直接使用 host 项目的构建输出）。</summary>
    public static string HostDirectory =>
        Environment.GetEnvironmentVariable("TKCODE_PTYHOST_DIR") is { Length: > 0 } custom ? custom : Path.Combine(AppContext.BaseDirectory, "ptyhost");

    private static string AppVersion => typeof(PtyHostService).Assembly.GetName().Version?.ToString(3) ?? "0";

    private static string GhosttyBuildId
    {
        get
        {
            var lib = PtyHostPaths.FindGhosttyLibrary(AppContext.BaseDirectory);
            return lib is not null ? GhosttyRuntime.ComputeBuildId(lib) : GhosttyRuntime.Version;
        }
    }

    /// <summary>取得一个已认证的连接；已有连接仍然有效时直接返回。</summary>
    public async Task<PtyHostClient> GetAsync(CancellationToken cancellationToken = default)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_client is { } current && !current.Completion.IsCompleted)
            {
                await PushDefaultShellAsync(current, cancellationToken).ConfigureAwait(false);
                return current;
            }
            var sw = Stopwatch.StartNew();
            var client = await PtyHostLocator.ConnectOrStartAsync(new PtyHostLaunchOptions
            {
                Home = Home,
                HostDirectory = HostDirectory,
                Build = Build,
                Client = new PtyHostClientOptions { Kind = ClientKind.Ui, ClientVersion = AppVersion, GhosttyBuildId = GhosttyBuildId },
            }, cancellationToken).ConfigureAwait(false);
            logger.LogInformation("pty host {Scope} connection established in {Ms} ms (host {Version})", client.Scope, sw.ElapsedMilliseconds, client.Host.HostVersion);
            if (client.Scope == "full")
            {
                var shell = DefaultShell;
                await client.ConfigureAsync(Settings(shell), cancellationToken).ConfigureAwait(false);
                _pushedShell = shell;
            }
            _client = client;
            return client;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>settings.json 的 terminal.defaultProfile（null 表示 auto）。</summary>
    private string? DefaultShell => settings?.Current.Terminal?.DefaultProfile is { } profile && !string.IsNullOrWhiteSpace(profile) ? profile.Trim() : null;

    /// <summary>默认 shell 改了：重新下发 host/configure（整份替换），只影响之后创建的会话。失败只记日志，沿用 host 现有的设置。</summary>
    private async Task PushDefaultShellAsync(PtyHostClient client, CancellationToken cancellationToken)
    {
        var shell = DefaultShell;
        if (client.Scope != "full" || shell == _pushedShell)
        {
            return;
        }
        try
        {
            await client.ConfigureAsync(Settings(shell), cancellationToken).ConfigureAwait(false);
            _pushedShell = shell;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "pty host did not accept the new default shell");
        }
    }

    /// <summary>host/configure 的设置（TODO：其余字段从 settings.json 的 terminal 节读取，M4）。</summary>
    private static HostSettingsDto Settings(string? defaultShell)
    {
        var env = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (System.Collections.DictionaryEntry e in Environment.GetEnvironmentVariables())
        {
            env[(string)e.Key] = (string?)e.Value ?? string.Empty;  // 按键赋值：Windows 的环境变量名可能只在大小写上不同
        }
        return new HostSettingsDto { BaseEnvironment = env, DefaultShell = defaultShell };
    }

    /// <summary>
    /// 应用内更新前让 host 按 revive 方式退出（保存快照，新版本启动后恢复；auto-update.md §7）。
    /// 没有连接过 host 时什么也不做；失败只记日志，不阻止更新。
    /// </summary>
    public async Task QuitForUpdateAsync()
    {
        if (_client is not { } client || client.Completion.IsCompleted || client.Scope != "full")
        {
            return;
        }
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await client.QuitAsync(new QuitParams { Mode = "revive", Reason = "update", DeadlineMs = 5000 }, cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "pty host did not quit before the update");
        }
    }

    public void Dispose()
    {
        _client?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _lock.Dispose();
    }
}
