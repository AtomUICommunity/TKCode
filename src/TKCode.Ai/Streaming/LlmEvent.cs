using TKCode.Ai.Messages;

namespace TKCode.Ai.Streaming;

/// <summary>
/// 单次模型调用的流式事件。顺序固定为 Retry* → Start → (块 Start/Delta/End)* → Done | Error；
/// 不同块的事件可能交错，消费方按 ContentIndex 区分。事件只携带增量，不共享可变状态。
/// </summary>
public abstract record LlmEvent;

/// <summary>
/// 首字节之前的自动重试（只作通知，不影响结果）：可以在 <see cref="StartEvent"/> 之前出现零到多次。
/// 界面据此显示「N 秒后重试（第 i/n 次）」（docs/design/message-features-report.md R1）。
/// </summary>
/// <param name="Attempt">即将进行的是第几次重试（从 1 开始）。</param>
/// <param name="MaxAttempts">最多重试几次。</param>
/// <param name="Delay">等待多久后重试。</param>
/// <param name="Reason">上一次失败的原因（状态码与服务端消息，或连接错误）。</param>
public sealed record RetryEvent(int Attempt, int MaxAttempts, TimeSpan Delay, string Reason) : LlmEvent;

public sealed record StartEvent : LlmEvent;

public sealed record TextStartEvent(int ContentIndex) : LlmEvent;
public sealed record TextDeltaEvent(int ContentIndex, string Delta) : LlmEvent;
public sealed record TextEndEvent(int ContentIndex, TextContent Content) : LlmEvent;

public sealed record ThinkingStartEvent(int ContentIndex) : LlmEvent;
public sealed record ThinkingDeltaEvent(int ContentIndex, string Delta) : LlmEvent;
public sealed record ThinkingEndEvent(int ContentIndex, ThinkingContent Content) : LlmEvent;

public sealed record ToolCallStartEvent(int ContentIndex, string Id, string Name) : LlmEvent;
/// <summary>工具参数的原始 JSON 片段，可用于实时预览（例如编辑 diff）。</summary>
public sealed record ToolCallDeltaEvent(int ContentIndex, string JsonFragment) : LlmEvent;
public sealed record ToolCallEndEvent(int ContentIndex, ToolCallContent ToolCall) : LlmEvent;

public sealed record DoneEvent(AssistantMessage Message) : LlmEvent;

/// <summary>失败或中止。消息中保留已生成的部分内容与用量，StopReason 为 Error 或 Aborted。</summary>
public sealed record ErrorEvent(AssistantMessage Message) : LlmEvent;
