using System.Text.Json.Serialization;

namespace TKCode.Rpc;

/// <summary>Host 推送的会话事件。Seq 单调递增，便于检测丢失与去重。</summary>
public sealed record SessionEventNotification(string SessionId, long Seq, SessionEvent Event);

/// <summary>子 Agent 转录的事件：SessionId 为父会话，ToolCallId 为派生它的工具调用。</summary>
public sealed record SubagentEventNotification(string SessionId, string ToolCallId, long Seq, SessionEvent Event);

/// <summary>
/// 跨进程的会话事件：只携带增量（不带"截至目前的完整消息"），保持流量与消息长度线性相关。
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(AgentStarted), "agentStarted")]
[JsonDerivedType(typeof(AgentEnded), "agentEnded")]
[JsonDerivedType(typeof(MessageStarted), "messageStarted")]
[JsonDerivedType(typeof(RequestRetrying), "requestRetrying")]
[JsonDerivedType(typeof(TextDelta), "textDelta")]
[JsonDerivedType(typeof(TextEnded), "textEnded")]
[JsonDerivedType(typeof(ThinkingDelta), "thinkingDelta")]
[JsonDerivedType(typeof(ThinkingEnded), "thinkingEnded")]
[JsonDerivedType(typeof(ToolCallStarted), "toolCallStarted")]
[JsonDerivedType(typeof(ToolCallDelta), "toolCallDelta")]
[JsonDerivedType(typeof(ToolCallEnded), "toolCallEnded")]
[JsonDerivedType(typeof(MessageEnded), "messageEnded")]
[JsonDerivedType(typeof(ToolExecutionStarted), "toolExecutionStarted")]
[JsonDerivedType(typeof(ToolExecutionUpdated), "toolExecutionUpdated")]
[JsonDerivedType(typeof(ToolExecutionEnded), "toolExecutionEnded")]
[JsonDerivedType(typeof(CommandsChanged), "commandsChanged")]
[JsonDerivedType(typeof(CompactionStarted), "compactionStarted")]
[JsonDerivedType(typeof(CompactionEnded), "compactionEnded")]
[JsonDerivedType(typeof(CompactionSuspended), "compactionSuspended")]
[JsonDerivedType(typeof(BackgroundTasksChanged), "backgroundTasksChanged")]
[JsonDerivedType(typeof(SessionTitleChanged), "sessionTitleChanged")]
[JsonDerivedType(typeof(SessionOptionsChanged), "optionsChanged")]
public abstract record SessionEvent;

/// <summary>
/// Host 主动改变了模式或权限（计划审批、enter_plan_mode、外部运行时上报的模式变化；docs/design/plan-mode.md §13.2）。
/// 界面据此更新选择器，不再回调 SetOptionsAsync。
/// </summary>
public sealed record SessionOptionsChanged(SessionOptionsDto Options) : SessionEvent;

public sealed record AgentStarted : SessionEvent;

/// <summary>外部运行时自带的斜杠命令（整体替换，docs/design/composer.md §11）。</summary>
public sealed record CommandsChanged(IReadOnlyList<RuntimeCommandDto> Commands) : SessionEvent;

/// <param name="Name">不带斜杠的命令名。</param>
public sealed record RuntimeCommandDto(string Name, string? Description, string? ArgumentHint);

public sealed record AgentEnded : SessionEvent;

/// <summary>首条用户消息的自动标题已生成并保存。</summary>
public sealed record SessionTitleChanged(string Title) : SessionEvent;

/// <summary>Role：user / assistant / toolResult。用户消息附带文本与附图路径；助手消息内容随后以增量到达。</summary>
/// <param name="Source">非用户输入的来源：task-notification / session-message；null 表示用户输入。</param>
/// <param name="Segments">用户消息在输入框里的样子（含 tag）；没有 tag 时为 null。</param>
/// <param name="Index">用户消息在会话完整历史中的位置（同 <see cref="HistoryMessageDto.Index"/>）；助手消息为 null。</param>
public sealed record MessageStarted(string MessageId, string Role, string? Text, IReadOnlyList<string>? Images = null, string? Source = null,
    IReadOnlyList<PromptSegmentDto>? Segments = null, int? Index = null) : SessionEvent;

/// <summary>模型请求失败、即将自动重试（首字节之前，docs/design/message-features-report.md R1）。</summary>
/// <param name="Attempt">即将进行的是第几次重试（从 1 开始）。</param>
/// <param name="DelayMs">等待多久后重试。</param>
/// <param name="Reason">上一次失败的原因。</param>
public sealed record RequestRetrying(string MessageId, int Attempt, int MaxAttempts, long DelayMs, string Reason) : SessionEvent;

public sealed record TextDelta(string MessageId, int ContentIndex, string Delta) : SessionEvent;

/// <summary>正文块已结束；所在助手消息和轮次可能仍在继续。</summary>
public sealed record TextEnded(string MessageId, int ContentIndex) : SessionEvent;

public sealed record ThinkingDelta(string MessageId, int ContentIndex, string Delta) : SessionEvent;

/// <summary>思考块已结束；所在助手消息和轮次可能仍在继续。</summary>
public sealed record ThinkingEnded(string MessageId, int ContentIndex) : SessionEvent;

public sealed record ToolCallStarted(string MessageId, int ContentIndex, string ToolCallId, string ToolName) : SessionEvent;

public sealed record ToolCallDelta(string MessageId, int ContentIndex, string JsonFragment) : SessionEvent;

public sealed record ToolCallEnded(string MessageId, int ContentIndex, string ToolCallId, string ArgumentsJson) : SessionEvent;

public sealed record MessageEnded(string MessageId, string Role, string StopReason, string? ErrorMessage, UsageDto? Usage) : SessionEvent;

public sealed record ToolExecutionStarted(string ToolCallId, string ToolName, string ArgumentsJson) : SessionEvent;

/// <summary>工具执行中的进度（例如命令输出、子 Agent 动态）。Text 为截至目前的输出；DetailsJson 为工具的结构化数据。</summary>
public sealed record ToolExecutionUpdated(string ToolCallId, string ToolName, string Text, string? DetailsJson = null) : SessionEvent;

/// <param name="DetailsJson">工具给 UI 的结构化数据（例如 diff），格式由工具约定。</param>
public sealed record ToolExecutionEnded(string ToolCallId, string ToolName, bool IsError, string ResultText, double DurationMs,
    string? DetailsJson = null) : SessionEvent;

/// <summary>开始压缩上下文（docs/design/compaction.md §12）。Trigger：manual / auto / reactive。</summary>
public sealed record CompactionStarted(string Trigger, long PreTokens) : SessionEvent;

/// <summary>压缩结束：成功时 Error 为 null，随后到达摘要消息（Source = compact-summary）；失败时历史不变。</summary>
public sealed record CompactionEnded(string Trigger, long PreTokens, long? PostTokens, string? Error) : SessionEvent;

/// <summary>本会话停用了自动压缩（连续失败、反复触发等）。</summary>
public sealed record CompactionSuspended(string Reason) : SessionEvent;

/// <summary>后台任务（shell / 后台子 Agent）的当前列表：启动、结束、停止时整体推送，界面据此刷新任务球。</summary>
public sealed record BackgroundTasksChanged(IReadOnlyList<BackgroundTaskDto> Tasks) : SessionEvent;

/// <summary>Type：bash / agent；Status：running / completed / failed / stopped。</summary>
public sealed record BackgroundTaskDto(string Id, string Type, string Description, string Status);
