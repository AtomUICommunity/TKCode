using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TKCode.Config;
using TKCode.Desktop.Localization;
using TKCode.Desktop.Services;

namespace TKCode.Desktop.ViewModels;

/// <summary>侧栏项目列表中的一行（docs/design/projects.md §8.3）。项目表变化时换成新的快照，行对象保持不变。</summary>
public sealed partial class ProjectRowViewModel(Project project) : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Name), nameof(Primary), nameof(IsPinned), nameof(IsMissing), nameof(FoldersTip), nameof(NewSessionTip),
        nameof(PinText), nameof(IsMultiFolder))]
    public partial Project Project { get; set; } = project;

    public string Id => Project.Id;
    public string Name => Project.Name;
    public string Primary => Project.Primary;
    public bool IsPinned => Project.Pinned;
    public bool IsMultiFolder => Project.Folders.Count > 1;

    /// <summary>主文件夹不存在：行上显示警告，不能在其中新建会话。</summary>
    public bool IsMissing => !Directory.Exists(Project.Primary);

    /// <summary>悬停提示：全部文件夹，主文件夹标「主要」，不存在的标「不存在」。</summary>
    public string FoldersTip => string.Join('\n', Project.Folders.Select((folder, i) =>
    {
        var marks = new List<string>();
        if (i == 0)
        {
            marks.Add(Strings.Get(TKCodeLangResourceKind.PrimaryFolder));
        }
        if (!Directory.Exists(folder))
        {
            marks.Add(Strings.Get(TKCodeLangResourceKind.FolderMissing));
        }
        return marks.Count == 0 ? folder : $"{folder}（{string.Join("，", marks)}）";
    }));

    public string NewSessionTip => Strings.Format(TKCodeLangResourceKind.ProjectNewSessionFormat, Name);

    public string PinText => Strings.Get(IsPinned ? TKCodeLangResourceKind.ProjectUnpin : TKCodeLangResourceKind.ProjectPin);

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    /// <summary>项目里有会话正在运行：行右侧显示点阵动画。</summary>
    [ObservableProperty]
    public partial bool IsRunning { get; set; }

    public void RefreshLanguage()
    {
        OnPropertyChanged(nameof(FoldersTip));
        OnPropertyChanged(nameof(NewSessionTip));
        OnPropertyChanged(nameof(PinText));
    }
}

/// <summary>
/// 侧栏（docs/design/projects.md §8）：常驻的项目列表 + 会话列表。点击项目即按它筛选会话，再次点击或清除筛选回到全部会话。
/// 项目来自 <see cref="ProjectService"/>；会话来自 Host 的 session/query 与当前打开的会话。
/// </summary>
public sealed partial class SidebarViewModel : ObservableObject
{
    /// <summary>项目超过这个数目时折叠，显示「展开更多」。</summary>
    public const int CollapsedProjectCount = 8;

