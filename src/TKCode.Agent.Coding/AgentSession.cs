using TKCode.Agent.Coding.Assets;
using TKCode.Agent.Coding.Memory;
using TKCode.Agent.Coding.Prompt;
using TKCode.Agent.Coding.Sessions;
using TKCode.Agent.Coding.Tools;
using TKCode.Agent.Coding.Tools.Plan;
using TKCode.Agent.Coding.Tools.Shell;
using TKCode.Agent.Coding.Workspace;
using TKCode.Ai;
using TKCode.Ai.Messages;
using TKCode.Ai.Models;

namespace TKCode.Agent.Coding;

/// <summary>
/// 编码会话：在 Agent 循环之上加入工作目录、系统提示、内置工具与持久化。
/// 循环可以是内置的 <see cref="TKCode.Agent.Agent"/>，也可以是外部运行时（<see cref="IExternalAgentLoop"/>）。
/// 持久化监听器最先订阅，保证 MessageEnd 写盘后才会继续执行工具（屏障语义）。
/// </summary>
public sealed class AgentSession : IAsyncDisposable
{
    private readonly IDisposable _persistence;
    /// <summary>本次打开后压缩掉的消息：不再发给模型，只用于界面回看（<see cref="History"/>）。</summary>
    private readonly List<Message> _earlier = [];
    /// <summary>打开时会话文件里已压缩掉的消息条数：不常驻内存，回看到这里时才从文件读取。</summary>
    private int _earlierOnDisk;
    /// <summary>文件里已压缩掉的那部分消息的缓存累计（只读一次文件）。</summary>
    private CacheTotals? _earlierOnDiskCache;
    /// <summary>内置 Agent 的系统提示（外部运行时为 null）。</summary>
    private readonly SystemPromptTracker? _prompt;

    private AgentSession(IAgentLoop agent, SessionStore store, ToolContext? tools = null, SystemPromptTracker? prompt = null)
    {
        Agent = agent;
        Store = store;
        Tools = tools;
        _prompt = prompt;
        Workspace = tools?.Workspace ?? new WorkspaceRoots(store.Cwd);
        _persistence = agent.Subscribe(async (e, ct) =>
        {
            if (e is MessageEndEvent end)
            {
                await store.AppendMessageAsync(end.Message, CancellationToken.None);
            }
            else if (e is TKCode.Agent.CompactionEndEvent { Result: not null } compacted)
            {
                lock (_earlier)
                {
                    _earlier.AddRange(compacted.Replaced);
                }
            }
        });
        if (agent is IExternalAgentLoop external)
        {
            // 原生会话 id 一确定就记下来（文件创建前先缓存，随第一条消息写入），用于之后恢复。
            external.NativeSessionIdChanged += id =>
                _ = store.AppendRuntimeSessionAsync(external.RuntimeId, id, CancellationToken.None);
        }
        if (tools is not null)
        {
            tools.BackgroundTasks.Completed += OnBackgroundTaskCompleted;
            tools.Tasks.Changed += OnTasksChanged;
        }
    }

    public IAgentLoop Agent { get; }

    /// <summary>外部运行时的会话（内置 Agent 时为 null）。</summary>
    public IExternalAgentLoop? External => Agent as IExternalAgentLoop;
    public SessionStore Store { get; }
    public string Id => Store.SessionId;
    public string Cwd => Store.Cwd;

    /// <summary>工作区根目录（cwd 与额外加入的目录），决定只读工具的免审批范围。</summary>
    public WorkspaceRoots Workspace { get; }

    /// <summary>内置工具共享的会话状态（外部运行时为 null）。</summary>
    public ToolContext? Tools { get; }

    private SessionRole _role = SessionRole.Agent;

    /// <summary>会话的 role（docs/design/composer.md §10.3）。内置 Agent 在下一批用户输入前按它过滤工具、更新 role 节。</summary>
    public SessionRole Role
    {
        get => _role;
        set
        {
            var previous = _role;
            _role = value;
            if (Tools is not null)
            {
                Tools.Role = value;
            }
            // 外部运行时：只读由运行时原生收紧，说明随下一条用户消息发送一次（docs/design/composer.md §11）
            if (External is { } external)
            {
                // 原生实现 plan 的运行时（Claude）：Plan 由运行时自己的提示词负责，不发 TKCode 的说明（docs/design/plan-mode.md §12.8）
                var reminder = external.NativePlanMode && value.Name == SessionRole.PlanName ? null : SessionRole.ExternalReminder(previous, value);
                external.SetMode(value.Name, value.IsReadOnly, reminder);
            }
        }
    }

