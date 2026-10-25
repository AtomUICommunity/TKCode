using System.Diagnostics;
using Avalonia.Threading;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using TKCode.Config;
using TKCode.Desktop.Controls.Composer;
using TKCode.Desktop.Icons;
using TKCode.Desktop.Localization;
using TKCode.Desktop.Services;
using TKCode.Desktop.ViewModels.Completion;
using TKCode.Rpc;

namespace TKCode.Desktop.ViewModels;

/// <summary>侧栏会话列表中的一行。会话在 UI 中打开后关联 <see cref="Live"/>，状态随事件更新。</summary>
public sealed partial class SessionItemViewModel : ObservableObject
{
    /// <param name="project">会话所属的项目（docs/design/projects.md §5.3）；不属于任何项目时为 null。</param>
    public SessionItemViewModel(string sessionId, string filePath, string cwd, Project? project = null)
    {
        SessionId = sessionId;
        FilePath = filePath;
        Cwd = cwd;
        SetProject(project);
    }

    public string SessionId { get; }
    public string FilePath { get; }
    public string Cwd { get; }

    /// <summary>所属项目的 id；不属于任何项目时为 null。</summary>
    public string? ProjectId { get; private set; }

    /// <summary>项目名；不属于任何项目时为 cwd 的文件夹名（命令面板的副标题）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProjectInitial))]
    public partial string ProjectName { get; private set; } = "";

    /// <summary>侧栏的项目徽标：只有属于某个项目的会话才显示。</summary>
    [ObservableProperty]
    public partial bool HasProject { get; private set; }

    public string ProjectInitial => ProjectName.Length > 0 ? ProjectName[..1].ToUpperInvariant() : "?";

    /// <summary>归属或项目名变化（重命名、移除项目）后更新。</summary>
    public void SetProject(Project? project)
    {
        ProjectId = project?.Id;
        HasProject = project is not null;
        ProjectName = project?.Name ?? System.IO.Path.GetFileName(Cwd.TrimEnd(System.IO.Path.DirectorySeparatorChar));
    }

    [ObservableProperty]
    public partial string? Title { get; set; }

    [ObservableProperty]
    public partial string? ModelId { get; set; }

    [ObservableProperty]
    public partial DateTimeOffset LastActivity { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowIdleDot), nameof(ShowSpinner))]
    public partial bool IsRunning { get; set; }

    /// <summary>有等待批准的操作：侧栏显示琥珀色圆点。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowIdleDot), nameof(ShowSpinner))]
    public partial bool IsWaitingApproval { get; set; }

    public bool ShowSpinner => IsRunning && !IsWaitingApproval;
    public bool ShowIdleDot => !IsRunning && !IsWaitingApproval;

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    [ObservableProperty]
    public partial SessionViewModel? Live { get; set; }

    public string DisplayTitle => CustomTitle ?? (string.IsNullOrWhiteSpace(Title) ? Strings.Get(TKCodeLangResourceKind.NewSession) : Title);

    /// <summary>用户重命名的标题（优先于第一条用户消息）。</summary>
    [ObservableProperty]
    public partial string? CustomTitle { get; set; }

    partial void OnCustomTitleChanged(string? value) => OnPropertyChanged(nameof(DisplayTitle));

    [ObservableProperty]
    public partial bool IsPinned { get; set; }

    /// <summary>外部运行时的原生会话 id（复制会话 ID 时一并复制）。</summary>
    public string? NativeSessionId { get; set; }

    /// <summary>右键菜单与行内重命名的执行者（由 <see cref="ConversationViewModel"/> 设置）。</summary>
    internal ConversationViewModel? Owner { get; set; }

    /// <summary>只有内置 Agent 的会话能在新会话中继续（外部 Agent 的上下文在 CLI 里）。</summary>
    public bool CanFork => Runtime is null;

    public string PinMenuText => Strings.Get(IsPinned ? TKCodeLangResourceKind.SessionUnpin : TKCodeLangResourceKind.SessionPin);

    public string RevealMenuText => Strings.Get(OperatingSystem.IsWindows() ? TKCodeLangResourceKind.SessionRevealExplorer
        : OperatingSystem.IsMacOS() ? TKCodeLangResourceKind.SessionRevealFinder : TKCodeLangResourceKind.SessionRevealFolder);

    partial void OnIsPinnedChanged(bool value) => OnPropertyChanged(nameof(PinMenuText));

    // ---- 行内重命名 ----

    [ObservableProperty]
    public partial bool IsRenaming { get; private set; }

    [ObservableProperty]
    public partial string EditTitle { get; set; } = "";

    [RelayCommand]
    private void BeginRename()
    {
        EditTitle = DisplayTitle;
        IsRenaming = true;
    }

    [RelayCommand]
    private void CancelRename() => IsRenaming = false;

    [RelayCommand]
    private async Task CommitRenameAsync()
    {
        if (!IsRenaming)
        {
            return;
        }
        IsRenaming = false;
        var title = EditTitle.Trim();
        if (title != DisplayTitle && Owner is not null)
        {
            await Owner.RenameSessionAsync(this, title);
        }
    }

    [RelayCommand]
    private Task TogglePinAsync() => Owner?.SetPinnedAsync(this, !IsPinned) ?? Task.CompletedTask;

    [RelayCommand]
    private Task DeleteAsync() => Owner?.DeleteSessionAsync(this) ?? Task.CompletedTask;

    [RelayCommand]
    private Task ForkAsync() => Owner?.ForkSessionAsync(this) ?? Task.CompletedTask;

    [RelayCommand]
    private Task CopyIdAsync() => Owner?.CopySessionIdAsync(this) ?? Task.CompletedTask;

    [RelayCommand]
    private void Reveal() => Owner?.RevealSessionFile(this);
    public string? ModelIconKey => ModelId is null ? null : ModelIcons.ForModel(ModelId);

    /// <summary>外部运行时会话：侧栏显示运行时小图标。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RuntimeIconKey), nameof(HasRuntime), nameof(RuntimeTip))]
    public partial string? Runtime { get; set; }

    public bool HasRuntime => Runtime is not null;

    partial void OnRuntimeChanged(string? value) => OnPropertyChanged(nameof(CanFork));
    public string? RuntimeIconKey => Runtime is null ? null : ModelIcons.ForProvider(Runtime);
    public string? RuntimeTip => Runtime is null ? null : Strings.Format(TKCodeLangResourceKind.RuntimeBadgeTip, Runtime);

    /// <summary>右侧的相对时间：刚刚 / 5 分钟 / 3 小时 / 2 天。</summary>
    public string RelativeTime
    {
        get
        {
            var age = DateTimeOffset.Now - LastActivity;
            return age.TotalMinutes < 1 ? Strings.Get(TKCodeLangResourceKind.TimeNow)
                : age.TotalHours < 1 ? Strings.Format(TKCodeLangResourceKind.MinutesAgoFormat, (int)age.TotalMinutes)
                : age.TotalDays < 1 ? Strings.Format(TKCodeLangResourceKind.HoursAgoFormat, (int)age.TotalHours)
                : Strings.Format(TKCodeLangResourceKind.DaysAgoFormat, (int)age.TotalDays);
        }
    }

    partial void OnTitleChanged(string? value) => OnPropertyChanged(nameof(DisplayTitle));
    partial void OnModelIdChanged(string? value) => OnPropertyChanged(nameof(ModelIconKey));
    partial void OnLastActivityChanged(DateTimeOffset value) => OnPropertyChanged(nameof(RelativeTime));

    public void Refresh()
    {
        OnPropertyChanged(nameof(DisplayTitle));
        OnPropertyChanged(nameof(RelativeTime));
        OnPropertyChanged(nameof(PinMenuText));
        OnPropertyChanged(nameof(RevealMenuText));
    }

    /// <summary>从打开的会话同步标题、模型、运行状态与最近活动时间。</summary>
    public void SyncFrom(SessionViewModel live)
    {
        Title = live.Title ?? Title;
        ModelId = live.ModelId;
        Runtime = live.Runtime;
        IsWaitingApproval = live.IsWaitingForUser;
        IsRunning = live.IsRunning;
        LastActivity = live.LastActivity;
    }
}

/// <summary>
/// 主区域：当前会话（或尚未创建会话的空状态）、Composer、模型选择器、项目 / 分支，以及全部会话的列表。
/// 每个已打开的会话对应一个 <see cref="SessionViewModel"/>；Host 事件按 SessionId 分发，后台会话也保持最新。
/// </summary>
public sealed partial class ConversationViewModel : ObservableObject
{
    private readonly AgentHostConnection _host;
    private readonly StateService _settings;
    private readonly ProjectService _projects;
    private readonly UserSettingsService _userSettings;
    private readonly DraftStore _drafts;
    private readonly ILogger<ConversationViewModel> _logger;
    private readonly Dictionary<string, SessionViewModel> _open = [];
    private readonly HashSet<string> _manualTitleEdits = new(StringComparer.Ordinal);
    /// <summary>导航历史：会话 ID，null 表示新对话（空状态）。</summary>
    private readonly List<string?> _history = [null];
    private int _historyIndex;
    private bool _navigating;
    private string? _defaultModelRef;

