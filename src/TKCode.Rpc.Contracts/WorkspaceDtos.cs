namespace TKCode.Rpc;

/// <summary>输入框 @ 文件搜索（docs/design/composer.md §8）。</summary>
/// <param name="Cwd">搜索的目录；结果路径相对于它。</param>
/// <param name="Limit">最多返回的条目数（1–200）。</param>
/// <param name="Roots">一起搜索的其他根目录（多文件夹项目，docs/design/projects.md §6.6）；结果按分数合并。</param>
public sealed record FileSearchRequest(string Cwd, string Query, int Limit, IReadOnlyList<string>? Roots = null);

/// <param name="Path">相对于所在根目录的路径，用 / 分隔；目录以 / 结尾。</param>
/// <param name="Positions">路径中命中查询的字符下标。</param>
/// <param name="Root">命中所在的根目录（绝对路径）；只在请求带 <see cref="FileSearchRequest.Roots"/> 时给出。</param>
public sealed record FileSearchItemDto(string Path, bool IsDirectory, int Score, IReadOnlyList<int> Positions, string? Root = null);

/// <summary>撤销 Agent 的一次文件改动（见 TKCode.Agent.Coding.Tools.Files.FileRevert）。RestoreText 为 null 时删除文件（新建的文件）。</summary>
/// <param name="ExpectedSha256">改动后文件的 SHA-256；不一致说明文件已被改过，撤销被拒绝。</param>
/// <param name="SessionId">回退后仍留在这个会话里时传入：Host 在下一批输入前追加一条系统消息告诉 Agent（docs/design/message-rewind.md §5.3）。</param>
public sealed record RevertFileRequest(string Path, string ExpectedSha256, string? RestoreText, string? SessionId = null);

/// <param name="Error">失败原因；成功时为 null。</param>
public sealed record RevertFileResult(string? Error);

/// <param name="Complete">索引是否完整（条目超过上限时为 false）。</param>
/// <param name="Indexing">索引正在建立或刷新。</param>
public sealed record FileSearchResultDto(IReadOnlyList<FileSearchItemDto> Items, bool Complete, bool Indexing);
