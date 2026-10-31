using System.Text.Json.Serialization;

namespace TKCode.Rpc;

public sealed record InitializeRequest(int ProtocolVersion, string ClientName);

public sealed record HostInfo(int ProtocolVersion, string HostVersion, int ProcessId);

/// <param name="Ref">全局唯一的模型引用：&lt;厂商 id&gt;/&lt;模型 id&gt;。不同厂商可能提供同 id 的模型，RPC 中一律使用 Ref。</param>
/// <param name="ThinkingLevels">可选思考档位（Off / Minimal / Low / Medium / High / XHigh / Max 的子集），空表示不可调。</param>
/// <param name="ContextWindows">可选的上下文预算，升序；最后一项为模型上限。</param>
/// <param name="Runtime">承载该模型的外部运行时 id（claude-code / codex / acp:*）；null 表示 TKCode 内置 Agent。</param>
public sealed record ModelDto(
    string Id,
    string Name,
    string Provider,
    string Api,
    bool Reasoning,
    string? Description,
    IReadOnlyList<string> ThinkingLevels,
    string DefaultThinkingLevel,
    IReadOnlyList<int> ContextWindows,
    bool SupportsFastMode,
    string Ref,
    string ProviderName,
    string? Runtime = null,
    bool SupportsImages = false,
    bool SupportsSubagents = true);

/// <summary>Key 的来源：none（缺失）/ settings（settings.json 明文）/ environment（环境变量）/ notRequired（本地服务）。</summary>
public enum KeySourceDto
{
    None,
    Settings,
    Environment,
    NotRequired,
}

/// <param name="Enabled">在模型选择器中可用（厂商启用且该模型被启用）。</param>
/// <param name="InCatalog">元数据来自内置目录（否则为默认值或用户覆盖）。</param>
/// <param name="IsDiscovered">来自 GET /models 自动发现。</param>
/// <param name="HasOverride">settings.json 中为该模型写了覆盖字段。</param>
/// <param name="Api">生效的协议（chat / responses / messages）。</param>
/// <param name="ApiOverridden">协议与厂商默认值不同：由用户为该模型单独指定，或目录中的固定规则（如 NextCoWork 的 Claude 系列）。</param>
/// <param name="DefaultApi">没有用户覆盖时该模型的协议（厂商默认值或目录规则）；设置页“跟随厂商”指的就是它。</param>
/// <param name="InputPrice">美元 / 百万 tokens（目录提供时）。</param>
public sealed record ProviderModelDto(
    string Id,
    string Name,
    bool Enabled,
    int ContextWindow,
    bool Reasoning,
    bool Images,
    bool InCatalog,
    int MaxTokens = 0,
    bool FastMode = false,
    double? InputPrice = null,
    double? OutputPrice = null,
    bool IsDiscovered = false,
    bool HasOverride = false,
    string? Api = null,
    bool ApiOverridden = false,
    string? DefaultApi = null);

/// <summary>测试连接：可以带上尚未保存的表单值（为 null 时使用已保存的配置）。</summary>
public sealed record ProviderTestRequest(string ProviderId, string? Api = null, string? BaseUrl = null, string? ApiKey = null);

/// <param name="ModelCount">通过 GET /models 测试时返回的模型数量。</param>
public sealed record ProviderTestResult(bool Success, int? StatusCode, double LatencyMs, string Message, int? ModelCount = null);

/// <summary>设置页中的一个厂商：内置（目录中有）或自定义。</summary>
/// <param name="IsConfigured">settings.json 中有该厂商的条目。</param>
/// <param name="IsActive">已启用且有可用的 Key，模型会出现在选择器中。</param>
/// <param name="KeyVariable">Key 来自环境变量时的变量名；缺失时为建议的变量名。</param>
/// <param name="MaskedKey">settings.json 中明文 Key 的遮罩（sk-ant-…a1b2）。</param>
/// <param name="HasModelsOverride">settings.json 中写了 models（只启用列出的模型）。</param>
public sealed record ProviderDto(
    string Id,
    string Name,
    string Api,
    string? BaseUrl,
    bool IsBuiltIn,
    bool IsConfigured,
    bool Enabled,
    bool IsActive,
    KeySourceDto KeySource,
    string? KeyVariable,
    string? MaskedKey,
    bool HasModelsOverride,
    IReadOnlyList<ProviderModelDto> Models,
    IReadOnlyList<string> Errors,
    bool SupportsDiscovery = false,
    string? DiscoveryError = null);

