using System.Security.Cryptography;
using System.Text;

namespace TKCode.Terminal.Protocol;

/// <summary>
/// 发现相关的路径（04 §1.4、§2.2）：<c>$TKCODE_HOME/run/</c>（0700）下每个构建一组 json / lock / start.lock / sock。
/// </summary>
public static class PtyHostPaths
{
    /// <summary>Unix socket 首选路径的 UTF-8 字节上限（sun_path 为 104 / 108，含 NUL）。</summary>
    public const int MaxSocketPathBytes = 100;

    public static string RunDirectory(string home) => Path.Combine(home, "run");

    public static string Registration(string home, string build) => Path.Combine(RunDirectory(home), $"ptyhost-{build}.json");

    public static string LifetimeLock(string home, string build) => Path.Combine(RunDirectory(home), $"ptyhost-{build}.lock");

    public static string StartLock(string home, string build) => Path.Combine(RunDirectory(home), $"ptyhost-{build}.start.lock");

    public static string LogDirectory(string home) => Path.Combine(home, "logs", "ptyhost");

    /// <summary>
    /// 目录里的 libghostty-vt：先找根目录（带 -r 发布 / 旧布局），再找 NuGet 包的 <c>runtimes/&lt;rid&gt;/native/</c>（不带 RID 的开发构建）。
    /// Desktop 与 host 必须用同一套规则，否则两边算出的构建 ID 不一致（build mismatch）。
    /// </summary>
    public static string? FindGhosttyLibrary(string directory)
    {
        string[] names = ["libghostty-vt.dylib", "libghostty-vt.so", "ghostty-vt.dll"];
        var rid = System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier;
        foreach (var dir in new[] { directory, Path.Combine(directory, "runtimes", rid, "native") })
        {
            foreach (var name in names)
            {
                var path = Path.Combine(dir, name);
                if (File.Exists(path))
                {
                    return path;
                }
            }
        }
        return null;
    }

    /// <summary><c>hex(SHA-256(protocolMajor ‖ appVersion ‖ ghosttyBuildId))[0..12]</c>。</summary>
    public static string ComputeBuild(string appVersion, string ghosttyBuildId, int protocolMajor = 1) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{protocolMajor}\n{appVersion}\n{ghosttyBuildId}")))[..12];

    /// <summary>
    /// host 选择 endpoint（§2.2）：Unix 首选 run 目录下的 socket；路径过长时回退到 XDG_RUNTIME_DIR / TMPDIR / /tmp 下的私有目录。
    /// Windows 为按用户 SID 与 home 散列的管道名。实际使用的值写进注册 json，客户端不自己推算。
    /// </summary>
    public static string ChooseEndpoint(string home, string build)
    {
        var h = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(home))));
        if (OperatingSystem.IsWindows())
        {
            var sid = System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
            var key = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sid + "\n" + Path.GetFullPath(home))))[..16];
            return $@"{PtyTransport.PipePrefix}tkcode-ptyhost-{key}-{build}";
        }
        var preferred = Path.Combine(RunDirectory(home), $"ptyhost-{build}.sock");
        if (Encoding.UTF8.GetByteCount(preferred) < MaxSocketPathBytes)
        {
            return preferred;
        }
        var bases = new List<string>();
        if (OperatingSystem.IsLinux() && Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") is { Length: > 0 } xdg)
        {
            bases.Add(Path.Combine(xdg, $"tkcode-{h[..12]}"));
        }
        if (OperatingSystem.IsMacOS() && Environment.GetEnvironmentVariable("TMPDIR") is { Length: > 0 } tmp)
        {
            bases.Add(Path.Combine(tmp, $"tkcode-{h[..12]}"));
        }
        bases.Add($"/tmp/tkcode-{Environment.UserName}-{h[..8]}");
        foreach (var dir in bases)
        {
            for (var attempt = 0; attempt < 3; attempt++)
            {
                var candidate = attempt == 0 ? dir : $"{dir}-{attempt}";
                if (TryPrivateDirectory(candidate))
                {
                    var path = Path.Combine(candidate, $"ptyhost-{build}.sock");
                    if (Encoding.UTF8.GetByteCount(path) < MaxSocketPathBytes)
                    {
                        return path;
                    }
                }
            }
        }
        throw new IOException("no private directory is available for the pty host socket");
    }

    /// <summary>
    /// 创建或校验私有目录（0700）。已存在时：不能是符号链接、权限必须恰好是 0700、并且我们能在里面创建文件
    /// （别人的 0700 目录我们写不进去），从而防止他人在 /tmp 预先创建同名目录或链接（§2.2）。
    /// </summary>
    public static bool TryPrivateDirectory(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(path);
            return true;
        }
        try
        {
            if (!Directory.Exists(path) && !File.Exists(path))
            {
                Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
            var info = new DirectoryInfo(path);
            if (!info.Exists || info.LinkTarget is not null)
            {
                return false;
            }
            if (File.GetUnixFileMode(path) != (UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute))
            {
                return false;
            }
            var probe = Path.Combine(path, $".probe-{Environment.ProcessId}");
            File.WriteAllBytes(probe, []);
            File.Delete(probe);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>非阻塞地独占一个锁文件（Unix 上 .NET 用 flock 实现 FileShare.None）。拿不到返回 null。</summary>
    public static FileStream? TryLock(string path)
    {
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.OpenOrCreate, Access = FileAccess.ReadWrite, Share = FileShare.None };
            if (!OperatingSystem.IsWindows())
            {
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            }
            return new FileStream(path, options);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>原子写入（先写 0600 的临时文件，再 rename）。</summary>
    public static void WriteAtomically(string path, ReadOnlySpan<byte> content)
    {
        var tmp = path + $".tmp{Environment.ProcessId}";
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }
        using (var f = new FileStream(tmp, options))
        {
            f.Write(content);
            f.Flush(flushToDisk: true);
        }
        File.Move(tmp, path, overwrite: true);
    }
}