    /// <summary>“本会话允许”的审批记录（仅内存）。</summary>
    public SessionApprovals Approvals { get; init; } = new();

    /// <summary>
    /// 用户选择的上下文预算（≤ 模型上下文窗口）：占用比例与自动压缩阈值的依据（docs/design/compaction.md §4）；
    /// 各厂商没有对应的请求参数。
    /// </summary>
    public int ContextWindow
    {
        get;
        set
        {
            field = value;
            if (Agent is TKCode.Agent.Agent builtin)
            {
                builtin.ContextBudget = value;
            }
        }
    }

    /// <summary>
    /// 界面显示的完整历史：压缩掉的消息加上 Agent 当前的历史（只增不减，分页下标保持稳定）。
    /// 打开前就已压缩掉的部分在访问到时才从会话文件读取。
    /// </summary>
    public IReadOnlyList<Message> History
    {
        get
        {
            lock (_earlier)
            {
                if (_earlierOnDisk == 0)
                {
                    return _earlier.Count == 0 ? Agent.Messages : [.. _earlier, .. Agent.Messages];
                }
                var count = _earlierOnDisk;
                var path = Store.FilePath;
                return new HistoryView(new Lazy<IReadOnlyList<Message>>(() => ReadEarlier(path, count)), count, [.. _earlier, .. Agent.Messages]);
            }
        }
    }

    /// <summary>会话文件中前 <paramref name="count"/> 条已压缩的消息；文件被改短时用不显示的占位补齐，保持下标。</summary>
    private static IReadOnlyList<Message> ReadEarlier(string path, int count)
    {
        IReadOnlyList<Message> earlier;
        try
        {
            earlier = SessionStore.Read(path).EarlierMessages;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            earlier = [];
        }
        return [.. earlier.Take(count), .. Enumerable.Repeat<Message>(new SystemMessage(""), Math.Max(0, count - earlier.Count))];
    }

    private sealed class HistoryView(Lazy<IReadOnlyList<Message>> earlier, int earlierCount, IReadOnlyList<Message> rest) : IReadOnlyList<Message>
    {
        public int Count => earlierCount + rest.Count;

        public Message this[int index] => index < earlierCount ? earlier.Value[index] : rest[index - earlierCount];

