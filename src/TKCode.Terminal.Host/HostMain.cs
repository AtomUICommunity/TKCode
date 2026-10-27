using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using TKCode.Terminal.Core;
using TKCode.Terminal.Protocol;

namespace TKCode.Terminal.Host;

/// <summary>
/// 常驻模式（04 §1.2–§1.4）：<c>tkcode-ptyhost --home H --build B [--ready-fd 1] [--log-level info] [--parent-pid N] [--job-bound]</c>。
/// 退出码：0 正常；2 已有同构建的 host；3 构建 ID 不一致；4 无法创建运行时目录或 socket。
/// </summary>
internal static partial class HostMain
{
    public static async Task<int> RunAsync(string[] args)
    {
        string? Arg(string name) => Array.IndexOf(args, name) is var i and >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        var home = Arg("--home") ?? throw new ArgumentException("--home is required");
        var readyFd = int.TryParse(Arg("--ready-fd"), out var fd) ? fd : 1;
        var level = HostLog.ParseLevel(Arg("--log-level") ?? Environment.GetEnvironmentVariable("TKCODE_PTYHOST_LOG"));
        FillStandardDescriptors();

        var ghosttyBuildId = GhosttyBuildId();
        var version = Version;
        var build = PtyHostPaths.ComputeBuild(version, ghosttyBuildId);
        if (Arg("--build") is { } expected && expected != build)
        {
            WriteReady(readyFd, $"ERROR build mismatch: expected {expected}, host is {build}");
            return PtyHostLocator.ExitBuildMismatch;
        }
        if (!PtyHostPaths.TryPrivateDirectory(home) && !Directory.Exists(home) || !PtyHostPaths.TryPrivateDirectory(PtyHostPaths.RunDirectory(home)))
        {
            WriteReady(readyFd, "ERROR runtime directory");
            return PtyHostLocator.ExitSetupFailed;
        }
        using var lifetimeLock = PtyHostPaths.TryLock(PtyHostPaths.LifetimeLock(home, build));
        if (lifetimeLock is null)
        {
            WriteReady(readyFd, "ERROR already running");
            return PtyHostLocator.ExitAlreadyRunning;
        }

        using var log = new HostLog(PtyHostPaths.LogDirectory(home), level);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => log.Error($"unhandled: {e.ExceptionObject}");
        var token = Handshake.NewToken();
        PtyHostServer server;
        string endpoint;
        try
        {
            endpoint = PtyHostPaths.ChooseEndpoint(home, build);
            server = new PtyHostServer(new PtyHostServerOptions
            {
                Endpoint = endpoint,
                Token = token,
                Home = home,
                Build = build,
                GhosttyBuildId = ghosttyBuildId,
                HostVersion = version,
                JobBound = args.Contains("--job-bound"),
            }, log);
        }
        catch (Exception e)
        {
            log.Error($"setup failed: {e}");
            WriteReady(readyFd, "ERROR setup");
            return PtyHostLocator.ExitSetupFailed;
        }
        var registrationPath = PtyHostPaths.Registration(home, build);
        var registration = new PtyHostRegistration
        {
            Pid = Environment.ProcessId,
            ProcessStartTime = DateTimeOffset.UtcNow,
            InstanceId = server.InstanceId,
            Endpoint = server.Endpoint,
            Token = Handshake.ToBase64Url(token),
            Protocol = ProtocolVersion.Current,
            HostVersion = version,
            Build = build,
            GhosttyBuildId = ghosttyBuildId,
            StartedAt = DateTimeOffset.UtcNow,
            JobBound = args.Contains("--job-bound"),
        };
        PtyHostPaths.WriteAtomically(registrationPath, JsonSerializer.SerializeToUtf8Bytes(registration, PtyHostJsonContext.Default.PtyHostRegistration));
        log.Info($"tkcode-ptyhost {version} pid {Environment.ProcessId} build {build} listening on {server.Endpoint} (parent {Arg("--parent-pid")})");

        var lifetime = new HostLifetime(server);
        using var term = RegisterSignal(PosixSignal.SIGTERM, server);
        using var hup = RegisterSignal(PosixSignal.SIGHUP, server);
        using var interrupt = RegisterSignal(PosixSignal.SIGINT, server);
        var run = server.RunAsync();
        WriteReady(readyFd, $"READY {server.InstanceId}");
        DetachStandardDescriptors();

        var reason = await server.Stopped.ConfigureAwait(false);
        await run.ConfigureAwait(false);
        log.Info($"exiting: {reason}");
        try
        {
            if (PtyHostLocator.ReadRegistration(registrationPath)?.InstanceId == server.InstanceId)
            {
                File.Delete(registrationPath);
            }
        }
        catch (IOException)
        {
        }
        return 0;
    }

