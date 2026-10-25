using TKCode.Agent.Plans;
using TKCode.Agent.Questions;
using TKCode.Ai.Messages;
using TKCode.Ai.Models;

namespace TKCode.Runtimes;

/// <summary>
/// 一种 Agent 运行时：外部 CLI（Claude Agent、Codex、ACP Agent）。
/// 运行时的 <see cref="Id"/> 同时是其模型的 <see cref="ModelInfo.Provider"/>，
/// 因此模型引用 <c>&lt;runtime&gt;/&lt;model&gt;</c> 就能确定由哪个运行时承载会话。
/// </summary>
public interface IAgentRuntime
{
    string Id { get; }

    /// <summary>界面显示名（例如 “Claude Agent”）。</summary>
    string DisplayName { get; }

    /// <summary>最近一次探测的结果；未探测前为 <see cref="RuntimeStatus.Unknown"/>。</summary>
    RuntimeDescriptor Descriptor { get; }

    /// <summary>探测安装、版本、认证与模型列表（只读操作，不会触发模型轮次）。结果缓存在 <see cref="Descriptor"/>。</summary>
    Task<RuntimeDescriptor> ProbeAsync(CancellationToken cancellationToken);

    /// <summary>描述（尤其是模型列表）在探测之外发生变化时触发，例如会话启动后得知了 Agent 的可选模型。</summary>
    event Action? DescriptorChanged;

    /// <summary>启动一个会话（通常对应一个 CLI 子进程）。</summary>
    Task<IRuntimeSession> StartSessionAsync(RuntimeSessionRequest request, CancellationToken cancellationToken);
}

public enum RuntimeStatus
{
    Unknown,
    /// <summary>找到了可执行文件并探测成功。</summary>
    Ready,
    /// <summary>未找到可执行文件。</summary>
    NotInstalled,
    /// <summary>可执行文件存在，但探测失败（版本过低、未登录、协议错误等），见 <see cref="RuntimeDescriptor.Error"/>。</summary>
    Error,
}

/// <summary>运行时的能力（参考 zeron：用数据描述差异，而不是在调用方写分支）。</summary>
public sealed record RuntimeCapabilities
{
    /// <summary>运行中能否追加用户消息并在下一步生效（Codex turn/steer、Claude stdin 追加）。不能时按 follow-up 排队。</summary>
    public bool SupportsSteering { get; init; }

    /// <summary>能否恢复 CLI 侧的会话。</summary>
    public bool SupportsResume { get; init; }

    /// <summary>能否在会话中途切换模型。</summary>
    public bool SupportsModelSwitch { get; init; }

    /// <summary>工具参数是否流式到达（否则 ToolCallStarted/Delta/Ended 连续发出）。</summary>
    public bool StreamsToolArguments { get; init; }

    /// <summary>主 Agent 能派生子 Agent（ACP 协调模式依赖它，docs/design/composer.md §11）。</summary>
    public bool SupportsSubagents { get; init; }

    /// <summary>原生实现 plan 模式与计划审批（Claude ExitPlanMode，docs/design/plan-mode.md §15）。</summary>
    public bool NativePlanMode { get; init; }
}

public sealed record RuntimeDescriptor(
    string Id,
    string DisplayName,
    RuntimeStatus Status,
    string? ExecutablePath = null,
    string? Version = null,
    string? AuthMethod = null,
    string? Error = null)
{
    public RuntimeCapabilities Capabilities { get; init; } = new();
    public IReadOnlyList<ModelInfo> Models { get; init; } = [];
}

/// <summary>
/// 会话的模式在外部运行时上的投影（docs/design/composer.md §11）：<see cref="ReadOnly"/> 时运行时用原生手段收紧
/// （Claude 的 plan 权限模式、Codex 的只读沙箱）；说明文字由 <see cref="ExternalAgent"/> 在模式变化后的第一轮注入。
/// </summary>
/// <param name="Name">模式名（agent / plan / acp / 自定义）。</param>
public sealed record RuntimeMode(string Name, bool ReadOnly)
{
    public static RuntimeMode Agent { get; } = new("agent", false);
}

/// <summary>运行时自带的斜杠命令（Claude initialize 的 commands、ACP available_commands_update）。</summary>
/// <param name="Name">不带斜杠的命令名。</param>
public sealed record RuntimeCommand(string Name, string? Description, string? ArgumentHint);

/// <summary>每轮生效的参数（模型与思考档位可在两轮之间改变）。</summary>
public sealed record TurnOptions(ModelInfo Model, ThinkingLevel ThinkingLevel, bool FastMode, TKCode.Agent.PermissionMode Permission)
{
    /// <summary>会话的模式；默认 Agent。</summary>
    public RuntimeMode Mode { get; init; } = RuntimeMode.Agent;

