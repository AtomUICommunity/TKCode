using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TKCode.Desktop.Localization;

namespace TKCode.Desktop.ViewModels;

public sealed partial class BranchItemViewModel(string name, bool isCurrent) : ObservableObject
{
    public string Name { get; } = name;
    public bool IsCurrent { get; } = isCurrent;
}

/// <summary>多文件夹项目中的一个 Git 仓库（docs/design/projects.md §10.3）。</summary>
public sealed partial class BranchRepositoryViewModel(string path, string name, string? branch) : ObservableObject
{
    public string Path { get; } = path;
    public string Name { get; } = name;

    [ObservableProperty]
    public partial string? Branch { get; set; } = branch;

    [ObservableProperty]
    public partial bool IsSelected { get; set; }
}

/// <summary>
/// 分支弹层：搜索本地分支，标出当前分支；选择其它分支时先在弹层内确认，再执行 git switch。
/// Agent 在该项目中运行时不允许切换。项目里有多个 Git 仓库时，弹层顶部先选仓库（docs/design/projects.md §10.3）；
/// 触发按钮显示主文件夹的分支，后面淡色的「+N」是其他仓库的数目。
/// </summary>
public sealed partial class BranchPickerViewModel : ObservableObject
{
    private readonly Func<string, Task<string?>> _switch;
    private readonly Func<bool> _isBusy;
    private readonly Action _reload;
    private IReadOnlyList<string> _all = [];

    /// <param name="reload">重新读取当前分支与分支列表并调用 <see cref="Load"/>（弹层打开时用，终端里新建 / 切换的分支才能出现）。</param>
    public BranchPickerViewModel(Func<string, Task<string?>> switchBranch, Func<bool> isBusy, Action reload)
    {
        _switch = switchBranch;
        _isBusy = isBusy;
        _reload = reload;
        Strings.LanguageChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(DisplayName));
            OnPropertyChanged(nameof(ConfirmText));
        };
    }

    public ObservableCollection<BranchItemViewModel> Items { get; } = [];

    /// <summary>项目里的 Git 仓库，主文件夹的在前；只有一个时不显示仓库列表。</summary>
    public ObservableCollection<BranchRepositoryViewModel> Repositories { get; } = [];

    public bool HasMultipleRepositories => Repositories.Count > 1;

    /// <summary>触发按钮上主文件夹分支后面的「+N」。</summary>
    public string? MoreText => Repositories.Count > 1 ? $"+{Repositories.Count - 1}" : null;

    /// <summary>弹层当前列出分支的仓库；null 表示主文件夹的仓库。</summary>
    [ObservableProperty]
    public partial string? SelectedRepository { get; private set; }

    public event Action? CloseRequested;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayName))]
    public partial string? Current { get; private set; }

    /// <summary>触发按钮上的文字：主文件夹的当前分支，不是 Git 仓库时为“无分支”。</summary>
    public string DisplayName => (Repositories.Count > 1 ? Repositories[0].Branch : Current) ?? Strings.Get(TKCodeLangResourceKind.NoBranch);

    /// <summary>
    /// 设置项目里的仓库（路径、名称与当前分支）。原来选中的仓库仍在时保持选中，否则回到第一个（主文件夹）。
    /// </summary>
    public void SetRepositories(IReadOnlyList<(string Path, string Name, string? Branch)> repositories)
    {
        var selected = SelectedRepository;
        if (!repositories.Select(r => r.Path).SequenceEqual(Repositories.Select(r => r.Path)))
        {
            Repositories.Clear();
            foreach (var (path, name, branch) in repositories)
            {
                Repositories.Add(new BranchRepositoryViewModel(path, name, branch));
            }
        }
        else
        {
            for (var i = 0; i < repositories.Count; i++)
            {
                Repositories[i].Branch = repositories[i].Branch;
            }
        }
        SelectedRepository = Repositories.Any(r => r.Path == selected) ? selected : Repositories.FirstOrDefault()?.Path;
        foreach (var repository in Repositories)
        {
            repository.IsSelected = repository.Path == SelectedRepository;
        }
        OnPropertyChanged(nameof(HasMultipleRepositories));
        OnPropertyChanged(nameof(MoreText));
        OnPropertyChanged(nameof(DisplayName));
    }

    /// <summary>弹层顶部选一个仓库：列出它的分支。</summary>
    [RelayCommand]
    private void SelectRepository(BranchRepositoryViewModel repository)
    {
        if (repository.Path == SelectedRepository)
        {
            return;
        }
        SelectedRepository = repository.Path;
        foreach (var item in Repositories)
        {
            item.IsSelected = item.Path == repository.Path;
        }
        Pending = null;
        Error = null;
        _reload();
    }

    [ObservableProperty]
    public partial string Search { get; set; } = "";

    /// <summary>等待确认的目标分支。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ConfirmText), nameof(IsConfirming))]
    public partial string? Pending { get; private set; }

    public bool IsConfirming => Pending is not null;
    public string ConfirmText => Pending is null ? "" : Strings.Format(TKCodeLangResourceKind.SwitchBranchConfirmFormat, Pending);

    [ObservableProperty]
    public partial string? Error { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConfirmCommand))]
    public partial bool IsSwitching { get; private set; }

    partial void OnSearchChanged(string value) => Refresh();

    public void Load(string? current, IReadOnlyList<string> branches)
    {
        IReadOnlyList<string> all = branches.Count > 0 || current is null ? branches : [current];
        if (current == Current && all.SequenceEqual(_all))
        {
            return;
        }
        Current = current;
        _all = all;
        Refresh();
    }

    /// <summary>弹层打开：分支可能已在应用外被切换 / 新建，先重新读取。</summary>
    public void OnOpened() => _reload();

    [RelayCommand]
    private void Select(BranchItemViewModel item)
    {
        Error = null;
        if (item.IsCurrent)
        {
            CloseRequested?.Invoke();
            return;
        }
        if (_isBusy())
        {
            Error = Strings.Get(TKCodeLangResourceKind.BranchBusy);
            return;
        }
        Pending = item.Name;
    }

    [RelayCommand]
    private void Cancel()
    {
        Pending = null;
        Error = null;
    }

    private bool CanConfirm() => !IsSwitching;

    [RelayCommand(CanExecute = nameof(CanConfirm))]
    private async Task ConfirmAsync()
    {
        if (Pending is not { } target)
        {
            return;
        }
        IsSwitching = true;
        try
        {
            var error = await _switch(target);
            if (error is null)
            {
                Pending = null;
                CloseRequested?.Invoke();
            }
            else
            {
                Error = Strings.Format(TKCodeLangResourceKind.SwitchBranchFailedFormat, error);
            }
        }
        finally
        {
            IsSwitching = false;
        }
    }

    public void OnClosed()
    {
        Search = "";
        Pending = null;
        Error = null;
    }

    private void Refresh()
    {
        Items.Clear();
        foreach (var name in _all.Where(b => Search.Length == 0 || b.Contains(Search, StringComparison.OrdinalIgnoreCase))
                     .OrderByDescending(b => b == Current))
        {
            Items.Add(new BranchItemViewModel(name, name == Current));
        }
    }
}
