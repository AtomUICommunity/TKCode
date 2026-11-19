using TKCode.Config;

namespace TKCode.Desktop.Services;

/// <summary>直接读取 .git 目录得到分支信息（不启动 git 进程）。支持 worktree / submodule / separate git dir。</summary>
public static class GitRepository
{
    /// <summary>读取 HEAD 得到当前分支（detached 时为短 hash）；不是 Git 仓库时返回 null。</summary>
    public static string? ReadBranch(string repository)
    {
        if (GitLayout.Find(repository) is not { } layout)
        {
            return null;
        }
        try
        {
            var head = Path.Combine(layout.GitDir, "HEAD");
            if (!File.Exists(head))
            {
                return null;
            }
            var text = File.ReadAllText(head).Trim();
            const string prefix = "ref: refs/heads/";
            return text.StartsWith(prefix, StringComparison.Ordinal) ? text[prefix.Length..] : text[..Math.Min(7, text.Length)];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// 工作区里的 Git 仓库（docs/design/projects.md §10.3）：<paramref name="cwd"/> 在前，然后是其他文件夹；
    /// 位于同一个仓库（同一个 git 目录）的文件夹只算一次，顺序保持不变。返回的是仓库的根文件夹，不是 git 目录。
    /// </summary>
    public static IReadOnlyList<string> Repositories(string? cwd, IReadOnlyList<string> additionalRoots)
    {
        var seen = new HashSet<string>(PathRules.Comparer);
        var result = new List<string>();
        foreach (var folder in (IEnumerable<string?>)[cwd, .. additionalRoots])
        {
            if (!string.IsNullOrEmpty(folder) && GitLayout.Find(folder) is { } layout && seen.Add(Path.GetFullPath(layout.GitDir)))
            {
                result.Add(folder);
            }
        }
        return result;
    }

    /// <summary>本地分支（refs/heads 与 packed-refs），按名称排序；不是 Git 仓库时为空。worktree 读主仓库的分支。</summary>
    public static IReadOnlyList<string> ReadBranches(string repository)
    {
        if (GitLayout.Find(repository) is not { } layout)
        {
            return [];
        }
        var names = new SortedSet<string>(StringComparer.Ordinal);
        try
        {
            var heads = Path.Combine(layout.CommonDir, "refs", "heads");
            if (Directory.Exists(heads))
            {
                foreach (var file in Directory.EnumerateFiles(heads, "*", SearchOption.AllDirectories))
                {
                    names.Add(Path.GetRelativePath(heads, file).Replace(Path.DirectorySeparatorChar, '/'));
                }
            }
            var packed = Path.Combine(layout.CommonDir, "packed-refs");
            if (File.Exists(packed))
            {
                const string prefix = "refs/heads/";
                foreach (var line in File.ReadLines(packed))
                {
                    var space = line.IndexOf(' ');
                    if (space > 0 && line.AsSpan(space + 1).StartsWith(prefix, StringComparison.Ordinal))
                    {
                        names.Add(line[(space + 1 + prefix.Length)..]);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        return names.ToArray();
    }
}