    /// <summary>审批自动允许（自动放行与完全授权）。</summary>
    public bool AutoApprove => Permission != TKCode.Agent.PermissionMode.Ask;

    /// <summary>
    /// 主文件夹（cwd）以外的工作区目录（项目的其他文件夹与本会话追加的文件夹，docs/design/projects.md §6.8），绝对路径。
    /// Claude 在启动时用 --add-dir、ACP 在 new / load / resume 时发送；Codex 每轮作为 writableRoots 发送。
    /// </summary>
    public IReadOnlyList<string> AdditionalDirectories { get; init; } = [];
}

public sealed record RuntimeSessionRequest(
    string Cwd,
    TurnOptions Options,
    /// <summary>恢复时的原生会话 id；null 表示新建。</summary>
    string? ResumeSessionId,
    IRuntimeHost Host,
    /// <summary>TKCode 的 MCP 桥接服务器（没有配置 MCP 服务器时为 null，不注入）。</summary>
    McpBridgeLaunch? McpBridge = null);

/// <summary>
/// 以 stdio MCP 服务器的形式注入外部运行时的 TKCode 桥接（docs/design/mcp.md §10）：服务器名固定为 tkcode，
/// 身份令牌与 socket 路径放在环境变量中，不出现在命令行参数里。
/// </summary>
public sealed record McpBridgeLaunch(string Command, IReadOnlyList<string> Args, IReadOnlyDictionary<string, string> Env)
{
    public const string ServerName = "tkcode";

    /// <summary>写 {command, args, env:{…}}（Claude --mcp-config 与 Codex mcp_servers 的写法）。</summary>
    internal void WriteObject(System.Text.Json.Utf8JsonWriter w, bool envAsArray = false)
    {
        w.WriteString("command", Command);
        w.WriteStartArray("args");
        foreach (var arg in Args)
        {
            w.WriteStringValue(arg);
        }
        w.WriteEndArray();
        if (envAsArray)
        {
            // ACP：env 为 [{name, value}]
            w.WriteStartArray("env");
            foreach (var (name, value) in Env)
            {
                w.WriteStartObject();
                w.WriteString("name", name);
                w.WriteString("value", value);
                w.WriteEndObject();
            }
            w.WriteEndArray();
        }
        else
        {
            w.WriteStartObject("env");
            foreach (var (name, value) in Env)
            {
                w.WriteString(name, value);
            }
            w.WriteEndObject();
        }
    }
}

/// <summary>会话向 TKCode 发起的交互：审批与提问。实现方负责超时与断连时的拒绝。</summary>
public interface IRuntimeHost
{
    ValueTask<ApprovalDecision> RequestApprovalAsync(RuntimeApprovalRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// 运行时自带的提问（Claude AskUserQuestion、Codex requestUserInput）交给 TKCode 的问答面板（docs/design/ask-user.md §8）。
    /// 不受权限档位影响；没有 UI 或失败时返回 Declined，轮次中止或请求被撤回时返回 Aborted。
    /// </summary>
    ValueTask<UserInputResult> RequestInputAsync(RuntimeInputRequest request, CancellationToken cancellationToken) =>
        ValueTask.FromResult(UserInputResult.Declined);

    /// <summary>
    /// 运行时请求结构化信息（ACP elicitation/create 的 form 模式），复用 MCP 表单卡片。
    /// 没有 UI、失败、中止时返回 cancel。
    /// </summary>
    ValueTask<RuntimeElicitationResult> RequestElicitationAsync(RuntimeElicitationRequest request, CancellationToken cancellationToken) =>
        ValueTask.FromResult(RuntimeElicitationResult.Cancel);

    /// <summary>
    /// 运行时自带的计划审批（Claude ExitPlanMode）交给 TKCode 的审批面板（docs/design/plan-mode.md §15）。
    /// 不受权限档位影响；没有 UI 或失败时为 Dismissed，轮次中止或请求被撤回时为 Aborted。批准时宿主已切换模式与权限。
    /// </summary>
    ValueTask<RuntimePlanReviewResult> RequestPlanReviewAsync(RuntimePlanReviewRequest request, CancellationToken cancellationToken) =>
        ValueTask.FromResult(new RuntimePlanReviewResult(PlanReviewResult.Dismissed, TKCode.Agent.PermissionMode.Ask));

    /// <summary>运行时自己进入或离开了 plan 模式（例如模型调用了 EnterPlanMode）：宿主同步 TKCode 的模式。</summary>
    void OnPlanModeChanged(bool planMode)
    {
    }

    /// <summary>原生会话 id 确定或改变（例如恢复失败后新建）。</summary>
    void OnNativeSessionId(string nativeSessionId);

    /// <summary>
    /// 子 Agent 的转录（docs/design/subagent-view.md §5.1）：同一 <paramref name="key"/>（Claude 为 parent_tool_use_id，Codex 为子 thread id）
    /// 返回同一个转录。宿主不支持时为 null，运行时照旧丢弃子 Agent 的帧。
    /// </summary>
    RuntimeSubagent? OpenSubagent(string key, string name, string? model) => null;

    /// <summary>运行时自带的斜杠命令列表（每次整体替换）。</summary>
    void OnCommands(IReadOnlyList<RuntimeCommand> commands)
    {
    }
}

/// <summary>
/// 一个运行时会话。<see cref="RunTurnAsync"/> 运行一整轮，直到 CLI 的终止帧（Claude <c>result</c>、
/// Codex <c>turn/completed</c>、ACP <c>session/prompt</c> 的响应）；取消令牌触发协议层中断，
/// 中断后仍等待终止帧（有上限），保证进程可继续使用。
/// </summary>
public interface IRuntimeSession : IAsyncDisposable
{
    string? NativeSessionId { get; }