        public IEnumerator<Message> GetEnumerator()
        {
            for (var i = 0; i < Count; i++)
            {
                yield return this[i];
            }
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    /// <summary>
    /// 上下文占用（docs/design/context-usage.md）：当前上下文的 token 数、内置 Agent 的分类估算（外部运行时为 null），
    /// 以及整个会话（含已压缩掉的消息）的提示缓存累计。
    /// </summary>
    public ContextUsageReport GetContextUsage()
    {
        var messages = Agent.Messages;
        ContextBreakdown? breakdown = null;
        if (Agent is TKCode.Agent.Agent builtin && _prompt is not null)
        {
            breakdown = ContextUsage.Measure(_prompt.InitialSections, builtin.Tools, messages);
        }
        CacheTotals earlier;
        int onDisk;
        lock (_earlier)
        {
            earlier = CacheTotals.Of(_earlier);
            onDisk = _earlierOnDisk;
        }
        if (onDisk > 0)
        {
            _earlierOnDiskCache ??= CacheTotals.Of(ReadEarlier(Store.FilePath, onDisk));
            earlier += _earlierOnDiskCache.Value;
        }
        var tokens = breakdown?.Total ?? TKCode.Agent.Compaction.ContextAccounting.ContextTokens(messages);
        return new ContextUsageReport(tokens, breakdown, earlier + CacheTotals.Of(messages));
    }

    /// <summary>手动压缩（/compact）；外部运行时与没有压缩器的会话不支持。</summary>
    public Task CompactAsync(string? instructions) =>
        Agent is TKCode.Agent.Agent { Compactor: not null } builtin
            ? builtin.CompactAsync(instructions)
            : throw new InvalidOperationException("Compaction is only available for the built-in agent.");

    public static AgentSession Create(WireApiRegistry apis, ModelInfo model, string cwd, string? sessionsRoot = null,
        Func<string, string?>? getApiKey = null, BuiltinOptions? options = null)
        => CreateBuiltin(apis, model, SessionStore.Create(cwd, sessionsRoot), getApiKey, options);

    /// <summary>
    /// 在给定的会话存储上创建内置 Agent 会话：新会话、子 Agent，或尚无消息的会话从外部运行时换回内置 Agent。
    /// 换运行时的调用方负责先释放旧会话；会话 id 与文件路径保持不变。
    /// </summary>
    public static AgentSession CreateBuiltin(WireApiRegistry apis, ModelInfo model, SessionStore store, Func<string, string?>? getApiKey = null,
        BuiltinOptions? options = null)
    {
        var (agent, tools, prompt) = CreateBuiltinLoop(apis, model, store, null, getApiKey, options ?? new BuiltinOptions());
        return new AgentSession(agent, store, tools, prompt)
        {
            ContextWindow = model.ContextWindow,
            Approvals = options?.Approvals ?? new SessionApprovals(),
        };
    }

    /// <param name="snapshot">调用方已读过的快照（避免再解析一遍会话文件）。</param>
    public static AgentSession Open(WireApiRegistry apis, ModelInfo model, string filePath, Func<string, string?>? getApiKey = null,
        BuiltinOptions? options = null, SessionSnapshot? snapshot = null)
    {
        (var store, snapshot) = SessionStore.OpenSnapshot(filePath, snapshot);
        var (agent, tools, prompt) = CreateBuiltinLoop(apis, model, store, snapshot, getApiKey, options ?? new BuiltinOptions());
        agent.LoadHistory(snapshot.Messages);
        tools.Tasks.Restore(snapshot.Tasks);
        return new AgentSession(agent, store, tools, prompt)
        {
            ContextWindow = model.ContextWindow,
            Approvals = options?.Approvals ?? new SessionApprovals(),
            _earlierOnDisk = snapshot.EarlierMessages.Count,
        };
    }

    /// <summary>内置 Agent 循环：系统提示（按节，冻结开头并追加变化）、工具集、会话 id 与 Key 获取方式。</summary>
    private static (Agent Agent, ToolContext Tools, SystemPromptTracker Prompt) CreateBuiltinLoop(WireApiRegistry apis, ModelInfo model, SessionStore store,
        SessionSnapshot? snapshot, Func<string, string?>? getApiKey, BuiltinOptions options)
    {
        var workspace = options.Workspace ?? new WorkspaceRoots(store.Cwd);
        var memory = options.MemoryDirectory is { } memoryDirectory ? new ProjectMemory(memoryDirectory) : null;
        if (memory is not null)
        {
            workspace.MemoryDirectory = memory.Directory;
        }
        var toolContext = new ToolContext(workspace, store.SessionId, options.BackgroundTasks, options.Tasks)
        {
            Services = options.Services,
            ShellProfile = options.ShellProfile ?? (() => null),
            // 主会话才有计划文件与计划审批（docs/design/plan-mode.md §6）
            PlanFile = options.ToolSet == ToolSet.Full && options.Services.PlanMode is not null ? PlanFiles.PathFor(store.FilePath) : null,
            TranscriptPath = store.FilePath,
            ReadOnlyToolSet = options.ToolSet == ToolSet.ReadOnly,
            Memory = memory,
        };
        var tools = BuiltinTools.Create(toolContext, options.ToolSet);
        Func<string> shellName = () => ShellResolver.Resolve(toolContext.ShellProfile()).Name;
        // memory 节排在 project_context 之后、其他额外节之前（docs/design/memory.md §4.1）
        IReadOnlyList<PromptSection>? extra = memory is null ? options.ExtraPromptSections : [memory.Section, .. options.ExtraPromptSections ?? []];
        var prompt = snapshot is null
            ? SystemPromptTracker.Create(store, workspace, tools, model, extra, shellName)
            : SystemPromptTracker.Restore(store, snapshot, workspace, tools, model, extra, shellName);
        var agent = new Agent(apis, model, prompt.InitialPrompt, tools)
        {
            SessionId = store.SessionId,
            GetApiKey = getApiKey,
            GetCacheTtl = options.CacheTtl,
            Compactor = new Compaction.Compactor(store, toolContext, prompt),
            AutoCompact = options.AutoCompact,
            Offload = ImageAttachments.Offload,
        };
        toolContext.RequestContextRefresh = agent.RequestContextRefresh;
        toolContext.RequestHandoff = agent.RequestHandoff;
        var dynamic = options.Services.DynamicTools?.Invoke(toolContext);
        toolContext.DynamicTools = dynamic;
        var firstTurn = true;
        // 进入 Plan 时取一次计划文件是否存在，留在 Plan 期间不变（模式节不随模型写文件反复变化）
        bool? planExisted = null;
        agent.ContextUpdates = async ct =>
        {
            // 每批用户输入之前：取当前的 MCP 工具、skills 与 role，同一轮内保持不变
            var set = DynamicToolSet.Empty;
            if (dynamic is not null)
            {
                try
                {
                    set = await dynamic.RefreshAsync(firstTurn, ct);
                }
                catch (Exception) when (!ct.IsCancellationRequested)
                {
                }
            }
            firstTurn = false;
            var role = toolContext.Role ?? SessionRole.Agent;
            // 没有计划审批（没有宿主）时，Plan 退回到以回复给出计划的版本
            if (ReferenceEquals(role, SessionRole.Plan) && toolContext.PlanFile is null)
            {
                role = SessionRole.PlanWithoutApproval;
            }
            // 从其他模式切回 Agent：模式节换成“可以动手了”的说明，而不是直接删掉（docs/design/composer.md §18.4）
            if (role.Section is null && role.Name == SessionRole.AgentName && prompt.HasSection(SessionRole.SectionName))
            {
                role = SessionRole.AgentResumed;
            }
            var planFile = toolContext.PlanFile;
            bool planExists;
            if (role.Name == SessionRole.PlanName)
            {
                planExisted ??= planFile is not null && File.Exists(planFile);
                planExists = planExisted.Value;
            }
            else
            {
                planExisted = null;
                planExists = planFile is not null && File.Exists(planFile);
            }
            // 项目的文件夹与本会话追加的文件夹可能已经变化：下一轮开始前同步，cwd 节随补丁更新
            if (options.AdditionalDirectories is { } directories)
            {
                var current = directories(store);
                workspace.Sync(current.All, current.SessionOnly);
            }
            IReadOnlyList<IAgentTool> all = set.Tools.Count == 0 ? tools : [.. tools, .. set.Tools];
            // 总是经过 Allows：模式专属工具（exit_plan_mode / enter_plan_mode）在 all 下也要过滤
            agent.Tools = all.Where(role.Allows).ToArray();
            prompt.Dynamic = role.SectionFor(planFile, planExists) is { } section ? [.. set.Sections, section] : set.Sections;
            var updates = await prompt.UpdatesAsync(agent.Model, ct);
            agent.SystemPrompt = prompt.InitialPrompt;
            // 当前历史里还没有记忆索引（新会话、启用前的旧会话）时随本批注入；压缩结果自带索引（docs/design/memory.md §4.2）
            IReadOnlyList<Message> result = memory is not null && !agent.Messages.Any(ProjectMemory.IsIndexMessage)
                ? [.. updates, memory.IndexMessage()]
                : updates;
            // 用户在界面上回退了文件：随本批告诉 Agent（docs/design/message-rewind.md §5.3）
            return toolContext.RevertNotices.Take() is { } reverted ? [.. result, reverted] : result;
        };
        toolContext.ModelSupportsImages = () => agent.Model.SupportsImages;
        toolContext.CurrentModel = () => agent.Model;
        return (agent, toolContext, prompt);
    }

    /// <summary>
    /// 在同一个会话存储上换成外部运行时（尚无消息的会话从内置 Agent 换到外部运行时）。
    /// 调用方负责先释放旧会话；会话 id 与文件路径保持不变。
    /// </summary>
    public static AgentSession Rebind(SessionStore store, IExternalAgentLoop loop) =>
        new(loop, store) { ContextWindow = loop.Model.ContextWindow };

    /// <summary>用外部运行时创建新会话。<paramref name="createLoop"/> 收到会话存储（含 TKCode 会话 id）。</summary>
    public static AgentSession Create(string cwd, Func<SessionStore, IExternalAgentLoop> createLoop, string? sessionsRoot = null)
    {
        var store = SessionStore.Create(cwd, sessionsRoot);
        var loop = createLoop(store);
        return new AgentSession(loop, store) { ContextWindow = loop.Model.ContextWindow };
    }

    /// <summary>打开外部运行时的会话：<paramref name="createLoop"/> 从快照中取得原生会话 id 以便恢复。</summary>
    public static AgentSession Open(string filePath, Func<SessionStore, SessionSnapshot, IExternalAgentLoop> createLoop,
        SessionSnapshot? snapshot = null)
    {
        (var store, snapshot) = SessionStore.OpenSnapshot(filePath, snapshot);
        var loop = createLoop(store, snapshot);
        // 外部运行时的历史只用于显示：图片同样只留路径
        loop.LoadHistory(snapshot.Messages.Select(ImageAttachments.Offload).ToArray());
        return new AgentSession(loop, store) { ContextWindow = loop.Model.ContextWindow };
    }

    /// <summary>空闲时开始新一轮；运行中则按 <paramref name="whileRunning"/> 排队。</summary>
    public PromptDisposition Prompt(string text, StreamingBehavior whileRunning = StreamingBehavior.FollowUp)
        => Prompt(UserMessage.FromText(text), whileRunning);

    /// <inheritdoc cref="Prompt(string, StreamingBehavior)"/>
    public PromptDisposition Prompt(UserMessage message, StreamingBehavior whileRunning = StreamingBehavior.FollowUp)
    {
        if (!Agent.IsRunning)
        {
            try
            {
                _ = Agent.PromptAsync(message);
                return PromptDisposition.Started;
            }
            catch (InvalidOperationException)
            {
                // 与另一路输入竞争：已经开始运行，改为排队。
            }
        }
        if (whileRunning == StreamingBehavior.Steer)
        {
            Agent.Steer(message);
        }
        else
        {
            Agent.FollowUp(message);
        }
        return PromptDisposition.Queued;
    }

    /// <summary>
    /// 上一次请求出错后重试：内置 Agent、空闲，且最后一条消息是出错的助手消息时，按现有历史再请求一次。
    /// </summary>
    /// <returns>是否开始了重试。</returns>
    public bool Retry()
    {
        if (Agent is not TKCode.Agent.Agent builtin || builtin.IsRunning ||
            builtin.Messages.LastOrDefault() is not AssistantMessage { StopReason: StopReason.Error })
        {
            return false;
        }
        try
        {
            _ = builtin.ContinueAsync();
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>
    /// 投递非用户输入（后台任务通知、其他会话的消息）：空闲时开始新一轮，运行中则在当前轮结束后插入。
    /// </summary>
    public PromptDisposition Deliver(UserMessage message) => Prompt(message, StreamingBehavior.Steer);

    private void OnBackgroundTaskCompleted(BackgroundTask task, string? owner)
    {
        if (owner is not null && owner != Id)
        {
            return;
        }
        Deliver(new UserMessage([new TextContent(BackgroundTasks.FormatNotification(task))]) { Source = "task-notification" });
    }

    private void OnTasksChanged(IReadOnlyList<TaskItem> tasks) =>
        _ = Store.AppendTasksAsync(tasks, CancellationToken.None);

    public async ValueTask DisposeAsync()
    {
        if (Tools is not null)
        {
            Tools.BackgroundTasks.Completed -= OnBackgroundTaskCompleted;
            Tools.Tasks.Changed -= OnTasksChanged;
            await Tools.BackgroundTasks.StopAllAsync(Id);
        }
        Agent.Abort();
        await Agent.WaitForIdleAsync();
        if (Tools?.DynamicTools is { } dynamic)
        {
            await dynamic.DisposeAsync();
        }
        _persistence.Dispose();
        if (Agent is IExternalAgentLoop external)
        {
            await external.DisposeAsync();
        }
    }
}

public enum StreamingBehavior
{
    Steer,
    FollowUp,
}

public enum PromptDisposition
{
    Started,
    Queued,
}
