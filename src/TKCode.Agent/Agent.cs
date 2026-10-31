using System.Diagnostics;
using System.Text.Json;
using TKCode.Agent.Compaction;
using TKCode.Ai;
using TKCode.Ai.Messages;
using TKCode.Ai.Models;
using TKCode.Ai.Streaming;

namespace TKCode.Agent;

public enum QueueMode
{
    /// <summary>每次只取最早的一条。</summary>
    OneAtATime,
    /// <summary>一次取出全部。</summary>
    All,
}

/// <summary>
/// 与协议无关的 Agent 循环：调用模型 → 执行工具 → 回传结果，直到模型不再调用工具。
/// steer：当前轮（含工具）结束后、下一次模型调用前插入；followUp：Agent 即将停止时插入。
/// 历史只追加，从不改写已有消息；唯一的例外是压缩（docs/design/compaction.md）：整段历史替换为摘要，
/// 只发生在两次模型请求之间。
/// </summary>
public sealed class Agent : IAgentLoop
{
    private readonly WireApiRegistry _apis;
    private readonly List<Message> _messages = [];
    private readonly List<Func<AgentEvent, CancellationToken, ValueTask>> _listeners = [];
    private readonly Queue<UserMessage> _steering = new();
    private readonly Queue<UserMessage> _followUps = new();
    private readonly Lock _gate = new();
    private CancellationTokenSource? _runCts;
    private Task _running = Task.CompletedTask;

    // 压缩状态（docs/design/compaction.md §5、§10）
    private bool _manualCompactPending;
    private string? _manualCompactInstructions;
    private bool _compactBeforeNextRequest;
    private int _autoCompactFailures;
    private int _rapidRefills;
    private int _turnsSinceCompact = int.MaxValue;

    // 工具触发的模式切换（docs/design/plan-mode.md §9、§11）
    private bool _contextRefreshRequested;
    private string? _handoff;

    public Agent(WireApiRegistry apis, ModelInfo model, string systemPrompt, IReadOnlyList<IAgentTool> tools)
    {
        _apis = apis;
        Model = model;
        SystemPrompt = systemPrompt;
        Tools = tools;
    }

    public ModelInfo Model { get; set; }

    /// <summary>开头的系统提示。只应在 <see cref="ContextUpdates"/> 中替换（新会话首轮前按当时的动态节重建），只在两次模型请求之间变化。</summary>
    public string SystemPrompt { get; set; }

    /// <summary>工具集。只应在 <see cref="ContextUpdates"/> 中替换（例如 MCP 工具变化、模式切换），只在两次模型请求之间变化。</summary>
    public IReadOnlyList<IAgentTool> Tools { get; set; }
    public ThinkingLevel ThinkingLevel { get; set; } = ThinkingLevel.Medium;
    /// <summary>请求 fast mode（模型不支持时由适配器忽略）。</summary>
    public bool FastMode { get; set; }

    /// <summary>权限档位；内置 Agent 由宿主的审批钩子读取（非 Ask 时直接允许）。</summary>
    public PermissionMode PermissionMode { get; set; }
    public QueueMode SteeringMode { get; set; } = QueueMode.OneAtATime;
    public QueueMode FollowUpMode { get; set; } = QueueMode.OneAtATime;
    public string? SessionId { get; init; }
    public Func<string, string?>? GetApiKey { get; init; }

    /// <summary>缓存断点的有效期（全局设置，每次请求读取，修改后立即生效）；为 null 时用 5 分钟。</summary>
    public Func<PromptCacheTtl>? GetCacheTtl { get; init; }

    /// <summary>执行前钩子：审批、拦截危险操作等。</summary>
    public Func<BeforeToolCallContext, CancellationToken, ValueTask<ToolCallDecision>>? BeforeToolCall { get; set; }

    /// <summary>
    /// 每批用户输入（新一次运行、followUp）追加之前调用：返回需要先追加的上下文消息
    /// （例如系统提示某节变化后的 <see cref="SystemMessage"/> 补丁）。历史只追加，不改写开头的系统提示。
    /// 工具调用 <see cref="RequestContextRefresh"/> 后，也会在这批工具结果之后调用一次。
    /// </summary>
    public Func<CancellationToken, ValueTask<IReadOnlyList<Message>>>? ContextUpdates { get; set; }