    private static PosixSignalRegistration? RegisterSignal(PosixSignal signal, PtyHostServer server)
    {
        try
        {
            return PosixSignalRegistration.Create(signal, ctx =>
            {
                ctx.Cancel = true;  // 自己处理：保存后退出（§12.3）
                _ = Task.Run(() => server.SaveAndShutdownAsync($"signal {signal}"));
            });
        }
        catch (PlatformNotSupportedException)
        {
            return null;
        }
    }

    internal static string Version => typeof(HostMain).Assembly.GetName().Version?.ToString(3) ?? "0";

    /// <summary>本 host 的构建 ID（Desktop 用同样的算法得到 --build）。</summary>
    internal static string CurrentBuild() => PtyHostPaths.ComputeBuild(Version, GhosttyBuildId());

    /// <summary>ghosttyBuildId = 版本 + 库文件 SHA-256（02 §3、04 §2.4）。</summary>
    internal static string GhosttyBuildId()
    {
        var lib = PtyHostPaths.FindGhosttyLibrary(AppContext.BaseDirectory);
        return lib is null ? GhosttyRuntime.Version : GhosttyRuntime.ComputeBuildId(lib);
    }

    private static void WriteReady(int fd, string line)
    {
        try
        {
            var bytes = Encoding.UTF8.GetBytes(line + "\n");
            using var handle = new Microsoft.Win32.SafeHandles.SafeFileHandle(
                OperatingSystem.IsWindows() ? GetStdHandle(-11) : fd, ownsHandle: false);
            using var stream = new FileStream(handle, FileAccess.Write, 1);
            stream.Write(bytes);
            stream.Flush();
        }
        catch (Exception)
        {
            // 没有人读就绪行（手动运行时）
        }
    }

    // ---- 标准 fd（§15）----

    private const int ORdwr = 2;

    [LibraryImport("libc", EntryPoint = "open", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int Open(string path, int flags);

    [LibraryImport("libc", EntryPoint = "dup2")]
    private static partial int Dup2(int from, int to);

    [LibraryImport("libc", EntryPoint = "fcntl")]
    private static partial int Fcntl(int fd, int cmd);

    [LibraryImport("libc", EntryPoint = "close")]
    private static partial int Close(int fd);

    [LibraryImport("kernel32.dll")]
    private static partial nint GetStdHandle(int which);

    /// <summary>--detach 之后 0–2 中可能有未打开的：用 /dev/null 补上，防止 PTY master 恰好占用它们。</summary>
    private static void FillStandardDescriptors()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        for (var i = 0; i <= 2; i++)
        {
            if (Fcntl(i, 1 /* F_GETFD */) == -1)
            {
                var n = Open("/dev/null", ORdwr);
                if (n >= 0 && n != i)
                {
                    Dup2(n, i);
                    Close(n);
                }
            }
        }
    }

    /// <summary>就绪之后把 0/1/2 指向 /dev/null：Desktop 关闭管道后 host 不会因写 stdout 收到 SIGPIPE 或阻塞。</summary>
    private static void DetachStandardDescriptors()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        var n = Open("/dev/null", ORdwr);
        if (n < 0)
        {
            return;
        }
        for (var i = 0; i <= 2; i++)
        {
            Dup2(n, i);
        }
        if (n > 2)
        {
            Close(n);
        }
    }
}