/// <param name="Errors">settings.json 的解析错误；有错误时 Host 继续使用上一次正确的配置。</param>
public sealed record ProvidersSnapshotDto(
    IReadOnlyList<ProviderDto> Providers,
    IReadOnlyList<string> Errors,
    IReadOnlyList<string> Warnings,
    string SettingsPath,
    string? CatalogDate);

/// <summary>目录中的厂商（设置页“添加厂商”列表）。</summary>
public sealed record CatalogProviderDto(string Id, string Name, string Api, string BaseUrl, IReadOnlyList<string> EnvVariables, int ModelCount, bool KeyOptional);

/// <param name="Source">synced（已应用 models.dev 的更新）或 embedded（内置快照）。</param>
/// <param name="Generated">目录日期（yyyy-MM-dd）。</param>
/// <param name="LastChecked">最近一次“检查更新”的时间（本次运行内）。</param>
public sealed record CatalogStatusDto(
    string Source,
    string? Generated,
    DateTimeOffset? SyncedAt,
    int ProviderCount,
    int ModelCount,
    bool IsChecking,
    string? LastError,
    DateTimeOffset? LastChecked = null);

public enum CatalogChangeKind
{
    Added,
    Removed,
    Changed,
}

/// <param name="Field">name / contextWindow / maxTokens / reasoning / images / efforts / thinkingToggle / inputPrice / outputPrice / baseUrl / api。</param>
public sealed record CatalogFieldChangeDto(string Field, string? OldValue, string? NewValue);

public sealed record CatalogModelChangeDto(string ModelId, string Name, CatalogChangeKind Kind, IReadOnlyList<CatalogFieldChangeDto> Changes);

/// <param name="IsConfigured">用户已在 settings.json 中添加该厂商（差异与其相关）。</param>
public sealed record CatalogProviderChangeDto(
    string ProviderId,
    string Name,
    CatalogChangeKind Kind,
    bool IsConfigured,
    IReadOnlyList<CatalogFieldChangeDto> Changes,
    IReadOnlyList<CatalogModelChangeDto> Models);

/// <summary>“检查更新”的结果：下载并裁剪后的新目录与当前目录的差异，等待用户确认后才应用。</summary>
/// <param name="UpToDate">远端没有变化（304 或内容相同）。</param>
/// <param name="HasPending">有可应用的更新（应用前不影响当前配置）。</param>
public sealed record CatalogUpdateDto(
    CatalogStatusDto Status,
    bool UpToDate,
    bool HasPending,
    string? Generated,
    int AddedModels,
    int RemovedModels,
    int ChangedModels,
    IReadOnlyList<CatalogProviderChangeDto> Providers);

/// <summary>settings.json 变化后 Host 推送的最新模型列表与配置错误。</summary>
public sealed record ModelsChangedNotification(IReadOnlyList<ModelDto> Models, IReadOnlyList<string> Errors);

/// <summary>会话参数；为 null 的字段保持不变（新建会话时取模型默认值）。</summary>
/// <param name="PermissionMode">权限档位：ask（询问，默认）/ autoApprove（自动放行，保留沙箱）/ fullAccess（完全授权，无沙箱）。
/// 所有会话都有；重新打开会话时沿用记录的档位，包括 fullAccess。</param>
/// <param name="Role">会话的 role（docs/design/composer.md §10.3）：agent / plan / acp / 自定义 role 名。</param>
public sealed record SessionOptionsDto(string? ThinkingLevel = null, bool? FastMode = null, int? ContextWindow = null,
    string? PermissionMode = null, string? Role = null);

/// <param name="Cwd">会话的 cwd：所选项目的主文件夹（docs/design/projects.md §5.3），不使用项目时为用户目录。</param>
/// <param name="ProjectId">会话所属的项目；写进会话头，之后不变。</param>
/// <param name="Directories">只给本会话追加的文件夹（§6.4，绝对路径）。</param>
public sealed record CreateSessionRequest(string Cwd, string ModelId, SessionOptionsDto? Options = null, string? ProjectId = null,
    IReadOnlyList<string>? Directories = null);

