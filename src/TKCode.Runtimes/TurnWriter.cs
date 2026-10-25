using System.Diagnostics;
using System.Text;
using System.Text.Json;
using TKCode.Agent;
using TKCode.Ai.Messages;
using TKCode.Ai.Models;
using TKCode.Ai.Streaming;

namespace TKCode.Runtimes;

public enum BlockKind
{
    Text,
    Thinking,
    ToolCall,
}

/// <summary>
/// 运行时驱动把 CLI 事件写进来，这里把它们组装成 TKCode 的规范消息与 <see cref="AgentEvent"/>：
/// <list type="bullet">
/// <item>文本、思考、工具调用累积在一条“草稿”助手消息里；草稿按需打开，懒关闭。</item>
/// <item>工具结果到达时先关闭草稿（助手消息 → 工具结果，保持历史顺序合法），之后的内容开新草稿。</item>
/// <item>轮次结束时关闭最后一条草稿，并附上整轮用量与停止原因（errors-as-messages）。</item>
/// </list>
/// 所有方法串行执行（内部加锁），驱动可以从任意线程调用。
/// </summary>
public sealed class TurnWriter
{
    private readonly Func<AgentEvent, ValueTask> _emit;
    private readonly Action<Message> _append;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly HashSet<string> _declaredToolCalls = new(StringComparer.Ordinal);
    private readonly HashSet<string> _startedTools = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _toolStartedAt = new(StringComparer.Ordinal);
    private Draft? _draft;
    private Usage _usage = Usage.Empty;
    private bool _completed;

    public TurnWriter(ModelInfo model, Func<AgentEvent, ValueTask> emit, Action<Message> append)
    {
        Model = model;
        _emit = emit;
        _append = append;
    }

    /// <summary>写入消息的模型；CLI 报告了实际模型时（例如 Claude 的 init）驱动可以修正。</summary>
    public ModelInfo Model { get; set; }

    public bool IsCompleted => _completed;

    // ---------------- 内容块 ----------------

