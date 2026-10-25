using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TKCode.Config;
using TKCode.Desktop.Localization;
using TKCode.Desktop.Services;

namespace TKCode.Desktop.ViewModels;

public sealed partial class ProjectItemViewModel(Project? project, string title, string? meta) : ObservableObject
{
    /// <summary>null 表示“不使用项目”。</summary>
    public Project? Project { get; } = project;
    public string Title { get; } = title;
    public string? Meta { get; } = meta;

    [ObservableProperty]
    public partial bool IsSelected { get; set; }
}

/// <summary>
/// 空状态输入框上方的项目选择器：搜索框 + 项目列表 + “新建项目…”（打开创建项目弹窗）+ “不使用项目”。
/// 侧栏的项目列表见 <see cref="SidebarViewModel"/>。
/// </summary>
public sealed partial class ProjectPickerViewModel : ObservableObject
{
    private readonly ObservableCollection<ProjectRowViewModel> _projects;
    private readonly Action<Project?> _onSelect;
    private readonly Action _onNewProject;
    private Project? _selected;

    public ProjectPickerViewModel(ObservableCollection<ProjectRowViewModel> projects, Action<Project?> onSelect, Action onNewProject)
    {
        _projects = projects;
        _onSelect = onSelect;
        _onNewProject = onNewProject;
        _projects.CollectionChanged += (_, _) => Refresh();
        Strings.LanguageChanged += (_, _) => Refresh();
        Refresh();
    }

    public ObservableCollection<ProjectItemViewModel> Items { get; } = [];

    /// <summary>请求关闭弹层（打开状态由视图持有）。</summary>
    public event Action? CloseRequested;

    [ObservableProperty]
    public partial string Search { get; set; } = "";

    /// <summary>触发按钮上的文字：当前选择的项目名，或“无项目”。</summary>
    [ObservableProperty]
    public partial string Title { get; private set; } = "";

    /// <summary>触发按钮的提示：项目的全部文件夹。</summary>
    [ObservableProperty]
    public partial string? Tip { get; private set; }

    partial void OnSearchChanged(string value) => Refresh();

    public void SetSelected(Project? selected)
    {
        _selected = selected is null ? null : _projects.FirstOrDefault(p => p.Id == selected.Id)?.Project ?? selected;
        Title = _selected?.Name ?? Strings.Get(TKCodeLangResourceKind.NoProjectName);
        Tip = _selected is null ? null : string.Join('\n', _selected.Folders);
        foreach (var item in Items)
        {
            item.IsSelected = item.Project?.Id == _selected?.Id;
        }
    }

    [RelayCommand]
    private void Select(ProjectItemViewModel item)
    {
        CloseRequested?.Invoke();
        _onSelect(item.Project);
    }

    [RelayCommand]
    private void NewProject()
    {
        CloseRequested?.Invoke();
        _onNewProject();
    }

    [RelayCommand]
    private void ChooseNoProject()
    {
        CloseRequested?.Invoke();
        _onSelect(null);
    }

    public void OnClosed() => Search = "";

    private void Refresh()
    {
        Items.Clear();
        var meta = "@ " + DeviceInfo.Name;
        foreach (var row in _projects.Where(p => Search.Length == 0 || p.Name.Contains(Search, StringComparison.OrdinalIgnoreCase)))
        {
            Items.Add(new ProjectItemViewModel(row.Project, row.Name, meta));
        }
        SetSelected(_selected);
    }
}