/// <param name="Options">生效后的完整参数（各字段均非 null）。</param>
/// <param name="Runtime">外部运行时 id；null 表示内置 Agent。会话内只能切换到同一运行时的模型。</param>
/// <param name="ProjectId">会话头里记录的项目（旧会话为 null，归属按 §5.3 的回退规则由 UI 计算）。</param>
/// <param name="SessionDirectories">只给本会话追加的文件夹（§6.4）。</param>
/// <param name="ForkedFrom">本会话从哪里分叉而来（docs/design/message-rewind.md §4.5）；不是分叉的会话为 null。</param>
public sealed record SessionDto(string SessionId, string Cwd, string ModelId, string FilePath, SessionOptionsDto Options, string? Runtime = null,
    string? ProjectId = null, IReadOnlyList<string>? SessionDirectories = null, ForkedFromDto? ForkedFrom = null);

/// <param name="Title">分叉时原会话的标题。</param>
public sealed record ForkedFromDto(string SessionId, string FilePath, string? Title);

/// <param name="Title">用户重命名的标题；没有时为第一条用户消息（截断），尚无消息时为 null。</param>
/// <param name="IsPinned">置顶。</param>
/// <param name="HasCustomTitle">标题由用户重命名。</param>
/// <param name="NativeSessionId">外部运行时的原生会话 id。</param>
/// <param name="IsRunning">会话已在 Host 中打开且 Agent 正在运行。</param>
/// <param name="ProjectId">按 docs/design/projects.md §5.3 解析出的所属项目；不属于任何项目时为 null。</param>
public sealed record SessionSummaryDto(
    string SessionId,
    string FilePath,
    string Cwd,
    string? Title,
    string? ModelId,
    DateTimeOffset LastModified,
    bool IsRunning,
    string? Runtime = null,
    bool IsPinned = false,
    bool HasCustomTitle = false,
    string? NativeSessionId = null,
    string? ProjectId = null);

/// <summary>一页会话（置顶在前，其余按最近修改倒序）。</summary>
/// <param name="NextOffset">下一页的起始位置；null 表示没有更多。</param>
public sealed record SessionPageDto(IReadOnlyList<SessionSummaryDto> Items, int Total, int? NextOffset);

/// <summary>恢复会话的结果：会话本身与当前分支上的历史（用于重建转录）。</summary>
/// <param name="HistoryStart">History 中第一条消息在完整历史中的位置；大于 0 表示还有更早的消息（session/history 分页获取）。</param>
public sealed record OpenSessionResult(SessionDto Session, IReadOnlyList<HistoryMessageDto> History, bool IsRunning, int HistoryStart = 0);

/// <summary>一页更早的历史（按轮次切分，每页以用户消息开头）。</summary>
/// <param name="Start">本页第一条消息在完整历史中的位置；0 表示已到开头。</param>
public sealed record HistoryPageDto(IReadOnlyList<HistoryMessageDto> History, int Start);

/// <summary>子 Agent 的转录：已结束的消息与最近一条消息结束后的事件（按顺序应用在历史之后）。</summary>
/// <param name="Model">模型显示名；未知时为 null。</param>
public sealed record SubagentOpenResult(string AgentId, string Name, string? Model, IReadOnlyList<HistoryMessageDto> History,
    IReadOnlyList<SessionEvent> Replay, bool IsRunning);

/// <param name="Role">user / assistant / toolResult。</param>
/// <param name="Index">这条消息在会话完整历史中的位置（分页下标，只增不减）；分叉的截止点用它（docs/design/message-rewind.md §5.1）。</param>
public sealed record HistoryMessageDto(
    string Role,
    DateTimeOffset Timestamp,
    IReadOnlyList<HistoryBlockDto> Blocks,
    string? StopReason = null,
    string? ErrorMessage = null,
    UsageDto? Usage = null,
    string? Source = null,
    IReadOnlyList<PromptSegmentDto>? Segments = null,
    int Index = -1);

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(HistoryText), "text")]
[JsonDerivedType(typeof(HistoryThinking), "thinking")]
[JsonDerivedType(typeof(HistoryToolCall), "toolCall")]
[JsonDerivedType(typeof(HistoryToolResult), "toolResult")]
[JsonDerivedType(typeof(HistoryImage), "image")]
public abstract record HistoryBlockDto;

