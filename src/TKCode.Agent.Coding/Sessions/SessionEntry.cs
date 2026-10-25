using System.Text.Json.Serialization;
using TKCode.Ai.Messages;
using TKCode.Agent.Coding.Prompt;
using TKCode.Ai.Models;

namespace TKCode.Agent.Coding.Sessions;

/// <summary>
/// 会话文件中的一行（JSONL）。除首行 header 外，每条记录都有 id / parentId，
/// 形成一棵只追加的树：分支 = 从某个旧节点继续追加，压缩 = 追加一条摘要记录。
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(SessionHeader), "session")]
[JsonDerivedType(typeof(MessageEntry), "message")]
[JsonDerivedType(typeof(ModelChangeEntry), "modelChange")]
[JsonDerivedType(typeof(OptionsChangeEntry), "optionsChange")]
[JsonDerivedType(typeof(RuntimeSessionEntry), "runtimeSession")]
[JsonDerivedType(typeof(SessionMetaEntry), "meta")]
[JsonDerivedType(typeof(SessionDirectoriesEntry), "directories")]
[JsonDerivedType(typeof(SystemPromptEntry), "systemPrompt")]
[JsonDerivedType(typeof(TasksEntry), "tasks")]
[JsonDerivedType(typeof(CompactionEntry), "compaction")]
public abstract record SessionEntry
{
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
}

/// <param name="ProjectId">会话所属的项目（docs/design/projects.md §5.3）；旧会话与不在项目中工作的会话为 null。</param>
public sealed record SessionHeader(string SessionId, string Cwd, int Version, string? ProjectId = null) : SessionEntry;

/// <summary>
/// 会话元数据（不属于对话树，最后一条生效）：自动生成或手动命名的标题、是否置顶。字段为 null 表示不变；
/// 标题为空串表示清除自定义标题（回到第一条用户消息）。
/// </summary>
public sealed record SessionMetaEntry(string? Title, bool? Pinned) : SessionEntry;

/// <summary>
/// 只给本会话追加的文件夹（docs/design/projects.md §6.4），不属于对话树：每次写完整列表，最后一条生效。
/// </summary>
public sealed record SessionDirectoriesEntry(IReadOnlyList<string> Directories) : SessionEntry;

public abstract record TreeEntry(string Id, string? ParentId) : SessionEntry;

public sealed record MessageEntry(string Id, string? ParentId, Message Message) : TreeEntry(Id, ParentId);

public sealed record ModelChangeEntry(string Id, string? ParentId, string Provider, string ModelId) : TreeEntry(Id, ParentId);

/// <summary>会话参数变化；为 null 的字段保持不变。</summary>
public sealed record OptionsChangeEntry(string Id, string? ParentId, ThinkingLevel? ThinkingLevel, bool? FastMode, int? ContextWindow,
    TKCode.Agent.PermissionMode? PermissionMode = null, string? Role = null)
    : TreeEntry(Id, ParentId);

/// <summary>外部运行时的原生会话 id（Claude session_id / Codex threadId / ACP sessionId），用于恢复。</summary>
public sealed record RuntimeSessionEntry(string Id, string? ParentId, string Runtime, string NativeSessionId) : TreeEntry(Id, ParentId);

/// <summary>
/// 内置 Agent 的系统提示快照（按节）。分支上第一条是发给模型的开头系统提示，
/// 之后每条对应一次中途变化（同时追加一条 SystemMessage 补丁）；恢复会话时以此为准，不从磁盘重建。
/// </summary>
public sealed record SystemPromptEntry(string Id, string? ParentId, IReadOnlyList<PromptSection> Sections) : TreeEntry(Id, ParentId);

/// <summary>任务清单的全量快照（最后一条生效）。</summary>
public sealed record TasksEntry(string Id, string? ParentId, IReadOnlyList<TKCode.Agent.Coding.Tools.TaskItem> Tasks) : TreeEntry(Id, ParentId);

/// <summary>
/// 上下文压缩（docs/design/compaction.md §8.2）：此前的消息不再发给模型（仍保留供界面回看），
/// 之后依次是新的开头系统提示和摘要 / 重新附加的两条用户消息。
/// </summary>
public sealed record CompactionEntry(string Id, string? ParentId, TKCode.Agent.Compaction.CompactionTrigger Trigger, string Summary,
    long PreTokens, long PostTokens, long DurationMs) : TreeEntry(Id, ParentId)
{
    public string? Instructions { get; init; }

    /// <summary>摘要请求超长、丢弃了最早的部分历史后才成功。</summary>
    public bool TruncatedForRetry { get; init; }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    AllowOutOfOrderMetadataProperties = true)]
[JsonSerializable(typeof(SessionEntry))]
internal sealed partial class SessionJsonContext : JsonSerializerContext;