    public async ValueTask<int> BeginBlockAsync(BlockKind kind, string? toolCallId = null, string? toolName = null)
    {
        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            return await BeginBlockCoreAsync(kind, toolCallId, toolName).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>追加增量。<paramref name="block"/> 来自 <see cref="BeginBlockAsync"/>；草稿已关闭时忽略。</summary>
    public async ValueTask AppendAsync(int block, string delta)
    {
        if (delta.Length == 0)
        {
            return;
        }
        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_draft?.Find(block) is { Ended: false } state)
            {
                await AppendCoreAsync(_draft, state, delta).ConfigureAwait(false);
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// 结束内容块。<paramref name="finalText"/> 为 CLI 给出的权威全文（例如 Codex 的 item/completed）：
    /// 若以已收到的增量为前缀，补发剩余部分；否则以全文为准写入消息。
    /// </summary>
    public async ValueTask EndBlockAsync(int block, string? finalText = null)
    {
        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_draft?.Find(block) is not { Ended: false } state)
            {
                return;
            }
            if (finalText is not null)
            {
                var current = state.Buffer.ToString();
                if (finalText.StartsWith(current, StringComparison.Ordinal))
                {
                    await AppendCoreAsync(_draft, state, finalText[current.Length..]).ConfigureAwait(false);
                }
                else
                {
                    state.Buffer.Clear().Append(finalText);
                }
            }
            await EndBlockCoreAsync(_draft, state).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>一次给全的文本块。</summary>
    public ValueTask TextAsync(string text) => WholeBlockAsync(BlockKind.Text, text, null, null);

    /// <summary>一次给全的思考块。</summary>
    public ValueTask ThinkingAsync(string text) => WholeBlockAsync(BlockKind.Thinking, text, null, null);

    /// <summary>参数一次给全的工具调用（Started、Delta、Ended 连续发出）。重复声明同一 id 时忽略。</summary>
    public ValueTask ToolCallAsync(string toolCallId, string toolName, string argumentsJson) =>
        WholeBlockAsync(BlockKind.ToolCall, argumentsJson, toolCallId, toolName);

    public bool IsToolCallDeclared(string toolCallId)
    {
        lock (_declaredToolCalls)
        {
            return _declaredToolCalls.Contains(toolCallId);
        }
    }

    // ---------------- 工具执行 ----------------

    /// <summary>工具开始执行（审批通过后）。同一 id 只发一次。</summary>
    public async ValueTask ToolStartedAsync(string toolCallId, string toolName, string argumentsJson)
    {
        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            await ToolStartedCoreAsync(toolCallId, toolName, argumentsJson).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>工具执行中的输出（例如命令的 stdout 增量，内容为截至目前的完整输出或新增片段由驱动约定）。</summary>
    public async ValueTask ToolProgressAsync(string toolCallId, string toolName, string text, string? details = null)
    {
        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            await _emit(new ToolExecutionUpdateEvent(toolCallId, toolName, AgentToolResult.Text(text) with { Details = details })).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// 工具结束：关闭当前草稿，发出 ToolExecutionEnd，并追加工具结果消息。
    /// 之前未声明过的工具调用会先补一条只含该调用的助手消息，保证历史中调用与结果成对。
    /// </summary>
    public async ValueTask ToolCompletedAsync(string toolCallId, string toolName, string resultText, bool isError,
        string argumentsJson = "{}", string? details = null)
    {
        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            bool declared;
            lock (_declaredToolCalls)
            {
                declared = _declaredToolCalls.Contains(toolCallId);
            }
            if (!declared)
            {
                var index = await BeginBlockCoreAsync(BlockKind.ToolCall, toolCallId, toolName).ConfigureAwait(false);
                var state = _draft!.Find(index)!;
                await AppendCoreAsync(_draft, state, argumentsJson).ConfigureAwait(false);
                await EndBlockCoreAsync(_draft, state).ConfigureAwait(false);
            }
            await CloseDraftAsync(null, null, final: false).ConfigureAwait(false);
            await ToolStartedCoreAsync(toolCallId, toolName, argumentsJson).ConfigureAwait(false);

            var result = new AgentToolResult([new TextContent(resultText)]) { IsError = isError, Details = details };
            var started = _toolStartedAt.GetValueOrDefault(toolCallId, System.Diagnostics.Stopwatch.GetTimestamp());
            await _emit(new ToolExecutionEndEvent(toolCallId, toolName, result, System.Diagnostics.Stopwatch.GetElapsedTime(started)))
                .ConfigureAwait(false);
            var message = new ToolResultMessage(toolCallId, toolName, result.Content) { IsError = isError, Details = details };
            await AppendMessageCoreAsync(message).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    // ---------------- 轮次 ----------------

    /// <summary>累加用量（多次调用求和）。</summary>
    public void AddUsage(Usage usage)
    {
        lock (_declaredToolCalls)
        {
            _usage = new Usage(_usage.Input + usage.Input, _usage.Output + usage.Output, _usage.CacheRead + usage.CacheRead,
                _usage.CacheWrite + usage.CacheWrite,
                usage.Reasoning is null && _usage.Reasoning is null ? null : (_usage.Reasoning ?? 0) + (usage.Reasoning ?? 0));
        }
    }

    /// <summary>以 CLI 报告的整轮用量为准（覆盖之前的累加）。</summary>
    public void SetUsage(Usage usage)
    {
        lock (_declaredToolCalls)
        {
            _usage = usage;
        }
    }

    /// <summary>运行中插入的用户消息（steer）：先关闭草稿，再追加。</summary>
    public async ValueTask UserMessageAsync(UserMessage message)
    {
        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            await CloseDraftAsync(null, null, final: false).ConfigureAwait(false);
            await AppendMessageCoreAsync(message).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// 结束整轮（只有第一次调用生效）：关闭草稿并写入停止原因、错误与整轮用量。
    /// 错误或中止而没有草稿时，追加一条只含错误信息的助手消息（errors-as-messages）。
    /// </summary>
    public async ValueTask CompleteAsync(StopReason stopReason, string? errorMessage = null)
    {
        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_completed)
            {
                return;
            }
            _completed = true;
            if (_draft is null && stopReason is StopReason.Error or StopReason.Aborted)
            {
                await OpenDraftAsync().ConfigureAwait(false);
            }
            await CloseDraftAsync(stopReason, errorMessage, final: true).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    // ---------------- 内部实现（调用方持有锁） ----------------

    private async ValueTask WholeBlockAsync(BlockKind kind, string text, string? toolCallId, string? toolName)
    {
        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (toolCallId is not null && IsToolCallDeclared(toolCallId))
            {
                return;
            }
            var index = await BeginBlockCoreAsync(kind, toolCallId, toolName).ConfigureAwait(false);
            var state = _draft!.Find(index)!;
            await AppendCoreAsync(_draft, state, text).ConfigureAwait(false);
            await EndBlockCoreAsync(_draft, state).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    private async ValueTask<int> BeginBlockCoreAsync(BlockKind kind, string? toolCallId, string? toolName)
    {
        var draft = _draft ?? await OpenDraftAsync().ConfigureAwait(false);
        var state = new BlockState(kind, draft.Blocks.Count) { ToolCallId = toolCallId, ToolName = toolName };
        draft.Blocks.Add(state);
        if (kind == BlockKind.ToolCall)
        {
            ArgumentNullException.ThrowIfNull(toolCallId);
            ArgumentNullException.ThrowIfNull(toolName);
            lock (_declaredToolCalls)
            {
                _declaredToolCalls.Add(toolCallId);
            }
        }
        LlmEvent e = kind switch
        {
            BlockKind.Text => new TextStartEvent(state.Index),
            BlockKind.Thinking => new ThinkingStartEvent(state.Index),
            _ => new ToolCallStartEvent(state.Index, toolCallId!, toolName!),
        };
        await _emit(new MessageUpdateEvent(draft.Id, e)).ConfigureAwait(false);
        // 驱动拿到的编号在草稿之间唯一：草稿序号 × 10000 + 块序号。
        return draft.Ordinal * BlockStride + state.Index;
    }

    private const int BlockStride = 10_000;

    private async ValueTask AppendCoreAsync(Draft draft, BlockState state, string delta)
    {
        if (delta.Length == 0)
        {
            return;
        }
        state.Buffer.Append(delta);
        state.LastDeltaAt = Stopwatch.GetTimestamp();
        LlmEvent e = state.Kind switch
        {
            BlockKind.Text => new TextDeltaEvent(state.Index, delta),
            BlockKind.Thinking => new ThinkingDeltaEvent(state.Index, delta),
            _ => new ToolCallDeltaEvent(state.Index, delta),
        };
        await _emit(new MessageUpdateEvent(draft.Id, e)).ConfigureAwait(false);
    }

    private async ValueTask EndBlockCoreAsync(Draft draft, BlockState state)
    {
        if (state.Ended)
        {
            return;
        }
        state.Ended = true;
        LlmEvent e = state.ToContent() switch
        {
            TextContent t => new TextEndEvent(state.Index, t),
            ThinkingContent t => new ThinkingEndEvent(state.Index, t),
            ToolCallContent c => new ToolCallEndEvent(state.Index, c),
            _ => throw new InvalidOperationException(),
        };
        await _emit(new MessageUpdateEvent(draft.Id, e)).ConfigureAwait(false);
    }

    private async ValueTask ToolStartedCoreAsync(string toolCallId, string toolName, string argumentsJson)
    {
        if (!_startedTools.Add(toolCallId))
        {
            return;
        }
        _toolStartedAt[toolCallId] = System.Diagnostics.Stopwatch.GetTimestamp();
        await _emit(new ToolExecutionStartEvent(toolCallId, toolName, argumentsJson)).ConfigureAwait(false);
    }

    private int _draftCount;

    private async ValueTask<Draft> OpenDraftAsync()
    {
        _draft = new Draft(Guid.CreateVersion7().ToString("N"), ++_draftCount);
        await _emit(new MessageStartEvent(_draft.Id, null)).ConfigureAwait(false);
        await _emit(new MessageUpdateEvent(_draft.Id, new StartEvent())).ConfigureAwait(false);
        return _draft;
    }

    /// <summary>关闭草稿。中途关闭（final=false）不带用量；最后一条带整轮用量与停止原因。</summary>
    private async ValueTask CloseDraftAsync(StopReason? stopReason, string? errorMessage, bool final)
    {
        if (_draft is not { } draft)
        {
            return;
        }
        foreach (var block in draft.Blocks)
        {
            await EndBlockCoreAsync(draft, block).ConfigureAwait(false);
        }
        var content = draft.Blocks.Select(b => b.ToContent()).ToArray();
        var hasToolCalls = content.OfType<ToolCallContent>().Any();
        Usage usage;
        lock (_declaredToolCalls)
        {
            usage = final ? _usage : Usage.Empty;
        }
        var message = new AssistantMessage(content)
        {
            Api = Model.Api,
            Provider = Model.Provider,
            Model = Model.Id,
            Usage = usage,
            StopReason = stopReason is null or StopReason.Stop && hasToolCalls && !final ? StopReason.ToolUse : stopReason ?? StopReason.Stop,
            ErrorMessage = errorMessage,
        };
        _draft = null;
        _append(message);
        await _emit(new MessageEndEvent(draft.Id, message)).ConfigureAwait(false);
    }

    private async ValueTask AppendMessageCoreAsync(Message message)
    {
        var id = Guid.CreateVersion7().ToString("N");
        await _emit(new MessageStartEvent(id, message)).ConfigureAwait(false);
        _append(message);
        await _emit(new MessageEndEvent(id, message)).ConfigureAwait(false);
    }

    private sealed class Draft(string id, int ordinal)
    {
        public string Id { get; } = id;
        public int Ordinal { get; } = ordinal;
        public List<BlockState> Blocks { get; } = [];

        public BlockState? Find(int handle) =>
            handle / BlockStride == Ordinal && handle % BlockStride is var i && i < Blocks.Count ? Blocks[i] : null;
    }

    private sealed class BlockState(BlockKind kind, int index)
    {
        public BlockKind Kind { get; } = kind;
        public int Index { get; } = index;
        public StringBuilder Buffer { get; } = new();
        public string? ToolCallId { get; init; }
        public string? ToolName { get; init; }
        public bool Ended { get; set; }
        public long StartedAt { get; } = Stopwatch.GetTimestamp();
        public long LastDeltaAt { get; set; }

        /// <summary>思考耗时：开始到最后一段增量（没有增量时为空）。</summary>
        public long? DurationMs => LastDeltaAt == 0 ? null : (long)Stopwatch.GetElapsedTime(StartedAt, LastDeltaAt).TotalMilliseconds;

        public ContentBlock ToContent() => Kind switch
        {
            BlockKind.Text => new TextContent(Buffer.ToString()),
            BlockKind.Thinking => new ThinkingContent(Buffer.ToString()) { DurationMs = DurationMs },
            _ => ToToolCall(),
        };

        private ToolCallContent ToToolCall()
        {
            var json = Buffer.Length == 0 ? "{}" : Buffer.ToString();
            try
            {
                using var doc = JsonDocument.Parse(json);
                return new ToolCallContent(ToolCallId!, ToolName!, json);
            }
            catch (JsonException ex)
            {
                return new ToolCallContent(ToolCallId!, ToolName!, "{}") { ArgumentsError = ex.Message };
            }
        }
    }
}