    public ConversationViewModel(AgentHostConnection host, StateService settings, UserSettingsService userSettings, DraftStore drafts,
        ProjectService projects, ILogger<ConversationViewModel> logger)
    {
        _host = host;
        _projects = projects;
        _userSettings = userSettings;
        _drafts = drafts;
        _draftSaveTimer = new DispatcherTimer { Interval = DraftSaveDelay };
        _draftSaveTimer.Tick += (_, _) =>
        {
            _draftSaveTimer.Stop();
            SaveCurrentDraft();
        };
        _defaultModelRef = userSettings.Current.Model;
        _host.ModelsChanged += OnModelsChanged;
        _host.ApprovalHandler = HandleApprovalAsync;
        _host.ElicitationHandler = HandleElicitationAsync;
        _host.UserInputHandler = HandleUserInputAsync;
        _host.PlanReviewHandler = HandlePlanReviewAsync;
        Mcp = new McpComposerViewModel(host, () => CurrentCwd, logger);
        Mcp.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(McpComposerViewModel.HasPrompt))
            {
                SendCommand.NotifyCanExecuteChanged();
                if (!Mcp.IsRestoring)
                {
                    MarkDraftEdited();
                }
            }
        };
        Mcp.ArgumentEdited += MarkDraftEdited;
        Files = new FileCompletionProvider(host, () => CurrentCwd, settings, () => CurrentAdditionalRoots);
        PendingDirectories.CollectionChanged += (_, _) => WorkspaceRootsChanged?.Invoke();
        Assets = new AssetCatalogCache(host, () => CurrentCwd, logger);
        var builtins = new BuiltinCommandProvider([
            new("new", TKCodeLangResourceKind.BuiltinNewDescription, LucideKind.SquarePen, NewChat),
            new("model", TKCodeLangResourceKind.BuiltinModelDescription, LucideKind.Box, () => ModelPickerRequested?.Invoke()),
            new("fork", TKCodeLangResourceKind.BuiltinForkDescription, LucideKind.GitBranch, () => _ = ForkActiveAsync(), () => ActiveItem is not null),
            new("role", TKCodeLangResourceKind.BuiltinRoleDescription, LucideKind.Layers, () => RolePickerRequested?.Invoke()),
            // 提示词型：Run 为 null，选中后插入 command tag，正文由 Host 展开（composer.md §9.3）
            new("init", TKCodeLangResourceKind.BuiltinInitDescription, LucideKind.FileText, null),
            new("commit", TKCodeLangResourceKind.BuiltinCommitDescription, LucideKind.GitCommit, null),
            // 外部运行时有自己的 /compact（由运行时命令列出并原样发送）
            new("compact", TKCodeLangResourceKind.BuiltinCompactDescription, LucideKind.Minimize2, () => _ = CompactActiveAsync(null),
                () => Active is { Runtime: null }),
            // 只给本会话追加文件夹（docs/design/projects.md §10.4）：选中后打开文件夹选择框；也可以输入 /add-dir 路径
            new("add-dir", TKCodeLangResourceKind.AddDirCommandDescription, LucideKind.FolderPlus, () => AddDirectoryRequested?.Invoke()),
        ]);
        RolePicker.RoleChosen += role => _ = ApplyRoleAsync(role);
        RolePicker.Show(userSettings.Current.DefaultRole);
        Assets.Changed += () => _ = LoadRolesAsync();
        Completion = new CompletionViewModel(Composer,
            [Files, new RoleCompletionProvider(RolePicker), builtins, new CommandCompletionProvider(Assets), new SkillCompletionProvider(Assets),
                new RuntimeCommandProvider(() => (Active?.RuntimeCommands ?? [], Active?.Runtime is null ? null : Picker.Selected?.Model.ProviderName)),
                new McpResourceProvider(Mcp), new McpPromptProvider(Mcp)], logger);
        Composer.Changed += _ =>
        {
            OnPropertyChanged(nameof(ComposerText));
            SendCommand.NotifyCanExecuteChanged();
            MarkDraftEdited();
        };
        PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(Active) or nameof(Workspace))
            {
                _ = Mcp.RefreshAttentionAsync();
                _ = LoadRolesAsync();
            }
        };
        _settings = settings;
        _logger = logger;
        projects.Changed += OnProjectsChanged;
        OpenPlanFile = path => ShellOpen.Open(path, logger);
        _host.SessionEvent += OnSessionEvent;
        _host.Disconnected += reason =>
        {
            foreach (var session in _open.Values)
            {
                session.SetRunning(false);
                session.CancelPendingRequests();
            }
            _open.Clear();
            foreach (var item in Sessions)
            {
                item.IsRunning = false;
                item.Live = null;
            }
            Active = null;
            StatusMessage = Strings.Format(TKCodeLangResourceKind.StatusHostDisconnectedFormat, reason);
        };
        BranchPicker = new BranchPickerViewModel(SwitchBranchAsync, IsWorkspaceBusy, RefreshBranch);
        // 切换会话或项目、文件夹变化：分支选择器的仓库随之更新
        WorkspaceRootsChanged += RefreshBranch;
        Picker = new ModelPickerViewModel(settings);
        ContextUsage = new ContextUsageViewModel(async (sessionId, ct) => await (await _host.GetServerAsync()).GetContextUsageAsync(sessionId, ct), logger);
        userSettings.Changed += () => ApplyDefaultModel();
        Picker.SelectionChanged += () =>
        {
            RolePicker.AcpAvailable = Picker.Selected?.Model.SupportsSubagents != false;
            _ = SyncSessionOptionsAsync();
            OnPropertyChanged(nameof(ShowImagesUnsupported));
        };
        Attachments.CollectionChanged += (_, _) =>
        {
            SendCommand.NotifyCanExecuteChanged();
            OnPropertyChanged(nameof(HasAttachments));
            OnPropertyChanged(nameof(ShowImagesUnsupported));
            MarkDraftEdited();
        };
        Permission.ModeChosen += mode => _ = ApplyPermissionAsync(mode);
        Permission.Show(RememberedPermission());
        Strings.LanguageChanged += (_, _) => RefreshLanguage();
        // 先让窗口完成首帧布局和渲染，再启动 Host、加载模型与会话。
        Dispatcher.UIThread.Post(() => _ = InitializeAsync(), DispatcherPriority.Background);
    }

    public ModelPickerViewModel Picker { get; }

    /// <summary>权限选择器（询问 / 自动放行 / 完全授权）：显示当前会话的档位；空状态显示项目记住的档位。</summary>
    public PermissionPickerViewModel Permission { get; } = new();

    /// <summary>全部会话（按最近活动倒序）。侧栏按项目筛选后显示。</summary>
    public ObservableCollection<SessionItemViewModel> Sessions { get; } = [];

    public BranchPickerViewModel BranchPicker { get; }

    /// <summary>各仓库的分支监视（多文件夹项目每个仓库一个）。</summary>
    private List<(string Path, GitBranchWatcher Watcher)> _branchWatchers = [];

    public string DeviceName => DeviceInfo.Name;

    /// <summary>当前会话；null 表示空状态（下一次发送时创建会话）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty), nameof(HeaderTitle), nameof(HeaderSubtitle), nameof(HeaderIconKey), nameof(IsAnsweringQuestion),
        nameof(IsReviewingPlan), nameof(ShowPlanReview), nameof(ShowDockedComposer))]
    public partial SessionViewModel? Active { get; private set; }

    public bool IsEmpty => Active is null || Active.IsEmpty;

    /// <summary>当前会话有待回答的提问：问答面板替换输入框，用户必须先回答或拒绝回答（docs/design/ask-user.md §9）。</summary>
    public bool IsAnsweringQuestion => Active?.HasPendingQuestion == true;

    /// <summary>当前会话有待批准的计划：审批面板替换输入框，用户必须先处理（docs/design/plan-mode.md §14.1）。</summary>
    public bool IsReviewingPlan => Active?.HasPendingPlanReview == true;

    /// <summary>审批面板可见（同时有提问时先显示问答面板）。</summary>
    public bool ShowPlanReview => IsReviewingPlan && !IsAnsweringQuestion;

    /// <summary>停靠的输入框可见：没有被问答面板或审批面板替换。草稿保留在 <see cref="Composer"/> 里。</summary>
    public bool ShowDockedComposer => !IsAnsweringQuestion && !IsReviewingPlan;

    /// <summary>用系统默认程序打开计划文件（审批面板的“在编辑器中打开”；测试中替换）。</summary>
    public Action<string> OpenPlanFile { get; set; }

    partial void OnActiveChanged(SessionViewModel? value)
    {
        SendCommand.NotifyCanExecuteChanged();
        Permission.Show(value is null ? RememberedPermission() : value.Options.PermissionMode);
        RolePicker.AcpAvailable = Picker.Selected?.Model.SupportsSubagents != false;
        // 空状态沿用当前的 role（新会话以它开始，与 Cursor 的模式一致）
        if (value is not null)
        {
            RolePicker.Show(value.Options.Role);
        }
    }

    /// <summary>新会话使用的项目；null 表示不在项目中工作（使用用户主目录）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WorkspaceName), nameof(HeaderSubtitle))]
    public partial Project? Workspace { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BranchName))]
    public partial string? Branch { get; private set; }

    /// <summary>当前会话的 cwd，没有会话时为所选项目的主文件夹：@ 文件路径相对于它。</summary>
    public string? CurrentCwd => Active?.Cwd ?? Workspace?.Primary;

    /// <summary>输入框的内容：文字与 tag（docs/design/composer.md §6.4）。</summary>
    public ComposerDocument Composer { get; } = new();

    /// <summary>输入框内容的纯文本投影（tag 展开为 @路径、/命令、[图片 #N]）；设置时替换为纯文本。</summary>
    public string ComposerText
    {
        get => Composer.ToPlainText();
        set => Composer.SetContent(string.IsNullOrEmpty(value) ? [] : [new TextSegment(value)]);
    }

    [ObservableProperty]
    public partial bool IsRunning { get; private set; }

    /// <summary>输入框中待发送的图片（粘贴或选择后已保存到附件目录）。</summary>
    public ObservableCollection<ImageAttachmentViewModel> Attachments { get; } = [];

    public bool HasAttachments => Attachments.Count > 0;

    /// <summary>带图片但当前模型不支持图片：只发送图片路径。</summary>
    public bool ShowImagesUnsupported => HasAttachments && Picker.Selected is { Model.SupportsImages: false };

    private int _nextImageNumber = 1;

    // ---- 草稿：每个会话（和“新对话”空状态）一份，切换时整体替换输入框的内容（docs/design/composer-drafts.md §4） ----

    private bool _applyingDraft;
    private DateTimeOffset _draftEditedAt;

    /// <summary>输入框当前内容所属的草稿键：会话 ID，空状态为 <see cref="DraftStore.NewChatKey"/>。</summary>
    public string DraftKey => Active?.SessionId ?? DraftStore.NewChatKey;

    private void MarkDraftEdited()
    {
        if (!_applyingDraft)
        {
            // 用户改了输入框：翻出来的历史消息成了他正在写的草稿，不再是“浏览”
            History.End();
            _historyVersion++;
            _draftEditedAt = _drafts.Now;
            // 防抖：停止编辑一会儿后写盘
            _draftSaveTimer.Stop();
            _draftSaveTimer.Start();
        }
    }

    private static readonly TimeSpan DraftSaveDelay = TimeSpan.FromMilliseconds(800);
    private readonly DispatcherTimer _draftSaveTimer;

    /// <summary>把输入框当前的内容存进 <see cref="DraftStore"/>（同时排队写盘）；空内容等于删除。</summary>
    private void SaveCurrentDraft() => _drafts.Set(DraftKey, CaptureDraft());

    /// <summary>退出前：立即保存当前输入框的内容，并等写盘完成。</summary>
    public void FlushDraft()
    {
        _draftSaveTimer.Stop();
        SaveCurrentDraft();
        _drafts.Flush();
    }

    /// <summary>启动后读入磁盘上的草稿；当前输入框还是空的（用户没有抢先开始写）就换上当前会话的那一份。</summary>
    private async Task LoadDraftsAsync()
    {
        try
        {
            await _drafts.LoadAsync();
            if (CaptureDraft().IsEmpty && _drafts.Get(DraftKey) is { } draft)
            {
                ApplyDraft(draft);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Loading drafts failed");
        }
    }

    /// <summary>
    /// 输入框里属于用户的草稿。正在翻历史时输入框里是历史消息，真正的草稿是浏览前暂存的那一份
    /// （切换会话、落盘都应该保存它，docs/design/composer-drafts.md §5.3）。
    /// </summary>
    internal ComposerDraft CaptureDraft() => History.Stash ?? CaptureLive();

    /// <summary>输入框此刻显示的全部状态。</summary>
    private ComposerDraft CaptureLive() =>
        new(Composer.Segments.ToArray(), Attachments.ToArray(), _nextImageNumber, Mcp.Capture(), _draftEditedAt);

    /// <summary>用草稿整体替换输入框的内容（null 为清空）。替换本身不算用户编辑。</summary>
    private void ApplyDraft(ComposerDraft? draft)
    {
        _applyingDraft = true;
        try
        {
            Completion.ResetForDocumentSwap();
            Composer.SetContent(draft?.Segments ?? []);
            Attachments.Clear();
            foreach (var image in draft?.Images ?? [])
            {
                Attachments.Add(image);
            }
            _nextImageNumber = draft?.NextImageNumber ?? 1;
            Mcp.Apply(draft?.McpPrompt);
            _draftEditedAt = draft?.EditedAt ?? default;
        }
        finally
        {
            _applyingDraft = false;
        }
    }

    /// <summary>当前会话变了（切换、新对话、删除、断线都经过这里）：旧会话的草稿存起来，换上新会话的。</summary>
    partial void OnActiveChanged(SessionViewModel? oldValue, SessionViewModel? newValue)
    {
        WorkspaceRootsChanged?.Invoke();
        var from = oldValue?.SessionId ?? DraftStore.NewChatKey;
        var to = newValue?.SessionId ?? DraftStore.NewChatKey;
        if (from == to)
        {
            return;
        }
        _drafts.Set(from, CaptureDraft());
        History.End();
        _historyVersion++;
        ApplyDraft(_drafts.Get(to));
    }

    // ---- ↑ ↓ 历史输入：当前会话已发送的消息（docs/design/composer-drafts.md §5） ----

    /// <summary>↑ ↓ 翻历史的状态。</summary>
    public InputHistory History { get; } = new();

    // 用户编辑或切换会话时加一：异步翻页回来时据此判断是否还该继续
    private int _historyVersion;
    private bool _pagingHistory;

    /// <summary>翻到更早的一条。返回是否处理了这次按键；没有历史可翻时返回 false，按键照常移动光标。</summary>
    public bool RecallOlder(bool allowPaging = true)
    {
        if (Active is not { } session)
        {
            return false;
        }
        if (_pagingHistory)
        {
            return true;
        }
        if (History.Older(InputHistory.Sources(session.Items), CaptureLive) is { } step)
        {
            ApplyHistoryStep(step, caretAtStart: true);
            return true;
        }
        // 已加载的部分翻完了：还有更早的就加载一页再接着翻
        if (allowPaging && session.HasEarlierHistory)
        {
            _ = RecallOlderAfterPagingAsync(session);
            return true;
        }
        return false;
    }

    /// <summary>翻到更新的一条；越过最新一条时恢复浏览前的草稿。没有在浏览时返回 false。</summary>
    public bool RecallNewer()
    {
        if (Active is not { } session || History.Newer(InputHistory.Sources(session.Items)) is not { } step)
        {
            return false;
        }
        ApplyHistoryStep(step, caretAtStart: false);
        return true;
    }

    private void ApplyHistoryStep(HistoryStep step, bool caretAtStart)
    {
        ApplyDraft(step.Entry is { } entry ? InputHistory.ToDraft(entry, _drafts.Now) : step.Restore);
        // 向上翻时光标放在开头，继续按 ↑ 就接着往前翻；向下翻放在末尾（ApplyDraft 默认把光标放到末尾）
        Completion.Host?.SetCaret(caretAtStart ? 0 : Composer.Length);
    }

    private async Task RecallOlderAfterPagingAsync(SessionViewModel session)
    {
        _pagingHistory = true;
        var version = _historyVersion;
        try
        {
            var before = InputHistory.Sources(session.Items).Count;
            while (session.HasEarlierHistory && Active == session && await LoadEarlierHistoryAsync())
            {
                if (InputHistory.Sources(session.Items).Count > before)
                {
                    break;
                }
            }
        }
        finally
        {
            _pagingHistory = false;
        }
        // 等待期间用户编辑了输入框或切走了会话：不再替他翻
        if (version == _historyVersion && Active == session)
        {
            RecallOlder(allowPaging: false);
        }
    }

    /// <summary>
    /// 把发送失败的内容放回它所属的草稿：还在那个会话的输入框里就直接恢复，用户已经切走就写进那个会话的草稿。
    /// 目标里已经有新内容时，失败的内容放在前面（<see cref="ComposerDraft.Merge"/>）。
    /// </summary>
    internal void RestoreFailedDraft(string key, ComposerDraft failed)
    {
        if (key == DraftKey)
        {
            var current = CaptureDraft();
            ApplyDraft(current.IsEmpty ? failed : ComposerDraft.Merge(failed, current));
        }
        else
        {
            _drafts.Set(key, _drafts.Get(key) is { } stored ? ComposerDraft.Merge(failed, stored) : failed);
        }
    }

    /// <summary>添加一张图片附件；返回它（占位符由输入框插入到光标处）。</summary>
    public ImageAttachmentViewModel AddAttachment(string path, string mimeType)
    {
        var attachment = new ImageAttachmentViewModel(path, mimeType, _nextImageNumber++);
        Attachments.Add(attachment);
        return attachment;
    }

    [RelayCommand]
    private void RemoveAttachment(ImageAttachmentViewModel attachment)
    {
        Attachments.Remove(attachment);
        // 删除对应的图片 tag
        var number = attachment.Number.ToString(System.Globalization.CultureInfo.InvariantCulture);
        for (var i = Composer.Length - 1; i >= 0; i--)
        {
            if (Composer.TagAt(i) is { Kind: TagKind.Image } tag && tag.Value == number)
            {
                Composer.Delete(i, 1);
            }
        }
        if (Attachments.Count == 0)
        {
            _nextImageNumber = 1;
        }
    }

    [ObservableProperty]
    public partial string? StatusMessage { get; private set; }

    /// <summary>输入框下方的上下文占用环与悬停卡片。</summary>
    public ContextUsageViewModel ContextUsage { get; }

    public string WorkspaceName => Workspace?.Name ?? Strings.Get(TKCodeLangResourceKind.NoProjectName);
    public string BranchName => Branch ?? Strings.Get(TKCodeLangResourceKind.NoBranch);

    public string HeaderTitle => Active?.Title ?? Strings.Get(TKCodeLangResourceKind.NewSession);
    public string? HeaderIconKey => Active?.ModelIconKey ?? Picker.Selected?.IconKey;
    public string HeaderSubtitle => Strings.Format(TKCodeLangResourceKind.SessionSubtitleFormat,
        Active is { } active ? _projects.Resolve(active.ProjectId, active.Cwd)?.Name ?? active.ProjectName : WorkspaceName, DeviceName);

    /// <summary>切换项目时通知侧栏同步选中状态。</summary>
    public event Action? WorkspaceChanged;

    public void SetWorkspace(Project? workspace)
    {
        Workspace = workspace;
        _settings.Current.LastProjectId = workspace?.Id;
        if (workspace is not null)
        {
            _settings.Current.TouchRecentProject(workspace.Id);
        }
        _settings.Save();
        WorkspaceChanged?.Invoke();
        WorkspaceRootsChanged?.Invoke();
        if (Active is not null && workspace is not null && ProjectOf(Active) != workspace.Id)
        {
            NewChat();
        }
        else if (Active is null)
        {
            Permission.Show(RememberedPermission());
        }
    }

    /// <summary>项目表变化（编辑、重命名、移除）：所选项目换成新的快照，会话行的项目徽标随之更新。</summary>
    private void OnProjectsChanged()
    {
        if (Workspace is { } current && _projects.Find(current.Id) is var updated && !ReferenceEquals(updated, current))
        {
            Workspace = updated;
            WorkspaceChanged?.Invoke();
        }
        foreach (var item in Sessions)
        {
            item.SetProject(_projects.Resolve(item.ProjectId, item.Cwd));
        }
        OnPropertyChanged(nameof(HeaderSubtitle));
        WorkspaceRootsChanged?.Invoke();
    }

    /// <summary>当前项目中有会话正在运行时不允许切换分支（避免 Agent 读写到一半时工作区变化）。</summary>
    private bool IsWorkspaceBusy() =>
        Workspace is { } workspace && _open.Values.Any(s => s.IsRunning && workspace.HasFolder(s.Cwd));

    private async Task<string?> SwitchBranchAsync(string branch)
    {
        if (BranchPicker.SelectedRepository is not { } repository)
        {
            return null;
        }
        var error = await GitService.SwitchAsync(repository, branch);
        RefreshBranch();
        return error;
    }

    /// <summary>
    /// 工作区里的 Git 仓库（docs/design/projects.md §10.3）：主文件夹在前，然后是项目的其他文件夹与本会话追加的文件夹；
    /// 位于同一个仓库（同一个 git 目录）的文件夹只算一次。
    /// </summary>
    private IReadOnlyList<string> Repositories()
    {
        if (CurrentCwd is not { } cwd)
        {
            return [];
        }
        var seen = new HashSet<string>(PathRules.Comparer);
        var result = new List<string>();
        foreach (var folder in (IEnumerable<string>)[cwd, .. CurrentAdditionalRoots])
        {
            if (GitLayout.Find(folder) is { } layout && seen.Add(Path.GetFullPath(layout.GitDir)))
            {
                result.Add(folder);
            }
        }
        return result;
    }

    /// <summary>重新读取各仓库的当前分支与所选仓库的分支列表（切换项目、弹层打开、.git 变化、文件夹变化时）。</summary>
    private void RefreshBranch()
    {
        var repositories = Repositories();
        BranchPicker.SetRepositories(repositories.Select(r => (r, Project.FolderName(r), GitRepository.ReadBranch(r))).ToArray());
        Branch = repositories.Count > 0 && CurrentCwd is { } cwd && PathRules.SamePath(repositories[0], cwd) ? BranchPicker.Repositories[0].Branch : null;
        var selected = BranchPicker.SelectedRepository;
        BranchPicker.Load(selected is null ? null : GitRepository.ReadBranch(selected), selected is null ? [] : GitRepository.ReadBranches(selected));
        WatchBranches(repositories);
    }

    /// <summary>
    /// 监视各仓库的分支变化：在终端 / Agent 里 git switch、新建或删除分支后，底部分支选择器随之更新，
    /// 不必等到下一次切换项目。仓库不变时保留已有的监视。
    /// </summary>
    private void WatchBranches(IReadOnlyList<string> repositories)
    {
        if (repositories.SequenceEqual(_branchWatchers.Select(w => w.Path), PathRules.Comparer))
        {
            return;
        }
        foreach (var (_, watcher) in _branchWatchers)
        {
            watcher.Dispose();
        }
        _branchWatchers = repositories
            .Select(r => (r, new GitBranchWatcher(r, () => Dispatcher.UIThread.Post(RefreshBranch, DispatcherPriority.Background))))
            .ToList();
    }

    [RelayCommand]
    public void NewChat()
    {
        Push(null);
        Active = null;
        // 新会话从默认模式开始（settings.json 的 defaultRole）
        RolePicker.Show(_userSettings.Current.DefaultRole);
        Picker.SetScope(null);
        ApplyDefaultModel(force: true);
        foreach (var item in Sessions)
        {
            item.IsSelected = false;
        }
        StatusMessage = null;
        IsRunning = false;
        UpdateContextUsage();
    }

    [RelayCommand]
    private async Task OpenSessionAsync(SessionItemViewModel item)
    {
        Push(item.SessionId);
        foreach (var s in Sessions)
        {
            s.IsSelected = s == item;
        }
        try
        {
            if (item.Live is null)
            {
                var server = await _host.GetServerAsync();
                // 只取最近若干轮，更早的消息在滚动到顶部时分页加载
                var result = await server.OpenSessionAsync(item.FilePath, HistoryTurnsPerPage);
                var live = Track(new SessionViewModel(result.Session) { Title = item.CustomTitle ?? item.Title });
                live.LoadHistory(result.History, result.HistoryStart);
                live.SetRunning(result.IsRunning);
                item.Live = live;
                item.SyncFrom(live);
            }
            Activate(item.Live);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Open session failed");
            StatusMessage = Strings.Format(TKCodeLangResourceKind.StatusSendFailedFormat, ex.Message);
        }
    }

    /// <summary>每页历史的轮数（每轮从一条用户消息开始）。</summary>
    private const int HistoryTurnsPerPage = 20;

    /// <summary>加载当前会话更早的一页历史（滚动到顶部时由界面调用）。返回是否插入了内容。</summary>
    public async Task<bool> LoadEarlierHistoryAsync()
    {
        if (Active is not { HasEarlierHistory: true, IsLoadingEarlier: false } session)
        {
            return false;
        }
        session.IsLoadingEarlier = true;
        try
        {
            var server = await _host.GetServerAsync();
            var page = await server.GetHistoryAsync(session.SessionId, session.HistoryStart, HistoryTurnsPerPage);
            session.PrependHistory(page.History, page.Start);
            return page.History.Count > 0;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Load earlier history failed");
            return false;
        }
        finally
        {
            session.IsLoadingEarlier = false;
        }
    }

    /// <summary>立即发送排队的消息：运行中插入当前轮（steer，在下一步生效），空闲时直接开始新的一轮。</summary>
    internal Task SendQueuedNowAsync(QueuedPromptViewModel queued) => SendQueuedAsync(queued, steer: queued.Session.IsRunning);

    /// <summary>上一轮被停止或出错后，继续发出队列中的消息。</summary>
    [RelayCommand]
    private Task ResumeQueueAsync() =>
        Active?.QueuedPrompts.FirstOrDefault() is { } next ? SendQueuedAsync(next, steer: Active.IsRunning) : Task.CompletedTask;

    private async Task SendQueuedAsync(QueuedPromptViewModel queued, bool steer)
    {
        var session = queued.Session;
        session.QueuedPrompts.Remove(queued);
        session.IsQueuePaused = false;
        try
        {
            var server = await _host.GetServerAsync();
            await server.PromptAsync(new PromptRequest(session.SessionId, queued.Text,
                steer ? StreamingBehaviorDto.Steer : StreamingBehaviorDto.FollowUp, ToDtos(queued.Images), Segments: queued.Segments));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Send queued prompt failed");
            // 发送失败：放回队首并暂停，避免丢失
            session.QueuedPrompts.Insert(0, queued);
            session.IsQueuePaused = true;
            StatusMessage = Strings.Format(TKCodeLangResourceKind.StatusSendFailedFormat, ex.Message);
        }
    }

    /// <summary>Composer 的 MCP 服务器提示与选中的提示词。</summary>
    public McpComposerViewModel Mcp { get; }

    /// <summary>输入框的 @ / 补全。</summary>
    public CompletionViewModel Completion { get; }

    /// <summary>@ 文件补全（主窗口为它提供编辑区打开的文件）。</summary>
    public FileCompletionProvider Files { get; }

    /// <summary>当前项目的 command / skill / role。</summary>
    public AssetCatalogCache Assets { get; }

    /// <summary>/model：请求打开模型选择器（由获得焦点的输入框处理）。</summary>
    public event Action? ModelPickerRequested;

    /// <summary>/role：请求打开模式选择器（由主窗口处理）。</summary>
    public event Action? RolePickerRequested;

    /// <summary>/add-dir：请求打开文件夹选择框（由主窗口处理），选中的文件夹交给 <see cref="AddSessionDirectoryAsync"/>。</summary>
    public event Action? AddDirectoryRequested;

    // ---- 多文件夹（docs/design/projects.md §10） ----

    /// <summary>空状态里用 /add-dir 追加的文件夹：创建会话时一起发给 Host（§6.4）。</summary>
    public ObservableCollection<string> PendingDirectories { get; } = [];

    /// <summary>工作区的根目录变了（项目的文件夹、本会话追加的文件夹、切换会话或项目）：文件树与 @ 搜索随之更新。</summary>
    public event Action? WorkspaceRootsChanged;

    /// <summary>某个打开的会话开始或停止运行（侧栏项目行的运行指示随之更新）。</summary>
    public event Action? RunningChanged;

    /// <summary>项目里有打开的会话正在运行（docs/design/projects.md §8.3）。</summary>
    public bool IsProjectRunning(string projectId) => _open.Values.Any(s => s.IsRunning && ProjectOf(s) == projectId);

    /// <summary>当前会话（没有会话时为所选项目）主文件夹以外的根目录：项目的其他文件夹与本会话追加的文件夹，只含存在的目录。</summary>
    public IReadOnlyList<string> CurrentAdditionalRoots
    {
        get
        {
            var project = Active is { } active ? _projects.Resolve(active.ProjectId, active.Cwd) : Workspace;
            var cwd = CurrentCwd;
            IEnumerable<string> folders = project?.Folders.Where(f => cwd is null || !PathRules.SamePath(f, cwd)) ?? [];
            return folders.Concat(CurrentSessionRoots).Where(Directory.Exists).Distinct(PathRules.Comparer).ToArray();
        }
    }

    /// <summary>只属于本会话（或空状态里待创建的会话）的文件夹。</summary>
    public IReadOnlyList<string> CurrentSessionRoots => Active?.SessionDirectories ?? PendingDirectories;

    /// <summary>/add-dir：只给本会话追加一个文件夹；没有会话时先记下，创建会话时一起发送。失败时在状态栏说明原因。</summary>
    public async Task AddSessionDirectoryAsync(string path)
    {
        if (Active is not { } session)
        {
            var full = PathRules.Normalize(path);
            if (!Directory.Exists(full))
            {
                StatusMessage = Strings.Format(TKCodeLangResourceKind.AddDirFailedFormat, path);
                return;
            }
            if (!PendingDirectories.Contains(full, PathRules.Comparer) && !(CurrentCwd is { } cwd && PathRules.SamePath(cwd, full)))
            {
                PendingDirectories.Add(full);
            }
            return;
        }
        try
        {
            var server = await _host.GetServerAsync();
            var updated = await server.AddSessionDirectoryAsync(session.SessionId, path);
            session.SessionDirectories = updated.SessionDirectories ?? [];
            StatusMessage = null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Adding a session folder failed");
            StatusMessage = Strings.Format(TKCodeLangResourceKind.AddDirFailedFormat, ex.Message);
        }
    }

    /// <summary>文件树根节点的「从本会话移除」。</summary>
    public async Task RemoveSessionDirectoryAsync(string path)
    {
        if (Active is not { } session)
        {
            var existing = PendingDirectories.FirstOrDefault(d => PathRules.SamePath(d, path));
            if (existing is not null)
            {
                PendingDirectories.Remove(existing);
            }
            return;
        }
        try
        {
            var server = await _host.GetServerAsync();
            var updated = await server.RemoveSessionDirectoryAsync(session.SessionId, path);
            session.SessionDirectories = updated.SessionDirectories ?? [];
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Removing a session folder failed");
            StatusMessage = ex.Message;
        }
    }

    /// <summary>输入的是 “/add-dir [路径]” 时返回路径（可能为空串），否则 null。</summary>
    private static string? AddDirArgument(string text)
    {
        const string command = "/add-dir";
        if (!text.StartsWith(command, StringComparison.OrdinalIgnoreCase) || (text.Length > command.Length && !char.IsWhiteSpace(text[command.Length])))
        {
            return null;
        }
        return text[command.Length..].Trim();
    }

    /// <summary>/add-dir 的路径参数：支持 ~ 与相对路径（基于当前的主文件夹）。</summary>
    private string ResolveDirectoryArgument(string argument)
    {
        if (argument == "~" || argument.StartsWith("~/", StringComparison.Ordinal) || argument.StartsWith("~\\", StringComparison.Ordinal))
        {
            argument = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), argument.Length > 2 ? argument[2..] : "");
        }
        return Path.GetFullPath(argument, CurrentCwd ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
    }

    /// <summary>会话的 role（Agent / Ask / Plan / 自定义）。</summary>
    public RolePickerViewModel RolePicker { get; } = new();

    /// <summary>当前项目的自定义 role（项目或其中的 .agents 变化时重新拉取）。</summary>
    private async Task LoadRolesAsync()
    {
        if (await Assets.GetAsync() is { } catalog)
        {
            RolePicker.Load(catalog.Roles);
        }
    }

    /// <summary>
    /// 选定 role：指定了模型时先切换模型（与在模型选择器中点选相同），再同步到当前会话；
    /// 空状态时随 CreateSessionRequest 发送。失败时恢复会话的实际 role。
    /// </summary>
    private async Task ApplyRoleAsync(RoleOptionViewModel role)
    {
        if (role.Model is { } model && !Picker.SelectByRef(model))
        {
            StatusMessage = Strings.Format(TKCodeLangResourceKind.RoleModelUnavailableFormat, model);
        }
        if (Active is not { } session)
        {
            return;
        }
        try
        {
            var server = await _host.GetServerAsync();
            session.Options = await server.SetOptionsAsync(session.SessionId, new SessionOptionsDto(Role: role.Name));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Set role failed");
            StatusMessage = Strings.Format(TKCodeLangResourceKind.StatusSendFailedFormat, ex.Message);
        }
        if (session == Active)
        {
            RolePicker.Show(session.Options.Role);
        }
    }

    private SessionItemViewModel? ActiveItem => Active is { } active ? Sessions.FirstOrDefault(s => s.SessionId == active.SessionId) : null;

    /// <summary>/fork：从当前会话分叉。</summary>
    private Task ForkActiveAsync() => ActiveItem is { } item ? ForkSessionAsync(item) : Task.CompletedTask;

    /// <summary>输入的是 “/compact [指令]” 时返回指令（可能为空串），否则 null。</summary>
    private static string? CompactInstructions(string text)
    {
        const string command = "/compact";
        if (!text.StartsWith(command, StringComparison.OrdinalIgnoreCase) || (text.Length > command.Length && !char.IsWhiteSpace(text[command.Length])))
        {
            return null;
        }
        return text[command.Length..].Trim();
    }

    /// <summary>压缩当前会话的上下文（docs/design/compaction.md §12）：进度与结果以会话事件显示为分隔卡片。</summary>
    private async Task CompactActiveAsync(string? instructions)
    {
        if (Active is not { Runtime: null } session)
        {
            StatusMessage = Strings.Get(TKCodeLangResourceKind.CompactionUnavailable);
            return;
        }
        try
        {
            var server = await _host.GetServerAsync();
            await server.CompactAsync(session.SessionId, instructions);
            StatusMessage = null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Compact failed");
            StatusMessage = Strings.Format(TKCodeLangResourceKind.CompactionFailedFormat, ex.Message);
        }
    }

    private bool CanSend() => !IsAnsweringQuestion && !IsReviewingPlan && (!string.IsNullOrWhiteSpace(ComposerText) || Attachments.Count > 0 || Mcp.HasPrompt);

    private static IReadOnlyList<ImageAttachmentDto>? ToDtos(IReadOnlyList<ImageAttachmentViewModel> images) =>
        images.Count == 0 ? null : images.Select(i => new ImageAttachmentDto(i.Path, i.MimeType)).ToArray();

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendAsync()
    {
        if (IsAnsweringQuestion || IsReviewingPlan)
        {
            return;
        }
        var images = Attachments.ToArray();
        var draft = Composer.Segments.ToArray();
        // 图片占位符展开为「[Image #N: 路径]」，图片本身由 AgentHost 读取后以 base64 发送。
        // 其他文件不进会话：tag 的纯文本已是「[Attached file: 绝对路径]」，再补一句让 Agent 自己去读。
        // command tag 不在文字里：Host 按 segments 读取正文放在前面
        var hasFile = draft.Any(s => s is TagSegment { Kind: TagKind.AttachedFile });
        var text = ImagePrompt.Expand(ComposerSegments.ModelText(draft).Trim(), images);
        var segments = ComposerSegments.ToDto(draft);
        var hasCommand = draft.Any(s => s is TagSegment { Kind: TagKind.Command });
        if (text.Length == 0 && !Mcp.HasPrompt && !hasCommand)
        {
            return;
        }
        if (images.Length == 0 && !hasFile && !hasCommand && !Mcp.HasPrompt && Active is { Runtime: null } && CompactInstructions(text) is { } compact)
        {
            ComposerText = "";
            await CompactActiveAsync(compact.Length == 0 ? null : compact);
            return;
        }
        if (images.Length == 0 && !hasFile && !hasCommand && !Mcp.HasPrompt && AddDirArgument(text) is { } directory)
        {
            ComposerText = "";
            if (directory.Length == 0)
            {
                AddDirectoryRequested?.Invoke();
            }
            else
            {
                await AddSessionDirectoryAsync(ResolveDirectoryArgument(directory.Trim('"')));
            }
            return;
        }
        if (hasFile)
        {
            text = AttachedFiles.AppendInstruction(text);
        }
        var title = ImagePrompt.Title(ComposerDocument.PlainText(draft).Trim());
        if ((Mcp.HasPrompt || McpComposerViewModel.ResourcesIn(Composer.Segments).Count > 0) && Active is { IsRunning: true })
        {
            StatusMessage = Strings.Get(TKCodeLangResourceKind.McpWaitForTurn);
            return;
        }
        var mcpSnapshot = Mcp.Capture();
        var (mcpResources, mcpPrompt, mcpError) = Mcp.Take(Composer.Segments);
        if (mcpError is not null)
        {
            StatusMessage = mcpError;
            return;
        }
        // 发送期间用户可能切走会话：发给哪个会话、失败时草稿回到哪里，都以发送这一刻为准（docs/design/composer-drafts.md §4.3）
        var target = Active;
        var ownerKey = DraftKey;
        ComposerText = "";
        Attachments.Clear();
        _nextImageNumber = 1;
        try
        {
            var server = await _host.GetServerAsync();
            if (target is null)
            {
                var cwd = Workspace?.Primary ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                var model = Picker.Selected?.Model.Ref ?? "fake/fake-sonnet";
                var session = await server.CreateSessionAsync(new CreateSessionRequest(cwd, model,
                    Picker.Options with { PermissionMode = Permission.Mode, Role = RolePicker.Role }, Workspace?.Id,
                    PendingDirectories.Count > 0 ? PendingDirectories.ToArray() : null));
                PendingDirectories.Clear();
                var live = Track(new SessionViewModel(session) { Title = title });
                Picker.ApplyHostOptions(session.Options);
                var item = new SessionItemViewModel(session.SessionId, session.FilePath, session.Cwd, _projects.Resolve(session.ProjectId, session.Cwd))
                {
                    Live = live,
                    Owner = this,
                };
                item.SyncFrom(live);
                Sessions.Insert(0, item);
                // 会话已经创建：之后失败，草稿属于这个新会话
                target = live;
                ownerKey = live.SessionId;
                // 等待期间用户已经去了别的会话时，不把人拉回新会话
                if (Active is null)
                {
                    foreach (var s in Sessions)
                    {
                        s.IsSelected = s == item;
                    }
                    Push(item.SessionId);
                    Activate(live);
                }
            }
            else if (target.IsRunning)
            {
                // 运行中再次发送：显示在输入框上方的队列里，本轮正常结束后按顺序发出（可编辑、移除或立即插入）
                target.QueuedPrompts.Add(new QueuedPromptViewModel(text, images, target, segments) { Owner = this });
                StatusMessage = null;
                return;
            }
            if (target is { IsRunning: false } idle)
            {
                await server.PromptAsync(new PromptRequest(idle.SessionId, text, Images: ToDtos(images), McpResources: mcpResources, McpPrompt: mcpPrompt,
                    Segments: segments));
            }
            StatusMessage = null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Prompt failed");
            StatusMessage = Strings.Format(TKCodeLangResourceKind.StatusSendFailedFormat, ex.Message);
            // 发送失败：恢复原文、图片与 MCP 引用
            RestoreFailedDraft(ownerKey, new ComposerDraft(draft, images, images.Length == 0 ? 1 : images.Max(i => i.Number) + 1, mcpSnapshot, _drafts.Now));
        }
    }

    [RelayCommand]
    private async Task StopAsync()
    {
        if (Active is { } session)
        {
            var server = await _host.GetServerAsync();
            await server.AbortAsync(session.SessionId);
        }
    }

    private bool CanGoBack() => _historyIndex > 0;

    private bool CanGoForward() => _historyIndex < _history.Count - 1;

    [RelayCommand(CanExecute = nameof(CanGoBack))]
    private Task GoBackAsync() => NavigateAsync(_historyIndex - 1);

    [RelayCommand(CanExecute = nameof(CanGoForward))]
    private Task GoForwardAsync() => NavigateAsync(_historyIndex + 1);

    private async Task NavigateAsync(int index)
    {
        _navigating = true;
        try
        {
            // 历史中的会话可能已不在列表中（例如 Host 重启），跳过这样的记录。
            var target = _history[index];
            var item = target is null ? null : Sessions.FirstOrDefault(s => s.SessionId == target);
            _historyIndex = index;
            if (item is null)
            {
                NewChat();
            }
            else
            {
                await OpenSessionAsync(item);
            }
        }
        finally
        {
            _navigating = false;
            GoBackCommand.NotifyCanExecuteChanged();
            GoForwardCommand.NotifyCanExecuteChanged();
        }
    }

    private void Push(string? sessionId)
    {
        if (_navigating || _history[_historyIndex] == sessionId)
        {
            return;
        }
        _history.RemoveRange(_historyIndex + 1, _history.Count - _historyIndex - 1);
        _history.Add(sessionId);
        _historyIndex = _history.Count - 1;
        GoBackCommand.NotifyCanExecuteChanged();
        GoForwardCommand.NotifyCanExecuteChanged();
    }

    // ---- 会话列表：分页加载 ----

    /// <summary>每次“展开更多”多显示的会话条数。</summary>
    private const int SessionPageSize = 20;
    private int _sessionLimit = SessionPageSize;
    private bool _loadingSessions;
    /// <summary>侧栏筛选的项目 id；null 表示全部会话。</summary>
    private string? _sessionFilter;

    /// <summary>还有更多会话：列表底部显示“展开更多”按钮。</summary>
    public bool HasMoreSessions { get; private set; }

    /// <summary>侧栏筛选的项目变化：按项目重新加载，重新从第一页开始。</summary>
    public Task SetSessionFilterAsync(string? projectId)
    {
        _sessionFilter = projectId;
        _sessionLimit = SessionPageSize;
        return RefreshSessionsAsync();
    }

    /// <summary>
    /// 从 Host 重新加载前 <see cref="_sessionLimit"/> 条会话（多取一条判断是否还有下一页），
    /// 保留已打开会话的实时状态。
    /// </summary>
    public async Task RefreshSessionsAsync()
    {
        try
        {
            var server = await _host.GetServerAsync();
            // 多取一条：返回条数超过显示上限，说明还有下一页
            var page = await server.QuerySessionsAsync(null, 0, _sessionLimit + 1, _sessionFilter);
            var existing = Sessions.ToDictionary(s => s.SessionId);
            Sessions.Clear();
            foreach (var summary in page.Items.Take(_sessionLimit))
            {
                Sessions.Add(Merge(existing.GetValueOrDefault(summary.SessionId), summary));
            }
            HasMoreSessions = page.Items.Count > _sessionLimit;
            // 刚创建、尚未写盘的会话不在文件列表中
            foreach (var item in existing.Values.Where(i => i.Live is not null && !Sessions.Contains(i)))
            {
                Sessions.Insert(0, item);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "List sessions failed");
        }
        OnPropertyChanged(nameof(HasMoreSessions));
    }

    /// <summary>
    /// “展开更多”：显示上限加一页，再取一次前 N 条（多取一条判断是否还有下一页），
    /// 只追加尚未显示的会话，保留列表滚动位置与行对象。
    /// </summary>
    [RelayCommand]
    private async Task LoadMoreSessionsAsync()
    {
        if (_loadingSessions || !HasMoreSessions)
        {
            return;
        }
        _loadingSessions = true;
        try
        {
            var server = await _host.GetServerAsync();
            var limit = _sessionLimit + SessionPageSize;
            var page = await server.QuerySessionsAsync(null, 0, limit + 1, _sessionFilter);
            var known = Sessions.Select(s => s.SessionId).ToHashSet();
            foreach (var summary in page.Items.Take(limit).Where(s => !known.Contains(s.SessionId)))
            {
                Sessions.Add(Merge(null, summary));
            }
            _sessionLimit = limit;
            // Host 单次查询有上限：达到上限后返回的条数不再增长，按钮随之隐藏
            HasMoreSessions = page.Items.Count > limit;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Load more sessions failed");
        }
        finally
        {
            _loadingSessions = false;
        }
        OnPropertyChanged(nameof(HasMoreSessions));
    }

    private SessionItemViewModel Merge(SessionItemViewModel? item, SessionSummaryDto summary)
    {
        item ??= new SessionItemViewModel(summary.SessionId, summary.FilePath, summary.Cwd) { Owner = this };
        item.Owner = this;
        item.SetProject(_projects.Find(summary.ProjectId));
        if (summary.HasCustomTitle)
        {
            item.CustomTitle = summary.Title;
        }
        else
        {
            item.Title ??= summary.Title;
        }
        item.IsPinned = summary.IsPinned;
        item.NativeSessionId = summary.NativeSessionId;
        item.ModelId ??= summary.ModelId;
        item.Runtime ??= summary.Runtime;
        item.LastActivity = summary.LastModified.ToLocalTime();
        item.IsRunning = summary.IsRunning || item.Live?.IsRunning == true;
        if (item.Live is { } live)
        {
            item.SyncFrom(live);
        }
        return item;
    }

    // ---- 会话右键菜单 ----

    /// <summary>危险操作的确认框（标题、正文）；由窗口提供，测试中可替换。未设置时视为取消。</summary>
    public Func<string, string, Task<bool>>? ConfirmDangerAsync { get; set; }

    /// <summary>写入剪贴板；由窗口提供。</summary>
    public Func<string, Task>? SetClipboardAsync { get; set; }

    internal async Task RenameSessionAsync(SessionItemViewModel item, string title)
    {
        await RunSessionActionAsync(async server =>
        {
            await server.RenameSessionAsync(item.FilePath, title);
            _manualTitleEdits.Add(item.SessionId);
            item.CustomTitle = title.Length == 0 ? null : title;
            if (item.Live is { } live)
            {
                live.Title = item.CustomTitle ?? item.Title;
            }
            OnPropertyChanged(nameof(HeaderTitle));
        });
    }

    internal Task SetPinnedAsync(SessionItemViewModel item, bool pinned) => RunSessionActionAsync(async server =>
    {
        await server.PinSessionAsync(item.FilePath, pinned);
        item.IsPinned = pinned;
        // 置顶在前：重新排序已加载的会话
        var ordered = Sessions.OrderByDescending(s => s.IsPinned).ThenByDescending(s => s.LastActivity).ToList();
        for (var i = 0; i < ordered.Count; i++)
        {
            var index = Sessions.IndexOf(ordered[i]);
            if (index != i)
            {
                Sessions.Move(index, i);
            }
        }
    });

    internal async Task DeleteSessionAsync(SessionItemViewModel item)
    {
        var confirmed = ConfirmDangerAsync is { } confirm && await confirm(Strings.Get(TKCodeLangResourceKind.SessionDeleteConfirmTitle),
            Strings.Format(TKCodeLangResourceKind.SessionDeleteConfirmText, item.DisplayTitle));
        if (!confirmed)
        {
            return;
        }
        await RunSessionActionAsync(async server =>
        {
            // 先撤回待处理的审批 / 表单 / 提问，Host 不必等到超时
            if (_open.TryGetValue(item.SessionId, out var open))
            {
                open.CancelPendingRequests();
            }
            // 会话文件尚未创建（新会话还没有消息）时只需关闭
            if (File.Exists(item.FilePath))
            {
                await server.DeleteSessionAsync(item.FilePath);
            }
            else
            {
                await server.CloseSessionAsync(item.SessionId);
            }
            if (Active is { } active && active.SessionId == item.SessionId)
            {
                NewChat();
            }
            _open.Remove(item.SessionId);
            Sessions.Remove(item);
            // 放在 NewChat() 之后：删除的是当前会话时，切换已把它的草稿存进了 DraftStore
            _drafts.Remove(item.SessionId);
        });
    }

    internal Task ForkSessionAsync(SessionItemViewModel item) => RunSessionActionAsync(async server =>
    {
        var result = await server.ForkSessionAsync(item.FilePath);
        var live = Track(new SessionViewModel(result.Session) { Title = item.CustomTitle ?? item.Title });
        live.LoadHistory(result.History, result.HistoryStart);
        var fork = new SessionItemViewModel(result.Session.SessionId, result.Session.FilePath, result.Session.Cwd,
            _projects.Resolve(result.Session.ProjectId, result.Session.Cwd))
        {
            Live = live,
            Owner = this,
            CustomTitle = item.CustomTitle,
        };
        fork.SyncFrom(live);
        Sessions.Insert(Sessions.TakeWhile(s => s.IsPinned).Count(), fork);
        foreach (var s in Sessions)
        {
            s.IsSelected = s == fork;
        }
        Push(fork.SessionId);
        Activate(live);
    });

    internal async Task CopySessionIdAsync(SessionItemViewModel item)
    {
        if (SetClipboardAsync is { } copy)
        {
            await copy(item.NativeSessionId is { } native ? $"{item.SessionId}\n{native}" : item.SessionId);
        }
    }

    /// <summary>在系统文件管理器中显示会话文件。</summary>
    internal void RevealSessionFile(SessionItemViewModel item)
    {
        try
        {
            var path = File.Exists(item.FilePath) ? item.FilePath : Path.GetDirectoryName(item.FilePath)!;
            var start = OperatingSystem.IsMacOS() ? new System.Diagnostics.ProcessStartInfo("open") { ArgumentList = { "-R", path } }
                : OperatingSystem.IsWindows() ? new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{path}\"")
                : new System.Diagnostics.ProcessStartInfo("xdg-open") { ArgumentList = { Path.GetDirectoryName(path)! } };
            start.UseShellExecute = false;
            System.Diagnostics.Process.Start(start)?.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Reveal session file failed");
        }
    }

    private async Task RunSessionActionAsync(Func<IAgentHostServer, Task> action)
    {
        try
        {
            await action(await _host.GetServerAsync());
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Session action failed");
            StatusMessage = Strings.Format(TKCodeLangResourceKind.StatusSessionActionFailedFormat, ex.Message);
        }
    }

    private async Task InitializeAsync()
    {
        // 草稿的读取不依赖 Host，与它的启动并行
        _ = LoadDraftsAsync();
        try
        {
            var server = await _host.GetServerAsync();
            Picker.Load(await server.ListModelsAsync(), _userSettings.Current.Model);
            OnPropertyChanged(nameof(HeaderIconKey));
            await RefreshSessionsAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start agent host");
            StatusMessage = Strings.Format(TKCodeLangResourceKind.StatusHostStartFailedFormat, ex.Message);
        }
    }

    private readonly TKCodeLangResourceKind _welcomeQuip = WelcomeQuips[Random.Shared.Next(WelcomeQuips.Length)];
    private static readonly TKCodeLangResourceKind[] WelcomeQuips =
    [
        TKCodeLangResourceKind.WelcomeQuip1,
        TKCodeLangResourceKind.WelcomeQuip2,
        TKCodeLangResourceKind.WelcomeQuip3,
        TKCodeLangResourceKind.WelcomeQuip4,
        TKCodeLangResourceKind.WelcomeQuip5,
        TKCodeLangResourceKind.WelcomeQuip6,
        TKCodeLangResourceKind.WelcomeQuip7,
        TKCodeLangResourceKind.WelcomeQuip8,
        TKCodeLangResourceKind.WelcomeQuip9,
        TKCodeLangResourceKind.WelcomeQuip10,
    ];

    /// <summary>空状态的静态歇后语，不使用动画或轮播计时器。</summary>
    public string WelcomeQuip => Strings.Get(_welcomeQuip);

    /// <summary>撤销文件改动：经宿主执行（宿主负责校验文件指纹）。</summary>
    private async Task<RevertFileResult> RevertFileAsync(RevertFileRequest request)
    {
        var server = await _host.GetServerAsync();
        return await server.RevertFileAsync(request);
    }

    private SessionViewModel Track(SessionViewModel session)
    {
        _open[session.SessionId] = session;
        session.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SessionViewModel.SessionDirectories) && ReferenceEquals(session, Active))
            {
                WorkspaceRootsChanged?.Invoke();
            }
            if (e.PropertyName == nameof(SessionViewModel.IsRunning))
            {
                RunningChanged?.Invoke();
            }
        };
        session.RevertFile = RevertFileAsync;
        // 本轮正常结束：发出队列中的下一条（后台会话同样继续）
        session.RunEnded += completed =>
        {
            if (completed && session.QueuedPrompts.FirstOrDefault() is { } next)
            {
                _ = SendQueuedAsync(next, steer: false);
            }
        };
        void SyncWaiting()
        {
            if (Sessions.FirstOrDefault(s => s.SessionId == session.SessionId) is { } item)
            {
                item.SyncFrom(session);
            }
        }
        session.PendingApprovals.CollectionChanged += (_, _) => SyncWaiting();
        session.PendingElicitations.CollectionChanged += (_, _) => SyncWaiting();
        session.PendingQuestions.CollectionChanged += (_, _) => SyncWaiting();
        session.PendingPlanReviews.CollectionChanged += (_, _) => SyncWaiting();
        // Host 主动切换了模式或权限（计划批准、进入 Plan 模式）：选择器随 Options 更新；权限变了时与手动选择一样按项目记住
        session.HostOptionsChanged += (previous, options) =>
        {
            if (options.PermissionMode != previous.PermissionMode)
            {
                RememberPermission(ProjectOf(session), options.PermissionMode);
            }
        };
        session.Items.CollectionChanged += (_, _) =>
        {
            if (session == Active)
            {
                OnPropertyChanged(nameof(IsEmpty));
            }
        };
        session.PropertyChanged += (_, e) =>
        {
            if (session != Active)
            {
                return;
            }
            switch (e.PropertyName)
            {
                case nameof(SessionViewModel.IsRunning):
                    IsRunning = session.IsRunning;
                    break;
                case nameof(SessionViewModel.HasPendingQuestion):
                    OnPropertyChanged(nameof(IsAnsweringQuestion));
                    OnPropertyChanged(nameof(ShowPlanReview));
                    OnPropertyChanged(nameof(ShowDockedComposer));
                    SendCommand.NotifyCanExecuteChanged();
                    break;
                case nameof(SessionViewModel.HasPendingPlanReview):
                    OnPropertyChanged(nameof(IsReviewingPlan));
                    OnPropertyChanged(nameof(ShowPlanReview));
                    OnPropertyChanged(nameof(ShowDockedComposer));
                    SendCommand.NotifyCanExecuteChanged();
                    break;
                case nameof(SessionViewModel.LastUsageTokens) or nameof(SessionViewModel.LastUsage):
                    UpdateContextUsage();
                    break;
                case nameof(SessionViewModel.Title):
                    OnPropertyChanged(nameof(HeaderTitle));
                    break;
                case nameof(SessionViewModel.ModelId):
                    OnPropertyChanged(nameof(HeaderIconKey));
                    break;
                case nameof(SessionViewModel.PickerScope):
                    Picker.SetScope(session.PickerScope);
                    break;
                case nameof(SessionViewModel.Options) when session == Active:
                    Permission.Show(session.Options.PermissionMode);
                    RolePicker.Show(session.Options.Role);
                    break;
            }
        };
        return session;
    }

    private void Activate(SessionViewModel session)
    {
        Active = session;
        IsRunning = session.IsRunning;
        // 有历史的会话只能切换到同一运行时（或内置 Agent）的模型
        Picker.SetScope(session.PickerScope);
        Picker.ShowSession(session.ModelId, session.Options);
        StatusMessage = null;
        UpdateContextUsage();
        OnPropertyChanged(nameof(HeaderIconKey));
    }

    private void OnSessionEvent(SessionEventNotification n)
    {
        if (!_open.TryGetValue(n.SessionId, out var session))
        {
            return;
        }
        var item = Sessions.FirstOrDefault(s => s.SessionId == n.SessionId);
        // 已保存的命名优先，避免排队中的自动标题事件覆盖刚完成的手动重命名。
        if (n.Event is SessionTitleChanged title && (_manualTitleEdits.Contains(n.SessionId) ||
            item?.CustomTitle is { } savedTitle && savedTitle != title.Title))
        {
            return;
        }
        session.Apply(n.Event);
        if (item is not null)
        {
            item.SyncFrom(session);
            // 有新活动的会话移到列表顶部
            var index = Sessions.IndexOf(item);
            if (n.Event is MessageStarted { Role: "user" } && index > 0)
            {
                Sessions.Move(index, 0);
            }
        }
    }

    /// <summary>
    /// Host 请求审批：加入对应会话的待审批队列，在 Composer 上方显示，等待用户选择。
    /// 未在 UI 中打开的会话（理论上不会发生）直接拒绝。
    /// </summary>
    private Task<ToolApprovalResponse> HandleApprovalAsync(ToolApprovalRequest request, CancellationToken ct)
    {
        if (!_open.TryGetValue(request.SessionId, out var session))
        {
            return Task.FromResult(new ToolApprovalResponse(false, "Session is not open in the UI."));
        }
        var approval = new ApprovalRequestViewModel(request);
        session.AddApproval(approval);
        WithdrawOnCancel(ct, approval.Cancel);
        if (Sessions.FirstOrDefault(s => s.SessionId == session.SessionId) is { } item)
        {
            item.SyncFrom(session);
        }
        return approval.Result;
    }

    /// <summary>
    /// MCP elicitation：显示在发起请求的会话中；找不到该会话（例如子 Agent 或设置页的临时连接）时显示在当前会话，
    /// 没有打开的会话时 cancel。
    /// </summary>
    private Task<McpElicitResponse> HandleElicitationAsync(McpElicitRequest request, CancellationToken ct)
    {
        var session = request.SessionId is { } id && _open.TryGetValue(id, out var owner) ? owner : Active;
        if (session is null)
        {
            return Task.FromResult(new McpElicitResponse("cancel"));
        }
        var elicitation = new McpElicitationViewModel(request, OpenExternalUrl);
        session.AddElicitation(elicitation);
        WithdrawOnCancel(ct, elicitation.Cancel);
        return elicitation.Result;
    }

    /// <summary>
    /// Agent 提问（docs/design/ask-user.md §7）：加入发起会话的提问队列，问答面板替换输入框。
    /// 会话不在 UI 中打开时（理论上不会发生）视为拒绝回答。
    /// </summary>
    private Task<UserInputResponse> HandleUserInputAsync(UserInputRequest request, CancellationToken ct)
    {
        if (!_open.TryGetValue(request.SessionId, out var session))
        {
            return Task.FromResult(UserInputResponse.Decline);
        }
        var question = new QuestionRequestViewModel(request, RuntimeDisplayName(request.Source));
        session.AddQuestion(question);
        WithdrawOnCancel(ct, question.Cancel);
        if (session == Active)
        {
            QuestionOpened?.Invoke();
        }
        return question.Result;
    }

    /// <summary>新的提问出现在当前会话（视图据此把焦点移到问答面板）。</summary>
    public event Action? QuestionOpened;

    /// <summary>
    /// 计划审批（docs/design/plan-mode.md §14.1）：加入发起会话的审批队列，审批面板替换输入框。
    /// 会话不在 UI 中打开时（理论上不会发生）视为暂不批准。
    /// </summary>
    private Task<PlanReviewResponse> HandlePlanReviewAsync(PlanReviewRequest request, CancellationToken ct)
    {
        if (!_open.TryGetValue(request.SessionId, out var session))
        {
            return Task.FromResult(PlanReviewResponse.Dismiss);
        }
        var review = new PlanReviewViewModel(request, RuntimeDisplayName(request.Source), ContextPercent(session), path => OpenPlanFile(path));
        session.AddPlanReview(review);
        WithdrawOnCancel(ct, review.Cancel);
        if (session == Active)
        {
            PlanReviewOpened?.Invoke();
        }
        return review.Result;
    }

    /// <summary>新的计划审批出现在当前会话（视图据此把焦点移到审批面板）。</summary>
    public event Action? PlanReviewOpened;

    /// <summary>规划用掉的上下文比例（与上下文占用环同一组数据：最近一次回复的 tokens / 会话的上下文窗口）；不知道时为 null。</summary>
    private int? ContextPercent(SessionViewModel session)
    {
        var budget = session.Options.ContextWindow ?? 0;
        if (budget <= 0)
        {
            budget = Picker.ContextWindowValue;
        }
        var tokens = session.LastUsageTokens;
        if (tokens <= 0 || budget <= 0)
        {
            return null;
        }
        return (int)Math.Round(Math.Clamp((double)tokens / budget, 0, 1) * 100);
    }

    /// <summary>面板标题里的来源名：内置为 null（“TKCode 提问”），外部运行时用其显示名。</summary>
    private string? RuntimeDisplayName(string source) => source switch
    {
        "builtin" => null,
        _ => Picker.AllModels.FirstOrDefault(m => m.Provider == source)?.ProviderName ?? source,
    };

    /// <summary>
    /// Host 撤回请求（中止、运行时取消、连接断开）时收起对应的卡片或面板；否则卡片会一直留着，点了也没有回应。
    /// </summary>
    private static void WithdrawOnCancel(CancellationToken ct, Action cancel)
    {
        if (ct.CanBeCanceled)
        {
            ct.Register(() => Avalonia.Threading.Dispatcher.UIThread.Post(cancel));
        }
    }

    /// <summary>用系统默认浏览器打开（elicitation 的 URL 模式，用户已确认）。</summary>
    private bool OpenExternalUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http"))
        {
            return false;
        }
        try
        {
            var start = OperatingSystem.IsMacOS() ? new ProcessStartInfo("open") { ArgumentList = { uri.AbsoluteUri } }
                : OperatingSystem.IsWindows() ? new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }
                : new ProcessStartInfo("xdg-open") { ArgumentList = { uri.AbsoluteUri } };
            using var process = Process.Start(start);
            return process is not null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            _logger.LogWarning(ex, "Opening {Url} failed", uri.Host);
            return false;
        }
    }

    // ---- 权限档位 ----

    /// <summary>项目记住的档位；空状态和新会话沿用，包括“完全授权”。</summary>
    private string RememberedPermission() =>
        Workspace?.Id is { } id && _settings.Current.PermissionModes.TryGetValue(id, out var mode)
            ? PermissionPickerViewModel.Normalize(mode)
            : PermissionPickerViewModel.Ask;

    /// <summary>按项目记住档位，包括“完全授权”（本项目的新会话沿用）。已有会话重新打开时也按文件里的档位恢复。</summary>
    private void RememberPermission(string? project, string? mode)
    {
        if (project is null || mode is null)
        {
            return;
        }
        var remembered = PermissionPickerViewModel.Normalize(mode);
        if (_settings.Current.PermissionModes.TryGetValue(project, out var existing) && existing == remembered)
        {
            return;
        }
        _settings.Current.PermissionModes[project] = remembered;
        _settings.Save();
    }

    /// <summary>会话所属项目的 id（docs/design/projects.md §5.3）；不在项目中工作时为 null。</summary>
    private string? ProjectOf(SessionViewModel session) => _projects.Resolve(session.ProjectId, session.Cwd)?.Id;

    /// <summary>
    /// 用户在选择器中选定档位：按项目记住（包括完全授权），并同步到当前会话；
    /// 放开后顺带处理已在等待的审批。失败时恢复会话的实际档位。
    /// </summary>
    private async Task ApplyPermissionAsync(string mode)
    {
        RememberPermission(Workspace?.Id, mode);
        if (Active is not { } session)
        {
            return; // 空状态：创建会话时随 CreateSessionRequest 发送
        }
        try
        {
            var server = await _host.GetServerAsync();
            session.Options = await server.SetOptionsAsync(session.SessionId, new SessionOptionsDto(PermissionMode: mode));
            if (session.Options.PermissionMode != PermissionPickerViewModel.Ask)
            {
                // Plan 模式下可能修改工作区的命令在任何档位下都要用户确认（docs/design/plan-mode.md §8），不跟着档位自动放行
                foreach (var pending in session.PendingApprovals.Where(a => !a.RequiresUser).ToList())
                {
                    pending.AllowOnceCommand.Execute(null);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Set permission mode failed");
            StatusMessage = Strings.Format(TKCodeLangResourceKind.StatusSendFailedFormat, ex.Message);
        }
        if (session == Active)
        {
            Permission.Show(session.Options.PermissionMode);
        }
    }

    /// <summary>settings.json 变化后 Host 推送的新模型列表：刷新选择器；当前会话的模型被移除时提示。</summary>
    private void OnModelsChanged(ModelsChangedNotification n)
    {
        Picker.Load(n.Models, _userSettings.Current.Model);
        if (Active is { } session)
        {
            Picker.ShowSession(session.ModelId, session.Options);
        }
        else
        {
            ApplyDefaultModel();
        }
        OnPropertyChanged(nameof(HeaderIconKey));
        ModelsChanged?.Invoke(n);
    }

    private void ApplyDefaultModel(bool force = false)
    {
        if (Active is not null)
        {
            return;
        }
        var modelRef = _userSettings.Current.Model;
        if (string.IsNullOrWhiteSpace(modelRef))
        {
            _defaultModelRef = modelRef;
        }
        else if ((force || modelRef != _defaultModelRef) && Picker.SelectByRef(modelRef))
        {
            // 模型可能和厂商配置一起新增；暂时找不到时等 models/changed 后重试。
            _defaultModelRef = modelRef;
        }
    }

    /// <summary>转发 Host 的模型变化（设置页刷新厂商状态用）。</summary>
    public event Action<ModelsChangedNotification>? ModelsChanged;

    private void UpdateContextUsage()
    {
        long budget = Picker.ContextWindowValue;
        var tokens = Active?.LastUsageTokens ?? 0;
        // 警告带：自动压缩阈值之前 20K（按 20K 输出预留估算；Host 用模型的真实 max_tokens，docs/design/compaction.md §4）
        var autoCompactAt = Math.Max(budget - 20_000 - 13_000, (long)((budget - 20_000) * 0.6));
        var nearlyFull = Active?.Runtime is null && tokens >= autoCompactAt - 20_000;
        ContextUsage.Update(Active?.SessionId, tokens, budget, nearlyFull);
    }

    /// <summary>选择器变化时同步到当前会话：换模型走 session/setModel，其余走 session/setOptions；以 Host 返回值为准。</summary>
    private async Task SyncSessionOptionsAsync()
    {
        OnPropertyChanged(nameof(HeaderIconKey));
        UpdateContextUsage();
        if (Active is not { } session || Picker.Selected is not { } selected)
        {
            return;
        }
        try
        {
            var server = await _host.GetServerAsync();
            if (session.ModelId != selected.Model.Ref)
            {
                await server.SetModelAsync(session.SessionId, selected.Model.Ref);
                session.ModelId = selected.Model.Ref;
                // 空会话可以换到其它运行时；以所选模型的运行时为准
                session.Runtime = selected.Model.Runtime;
                if (Sessions.FirstOrDefault(s => s.SessionId == session.SessionId) is { } item)
                {
                    item.SyncFrom(session);
                }
            }
            session.Options = await server.SetOptionsAsync(session.SessionId, Picker.Options);
            Picker.ApplyHostOptions(session.Options);
            UpdateContextUsage();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Sync session options failed");
            // Host 拒绝（例如有历史的会话跨运行时切换）：把选择器恢复到会话当前的模型，避免反复重试
            Picker.ShowSession(session.ModelId, session.Options);
            StatusMessage = Strings.Format(TKCodeLangResourceKind.StatusSendFailedFormat, ex.Message);
        }
    }

    private void RefreshLanguage()
    {
        OnPropertyChanged(nameof(WelcomeQuip));
        OnPropertyChanged(nameof(WorkspaceName));
        OnPropertyChanged(nameof(BranchName));
        OnPropertyChanged(nameof(HeaderTitle));
        OnPropertyChanged(nameof(HeaderSubtitle));
        foreach (var item in Sessions)
        {
            item.Refresh();
        }
        foreach (var session in _open.Values)
        {
            session.RefreshLanguage();
        }
        UpdateContextUsage();
    }
}