    Task RunTurnAsync(UserMessage message, TurnOptions options, TurnWriter writer, CancellationToken cancellationToken);

    /// <summary>
    /// 每轮都按 <see cref="TurnOptions.AdditionalDirectories"/> 设置目录（Codex 的 writableRoots）。为 false 时目录只在启动会话时生效，
    /// 目录变化后由 <see cref="ExternalAgent"/> 在下一轮之前用原生会话 id 重启会话（docs/design/projects.md §6.8）。
    /// </summary>
    bool AppliesDirectoriesPerTurn => false;

    /// <summary>运行中追加用户消息；返回 false 表示当前无法插入（调用方改为排队）。</summary>
    ValueTask<bool> SteerAsync(UserMessage message, CancellationToken cancellationToken);
}

/// <param name="SchemaJson">扁平对象 schema（requestedSchema）。</param>
public sealed record RuntimeElicitationRequest(string Message, string? SchemaJson);

/// <param name="Action">accept / decline / cancel。</param>
/// <param name="ContentJson">accept 时的表单内容（JSON 对象）。</param>
public sealed record RuntimeElicitationResult(string Action, string? ContentJson = null)
{
    public static RuntimeElicitationResult Cancel { get; } = new("cancel");
}

/// <param name="ToolCallId">对应的工具调用（Codex 为合成的工具项 id）。</param>
public sealed record RuntimeInputRequest(string ToolCallId, IReadOnlyList<UserQuestion> Questions);

/// <param name="PlanPath">运行时自己的计划文件；没有时为 null。</param>
public sealed record RuntimePlanReviewRequest(string ToolCallId, string Plan, string? PlanPath);

/// <param name="Permission">批准后的权限档位（宿主已切换）：运行时据此设置原生权限模式。</param>
/// <param name="Steer">批准后要立即追加给模型的说明（交给 ACP 时的模式说明）；null 表示没有。</param>
public sealed record RuntimePlanReviewResult(PlanReviewResult Result, TKCode.Agent.PermissionMode Permission, string? Steer = null);

public enum ApprovalKind
{
    /// <summary>执行命令。</summary>
    Command,
    /// <summary>修改文件。</summary>
    FileChange,
    /// <summary>其他工具（MCP、网络等）。</summary>
    Tool,
}

public sealed record RuntimeApprovalRequest(
    string ToolCallId,
    string ToolName,
    string ArgumentsJson,
    ApprovalKind Kind,
    /// <summary>给用户看的一句话，例如命令行或文件路径。</summary>
    string? Title = null,
    /// <summary>CLI 给出的理由（例如 Codex 的 reason）。</summary>
    string? Reason = null,
    /// <summary>运行时是否支持“本会话内允许”。</summary>
    bool CanAllowForSession = true);

public enum ApprovalOutcome
{
    AllowOnce,
    AllowForSession,
    Deny,
    /// <summary>中止整轮（用户取消，或轮次被中断时还未答复的审批）。</summary>
    Cancel,
}

public sealed record ApprovalDecision(ApprovalOutcome Outcome, string? Message = null)
{
    public static ApprovalDecision AllowOnce { get; } = new(ApprovalOutcome.AllowOnce);
    public static ApprovalDecision Cancel { get; } = new(ApprovalOutcome.Cancel);
    public static ApprovalDecision Deny(string? message = null) => new(ApprovalOutcome.Deny, message);
    public bool IsAllowed => Outcome is ApprovalOutcome.AllowOnce or ApprovalOutcome.AllowForSession;
}