/// <summary>用户消息里的附图（附件路径）。</summary>
public sealed record HistoryImage(string Path) : HistoryBlockDto;

public sealed record HistoryText(string Text) : HistoryBlockDto;

/// <summary>思考内容（超长时截断）；<paramref name="DurationMs"/> 为记录的思考耗时（旧记录没有）。</summary>
public sealed record HistoryThinking(string Text, long? DurationMs = null) : HistoryBlockDto;

public sealed record HistoryToolCall(string ToolCallId, string ToolName, string ArgumentsJson) : HistoryBlockDto;

public sealed record HistoryToolResult(string ToolCallId, string ToolName, bool IsError, string Text, string? DetailsJson = null) : HistoryBlockDto;

public enum StreamingBehaviorDto
{
    Steer,
    FollowUp,
}

/// <param name="Images">图片附件（已保存在客户端附件目录）；AgentHost 读取文件、以 base64 发给模型，会话记录只保存路径。</param>
/// <param name="McpResources">@ 引用的 MCP 资源（Host 读取后附在消息末尾）。</param>
/// <param name="McpPrompt">/ 选择的 MCP 提示词（Host 展开后放在消息开头）。</param>
/// <param name="Text">发给模型的文字（command tag 不在其中，由 Host 按 <paramref name="Segments"/> 展开后加在前面）。</param>
/// <param name="Segments">输入框的内容（文字与 tag），用于展开 command 与在转录中显示；没有 tag 时可以省略。</param>
public sealed record PromptRequest(string SessionId, string Text, StreamingBehaviorDto WhileRunning = StreamingBehaviorDto.FollowUp,
    IReadOnlyList<ImageAttachmentDto>? Images = null, IReadOnlyList<McpResourceRefDto>? McpResources = null, McpPromptRefDto? McpPrompt = null,
    IReadOnlyList<PromptSegmentDto>? Segments = null);

/// <summary>输入框的一段（docs/design/composer.md §12）：文字，或 tag（kind 为 file / folder / command / skill / mcpResource / mcpPrompt / image）。</summary>
public sealed record PromptSegmentDto(string Kind, string? Text = null, string? Value = null, string? Label = null);

public sealed record ImageAttachmentDto(string Path, string MimeType);

public sealed record PromptResult(bool Started);

/// <param name="Kind">command / fileChange / read / network / tool。</param>
/// <param name="Title">一句话描述，例如命令行或文件路径。</param>
/// <param name="Reason">Agent 给出的理由。</param>
/// <param name="CanAllowForSession">可以选择“本会话内允许”。</param>
public sealed record ToolApprovalRequest(
    string SessionId,
    string ToolCallId,
    string ToolName,
    string ArgumentsJson,
    string? Kind = null,
    string? Title = null,
    string? Reason = null,
    bool CanAllowForSession = false);

/// <param name="ForSession">与 Approved 同时为 true 时表示“本会话内允许”（同类操作不再询问）。</param>
public sealed record ToolApprovalResponse(bool Approved, string? Reason = null, bool ForSession = false);

/// <summary>向用户提问（input/request，docs/design/ask-user.md §6）。</summary>
/// <param name="Source">builtin / claude-code / codex：决定面板标题。</param>
/// <param name="ToolCallId">对应的工具调用（转录里的问答卡片据此关联）。</param>
public sealed record UserInputRequest(
    string SessionId,
    string RequestId,
    string Source,
    string? ToolCallId,
    IReadOnlyList<UserQuestionDto> Questions,
    string? AgentName = null);

/// <param name="Id">问题标识：内置与 Claude 为问题原文，Codex 为问题 id。</param>
/// <param name="Options">为空时只有文本框。</param>
/// <param name="AllowOther">是否提供“其他”自由输入。</param>
/// <param name="IsSecret">文本框掩码显示。</param>
public sealed record UserQuestionDto(
    string Id,
    string Header,
    string Question,
    IReadOnlyList<UserQuestionOptionDto> Options,
    bool MultiSelect = false,
    bool AllowOther = true,
    bool IsSecret = false);

