using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Avalonia.Threading;
using TKCode.Desktop.Icons;
using TKCode.Desktop.Localization;
using TKCode.Rpc;

namespace TKCode.Desktop.ViewModels;

/// <summary>
/// 一个 Host 会话在 UI 中的状态：转录、运行状态、等待首个输出时的“思考中”计时、上下文占用。
/// 会话切换时保留实例，后台会话的事件也会继续更新它，切回来时转录是完整的。
/// </summary>
public sealed partial class SessionViewModel : ObservableObject
{
    private readonly Dictionary<string, ToolCallBlockViewModel> _toolCalls = [];
    private readonly DispatcherTimer _ponderTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private DateTimeOffset _ponderStart;

    public SessionViewModel(SessionDto session)
    {
        QueuedPrompts.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasQueuedPrompts));
            OnPropertyChanged(nameof(QueueTitle));
        };
        SessionId = session.SessionId;
        FilePath = session.FilePath;
        Cwd = session.Cwd;
        ProjectId = session.ProjectId;
        SessionDirectories = session.SessionDirectories ?? [];
        ModelId = session.ModelId;
        Options = session.Options;
        Runtime = session.Runtime;
        _ponderTimer.Tick += (_, _) => UpdatePonderText();
        Items.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(IsEmpty));
            OnPropertyChanged(nameof(PickerScope));
        };
        PendingApprovals.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(CurrentApproval));
            OnPropertyChanged(nameof(HasPendingApproval));
            OnPropertyChanged(nameof(HasMoreApprovals));
            OnPropertyChanged(nameof(MoreApprovalsText));
            Changed?.Invoke();
        };
    }

    public string SessionId { get; }

    /// <summary>
    /// 只用于显示子 Agent 的转录（右侧标签，docs/design/subagent-view.md §6.3）：不汇总本轮改动（改动属于父会话），
    /// 也没有输入框、排队与审批。
    /// </summary>
    public bool IsSubagent { get; init; }

    /// <summary>
    /// 外部运行时 id（claude-code / codex / acp:*）；null 表示内置 Agent。
    /// 没有消息的会话可以在运行时之间切换（Host 替换底层循环），因此可变。
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsExternal))]
    public partial string? Runtime { get; set; }

    /// <summary>外部运行时自带的斜杠命令（运行时启动后报告；内置 Agent 为空）。</summary>
    public IReadOnlyList<RuntimeCommandDto> RuntimeCommands { get; private set; } = [];

    public bool IsExternal => Runtime is not null;

    /// <summary>模型选择器范围：没有历史时可选全部模型，有历史后只能选同一运行时（内置为 ""）。</summary>
    public string? PickerScope => IsEmpty ? null : Runtime ?? "";
    public string FilePath { get; }
    public string Cwd { get; }

    /// <summary>会话头里记录的项目（旧会话为 null，按 cwd 回退归属，docs/design/projects.md §5.3）。</summary>
    public string? ProjectId { get; }

    /// <summary>只给本会话追加的文件夹（§6.4）。</summary>
    [ObservableProperty]
    public partial IReadOnlyList<string> SessionDirectories { get; set; }

    public string ProjectName => System.IO.Path.GetFileName(Cwd.TrimEnd(System.IO.Path.DirectorySeparatorChar));
    public ObservableCollection<TranscriptItemViewModel> Items { get; } = [];

    /// <summary>等待用户决定的工具审批（按到达顺序）。</summary>
    public ObservableCollection<ApprovalRequestViewModel> PendingApprovals { get; } = [];

    public ApprovalRequestViewModel? CurrentApproval => PendingApprovals.FirstOrDefault();
    public bool HasPendingApproval => PendingApprovals.Count > 0;
    public bool HasMoreApprovals => PendingApprovals.Count > 1;
    public string MoreApprovalsText => Strings.Format(TKCodeLangResourceKind.ApprovalPendingFormat, PendingApprovals.Count - 1);

    public void AddApproval(ApprovalRequestViewModel approval)
    {
        approval.Decided += a => PendingApprovals.Remove(a);
        PendingApprovals.Add(approval);
    }

    /// <summary>MCP 服务器的 elicitation 请求（按到达顺序，一次显示一个）。</summary>
    public ObservableCollection<McpElicitationViewModel> PendingElicitations { get; } = [];

    public McpElicitationViewModel? CurrentElicitation => PendingElicitations.FirstOrDefault();
    public bool HasPendingElicitation => PendingElicitations.Count > 0;

    public void AddElicitation(McpElicitationViewModel elicitation)
    {
        elicitation.Decided += e =>
        {
            PendingElicitations.Remove(e);
            OnPropertyChanged(nameof(CurrentElicitation));
            OnPropertyChanged(nameof(HasPendingElicitation));
        };
        PendingElicitations.Add(elicitation);
        OnPropertyChanged(nameof(CurrentElicitation));
        OnPropertyChanged(nameof(HasPendingElicitation));
    }

    /// <summary>
    /// Agent 的提问（input/request，按到达顺序，一次回答一个）。待回答时问答面板替换输入框（docs/design/ask-user.md §7）。
    /// 与审批分开存放：切换权限档位只会自动处理审批，从不回答提问。
    /// </summary>
    public ObservableCollection<QuestionRequestViewModel> PendingQuestions { get; } = [];

    public QuestionRequestViewModel? CurrentQuestion => PendingQuestions.FirstOrDefault();
    public bool HasPendingQuestion => PendingQuestions.Count > 0;

    public void AddQuestion(QuestionRequestViewModel question)
    {
        question.Decided += q =>
        {
            PendingQuestions.Remove(q);
            SyncQuestions();
        };
        PendingQuestions.Add(question);
        SyncQuestions();
    }

    private void SyncQuestions()
    {
        if (CurrentQuestion is { } current)
        {
            current.MoreCount = PendingQuestions.Count - 1;
        }
        OnPropertyChanged(nameof(CurrentQuestion));
        OnPropertyChanged(nameof(HasPendingQuestion));
        OnPropertyChanged(nameof(IsWaitingForUser));
    }

    /// <summary>
    /// 计划审批（plan/review，按到达顺序，一次处理一个）。待审批时审批面板替换输入框（docs/design/plan-mode.md §14.1）。
    /// 与审批、提问分开存放：切换权限档位从不替用户批准计划。
    /// </summary>
    public ObservableCollection<PlanReviewViewModel> PendingPlanReviews { get; } = [];

    public PlanReviewViewModel? CurrentPlanReview => PendingPlanReviews.FirstOrDefault();
    public bool HasPendingPlanReview => PendingPlanReviews.Count > 0;

    public void AddPlanReview(PlanReviewViewModel review)
    {
        review.Decided += r =>
        {
            PendingPlanReviews.Remove(r);
            SyncPlanReviews();
        };
        PendingPlanReviews.Add(review);
        SyncPlanReviews();
    }

    private void SyncPlanReviews()
    {
        if (CurrentPlanReview is { } current)
        {
            current.MoreCount = PendingPlanReviews.Count - 1;
        }
        OnPropertyChanged(nameof(CurrentPlanReview));
        OnPropertyChanged(nameof(HasPendingPlanReview));
        OnPropertyChanged(nameof(IsWaitingForUser));
    }

    /// <summary>有等待用户处理的审批、表单、提问或计划审批（侧边栏橙点）。</summary>
    public bool IsWaitingForUser => HasPendingApproval || HasPendingElicitation || HasPendingQuestion || HasPendingPlanReview;

    /// <summary>会话被删除或关闭：撤回所有待处理的请求，Host 不会一直等待。</summary>
    public void CancelPendingRequests()
    {
        foreach (var approval in PendingApprovals.ToList())
        {
            approval.Cancel();
        }
        foreach (var elicitation in PendingElicitations.ToList())
        {
            elicitation.Cancel();
        }
        foreach (var question in PendingQuestions.ToList())
        {
            question.Cancel();
        }
        foreach (var review in PendingPlanReviews.ToList())
        {
            review.Cancel();
        }
    }

    /// <summary>
    /// Host 主动改变了模式或权限（optionsChanged，docs/design/plan-mode.md §13.2）：选择器随 <see cref="Options"/> 更新，不回调 Host。
    /// 参数是变化前与变化后的参数。
    /// </summary>
    public event Action<SessionOptionsDto, SessionOptionsDto>? HostOptionsChanged;
    public bool IsEmpty => Items.Count == 0;

    /// <summary>任务清单（task_* 工具的最新快照）。</summary>
    public TaskListViewModel TaskList { get; } = new();

    /// <summary>任务球：后台 shell。</summary>
    public ObservableCollection<BackgroundTaskItemViewModel> Shells { get; } = [];

    /// <summary>任务球：Agent（后台子 Agent 来自后台任务事件，前台子 Agent 来自正在执行的工具调用）。</summary>
    public ObservableCollection<BackgroundTaskItemViewModel> Agents { get; } = [];

    public int RunningTaskCount => Shells.Count(s => s.IsRunning) + Agents.Count(a => a.IsRunning);

    public bool HasRunningTasks => RunningTaskCount > 0;
    public bool HasShells => Shells.Count > 0;
    public bool HasAgents => Agents.Count > 0;

    /// <summary>任务球是否可见：有后台任务、Agent、任务清单或本会话的改动。</summary>
    public bool HasTaskOrb => Shells.Count > 0 || Agents.Count > 0 || TaskList.IsVisible || HasChanges;

    private void NotifyTasks()
    {
        OnPropertyChanged(nameof(RunningTaskCount));
        OnPropertyChanged(nameof(HasRunningTasks));
        OnPropertyChanged(nameof(HasShells));
        OnPropertyChanged(nameof(HasAgents));
        OnPropertyChanged(nameof(HasTaskOrb));
    }

    /// <summary>后台任务是快照（主机只推送运行中的和最近结束的），按 id 更新，快照里没有的后台项移除。</summary>
    private void SyncBackgroundTasks(IReadOnlyList<BackgroundTaskDto> tasks)
    {
        SyncBackground(Shells, tasks.Where(t => t.Type != "agent"), isAgent: false);
        SyncBackground(Agents, tasks.Where(t => t.Type == "agent"), isAgent: true);
        NotifyTasks();
    }

    private static void SyncBackground(ObservableCollection<BackgroundTaskItemViewModel> list, IEnumerable<BackgroundTaskDto> tasks, bool isAgent)
    {
        var snapshot = tasks.ToList();
        for (var i = list.Count - 1; i >= 0; i--)
        {
            if (list[i].IsBackground && snapshot.All(t => t.Id != list[i].Id))
            {
                list.RemoveAt(i);
            }
        }
        foreach (var task in snapshot)
        {
            var item = list.FirstOrDefault(i => i.IsBackground && i.Id == task.Id);
            if (item is null)
            {
                item = new BackgroundTaskItemViewModel(task.Id, task.Description, isAgent, isBackground: true);
                list.Add(item);
            }
            item.Update(task.Description, task.Status);
        }
    }

    /// <summary>前台子 Agent 正在执行：在任务球里显示，结束后移除。</summary>
    private void UpsertForegroundAgent(ToolCallBlockViewModel call)
    {
        var item = Agents.FirstOrDefault(a => !a.IsBackground && a.Id == call.ToolCallId);
        if (item is null)
        {
            Agents.Add(new BackgroundTaskItemViewModel(call.ToolCallId, call.Parsed?.SubagentName ?? call.ToolName, isAgent: true, isBackground: false));
        }
        NotifyTasks();
    }

    private void RemoveForegroundAgent(string toolCallId)
    {
        var item = Agents.FirstOrDefault(a => !a.IsBackground && a.Id == toolCallId);
        if (item is not null)
        {
            Agents.Remove(item);
            NotifyTasks();
        }
    }

    /// <summary>工具结果带任务清单快照时刷新输入框上方的清单。</summary>
    private void ApplyDetails(ToolCallBlockViewModel call, string? detailsJson)
    {
        if (detailsJson is null)
        {
            return;
        }
        call.DetailsJson = detailsJson;
        if (call.Parsed is { Kind: "tasks", Tasks: { } tasks })
        {
            TaskList.Update(tasks);
            NotifyTasks();
        }
        if (call.Parsed is { Kind: "subagent" } && call.IsBusy)
        {
            UpsertForegroundAgent(call);
        }
    }

    /// <summary>运行中或未结束的事件到达时更新。</summary>
    public event Action? Changed;

    [ObservableProperty]
    public partial string? Title { get; set; }

    [ObservableProperty]
    public partial string ModelId { get; set; }

    [ObservableProperty]
    public partial SessionOptionsDto Options { get; set; }

    [ObservableProperty]
    public partial bool IsRunning { get; private set; }

    /// <summary>已开始运行、但本轮还没有收到任何输出（等待模型首个 token 或工具后的下一次请求）。</summary>
    [ObservableProperty]
    public partial bool IsPondering { get; private set; }

    [ObservableProperty]
    public partial string PonderText { get; private set; } = "";

    [ObservableProperty]
    public partial DateTimeOffset LastActivity { get; set; } = DateTimeOffset.Now;

    /// <summary>最近一次助手回复的 输入 + 缓存 + 输出 tokens，用于计算上下文占用。</summary>
    [ObservableProperty]
    public partial long LastUsageTokens { get; private set; }

    /// <summary>最近一次助手回复的用量明细（上下文占用提示里显示缓存读写）；压缩后清空。</summary>
    [ObservableProperty]
    public partial UsageDto? LastUsage { get; private set; }

    public string? ModelIconKey => ModelIcons.ForModel(ModelId);

    partial void OnModelIdChanged(string value) => OnPropertyChanged(nameof(ModelIconKey));

    /// <summary>撤销文件改动的宿主调用（由会话所属的连接提供）。</summary>
    public Func<RevertFileRequest, Task<RevertFileResult>>? RevertFile { get; set; }

    /// <summary>文件改动有变化（一轮结束、载入历史）时发出，变更面板据此刷新。</summary>
    public event Action? ChangesChanged;

    /// <summary>转录中的“审查”：请求在变更面板中打开该文件。</summary>
    public event Action<string>? ReviewRequested;

    /// <summary>转录中的“打开”：请求在文件标签页中打开该文件。</summary>
    public event Action<string>? OpenRequested;

    /// <summary>点击了子 Agent 卡片：在右侧打开它的转录。</summary>
    public event Action<SessionViewModel, ToolCallBlockViewModel>? SubagentRequested;

    internal Task<RevertFileResult> RevertAsync(RevertFileRequest request) =>
        RevertFile is { } revert ? revert(request) : Task.FromResult(new RevertFileResult(Strings.Get(TKCodeLangResourceKind.ChangesRevertFailedNoHost)));

    /// <summary>整个会话的文件改动，按文件合并（最早的快照、最后的指纹）。</summary>
    public IReadOnlyList<FileChangeViewModel> FileChanges() => FileChangeSet.Group(EditsIn(Items), RevertAsync);

    /// <summary>会话的改动总计（转录上方的“更改 +X −Y”）。</summary>
    [ObservableProperty]
    public partial string ChangesStats { get; private set; } = "";

    [ObservableProperty]
    public partial bool HasChanges { get; private set; }

    private void NotifyChanges()
    {
        var files = FileChanges();
        HasChanges = files.Count > 0;
        NotifyTasks();
        ChangesStats = HasChanges ? $"+{files.Sum(f => f.Added)} −{files.Sum(f => f.Removed)}" : "";
        ChangesChanged?.Invoke();
    }

    /// <summary>载入最近的历史（<paramref name="start"/> 为其在完整历史中的位置，大于 0 表示还有更早的消息）。</summary>
    public void LoadHistory(IReadOnlyList<HistoryMessageDto> history, int start = 0)
    {
        Items.Clear();
        _toolCalls.Clear();
        foreach (var item in BuildItems(history, latest: true))
        {
            Items.Add(item);
        }
        HistoryStart = start;
        NotifyChanges();
    }

    /// <summary>在顶部插入更早的一页历史。</summary>
    public void PrependHistory(IReadOnlyList<HistoryMessageDto> history, int start)
    {
        var items = BuildItems(history, latest: false);
        for (var i = 0; i < items.Count; i++)
        {
            Items.Insert(i, items[i]);
        }
        HistoryStart = start;
        NotifyChanges();
    }

    private int _historyKey;

    /// <summary>已加载历史中第一条消息在完整历史中的位置；大于 0 表示还有更早的消息。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasEarlierHistory))]
    public partial int HistoryStart { get; private set; }

    public bool HasEarlierHistory => HistoryStart > 0;

    // ---- 运行中发送的消息队列 ----

    private string? _lastStopReason;

    /// <summary>Agent 运行中发送的消息：本轮正常结束后按顺序发出。</summary>
    public System.Collections.ObjectModel.ObservableCollection<QueuedPromptViewModel> QueuedPrompts { get; } = [];

    public bool HasQueuedPrompts => QueuedPrompts.Count > 0;

    public string QueueTitle => Strings.Format(TKCodeLangResourceKind.QueueTitleFormat, QueuedPrompts.Count);

    /// <summary>上一轮被停止或出错，队列暂停（需要用户点“继续”）。</summary>
    [ObservableProperty]
    public partial bool IsQueuePaused { get; set; }

    /// <summary>一轮结束；参数表示是否正常完成（未被停止、未出错）。</summary>
    public event Action<bool>? RunEnded;

    /// <summary>会话顶部的“更早的消息”条目（有更早的历史时显示）。</summary>
    public EarlierHistoryViewModel EarlierHistory => _earlier ??= new EarlierHistoryViewModel(this);

    private EarlierHistoryViewModel? _earlier;

    /// <summary>正在加载更早的历史（顶部显示加载提示）。</summary>
    [ObservableProperty]
    public partial bool IsLoadingEarlier { get; set; }

    /// <summary>标题用输入框里的样子（command 显示为 /名称，不用展开后的正文）。</summary>
    private static string? TitleText(string? text, IReadOnlyList<PromptSegmentDto>? segments) =>
        segments is { Count: > 0 } ? Controls.Composer.ComposerDocument.PlainText(Controls.Composer.ComposerSegments.FromDto(segments)) : text;

    /// <summary>由历史消息构建会话条目（同一轮的多条助手消息合并为一条）。</summary>
    private List<TranscriptItemViewModel> BuildItems(IReadOnlyList<HistoryMessageDto> history, bool latest)
    {
        var items = new List<TranscriptItemViewModel>();
        var turnStart = 0;
        AssistantMessageItemViewModel? current = null;
        foreach (var message in history)
        {
            switch (message.Role)
            {
                case "user":
                    current = null;
                    var text = string.Concat(message.Blocks.OfType<HistoryText>().Select(t => t.Text));
                    if (message.Source is CompactSummarySource or HandoffSource)
                    {
                        items.Add(message.Source is HandoffSource ? CompactionItemViewModel.FromHandoff(text) : CompactionItemViewModel.FromSummary(text));
                        if (latest)
                        {
                            // 压缩前的用量已不代表当前上下文；等压缩后的第一次回复再显示
                            LastUsage = null;
                            LastUsageTokens = 0;
                        }
                        break;
                    }
                    if (message.Source is CompactContextSource)
                    {
                        break;
                    }
                    if (message.Source is { } source)
                    {
                        items.Add(new NotificationItemViewModel(source, text));
                        break;
                    }
                    if (!IsSubagent)
                    {
                        AppendTurnChanges(items, turnStart);
                    }
                    if (latest)
                    {
                        Title ??= ImagePrompt.Title(TitleText(text, message.Segments));
                    }
                    var images = message.Blocks.OfType<HistoryImage>().Select(i => i.Path).ToArray();
                    items.Add(new UserMessageItemViewModel(text, images, message.Segments));
                    turnStart = items.Count;
                    break;
                case "assistant":
                    // 同一轮的多条助手消息（工具调用前后）合并显示为一条。
                    if (current is null)
                    {
                        current = new AssistantMessageItemViewModel($"history-{_historyKey++}", message.Timestamp);
                        items.Add(current);
                    }
                    foreach (var block in message.Blocks)
                    {
                        var key = current.Blocks.Count;
                        switch (block)
                        {
                            case HistoryText t:
                                current.GetOrAdd(key, () => TextBlockViewModel.FromHistory(t.Text));
                                break;
                            case HistoryThinking t when t.Text.Length > 0:
                                current.GetOrAdd(key, () => new ThinkingBlockViewModel(t.DurationMs) { Text = t.Text });
                                break;
                            case HistoryToolCall c:
                                _toolCalls[c.ToolCallId] = current.GetOrAdd(key, () => BindTool(new ToolCallBlockViewModel(c.ToolCallId, c.ToolName)
                                {
                                    Arguments = c.ArgumentsJson,
                                    Status = ToolCallStatus.Running,
                                }));
                                break;
                        }
                    }
                    if (message.StopReason is "Error" or "Aborted")
                    {
                        current.ErrorMessage = ErrorText(message.StopReason, message.ErrorMessage);
                    }
                    if (latest && message.Usage is { } usage && HasTokens(usage))
                    {
                        LastUsage = usage;
                        LastUsageTokens = usage.Input + usage.CacheRead + usage.CacheWrite + usage.Output;
                    }
                    current.Complete(message.Timestamp);
                    break;
                case "toolResult":
                    foreach (var result in message.Blocks.OfType<HistoryToolResult>())
                    {
                        if (_toolCalls.TryGetValue(result.ToolCallId, out var call))
                        {
                            call.Status = result.IsError ? ToolCallStatus.Failed : ToolCallStatus.Succeeded;
                            call.Result = result.Text.TrimEnd();
                            call.IsStreaming = false;
                            ApplyDetails(call, result.DetailsJson);
                        }
                    }
                    break;
            }
        }
        if (!IsSubagent)
        {
            AppendTurnChanges(items, turnStart);
        }
        if (latest && history.Count > 0)
        {
            LastActivity = history[^1].Timestamp.ToLocalTime();
        }
        return items;
    }

    /// <summary>工具块交给会话：read 的“打开文件”请求在这里把相对路径解析到工作目录下。</summary>
    private ToolCallBlockViewModel BindTool(ToolCallBlockViewModel call)
    {
        call.OpenFileRequested = path => OpenRequested?.Invoke(System.IO.Path.IsPathRooted(path) ? path : System.IO.Path.Combine(Cwd, path));
        call.OpenSubagentRequested = c => SubagentRequested?.Invoke(this, c);
        return call;
    }

    /// <summary>从 <paramref name="from"/> 起的这一轮的改动汇总成一张卡片；同一位置的旧卡片先移除。</summary>
    private void AppendTurnChanges(IList<TranscriptItemViewModel> items, int from)
    {
        if (items.Count > 0 && items[^1] is TurnChangesItemViewModel)
        {
            items.RemoveAt(items.Count - 1);
        }
        var edits = EditsIn(items.Skip(from)).ToArray();
        if (edits.Length == 0)
        {
            return;
        }
        items.Add(new TurnChangesItemViewModel(FileChangeSet.Group(edits, RevertAsync),
            path => ReviewRequested?.Invoke(path),
            path => OpenRequested?.Invoke(path)));
    }

    /// <summary>最后一条用户消息之后的位置（轮次的起点）。</summary>
    private static int TurnStart(IList<TranscriptItemViewModel> items)
    {
        for (var i = items.Count - 1; i >= 0; i--)
        {
            if (items[i] is UserMessageItemViewModel)
            {
                return i + 1;
            }
        }
        return 0;
    }

    /// <summary>工具调用中的 diff 结果（按出现顺序）。</summary>
    private static IEnumerable<ParsedToolDetails> EditsIn(IEnumerable<TranscriptItemViewModel> items) =>
        items.OfType<AssistantMessageItemViewModel>()
            .SelectMany(m => m.Blocks.OfType<ToolCallBlockViewModel>())
            .Select(t => t.Parsed)
            .OfType<ParsedToolDetails>()
            .Where(p => p.Kind == "diff" && p.Path is not null);

    public void SetRunning(bool running)
    {
        IsRunning = running;
        SetPondering(running);
    }

    public void Apply(SessionEvent e)
    {
        LastActivity = DateTimeOffset.Now;
        switch (e)
        {
            case SessionTitleChanged title:
                Title = title.Title;
                break;
            case CommandsChanged c:
                RuntimeCommands = c.Commands;
                break;
            case SessionOptionsChanged c:
                var previous = Options;
                Options = c.Options;
                HostOptionsChanged?.Invoke(previous, c.Options);
                break;
            case AgentStarted:
                SetRunning(true);
                _lastStopReason = null;
                IsQueuePaused = false;
                break;
            case AgentEnded:
                SetRunning(false);
                if (!IsSubagent)
                {
                    AppendTurnChanges(Items, TurnStart(Items));
                }
                NotifyChanges();
                // 本轮被停止或出错：排队的消息暂停，不自动发出
                var completed = _lastStopReason is not ("Error" or "Aborted");
                IsQueuePaused = !completed && QueuedPrompts.Count > 0;
                RunEnded?.Invoke(completed);
                break;
            case CompactionStarted c:
                Items.Add(CompactionItemViewModel.Running(c.PreTokens, handoff: c.Trigger == PlanHandoffTrigger));
                break;
            case CompactionEnded c:
                if (Items.LastOrDefault(i => i is CompactionItemViewModel { IsRunning: true }) is CompactionItemViewModel running)
                {
                    running.Complete(c.PreTokens, c.PostTokens, c.Error);
                }
                if (c.Error is null && c.PostTokens is { } post)
                {
                    LastUsage = null;
                    LastUsageTokens = post;
                }
                break;
            case BackgroundTasksChanged tasks:
                SyncBackgroundTasks(tasks.Tasks);
                break;
            case CompactionSuspended c:
                Items.Add(CompactionItemViewModel.SuspendedNotice(c.Reason));
                break;
            case MessageStarted { Role: "user", Source: CompactSummarySource or HandoffSource } m:
                if (Items.LastOrDefault(i => i is CompactionItemViewModel) is CompactionItemViewModel compacted)
                {
                    compacted.SetSummary(m.Text ?? "");
                }
                else if (m.Source is HandoffSource)
                {
                    Items.Add(CompactionItemViewModel.FromHandoff(m.Text ?? ""));
                }
                break;
            case MessageStarted { Role: "user", Source: CompactContextSource }:
                break;
            case MessageStarted { Role: "user", Source: { } source } m:
                Items.Add(new NotificationItemViewModel(source, m.Text ?? ""));
                break;
            case MessageStarted { Role: "user" } m:
                Title ??= ImagePrompt.Title(TitleText(m.Text, m.Segments));
                Items.Add(new UserMessageItemViewModel(m.Text ?? "", m.Images, m.Segments));
                break;
            case MessageStarted { Role: "assistant" } m:
                // 同一轮内工具调用后的后续回复接在同一条消息里显示。
                if (Items.LastOrDefault() is not AssistantMessageItemViewModel)
                {
                    Items.Add(new AssistantMessageItemViewModel(m.MessageId, DateTimeOffset.Now));
                }
                _currentMessageId = m.MessageId;
                _blockOffset = Current?.Blocks.Count ?? 0;
                if (Current is { } existing)
                {
                    existing.IsStreaming = true;
                }
                break;
            case TextDelta d when Current is { } msg:
                SetPondering(false);
                msg.GetOrAdd(_blockOffset + d.ContentIndex, () => new TextBlockViewModel(live: true)).Append(d.Delta);
                break;
            case TextEnded end when end.MessageId == _currentMessageId && Current is { } msg:
                msg.CompleteBlock(_blockOffset + end.ContentIndex);
                break;
            case ThinkingDelta d when Current is { } msg:
                SetPondering(false);
                msg.GetOrAdd(_blockOffset + d.ContentIndex, () => new ThinkingBlockViewModel()).Append(d.Delta);
                break;
            case ThinkingEnded end when end.MessageId == _currentMessageId && Current is { } msg:
                msg.CompleteBlock(_blockOffset + end.ContentIndex);
                break;
            case ToolCallStarted s when Current is { } msg:
                SetPondering(false);
                _toolCalls[s.ToolCallId] = msg.GetOrAdd(_blockOffset + s.ContentIndex, () => BindTool(new ToolCallBlockViewModel(s.ToolCallId, s.ToolName)));
                break;
            case ToolCallDelta d when Current is { } msg:
                msg.GetOrAdd(_blockOffset + d.ContentIndex, () => BindTool(new ToolCallBlockViewModel("", "tool"))).AppendArguments(d.JsonFragment);
                break;
            case ToolCallEnded e2 when _toolCalls.TryGetValue(e2.ToolCallId, out var call):
                call.Arguments = e2.ArgumentsJson;
                call.IsStreaming = false;
                break;
            case MessageEnded { Role: "assistant" } end when Current is { } msg:
                _lastStopReason = end.StopReason;
                msg.Complete();
                if (end.StopReason is "Error" or "Aborted")
                {
                    msg.ErrorMessage = ErrorText(end.StopReason, end.ErrorMessage);
                }
                if (end.Usage is { } usage && HasTokens(usage))
                {
                    LastUsage = usage;
                    LastUsageTokens = usage.Input + usage.CacheRead + usage.CacheWrite + usage.Output;
                }
                break;
            case ToolExecutionStarted s when _toolCalls.TryGetValue(s.ToolCallId, out var call):
                call.Status = ToolCallStatus.Running;
                break;
            case ToolExecutionUpdated update when _toolCalls.TryGetValue(update.ToolCallId, out var call):
                // 命令的实时输出、子 Agent 的当前动作
                if (update.Text.Length > 0)
                {
                    call.Result = update.Text.TrimEnd();
                }
                ApplyDetails(call, update.DetailsJson);
                break;
            case ToolExecutionEnded end when _toolCalls.TryGetValue(end.ToolCallId, out var call):
                call.Status = end.IsError ? ToolCallStatus.Failed : ToolCallStatus.Succeeded;
                call.Result = end.ResultText.TrimEnd();
                ApplyDetails(call, end.DetailsJson);
                RemoveForegroundAgent(call.ToolCallId);
                call.Duration = end.DurationMs < 1000 ? $"{end.DurationMs:0} ms" : $"{end.DurationMs / 1000:0.0} s";
                // 工具执行完后到下一次模型输出之间同样处于“思考中”。
                SetPondering(IsRunning);
                break;
        }
        Changed?.Invoke();
    }

    public void RefreshLanguage()
    {
        UpdatePonderText();
        foreach (var item in Items.OfType<AssistantMessageItemViewModel>())
        {
            item.RefreshLanguage();
        }
        foreach (var item in Items.OfType<NotificationItemViewModel>())
        {
            item.RefreshLanguage();
        }
        foreach (var item in Items.OfType<CompactionItemViewModel>())
        {
            item.RefreshLanguage();
        }
        foreach (var item in Items.OfType<TurnChangesItemViewModel>())
        {
            item.RefreshLanguage();
        }
        foreach (var question in PendingQuestions)
        {
            question.RefreshLanguage();
        }
        foreach (var review in PendingPlanReviews)
        {
            review.RefreshLanguage();
        }
        TaskList.RefreshLanguage();
    }

    private string? _currentMessageId;
    private int _blockOffset;

    /// <summary>压缩后的摘要消息与重新附加的上下文（Host 的 CompactionPrompts.SummarySource / ContextSource）。</summary>
    private const string CompactSummarySource = "compact-summary";
    private const string CompactContextSource = "compact-context";

    /// <summary>
    /// 批准计划并清空上下文后的交接消息（CompactionPrompts.HandoffSource）与对应的压缩触发类型（docs/design/plan-mode.md §11）：
    /// 显示为“已清空上下文，按计划执行”的分隔线。
    /// </summary>
    private const string HandoffSource = "plan-handoff";
    private const string PlanHandoffTrigger = "planHandoff";

    private AssistantMessageItemViewModel? Current =>
        _currentMessageId is null ? null : Items.LastOrDefault() as AssistantMessageItemViewModel;

    private void SetPondering(bool pondering)
    {
        if (IsPondering == pondering)
        {
            return;
        }
        IsPondering = pondering;
        if (pondering)
        {
            _ponderStart = DateTimeOffset.Now;
            PonderVerb = PickPonderVerb();
            UpdatePonderText();
            _ponderTimer.Start();
        }
        else
        {
            _ponderTimer.Stop();
        }
    }

    /// <summary>等待输出时的俏皮动词（“琢磨中…”），每次进入等待随机换一个。</summary>
    [ObservableProperty]
    public partial string PonderVerb { get; private set; } = "";

    private static string PickPonderVerb()
    {
        var verbs = Strings.Get(TKCodeLangResourceKind.PonderVerbs).Split('|', StringSplitOptions.RemoveEmptyEntries);
        return verbs.Length == 0 ? "…" : verbs[Random.Shared.Next(verbs.Length)];
    }

    private void UpdatePonderText()
    {
        var seconds = Math.Max(1, (int)(DateTimeOffset.Now - _ponderStart).TotalSeconds);
        PonderText = $"{seconds}s";
    }

    private static string ErrorText(string stopReason, string? message) =>
        stopReason == "Aborted" ? Strings.Get(TKCodeLangResourceKind.Aborted) : message ?? Strings.Get(TKCodeLangResourceKind.RequestFailed);

    /// <summary>
    /// 消息带了用量。外部运行时只在一轮的最后一条助手消息上给整轮用量，中间的消息是全 0：
    /// 不能拿它覆盖上一次的用量，否则一轮里占用环（和它的悬停卡片）会反复消失又出现。
    /// </summary>
    private static bool HasTokens(UsageDto usage) => usage.Input + usage.CacheRead + usage.CacheWrite + usage.Output > 0;
}
