using TKCode.Config;
using TKCode.Desktop.Controls.Composer;
using TKCode.Desktop.Icons;
using TKCode.Desktop.Services;
using TKCode.Rpc;

namespace TKCode.Desktop.ViewModels.Completion;

/// <summary>
/// @ 补全：工作区的文件与文件夹（docs/design/composer.md §8）。搜索在 Host 的文件索引中进行；
/// 没有查询词时依次列出最近 @ 过的文件、编辑区打开的文件、浅层目录；有查询词时最近用过的文件加分。
/// 选中后插入文件 / 文件夹 tag（路径相对于 cwd），文件夹按 Tab 进入继续搜索。
/// 多文件夹项目（docs/design/projects.md §10.2）：一起搜索其他文件夹，显示为「文件夹名/相对路径」，tag 用绝对路径。
/// </summary>
/// <param name="extraRoots">主文件夹（cwd）以外要一起搜索的根目录；null 表示没有。</param>
public sealed class FileCompletionProvider(AgentHostConnection host, Func<string?> cwd, StateService state,
    Func<IReadOnlyList<string>>? extraRoots = null) : ICompletionProvider
{
    private const int Limit = 50;
    private const int MaxRecent = 50;
    private const int RecentBonus = 24;

    public char Trigger => '@';

    public int Order => 0;

    /// <summary>编辑区打开的文件（绝对路径），由主窗口提供。</summary>
    public Func<IEnumerable<string>>? OpenFiles { get; set; }

    public async ValueTask<IReadOnlyList<CompletionItem>> QueryAsync(string query, CancellationToken cancellationToken)
    {
        if (cwd() is not { } root)
        {
            return [];
        }
        var extra = extraRoots?.Invoke().Where(r => !PathRules.SamePath(r, root)).ToArray() ?? [];
        var server = await host.GetServerAsync();
        var result = await server.SearchFilesAsync(new FileSearchRequest(root, query, Limit, extra.Length > 0 ? extra : null), cancellationToken);
        var recent = Recent(root);
        if (query.Length > 0)
        {
            return result.Items.Select(i => IsPrimary(root, i.Root)
                    ? Create(root, i.Path, i.IsDirectory, i.Score + (recent.Contains(i.Path) ? RecentBonus : 0), i.Positions)
                    : CreateInRoot(i.Root!, i.Path, i.IsDirectory, i.Score + (Recent(i.Root!).Contains(i.Path) ? RecentBonus : 0), i.Positions))
                .ToList();
        }
        // 空查询：最近用过的、打开的文件在前，然后是浅层条目（去重）
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var items = new List<CompletionItem>();
        void Add(string path, bool isDirectory)
        {
            if (seen.Add(path))
            {
                items.Add(Create(root, path, isDirectory, 0, []));
            }
        }
        foreach (var path in recent.Where(p => Exists(root, p)))
        {
            Add(path, path.EndsWith('/'));
        }
        foreach (var file in OpenFiles?.Invoke() ?? [])
        {
            if (Relative(root, file) is { } relative)
            {
                Add(relative, false);
            }
            else if (extra.FirstOrDefault(r => Relative(r, file) is not null) is { } other && seen.Add(file))
            {
                items.Add(CreateInRoot(other, Relative(other, file)!, false, 0, []));
            }
        }
        foreach (var item in result.Items)
        {
            if (IsPrimary(root, item.Root))
            {
                Add(item.Path, item.IsDirectory);
            }
            else if (seen.Add(Path.Combine(item.Root!, item.Path)))
            {
                items.Add(CreateInRoot(item.Root!, item.Path, item.IsDirectory, 0, []));
            }
        }
        return items.Take(Limit).ToList();
    }

    private static bool IsPrimary(string root, string? itemRoot) => itemRoot is null || PathRules.SamePath(itemRoot, root);

    /// <summary>
    /// 其他文件夹里的条目：标题仍是文件名，副标题是「文件夹名/所在目录」；tag 用绝对路径（模型那边相对路径以主文件夹为准，§6.6）。
    /// 文件夹不能按 Tab 进入（索引里的路径不带文件夹名前缀）。
    /// </summary>
    private CompletionItem CreateInRoot(string root, string path, bool isDirectory, int score, IReadOnlyList<int> positions)
    {
        var nameStart = FuzzyMatcher.NameStart(path);
        var name = path[nameStart..];
        var folder = Project.FolderName(root);
        var directory = nameStart > 0 ? $"{folder}/{path[..(nameStart - 1)]}" : folder;
        var absolute = Path.GetFullPath(Path.Combine(root, path.TrimEnd('/')));
        var tag = new TagSegment(isDirectory ? TagKind.Folder : TagKind.File, absolute,
            isDirectory ? $"{folder}/{path.TrimEnd('/')}" : name.TrimEnd('/'));
        var highlights = positions.Where(p => p >= nameStart).Select(p => p - nameStart).ToArray();
        return new CompletionItem(name, directory, isDirectory ? LucideKind.Folder : LucideKind.FileText, null,
            new CompletionAction(tag, () => Remember(root, path)), score, highlights);
    }

    private CompletionItem Create(string root, string path, bool isDirectory, int score, IReadOnlyList<int> positions)
    {
        var nameStart = FuzzyMatcher.NameStart(path);
        var name = path[nameStart..];
        var directory = nameStart > 0 ? path[..(nameStart - 1)] : null;
        var value = path.TrimEnd('/');
        var tag = new TagSegment(isDirectory ? TagKind.Folder : TagKind.File, value, isDirectory ? value : name.TrimEnd('/'));
        var highlights = positions.Where(p => p >= nameStart).Select(p => p - nameStart).ToArray();
        return new CompletionItem(name, directory, isDirectory ? LucideKind.Folder : LucideKind.FileText, null,
            new CompletionAction(tag, () => Remember(root, path)), score, highlights,
            TabText: isDirectory ? "@" + (path.EndsWith('/') ? path : path + "/") : null);
    }

    private List<string> Recent(string root) =>
        state.Current.RecentMentions.TryGetValue(root, out var list) ? list : [];

    /// <summary>记住这个 @ 过的路径（每个工作区最多 50 条，最近的在前）。</summary>
    private void Remember(string root, string path)
    {
        if (!state.Current.RecentMentions.TryGetValue(root, out var list))
        {
            list = [];
            state.Current.RecentMentions[root] = list;
        }
        list.Remove(path);
        list.Insert(0, path);
        if (list.Count > MaxRecent)
        {
            list.RemoveRange(MaxRecent, list.Count - MaxRecent);
        }
        state.Save();
    }

    private static bool Exists(string root, string relative)
    {
        var full = Path.Combine(root, relative);
        return relative.EndsWith('/') ? Directory.Exists(full) : File.Exists(full);
    }

    private static string? Relative(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        return relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative) ? null : relative.Replace('\\', '/');
    }
}