public sealed record UserQuestionOptionDto(string Label, string? Description = null);

/// <param name="Outcome">answered / declined。</param>
public sealed record UserInputResponse(string Outcome, IReadOnlyList<UserAnswerDto>? Answers = null)
{
    public const string Answered = "answered";
    public const string Declined = "declined";

    public static UserInputResponse Decline { get; } = new(Declined);
}

/// <param name="Selected">选中的选项标签（按选项顺序）。</param>
/// <param name="Text">“其他”里输入的文本；空表示没有。</param>
public sealed record UserAnswerDto(string QuestionId, IReadOnlyList<string> Selected, string? Text = null);

/// <summary>计划审批（plan/review，docs/design/plan-mode.md §13.1）。</summary>
/// <param name="Source">builtin / claude-code：决定面板标题。</param>
/// <param name="ToolCallId">对应的工具调用（转录里的计划卡片据此关联）。</param>
/// <param name="PlanPath">计划文件的绝对路径（“在编辑器中打开”）。</param>
/// <param name="Plan">计划全文（Markdown）。</param>
/// <param name="Choices">可选的批准方式，按显示顺序：clear / auto / ask / acp 的子集。</param>
/// <param name="PermissionMode">当前权限档位（ask / autoApprove / fullAccess）：决定选项上显示的档位。</param>
public sealed record PlanReviewRequest(
    string SessionId,
    string RequestId,
    string Source,
    string? ToolCallId,
    string PlanPath,
    string Plan,
    IReadOnlyList<string> Choices,
    string PermissionMode);

/// <param name="Outcome">approved / revise / dismissed。</param>
/// <param name="Choice">批准时选中的方式（<see cref="PlanReviewRequest.Choices"/> 之一）。</param>
/// <param name="Feedback">继续完善时的反馈；空表示没有。</param>
/// <param name="EditedPlan">用户在面板里编辑过计划时的新版本；null 表示没改。</param>
public sealed record PlanReviewResponse(string Outcome, string? Choice = null, string? Feedback = null, string? EditedPlan = null)
{
    public const string Approved = "approved";
    public const string Revise = "revise";
    public const string Dismissed = "dismissed";

    public static PlanReviewResponse Dismiss { get; } = new(Dismissed);
}

public enum RuntimeStatusDto
{
    Unknown,
    Ready,
    NotInstalled,
    Error,
}

/// <summary>外部 Agent 运行时（自动发现，免配置）。</summary>
/// <param name="Id">运行时 id，同时是其模型的 Provider：claude-code / codex / acp:&lt;agent&gt;。</param>
/// <param name="Name">显示名，例如 “Claude Agent”。</param>
/// <param name="AuthMethod">CLI 报告的认证方式（只用于显示）。</param>
/// <param name="SupportsSteering">运行中发送的消息能插入当前轮；否则排队到本轮结束后。</param>
public sealed record RuntimeDto(
    string Id,
    string Name,
    RuntimeStatusDto Status,
    string? Version,
    string? ExecutablePath,
    string? AuthMethod,
    string? Error,
    int ModelCount,
    bool SupportsSteering,
    bool SupportsResume);

public sealed record UsageDto(long Input, long Output, long CacheRead, long CacheWrite);

/// <summary>上下文占用（输入框下方占用环的悬停卡片，docs/design/context-usage.md）。</summary>
/// <param name="Tokens">当前上下文的 token 数（最近一次真实用量加之后新增内容的估算）。</param>
/// <param name="Breakdown">按来源分类，各类之和等于 <paramref name="Tokens"/>；外部运行时为 null。</param>
/// <param name="CacheHitRate">整个会话（含已压缩掉的消息）按 token 加权的提示缓存命中率（0–1）；没有任何缓存读写时为 null。</param>
public sealed record ContextUsageDto(long Tokens, ContextBreakdownDto? Breakdown, double? CacheHitRate);

/// <summary>上下文按来源分类的 token 数：估算后按真实用量归一，差额计入 <paramref name="Other"/>。</summary>
public sealed record ContextBreakdownDto(long SystemPrompt, long SystemTools, long McpTools, long Skills, long Messages, long Other);