    /// <summary>
    /// 请求在本批工具结果之后、下一次模型请求之前重新计算上下文（<see cref="ContextUpdates"/>）：模式切换类工具
    /// （exit_plan_mode / enter_plan_mode）用它让新的工具集与模式节在同一轮内生效（docs/design/plan-mode.md §9）。
    /// </summary>
    public void RequestContextRefresh()
    {
        lock (_gate)
        {
            _contextRefreshRequested = true;
        }
    }

    /// <summary>
    /// 下一次模型请求之前把历史换成交接消息（批准计划并清空上下文，docs/design/plan-mode.md §11）。
    /// 走压缩的替换路径（<see cref="CompactionTrigger.PlanHandoff"/>），不请求模型；没有压缩器时忽略。
    /// </summary>
    public void RequestHandoff(string message)
    {
        lock (_gate)
        {
            _handoff = message;
        }
    }

    /// <summary>
    /// 消息进入历史之前调用：把图片数据转存到文件，历史里只留路径（<see cref="ImageContent.Path"/>），
    /// 发给模型时再读回。
    /// </summary>
    public Func<Message, Message>? Offload { get; set; }

    /// <summary>压缩器；为 null 时不压缩（自动、被动与手动都不可用）。</summary>
    public ICompactor? Compactor { get; set; }

    /// <summary>上下文预算（用户选择的档位，≤ 模型窗口）；0 表示用模型窗口。自动压缩阈值据此计算。</summary>
    public int ContextBudget { get; set; }

    /// <summary>自动压缩（请求前超过阈值、超窗后重试）。关闭后仍可手动压缩。</summary>
    public bool AutoCompact { get; set; } = true;

    /// <summary>本会话已停用自动压缩的原因（连续失败、反复触发等，§10）；null 表示正常。</summary>
    public string? AutoCompactSuspended { get; private set; }

    /// <summary>当前预算与模型下的压缩阈值。</summary>
    public ContextThresholds Thresholds => ContextAccounting.Thresholds(ContextBudget, Model);

    public bool IsRunning
    {
        get
        {
            lock (_gate)
            {
                return _runCts is not null;
            }
        }
    }

    public IReadOnlyList<Message> Messages
    {
        get
        {
            lock (_gate)
            {
                return _messages.ToArray();
            }
        }
    }

    /// <summary>恢复会话时载入既有历史（不触发事件）。</summary>
    public void LoadHistory(IEnumerable<Message> messages)
    {
        lock (_gate)
        {
            if (_runCts is not null)
            {
                throw new InvalidOperationException("Cannot load history while running.");
            }
            _messages.Clear();
            _messages.AddRange(Offload is { } offload ? messages.Select(offload) : messages);
        }
    }

    public IDisposable Subscribe(Func<AgentEvent, CancellationToken, ValueTask> listener)
    {
        lock (_gate)
        {
            _listeners.Add(listener);
        }
        return new Unsubscriber(() =>
        {
            lock (_gate)
            {
                _listeners.Remove(listener);
            }
        });
    }

    /// <summary>开始一次运行。运行中请改用 <see cref="Steer"/> 或 <see cref="FollowUp"/>。</summary>
    public Task PromptAsync(UserMessage message)
    {
        CancellationTokenSource cts;
        lock (_gate)
        {
            if (_runCts is not null)
            {
                throw new InvalidOperationException("Agent is already running; use Steer() or FollowUp().");
            }
            cts = _runCts = new CancellationTokenSource();
            _running = Task.Run(() => RunAsync([message], cts.Token));
        }
        return _running;
    }

    /// <summary>
    /// 不追加新的输入，按现有历史再请求一次：上一次请求出错后「重试」（docs/design/message-features-report.md R1）。
    /// 出错 / 中止的助手消息在构造请求时本来就被跳过，历史只追加、不改写。
    /// </summary>
    public Task ContinueAsync()
    {
        CancellationTokenSource cts;
        lock (_gate)
        {
            if (_runCts is not null)
            {
                throw new InvalidOperationException("Agent is already running.");
            }
            if (_messages.Count == 0)
            {
                throw new InvalidOperationException("There is nothing to continue.");
            }
            cts = _runCts = new CancellationTokenSource();
            _running = Task.Run(() => RunAsync([], cts.Token));
        }
        return _running;
    }

    public void Steer(UserMessage message)
    {
        lock (_gate)
        {
            _steering.Enqueue(message);
        }
    }

