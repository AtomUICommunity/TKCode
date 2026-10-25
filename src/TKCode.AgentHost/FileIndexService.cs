using TKCode.Agent.Coding.Workspace;
using TKCode.Rpc;

namespace TKCode.AgentHost;

/// <summary>
/// 各目录的 @ 文件索引（docs/design/composer.md §8.1），所有会话与窗口共用。
/// 同时最多保留 <see cref="MaxIndexes"/> 个，超出时淘汰最久未使用的（释放其文件监听）。
/// 多文件夹项目每个根目录一个索引，多个项目共用同一个文件夹时也共用它的索引（docs/design/projects.md §6.6）。
/// </summary>
public sealed class FileIndexService : IDisposable
{
    private const int MaxIndexes = 16;

    private readonly Dictionary<string, WorkspaceFileIndex> _indexes = new(OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal);

    public async Task<FileSearchResultDto> SearchAsync(FileSearchRequest request, CancellationToken cancellationToken)
    {
        var limit = Math.Clamp(request.Limit, 1, 200);
        if (request.Roots is not { Count: > 0 } extra)
        {
            if (!Directory.Exists(request.Cwd))
            {
                return new FileSearchResultDto([], true, false);
            }
            var result = await Get(Path.GetFullPath(request.Cwd)).SearchAsync(request.Query, limit, cancellationToken);
            return new FileSearchResultDto(result.Hits.Select(h => new FileSearchItemDto(h.Path, h.IsDirectory, h.Score, h.Positions)).ToList(),
                result.Complete, result.Indexing);
        }
        var roots = new List<string>();
        foreach (var root in (IEnumerable<string>)[request.Cwd, .. extra])
        {
            var full = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (Directory.Exists(full) && !roots.Contains(full, _indexes.Comparer))
            {
                roots.Add(full);
            }
        }
        var results = await Task.WhenAll(roots.Select(async root => (Root: root, Result: await Get(root).SearchAsync(request.Query, limit, cancellationToken))));
        var items = results
            .SelectMany(r => r.Result.Hits.Select(h => new FileSearchItemDto(h.Path, h.IsDirectory, h.Score, h.Positions, r.Root)))
            // 嵌套的根目录会命中同一个文件：按绝对路径去重，保留分数高的
            .GroupBy(i => Path.Combine(i.Root!, i.Path.TrimEnd('/')), _indexes.Comparer)
            .Select(g => g.MaxBy(i => i.Score)!)
            .OrderByDescending(i => i.Score)
            .Take(limit)
            .ToList();
        return new FileSearchResultDto(items, results.All(r => r.Result.Complete), results.Any(r => r.Result.Indexing));
    }

    private WorkspaceFileIndex Get(string root)
    {
        lock (_indexes)
        {
            if (!_indexes.TryGetValue(root, out var index))
            {
                if (_indexes.Count >= MaxIndexes)
                {
                    var oldest = _indexes.Values.MinBy(i => i.LastUsed)!;
                    _indexes.Remove(oldest.Root);
                    oldest.Dispose();
                }
                index = new WorkspaceFileIndex(root);
                _indexes[root] = index;
            }
            return index;
        }
    }

    public void Dispose()
    {
        lock (_indexes)
        {
            foreach (var index in _indexes.Values)
            {
                index.Dispose();
            }
            _indexes.Clear();
        }
    }
}
