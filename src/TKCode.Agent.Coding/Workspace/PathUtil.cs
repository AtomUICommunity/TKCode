using TKCode.Config;

namespace TKCode.Agent.Coding.Workspace;

public static class PathUtil
{
    /// <summary>Windows 与 macOS 的默认文件系统不区分大小写（规则见 <see cref="PathRules"/>）。</summary>
    public static bool IgnoreCase => PathRules.IgnoreCase;

    public static StringComparer Comparer => PathRules.Comparer;

    public static StringComparison Comparison => PathRules.Comparison;

    /// <summary>把工具参数中的路径解析为绝对路径：相对路径基于 <paramref name="cwd"/>，支持 <c>~</c>。</summary>
    public static string Resolve(string cwd, string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path == ".")
        {
            return Path.GetFullPath(cwd);
        }
        if (path == "~" || path.StartsWith("~/", StringComparison.Ordinal) || path.StartsWith("~\\", StringComparison.Ordinal))
        {
            path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path.Length > 2 ? path[2..] : "");
        }
        return Path.GetFullPath(path, cwd);
    }

    /// <summary>解析路径中每一级的符号链接；见 <see cref="PathRules.RealPath"/>。</summary>
    public static string RealPath(string path) => PathRules.RealPath(path);

    /// <summary><paramref name="path"/> 等于 <paramref name="root"/> 或位于其下（两者都应是真实路径）。</summary>
    public static bool IsUnder(string path, string root) => PathRules.IsUnder(path, root);

    public static bool SamePath(string a, string b) => PathRules.SamePath(a, b);
}