    public void FollowUp(UserMessage message)
    {
        lock (_gate)
        {
            _followUps.Enqueue(message);
        }
    }

    /// <summary>
    /// 手动压缩（/compact）。空闲时立即作为一次运行执行；运行中则在下一次模型请求前执行，
    /// 若本次运行没有下一次请求，则在运行结束后执行。
    /// </summary>
    public Task CompactAsync(string? instructions = null)
    {
        if (Compactor is null)
        {
            throw new InvalidOperationException("Compaction is not available for this session.");
        }
        lock (_gate)
        {
            if (_runCts is not null)
            {
                _manualCompactPending = true;
                _manualCompactInstructions = instructions;
                return Task.CompletedTask;
            }
            var cts = _runCts = new CancellationTokenSource();
            _running = Task.Run(() => RunCompactionAsync(instructions, cts.Token));
            return _running;
        }
    }

    public void Abort()
    {
        lock (_gate)
        {
            _runCts?.Cancel();
        }
    }

    /// <summary>等到没有运行为止（包括收尾时因排队消息自动开始的后续运行）。</summary>
    public async Task WaitForIdleAsync()
    {
        while (true)
        {
            Task running;
            lock (_gate)
            {
                running = _running;
            }
            await running;
            lock (_gate)
            {
                if (ReferenceEquals(running, _running) && _runCts is null)
                {
                    return;
                }
            }
        }
    }

