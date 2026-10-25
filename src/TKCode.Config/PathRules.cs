namespace TKCode.Config;

/// <summary>
/// 路径比较的统一规则（Host 与 Desktop 共用）：Windows 与 macOS 的默认文件系统不区分大小写；
/// 比较前先取绝对路径并去掉末尾的分隔符，需要时再解析符号链接。
/// </summary>
public static class PathRules
{
    public static bool IgnoreCase { get; } = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS();

    public static StringComparer Comparer { get; } = IgnoreCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    public static StringComparison Comparison => IgnoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>绝对路径，去掉末尾的分隔符（根目录除外）。</summary>
    public static string Normalize(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full) ?? "";
        return full.Length > root.Length ? full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) : full;
    }

    public static bool SamePath(string a, string b) => string.Equals(Normalize(a), Normalize(b), Comparison);

    /// <summary><paramref name="path"/> 等于 <paramref name="root"/> 或位于其下（两者都应是绝对路径，需要时先取真实路径）。</summary>
    public static bool IsUnder(string path, string root)
    {
        var trimmedRoot = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (path.Length == trimmedRoot.Length)
        {
            return path.Equals(trimmedRoot, Comparison);
        }
        if (trimmedRoot.Length == 0)
        {
            return true;
        }
        return path.Length > trimmedRoot.Length
               && path.StartsWith(trimmedRoot, Comparison)
               && (path[trimmedRoot.Length] == Path.DirectorySeparatorChar || path[trimmedRoot.Length] == Path.AltDirectorySeparatorChar);
    }

    /// <summary>
    /// 解析路径中每一级的符号链接（<see cref="FileSystemInfo.ResolveLinkTarget"/> 只处理最后一级）。
    /// 不存在的部分原样保留，因此也可用于即将创建的文件。
    /// </summary>
    public static string RealPath(string path) => RealPath(Path.GetFullPath(path), 0);

    private static string RealPath(string full, int depth)
    {
        if (depth > 32)
        {
            return full;
        }
        var root = Path.GetPathRoot(full) ?? "";
        var current = root;
        var parts = full[root.Length..].Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < parts.Length; i++)
        {
            var next = Path.Combine(current, parts[i]);
            string? target = null;
            try
            {
                target = new FileInfo(next).LinkTarget;
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
            if (target is null)
            {
                current = next;
                continue;
            }
            var resolved = RealPath(Path.GetFullPath(target, current), depth + 1);
            var rest = string.Join(Path.DirectorySeparatorChar, parts[(i + 1)..]);
            return rest.Length == 0 ? resolved : RealPath(Path.Combine(resolved, rest), depth + 1);
        }
        return current;
    }
}
