using TKCode.Agent.Coding.Prompt;

namespace TKCode.Agent.Coding.Tools.Agents;

/// <summary>
/// 子 Agent 与跨会话消息的协调器（由 Host 实现）。工具只通过这个接口与其他会话交互，
/// 审批、会话生命周期与跨进程通信都留在 Host（docs/design/builtin-agent-tools.md §9）。
/// </summary>
public interface IAgentCoordinator
{
    /// <summary>创建并启动一个子 Agent。</summary>
    Task<SubagentHandle> SpawnAsync(SubagentRequest request, CancellationToken cancellationToken);

    /// <summary>调用方可以发消息的对象：自己的子 Agent、父会话、同一 Host 的其他会话、本机其他 Host 的会话。</summary>
    Task<IReadOnlyList<AgentPeer>> ListAsync(string callerSessionId, CancellationToken cancellationToken);

    /// <summary>投递一条消息；返回给模型的投递结果说明。</summary>
    Task<string> SendAsync(string callerSessionId, string to, string message, string? summary, CancellationToken cancellationToken);
}

/// <param name="AgentType">general-purpose / explore / plan。</param>
/// <param name="Model">模型引用（厂商/模型）；null 表示与父会话相同。</param>
/// <param name="Name">可选的名字，之后可用 send_message 按名字寻址。</param>
/// <param name="ToolCallId">父会话中派生它的工具调用；UI 按它打开子 Agent 的转录（docs/design/subagent-view.md §4）。</param>
public sealed record SubagentRequest(string ParentSessionId, string Description, string Prompt, string AgentType, string? Model, string? Name,
    string? ToolCallId = null);

/// <param name="Status">completed / failed / stopped。</param>
/// <param name="Report">子 Agent 最后一条消息的文本（交给父 Agent）。</param>
public sealed record SubagentOutcome(string Status, string Report, int ToolUses, long Tokens);

/// <param name="Kind">subagent / parent / session / peer。</param>
/// <param name="Status">busy / idle / completed。</param>
public sealed record AgentPeer(string Name, string Ref, string Kind, string Status, string? Cwd = null, string? Title = null);

/// <summary>运行中的子 Agent。</summary>
public sealed class SubagentHandle(string agentId, string name, string agentType, Task<SubagentOutcome> completion, Action abort,
    string? model = null)
{
    public string AgentId { get; } = agentId;
    public string Name { get; } = name;
    public string AgentType { get; } = agentType;

    /// <summary>子 Agent 使用的模型（显示名）。</summary>
    public string? Model { get; } = model;
    public Task<SubagentOutcome> Completion { get; } = completion;

    /// <summary>进度：已调用的工具数、累计 tokens、当前在做什么。</summary>
    public event Action<int, long, string?>? Progress;

    public void Abort() => abort();

    public void ReportProgress(int toolUses, long tokens, string? activity) => Progress?.Invoke(toolUses, tokens, activity);
}

/// <summary>子 Agent 的类型：决定工具集与追加的系统提示节。</summary>
public static class SubagentTypes
{
    public const string GeneralPurpose = "general-purpose";
    public const string Explore = "explore";
    public const string Plan = "plan";

    public static readonly string[] All = [GeneralPurpose, Explore, Plan];

    public static ToolSet ToolSetOf(string type) => type == GeneralPurpose ? ToolSet.NoSubagents : ToolSet.ReadOnly;

    public static IReadOnlyList<PromptSection> PromptOf(string type, string parentName)
    {
        var common =
            $"You are a subagent launched by the agent \"{parentName}\" to handle one task. The user does not see your intermediate messages; " +
            "only your final message is returned to the parent agent, so end with a self-contained report: what you did, what you found, " +
            "and the relevant file paths (with line numbers where useful). Do not ask the parent questions you can answer yourself; if you are blocked, explain why in the report. " +
            "Use send_message with to=\"main\" only if the parent must know something before you finish.";
        var role = type switch
        {
            Explore =>
                "Role: read-only exploration. Search and read the codebase (and the web if available) to answer the question. " +
                "You cannot modify files. Be thorough but report only conclusions and evidence, not file dumps.",
            Plan =>
                "Role: software architect. Investigate the codebase and produce a step-by-step implementation plan: the approach, " +
                "the files to change and why, risks and trade-offs, and how to verify the change. You cannot modify files.",
            _ =>
                "Role: general-purpose agent. Research, search, edit and run commands as needed to complete the task fully. " +
                "You cannot launch further subagents.",
        };
        return [new PromptSection("agent_role", common + "\n\n" + role)];
    }
}