    private async Task RunAsync(IReadOnlyList<UserMessage> prompts, CancellationToken ct)
    {
        var newMessages = new List<Message>();
        try
        {
            await EmitAsync(new AgentStartEvent(), ct);
            IReadOnlyList<Message> pending = await WithContextUpdatesAsync(prompts, ct);

            while (true)
            {
                var stopped = false;
                var reactiveTried = false;
                while (true)
                {
                    // 新输入追加之前压缩：压缩后的历史 = 摘要 + 新的用户输入（§8.1）；已合并进新系统提示的补丁不再追加
                    if (await CompactBeforeRequestAsync(pending, newMessages, ct))
                    {
                        pending = pending.Where(m => m is not SystemMessage).ToArray();
                    }
                    foreach (var prompt in pending)
                    {
                        await AppendAsync(prompt, newMessages, ct);
                    }
                    pending = [];

                    await EmitAsync(new TurnStartEvent(), ct);
                    var assistant = await StreamAssistantAsync(newMessages, ct);
                    if (assistant.StopReason is StopReason.Error or StopReason.Aborted)
                    {
                        await EmitAsync(new TurnEndEvent(assistant, []), CancellationToken.None);
                        // 超窗：压缩后重试同一请求一次（§5 reactive）
                        if (assistant.StopReason == StopReason.Error && ContextOverflow.Is(assistant) && !reactiveTried &&
                            Compactor is not null && AutoCompact)
                        {
                            reactiveTried = true;
                            if (await TryCompactAsync(CompactionTrigger.Reactive, null, resumesWork: true, newMessages, ct))
                            {
                                continue;
                            }
                        }
                        stopped = true;
                        break;
                    }
                    reactiveTried = false;
                    _turnsSinceCompact = _turnsSinceCompact == int.MaxValue ? int.MaxValue : _turnsSinceCompact + 1;
                    if (ContextOverflow.Is(assistant))
                    {
                        // 输出撞上模型窗口（截断）：下一次请求前先压缩
                        _compactBeforeNextRequest = true;
                    }

                    var toolCalls = assistant.Content.OfType<ToolCallContent>().ToList();
                    var results = toolCalls.Count == 0
                        ? []
                        : await ExecuteToolCallsAsync(assistant, toolCalls, newMessages, ct);
                    await EmitAsync(new TurnEndEvent(assistant, results), ct);

                    pending = Dequeue(_steering, SteeringMode);
                    // 工具切换了模式：新的工具集与模式节补丁在下一次请求前生效，补丁紧跟在工具结果之后
                    if (TakeContextRefresh() && ContextUpdates is { } refresh)
                    {
                        var patch = await refresh(ct);
                        if (patch.Count > 0)
                        {
                            pending = [.. patch, .. pending];
                        }
                    }
                    if (pending.Count > 0 || toolCalls.Count > 0)
                    {
                        continue;
                    }
                    break;
                }

                if (stopped)
                {
                    break;
                }
                var followUps = Dequeue(_followUps, FollowUpMode);
                if (followUps.Count == 0)
                {
                    break;
                }
                pending = await WithContextUpdatesAsync(followUps, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 中止：已产生的部分消息已追加，直接结束。
        }
        finally
        {
            await EmitAsync(new AgentEndEvent(newMessages), CancellationToken.None);
            EndRun(ct);
        }
    }

    /// <summary>
    /// 结束一次运行。收尾期间排进队列的消息（例如后台任务完成通知）不能丢：未被中止时自动开始下一次运行；
    /// 运行中请求的手动压缩若还没执行，先执行它。
    /// </summary>
    private void EndRun(CancellationToken ct)
    {
        lock (_gate)
        {
            _runCts?.Dispose();
            _runCts = null;
            // 本轮没用上的模式切换请求作废：下一批输入前会照常重新计算上下文
            _contextRefreshRequested = false;
            _handoff = null;
            if (ct.IsCancellationRequested)
            {
                _manualCompactPending = false;
                return;
            }
            if (_manualCompactPending)
            {
                var instructions = _manualCompactInstructions;
                _manualCompactPending = false;
                _manualCompactInstructions = null;
                var compact = _runCts = new CancellationTokenSource();
                _running = Task.Run(() => RunCompactionAsync(instructions, compact.Token));
                return;
            }
            if (_steering.Count > 0 || _followUps.Count > 0)
            {
                var queued = _steering.Concat(_followUps).ToArray();
                _steering.Clear();
                _followUps.Clear();
                var next = _runCts = new CancellationTokenSource();
                _running = Task.Run(() => RunAsync(queued, next.Token));
            }
        }
    }

    /// <summary>只做一次手动压缩的运行（空闲时执行 /compact）。</summary>
    private async Task RunCompactionAsync(string? instructions, CancellationToken ct)
    {
        var newMessages = new List<Message>();
        try
        {
            await EmitAsync(new AgentStartEvent(), ct);
            await TryCompactAsync(CompactionTrigger.Manual, instructions, resumesWork: false, newMessages, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        finally
        {
            await EmitAsync(new AgentEndEvent(newMessages), CancellationToken.None);
            EndRun(ct);
        }
    }

    /// <summary>
    /// 每次模型请求前（<paramref name="pending"/> 是即将追加的新输入）：执行排队的手动压缩、超窗后的补偿压缩，
    /// 或在超过阈值时自动压缩（§5、§10）。返回是否压缩了。
    /// </summary>
    private async Task<bool> CompactBeforeRequestAsync(IReadOnlyList<Message> pending, List<Message> newMessages, CancellationToken ct)
    {
        if (Compactor is null)
        {
            return false;
        }
        // 没有新输入（工具轮之间）时，压缩后模型应直接接着干活
        var resumesWork = pending.Count == 0;
        string? manual;
        bool hasManual;
        string? handoff;
        lock (_gate)
        {
            hasManual = _manualCompactPending;
            manual = _manualCompactInstructions;
            _manualCompactPending = false;
            _manualCompactInstructions = null;
            handoff = _handoff;
            _handoff = null;
        }
        if (handoff is not null)
        {
            return await TryCompactAsync(CompactionTrigger.PlanHandoff, null, resumesWork: false, newMessages, ct, handoff);
        }
        if (hasManual)
        {
            return await TryCompactAsync(CompactionTrigger.Manual, manual, resumesWork: false, newMessages, ct);
        }
        var force = _compactBeforeNextRequest;
        _compactBeforeNextRequest = false;
        if (!AutoCompact)
        {
            return false;
        }
        if (force)
        {
            return await TryCompactAsync(CompactionTrigger.Reactive, null, resumesWork, newMessages, ct);
        }
        if (AutoCompactSuspended is not null)
        {
            return false;
        }
        var thresholds = Thresholds;
        if (ContextAccounting.ContextTokens(Messages) + ContextAccounting.Estimate(pending) < thresholds.AutoCompactAt)
        {
            return false;
        }
        // 系统提示与工具定义本身已超过阈值：压缩也无济于事
        var prefix = ContextAccounting.EstimateText(SystemPrompt) +
                     Tools.Sum(t => ContextAccounting.EstimateText(t.Description) + ContextAccounting.EstimateText(t.ParametersSchemaJson));
        if (prefix >= thresholds.AutoCompactAt)
        {
            await SuspendAutoCompactAsync("The system prompt and tool definitions alone exceed the auto-compact threshold; compaction cannot help.");
            return false;
        }
        // 压缩后 3 轮内又到阈值，连续 3 次：判定为反复触发
        if (_turnsSinceCompact <= 3)
        {
            if (++_rapidRefills >= 3)
            {
                await SuspendAutoCompactAsync(
                    "Auto-compact is thrashing: the context refilled to the limit within a few turns of the previous compaction, several times in a row. " +
                    "A file being read or a tool output is likely too large for the context window. Try reading in smaller chunks, or start a new session.");
                return false;
            }
        }
        else
        {
            _rapidRefills = 0;
        }
        if (await TryCompactAsync(CompactionTrigger.Auto, null, resumesWork, newMessages, ct))
        {
            _autoCompactFailures = 0;
            return true;
        }
        if (++_autoCompactFailures >= 3)
        {
            await SuspendAutoCompactAsync("Auto-compact failed 3 times in a row and has been disabled for this session. Use /compact to retry.");
        }
        return false;
    }

    private async Task SuspendAutoCompactAsync(string reason)
    {
        AutoCompactSuspended = reason;
        await EmitAsync(new CompactionSuspendedEvent(reason), CancellationToken.None);
    }

    /// <summary>压缩并替换历史；失败时历史不变。用户中止时抛出取消异常。</summary>
    /// <param name="resumesWork">压缩后没有新的用户输入、模型应直接接着干活（摘要消息里加上“不要询问，直接继续”）。</param>
    /// <param name="handoff">交接消息（<see cref="CompactionTrigger.PlanHandoff"/>）：不生成摘要。</param>
    private async Task<bool> TryCompactAsync(CompactionTrigger trigger, string? instructions, bool resumesWork, List<Message> newMessages,
        CancellationToken ct, string? handoff = null)
    {
        var compactor = Compactor ?? throw new InvalidOperationException("No compactor.");
        var messages = Messages;
        var preTokens = ContextAccounting.ContextTokens(messages);
        await EmitAsync(new CompactionStartEvent(trigger, preTokens), ct);
        CompactionResult result;
        try
        {
            var request = new CompactionRequest(trigger, instructions, Model, SystemPrompt, Tools.Select(t => t.ToDefinition()).ToArray(),
                ForModel(messages), preTokens, CompleteAsync)
            {
                ResumesWork = resumesWork,
                Handoff = handoff,
            };
            result = await compactor.CompactAsync(request, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await EmitAsync(new CompactionEndEvent(trigger, preTokens, null, "Compaction was cancelled.", []), CancellationToken.None);
            throw;
        }
        catch (Exception ex)
        {
            await EmitAsync(new CompactionEndEvent(trigger, preTokens, null, ex.Message, []), CancellationToken.None);
            return false;
        }

        lock (_gate)
        {
            _messages.Clear();
        }
        SystemPrompt = result.SystemPrompt;
        _turnsSinceCompact = 0;
        await EmitAsync(new CompactionEndEvent(trigger, preTokens, result, null, messages), CancellationToken.None);
        foreach (var message in result.Messages)
        {
            await AppendAsync(message, newMessages, CancellationToken.None);
        }
        return true;
    }

    /// <summary>压缩用的一次性请求：当前模型、思考档位与 Key，不写入新的缓存断点（§6.1）。</summary>
    private async Task<AssistantMessage> CompleteAsync(LlmContext context, int maxTokens, CancellationToken ct)
    {
        var options = new StreamOptions
        {
            ApiKey = GetApiKey?.Invoke(Model.Provider),
            ThinkingLevel = ThinkingLevel,
            FastMode = FastMode,
            SessionId = SessionId,
            CacheTtl = CacheTtl,
            MaxTokens = maxTokens,
            SkipCacheWrite = true,
        };
        var stream = _apis.Stream(Model, context, options, ct);
        await foreach (var _ in stream.WithCancellation(CancellationToken.None))
        {
        }
        return await stream.Result;
    }

    private PromptCacheTtl CacheTtl => GetCacheTtl?.Invoke() ?? PromptCacheTtl.FiveMinutes;

    /// <summary>发给模型的消息：图片数据在这里才从文件读回，请求结束后即可回收。</summary>
    private Message[] ForModel(IEnumerable<Message> messages) => messages.Select(m => m switch
    {
        UserMessage user => user.ForModel(Model.SupportsImages),
        ToolResultMessage result => result.ForModel(),
        _ => m,
    }).ToArray();

    private bool TakeContextRefresh()
    {
        lock (_gate)
        {
            var requested = _contextRefreshRequested;
            _contextRefreshRequested = false;
            return requested;
        }
    }

    private async Task<IReadOnlyList<Message>> WithContextUpdatesAsync(IReadOnlyList<UserMessage> prompts, CancellationToken ct)
    {
        if (ContextUpdates is not { } updates)
        {
            return prompts;
        }
        var context = await updates(ct);
        return context.Count == 0 ? prompts : [.. context, .. prompts];
    }

    private async Task<AssistantMessage> StreamAssistantAsync(List<Message> newMessages, CancellationToken ct)
    {
        var messageId = NewId();
        var context = new LlmContext(SystemPrompt, ForModel(Messages), Tools.Select(t => t.ToDefinition()).ToArray());
        var options = new StreamOptions
        {
            ApiKey = GetApiKey?.Invoke(Model.Provider),
            ThinkingLevel = ThinkingLevel,
            FastMode = FastMode,
            SessionId = SessionId,
            CacheTtl = CacheTtl,
        };

        await EmitAsync(new MessageStartEvent(messageId, null), ct);
        var stream = _apis.Stream(Model, context, options, ct);
        // 不把 ct 传给枚举：流在中止时会以 ErrorEvent 收尾，需要读完它。
        await foreach (var e in stream.WithCancellation(CancellationToken.None))
        {
            if (e is not DoneEvent and not ErrorEvent)
            {
                await EmitAsync(new MessageUpdateEvent(messageId, e), CancellationToken.None);
            }
        }
        var message = await stream.Result;
        await AppendAsync(message, newMessages, CancellationToken.None, messageId);
        return message;
    }

    private async Task<IReadOnlyList<ToolResultMessage>> ExecuteToolCallsAsync(
        AssistantMessage assistant,
        List<ToolCallContent> toolCalls,
        List<Message> newMessages,
        CancellationToken ct)
    {
        var prepared = new List<(ToolCallContent Call, IAgentTool? Tool, JsonElement Args, AgentToolResult? Immediate)>();

        // 预检按源顺序串行执行（审批弹窗不应并发）。
        foreach (var call in toolCalls)
        {
            await EmitAsync(new ToolExecutionStartEvent(call.Id, call.Name, call.ArgumentsJson), ct);
            prepared.Add(await PrepareAsync(assistant, call, ct));
        }

        AgentToolResult[] results;
        if (prepared.Any(p => p.Immediate is null && p.Tool?.Concurrency == ToolConcurrency.Exclusive))
        {
            // 批内有独占工具：按源顺序串行，避免与其它调用交错（例如命令依赖前一次写入）。
            results = new AgentToolResult[prepared.Count];
            for (var i = 0; i < prepared.Count; i++)
            {
                var p = prepared[i];
                results[i] = await RunToolAsync(p.Call, p.Tool, p.Args, p.Immediate, ct);
            }
        }
        else
        {
            results = await Task.WhenAll(prepared.Select(p => RunToolAsync(p.Call, p.Tool, p.Args, p.Immediate, ct)));
        }

        var messages = new List<ToolResultMessage>();
        for (var i = 0; i < prepared.Count; i++)
        {
            var call = prepared[i].Call;
            var message = new ToolResultMessage(call.Id, call.Name, results[i].Content)
            {
                IsError = results[i].IsError,
                Details = results[i].Details,
            };
            messages.Add(message);
            await AppendAsync(message, newMessages, CancellationToken.None);
        }
        return messages;
    }

    private async Task<(ToolCallContent, IAgentTool?, JsonElement, AgentToolResult?)> PrepareAsync(
        AssistantMessage assistant, ToolCallContent call, CancellationToken ct)
    {
        if (assistant.StopReason == StopReason.Length)
        {
            return (call, null, default, AgentToolResult.Error("The response was truncated (max tokens); re-issue the tool call."));
        }
        var tool = Tools.FirstOrDefault(t => t.Name == call.Name);
        if (tool is null)
        {
            return (call, null, default, AgentToolResult.Error($"Tool '{call.Name}' not found."));
        }
        if (call.ArgumentsError is not null)
        {
            return (call, tool, default, AgentToolResult.Error($"Invalid tool arguments: {call.ArgumentsError}"));
        }

        JsonElement args;
        using (var doc = JsonDocument.Parse(call.ArgumentsJson))
        {
            args = doc.RootElement.Clone();
        }

        if (BeforeToolCall is { } hook)
        {
            ToolPermission permission;
            try
            {
                permission = tool.DescribePermission(args);
            }
            catch (Exception ex)
            {
                return (call, tool, args, AgentToolResult.Error($"Invalid tool arguments: {ex.Message}"));
            }
            var decision = await hook(new BeforeToolCallContext(tool, call, args, permission), ct);
            if (decision.Block)
            {
                return (call, tool, args, AgentToolResult.Error(decision.Reason ?? "Tool execution was blocked."));
            }
        }
        return (call, tool, args, null);
    }

    private async Task<AgentToolResult> RunToolAsync(
        ToolCallContent call, IAgentTool? tool, JsonElement args, AgentToolResult? immediate, CancellationToken ct)
    {
        var started = Stopwatch.GetTimestamp();
        AgentToolResult result;
        if (immediate is not null || tool is null)
        {
            result = immediate ?? AgentToolResult.Error("Tool unavailable.");
        }
        else if (ct.IsCancellationRequested)
        {
            result = AgentToolResult.Error("Operation aborted.");
        }
        else
        {
            var progress = new OrderedProgress(partial => EmitAsync(new ToolExecutionUpdateEvent(call.Id, call.Name, partial), CancellationToken.None));
            try
            {
                result = await tool.ExecuteAsync(new ToolInvocation(call.Id, args, progress), ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                result = AgentToolResult.Error("Operation aborted.");
            }
            catch (Exception ex)
            {
                result = AgentToolResult.Error(ex.Message);
            }
            // 进度事件必须先于结束事件送达。
            await progress.DrainAsync();
        }
        await EmitAsync(new ToolExecutionEndEvent(call.Id, call.Name, result, Stopwatch.GetElapsedTime(started)), CancellationToken.None);
        return result;
    }

    private async Task AppendAsync(Message message, List<Message> newMessages, CancellationToken ct, string? messageId = null)
    {
        var id = messageId ?? NewId();
        message = Offload?.Invoke(message) ?? message;
        if (messageId is null)
        {
            await EmitAsync(new MessageStartEvent(id, message), ct);
        }
        lock (_gate)
        {
            _messages.Add(message);
        }
        newMessages.Add(message);
        await EmitAsync(new MessageEndEvent(id, message), CancellationToken.None);
    }

    private IReadOnlyList<UserMessage> Dequeue(Queue<UserMessage> queue, QueueMode mode)
    {
        lock (_gate)
        {
            if (queue.Count == 0)
            {
                return [];
            }
            if (mode == QueueMode.OneAtATime)
            {
                return [queue.Dequeue()];
            }
            var all = queue.ToArray();
            queue.Clear();
            return all;
        }
    }

    private async ValueTask EmitAsync(AgentEvent e, CancellationToken ct)
    {
        Func<AgentEvent, CancellationToken, ValueTask>[] listeners;
        lock (_gate)
        {
            listeners = _listeners.ToArray();
        }
        foreach (var listener in listeners)
        {
            await listener(e, ct);
        }
    }

    private static string NewId() => Guid.CreateVersion7().ToString("N");

    /// <summary>按报告顺序依次投递进度（<see cref="Progress{T}"/> 经线程池投递，可能乱序）。</summary>
    private sealed class OrderedProgress(Func<AgentToolResult, ValueTask> emit) : IProgress<AgentToolResult>
    {
        private readonly Lock _gate = new();
        private Task _tail = Task.CompletedTask;

        public void Report(AgentToolResult value)
        {
            lock (_gate)
            {
                _tail = _tail.ContinueWith(_ => emit(value).AsTask(), CancellationToken.None,
                    TaskContinuationOptions.None, TaskScheduler.Default).Unwrap();
            }
        }

        public Task DrainAsync()
        {
            lock (_gate)
            {
                return _tail.ContinueWith(_ => { }, TaskScheduler.Default);
            }
        }
    }

    private sealed class Unsubscriber(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }
}
