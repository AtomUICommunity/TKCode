using TKCode.Config;

namespace TKCode.Desktop.ViewModels;

/// <summary>
/// 文件改动里路径的判定与显示：应用数据目录下的计划文件不算工作区改动；
/// 工作区里的文件显示成相对路径，工作区外显示完整路径。规则集中在这里，回合卡片、变更面板与回退计数一致。
/// </summary>
public static class ChangePaths
{
    /// <summary>Plan 模式用 write 工具写在这里（详见 docs/design/plan-mode.md），属于会话数据而不是工作区改动。</summary>
    private static string PlansDirectory => Path.Combine(ConfigPaths.Home, "plans");

    /// <summary>
    /// 这个路径算不算工作区改动。<see cref="ConfigPaths.Home"/> 随 TKCODE_HOME 变化，所以每次调用时现算。
    /// </summary>
    public static bool IsTracked(string path)
    {
        try
        {
            return !PathRules.IsUnder(PathRules.Normalize(path), PathRules.Normalize(PlansDirectory));
        }
        catch (Exception e) when (e is ArgumentException or IOException or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>列表里显示的路径：工作区内为以 / 分隔的相对路径，工作区外为完整路径。</summary>
    public static string Display(string path, string? cwd)
    {
        try
        {
            var full = PathRules.Normalize(path);
            if (!string.IsNullOrEmpty(cwd))
            {
                var root = PathRules.Normalize(cwd);
                if (full.Length > root.Length && PathRules.IsUnder(full, root))
                {
                    return full[(root.Length + 1)..].Replace(Path.DirectorySeparatorChar, '/');
                }
            }
            return full;
        }
        catch (Exception e) when (e is ArgumentException or IOException or NotSupportedException)
        {
            return path;
        }
    }
}
