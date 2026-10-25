using TKCode.Agent;
using TKCode.Ai.Messages;
using TKCode.Ai.Models;
using TKCode.Runtimes.Json;

namespace TKCode.Runtimes;

/// <summary>
/// 外部运行时的一个子 Agent 的转录（docs/design/subagent-view.md §5.1）：运行时把子 Agent 的帧写进 <see cref="Writer"/>，
/// 宿主据此累积、落盘并转发给打开了它的 UI。一个子 Agent 可以运行多次（Codex sendInput、Claude 追问），每次运行一个 <see cref="TurnWriter"/>。
/// </summary>
public sealed class RuntimeSubagent(Func<AgentEvent, ValueTask> emit, Action<string> link, Action<string?, string?> describe)
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>当前运行的写入器；没有在运行时为 null。</summary>
    public TurnWriter? Writer { get; private set; }

    public bool IsRunning => Writer is not null;

    /// <summary>关联到父会话中派生它的工具调用（UI 卡片按它打开转录）。</summary>
    public void Link(string toolCallId) => link(toolCallId);

    /// <summary>运行时晚些时候报告的名称或模型（例如 Claude 的 resolvedModel、Codex 子 thread 的 agentNickname）。</summary>
    public void Describe(string? name, string? model) => describe(name, model);

    /// <summary>开始一次运行（已在运行时返回当前写入器）。</summary>
    public async ValueTask<TurnWriter> BeginRunAsync(ModelInfo model)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (Writer is { } running)
            {
                return running;
            }
            await emit(new AgentStartEvent()).ConfigureAwait(false);
            return Writer = new TurnWriter(model, emit, _ => { });
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>结束当前运行（没有在运行时什么也不做）。</summary>
    public async ValueTask EndRunAsync(StopReason reason, string? errorMessage = null)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (Writer is not { } writer)
            {
                return;
            }
            Writer = null;
            await writer.CompleteAsync(reason, errorMessage).ConfigureAwait(false);
            await emit(new AgentEndEvent([])).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }
}

/// <summary>
/// 外部运行时的子 Agent 工具调用规范化为内置 Agent 的 SubagentDetails（<c>kind:"subagent"</c>），
/// UI 的卡片只认这一种格式（docs/design/subagent-view.md §5.4）。
/// </summary>
public static class SubagentDetailsJson
{
    /// <param name="status">running / completed / failed / stopped。</param>
    public static string Create(string agentId, string name, string agentType, string description, string status, bool background,
        long toolUses = 0, long tokens = 0, string? model = null) =>
        JsonBuilder.Object(w =>
        {
            w.WriteString("kind", "subagent");
            w.WriteString("agentId", agentId);
            w.WriteString("name", name);
            w.WriteString("agentType", agentType);
            w.WriteString("description", description);
            w.WriteString("status", status);
            w.WriteBoolean("background", background);
            w.WriteNumber("toolUses", toolUses);
            w.WriteNumber("tokens", tokens);
            if (model is { Length: > 0 })
            {
                w.WriteString("model", model);
            }
        }).GetRawText();
}