    private readonly ProjectService _projects;
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(30) };

    public SidebarViewModel(ProjectService projects, ConversationViewModel conversation, StateService settings)
    {
        _projects = projects;
        Conversation = conversation;
        SyncProjects();
        WorkspacePicker = new ProjectPickerViewModel(Projects, conversation.SetWorkspace, () => CreateProjectRequested?.Invoke());

        projects.Changed += OnProjectsChanged;
        conversation.RunningChanged += SyncRunning;
        conversation.Sessions.CollectionChanged += (_, _) => RefreshVisibleSessions();
        conversation.WorkspaceChanged += () => WorkspacePicker.SetSelected(conversation.Workspace);
        Strings.LanguageChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(SessionsTitle));
            OnPropertyChanged(nameof(MoreProjectsText));
            foreach (var row in Projects)
            {
                row.RefreshLanguage();
            }
        };
        _clock.Tick += (_, _) =>
        {
            foreach (var item in Conversation.Sessions)
            {
                item.Refresh();
            }
        };
        _clock.Start();

        var last = projects.Find(settings.Current.LastProjectId);
        conversation.SetWorkspace(last ?? projects.Ordered.FirstOrDefault());
        if (last is not null)
        {
            SetFilter(last);
        }
    }

    public ConversationViewModel Conversation { get; }

    /// <summary>全部项目，按侧栏顺序（置顶的在前）。</summary>
    public ObservableCollection<ProjectRowViewModel> Projects { get; } = [];

    /// <summary>侧栏实际显示的项目：折叠时只显示前 <see cref="CollapsedProjectCount"/> 个，选中的项目始终显示。</summary>
    public ObservableCollection<ProjectRowViewModel> VisibleProjects { get; } = [];

    /// <summary>空状态输入框上方的项目选择器（新会话在哪个项目中开始）。</summary>
    public ProjectPickerViewModel WorkspacePicker { get; }

    /// <summary>当前筛选下的会话。</summary>
    public ObservableCollection<SessionItemViewModel> VisibleSessions { get; } = [];

    public bool HasNoSessions => VisibleSessions.Count == 0;
    public bool HasNoProjects => Projects.Count == 0;

    public string UserName { get; } = Environment.UserName;
    public string UserInitial => string.IsNullOrEmpty(UserName) ? "?" : UserName[..1].ToUpperInvariant();

    /// <summary>侧栏筛选的项目；null 表示全部会话。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SessionsTitle), nameof(IsFiltered), nameof(ShowsProjectBadges))]
    public partial Project? Filter { get; private set; }

    /// <summary>筛选为「无项目」：只列出不属于任何项目的会话（移除项目后落在这里）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SessionsTitle), nameof(IsFiltered), nameof(ShowsProjectBadges))]
    public partial bool IsNoProjectFilter { get; private set; }

    public bool IsFiltered => Filter is not null || IsNoProjectFilter;

    /// <summary>会话行的项目徽标只在「全部会话」里显示，筛选中多余（§8.2）。</summary>
    public bool ShowsProjectBadges => !IsFiltered;

    /// <summary>会话区的标题：「会话」，筛选中为「会话 · 项目名」，无项目为「会话 · 无项目」。</summary>
    public string SessionsTitle => Filter is { } project
        ? Strings.Format(TKCodeLangResourceKind.SessionsInProjectFormat, project.Name)
        : IsNoProjectFilter
            ? Strings.Get(TKCodeLangResourceKind.SessionsNoProject)
            : Strings.Get(TKCodeLangResourceKind.Sessions);

    [ObservableProperty]
    public partial bool IsSessionsExpanded { get; set; } = true;

    [ObservableProperty]
    public partial bool IsProjectsExpanded { get; set; } = true;

    /// <summary>项目超过 <see cref="CollapsedProjectCount"/> 个时，是否显示全部。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MoreProjectsText))]
    public partial bool ShowAllProjects { get; set; }

    public bool HasMoreProjects => Projects.Count > CollapsedProjectCount;

    public string MoreProjectsText => ShowAllProjects
        ? Strings.Get(TKCodeLangResourceKind.Collapse)
        : Strings.Format(TKCodeLangResourceKind.ShowMoreProjectsFormat, Projects.Count - CollapsedProjectCount);

    /// <summary>「新建项目」：由主窗口打开创建项目弹窗。</summary>
    public event Action? CreateProjectRequested;

    /// <summary>「编辑项目…」：由主窗口打开编辑弹窗。</summary>
    public event Action<Project>? EditProjectRequested;

    /// <summary>「移除项目…」：由主窗口确认后调用 <see cref="RemoveProject"/>。</summary>
    public event Action<Project>? RemoveProjectRequested;

    /// <summary>「在 Finder 中显示」：由主窗口调用平台操作。</summary>
    public event Action<Project>? RevealProjectRequested;

    /// <summary>「复制路径」：由主窗口写入剪贴板。</summary>
    public event Action<string>? CopyPathRequested;

    partial void OnShowAllProjectsChanged(bool value) => SyncVisibleProjects();

    [RelayCommand]
    private void ToggleSessions() => IsSessionsExpanded = !IsSessionsExpanded;

    [RelayCommand]
    private void ToggleProjects() => IsProjectsExpanded = !IsProjectsExpanded;

    [RelayCommand]
    private void ToggleShowAllProjects() => ShowAllProjects = !ShowAllProjects;

    [RelayCommand]
    private void CreateProject() => CreateProjectRequested?.Invoke();

    /// <summary>点击项目行：选中并按它筛选；已选中时取消筛选。</summary>
    [RelayCommand]
    private void SelectProject(ProjectRowViewModel row) => SetFilter(Filter?.Id == row.Id ? null : row.Project);

    [RelayCommand]
    private void ClearFilter() => SetFilter(null);

    /// <summary>项目行的「⊕」：选中这个项目，并在其中新建会话。</summary>
    [RelayCommand]
    private void NewSessionInProject(ProjectRowViewModel row)
    {
        if (Filter?.Id != row.Id)
        {
            SetFilter(row.Project);
        }
        Conversation.SetWorkspace(row.Project);
        Conversation.NewChat();
    }

    [RelayCommand]
    private void EditProject(ProjectRowViewModel row) => EditProjectRequested?.Invoke(row.Project);

    [RelayCommand]
    private void RequestRemoveProject(ProjectRowViewModel row) => RemoveProjectRequested?.Invoke(row.Project);

    [RelayCommand]
    private void RevealProject(ProjectRowViewModel row) => RevealProjectRequested?.Invoke(row.Project);

    [RelayCommand]
    private void CopyProjectPath(ProjectRowViewModel row) => CopyPathRequested?.Invoke(row.Primary);

    [RelayCommand]
    private void TogglePin(ProjectRowViewModel row) => _projects.SetPinned(row.Id, !row.IsPinned);

    [RelayCommand]
    private void NewSession()
    {
        if (Filter is not null && Filter.Id != Conversation.Workspace?.Id)
        {
            Conversation.SetWorkspace(Filter);
        }
        Conversation.NewChat();
    }

    /// <summary>创建项目弹窗提交：加入项目表，选中它，并设为新会话的项目。</summary>
    public Project CreateProject(string? name, IReadOnlyList<string> folders)
    {
        var project = _projects.Add(name, folders);
        SetFilter(project);
        return project;
    }

    /// <summary>编辑项目弹窗提交（名称与文件夹）。</summary>
    public Project? UpdateProject(Project project, string? name, IReadOnlyList<string> folders) => _projects.Update(project.With(name, folders));

    /// <summary>移除项目（已确认）：只从项目表移除，文件与会话都保留（会话变为不属于任何项目，§9.3）。</summary>
    public void RemoveProject(Project project)
    {
        var wasFiltered = Filter?.Id == project.Id;
        if (Conversation.Workspace?.Id == project.Id)
        {
            Conversation.SetWorkspace(null);
        }
        // 先移除再换筛选：OnProjectsChanged 看到被筛选的项目消失时会回退到全部会话，这里要的是「无项目」。
        _projects.Remove(project.Id);
        if (wasFiltered)
        {
            SetFilter(null, noProject: true);
        }
    }

    /// <summary>
    /// 托盘 / Dock / 跳转列表里的项目条目：和在侧栏里选中该项目一样（筛选会话、新会话默认在该项目中）。
    /// 没有包含这个文件夹的项目时新建一个；目录已不存在则忽略。
    /// </summary>
    public void OpenProject(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }
        SetFilter(_projects.AddFolder(path));
    }

    private void SetFilter(Project? project, bool noProject = false)
    {
        var changed = Filter?.Id != project?.Id || IsNoProjectFilter != noProject;
        Filter = project;
        IsNoProjectFilter = noProject;
        foreach (var row in Projects)
        {
            row.IsSelected = row.Id == project?.Id;
        }
        SyncVisibleProjects();
        // 选中某个项目时，新会话也默认在该项目中创建。
        if (project is not null)
        {
            Conversation.SetWorkspace(project);
        }
        if (changed)
        {
            // 换数据源：整体重建（清空后逐项添加），列表回到顶部且不播放入场动画。
            VisibleSessions.Clear();
            OnPropertyChanged(nameof(HasNoSessions));
        }
        // 按项目在 Host 端分页查询（只加载该项目的会话）
        _ = Conversation.SetSessionFilterAsync(noProject ? ProjectCatalog.NoProjectFilter : project?.Id);
    }

    private void OnProjectsChanged()
    {
        SyncProjects();
        // 筛选的项目被修改（重命名、文件夹变化）时换成新快照；被移除时改为「无项目」筛选
        if (Filter is { } filter)
        {
            var updated = _projects.Find(filter.Id);
            if (updated is null)
            {
                SetFilter(null, noProject: true);
            }
            else if (!ReferenceEquals(updated, filter))
            {
                Filter = updated;
            }
        }
        WorkspacePicker.SetSelected(Conversation.Workspace);
    }

    /// <summary>按 <see cref="ProjectService.Ordered"/> 增量同步行对象（保留选中状态与已创建的行）。</summary>
    private void SyncProjects()
    {
        var ordered = _projects.Ordered;
        var rows = Projects.ToDictionary(r => r.Id);
        for (var i = 0; i < ordered.Count; i++)
        {
            var project = ordered[i];
            if (rows.TryGetValue(project.Id, out var row))
            {
                row.Project = project;
                var index = Projects.IndexOf(row);
                if (index != i)
                {
                    Projects.Move(index, i);
                }
            }
            else
            {
                Projects.Insert(i, new ProjectRowViewModel(project) { IsSelected = project.Id == Filter?.Id });
            }
        }
        while (Projects.Count > ordered.Count)
        {
            Projects.RemoveAt(Projects.Count - 1);
        }
        OnPropertyChanged(nameof(HasNoProjects));
        OnPropertyChanged(nameof(HasMoreProjects));
        OnPropertyChanged(nameof(MoreProjectsText));
        SyncVisibleProjects();
        SyncRunning();
    }

    private void SyncRunning()
    {
        foreach (var row in Projects)
        {
            row.IsRunning = Conversation.IsProjectRunning(row.Id);
        }
    }

    private void SyncVisibleProjects()
    {
        var wanted = ShowAllProjects || Projects.Count <= CollapsedProjectCount
            ? Projects.ToList()
            : Projects.Take(CollapsedProjectCount).Concat(Projects.Skip(CollapsedProjectCount).Where(r => r.IsSelected)).ToList();
        SyncList(VisibleProjects, wanted);
    }

    private void RefreshVisibleSessions()
    {
        // 增量同步而不是清空重建：分页追加、置顶排序时保留滚动位置与已创建的行。
        SyncList(VisibleSessions, Conversation.Sessions.Where(s => IsNoProjectFilter ? s.ProjectId is null : Filter is null || s.ProjectId == Filter.Id).ToList());
        OnPropertyChanged(nameof(HasNoSessions));
    }

    private static void SyncList<T>(ObservableCollection<T> target, IReadOnlyList<T> wanted) where T : class
    {
        var keep = wanted.ToHashSet();
        for (var i = target.Count - 1; i >= 0; i--)
        {
            if (!keep.Contains(target[i]))
            {
                target.RemoveAt(i);
            }
        }
        for (var i = 0; i < wanted.Count; i++)
        {
            var index = target.IndexOf(wanted[i]);
            if (index < 0)
            {
                target.Insert(i, wanted[i]);
            }
            else if (index != i)
            {
                target.Move(index, i);
            }
        }
    }
}
