using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TKCode.Config;
using TKCode.Desktop.Icons;
using TKCode.Desktop.Localization;
using TKCode.Desktop.Services;

namespace TKCode.Desktop.ViewModels;

/// <summary>历史面板顶部仓库选择器的一项（<c>arc:Select</c> 通过 <see cref="ToString"/> 显示名称）。</summary>
public sealed record RepositoryItemViewModel(string Path, string Name)
{
    public override string ToString() => Name;
}

/// <summary>
/// 提交列表中的一条：元数据来自 <c>git log</c>，正文与文件列表在第一次展开时才读取。
/// 同一时间只展开一条（手风琴），点击由 <see cref="HistoryTabViewModel"/> 处理。
/// </summary>
public sealed partial class GitCommitViewModel : ObservableObject
{
    private readonly Action<GitCommitViewModel> _toggle;

    public GitCommitViewModel(GitCommitInfo info, Action<GitCommitViewModel> toggle)
    {
        _toggle = toggle;
        Sha = info.Sha;
        ShortSha = info.ShortSha;
        Subject = info.Subject;
        Author = info.Author;
        AuthoredAt = info.AuthoredAt;
        IsMerge = info.Parents.Count > 1;
    }

    public string Sha { get; }
    public string ShortSha { get; }
    public string Subject { get; }
    public string Author { get; }
    public DateTimeOffset AuthoredAt { get; }

    /// <summary>合并提交（相对第一个父提交显示改动，见历史面板设计稿）。</summary>
    public bool IsMerge { get; }

    /// <summary>本地时间：当年只显示月日与时分，往年显示日期（与侧栏会话的时间一致）。</summary>
    public string DateText
    {
        get
        {
            var local = AuthoredAt.LocalDateTime;
            return local.Year == DateTime.Now.Year
                ? local.ToString("MM-dd HH:mm", CultureInfo.CurrentCulture)
                : local.ToString("yyyy-MM-dd", CultureInfo.CurrentCulture);
        }
    }

    [ObservableProperty]
    public partial bool IsExpanded { get; set; }

    /// <summary>正在读取提交正文与文件列表。</summary>
    [ObservableProperty]
    public partial bool IsLoading { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? Error { get; private set; }

    public bool HasError => Error is not null;

    /// <summary>完整提交正文（第一次展开后才有）。</summary>
    [ObservableProperty]
    public partial string Message { get; private set; } = "";

    public ObservableCollection<GitFileViewModel> Files { get; } = [];

    public bool HasFiles => Files.Count > 0;

    /// <summary>正文与文件列表已经取过：折叠后再展开不重复请求。</summary>
    public bool IsDetailLoaded { get; private set; }

    [RelayCommand]
    private void ToggleExpanded() => _toggle(this);

    internal void BeginLoad()
    {
        IsLoading = true;
        Error = null;
    }

    internal void EndLoad() => IsLoading = false;

    internal void FailLoad(string? error)
    {
        IsLoading = false;
        Error = error;
    }

    internal void SetDetail(string message, IReadOnlyList<GitFileChange> files, Func<GitFileViewModel, Task> loadFile)
    {
        Message = message;
        Files.Clear();
        foreach (var file in files)
        {
            Files.Add(new GitFileViewModel(file, loadFile));
        }
        OnPropertyChanged(nameof(HasFiles));
        IsDetailLoaded = true;
        IsLoading = false;
        Error = null;
    }
}

/// <summary>
/// 提交中的一个文件：增删行数来自 numstat；第一次展开时才取该文件的 patch（二进制文件不取），
/// 结果缓存后折叠再展开不重复请求。
/// </summary>
public sealed partial class GitFileViewModel : ObservableObject
{
    private readonly Func<GitFileViewModel, Task> _load;
    private bool _loaded;

    public GitFileViewModel(GitFileChange change, Func<GitFileViewModel, Task> load)
    {
        Change = change;
        _load = load;
        (Icon, IconColor) = FileIcons.For(Name, false);
    }

    public GitFileChange Change { get; }
    public string Path => Change.Path;
    public string Name => System.IO.Path.GetFileName(Path);
    public string Directory => System.IO.Path.GetDirectoryName(Path) ?? "";

    /// <summary>文件类型图标与调色板颜色键（与文件树 / 变更面板一致）。</summary>
    public LucideKind Icon { get; }
    public string IconColor { get; }

    /// <summary>重命名前的路径；不是重命名时为 null。</summary>
    public string? OldPath => Change.OldPath;

    /// <summary>二进制 / 子模块（numstat 为 <c>-</c>）：没有增删行数，也不取 patch。</summary>
    public bool IsBinary => Change.IsBinary;

    public bool IsRename => Change.IsRename;

    /// <summary>“+N”；二进制 / 子模块为空串。</summary>
    public string AddedText => Change.Added is { } added ? $"+{added}" : "";

    /// <summary>“−N”；二进制 / 子模块为空串。</summary>
    public string RemovedText => Change.Removed is { } removed ? $"−{removed}" : "";

    [ObservableProperty]
    public partial bool IsExpanded { get; set; }

    [ObservableProperty]
    public partial bool IsLoading { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? Error { get; private set; }

    public bool HasError => Error is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLines))]
    public partial IReadOnlyList<DiffLineViewModel> Lines { get; private set; } = [];

    public bool HasLines => Lines.Count > 0;

    /// <summary>只是重命名、没有内容改动（<see cref="Lines"/> 为空）。</summary>
    [ObservableProperty]
    public partial bool IsRenameOnly { get; private set; }

    /// <summary>改动超过解析器上限，只显示开头。</summary>
    [ObservableProperty]
    public partial bool IsDiffTruncated { get; private set; }

    [RelayCommand]
    private async Task ToggleExpandedAsync()
    {
        IsExpanded = !IsExpanded;
        if (!IsExpanded || _loaded || IsLoading || IsBinary)
        {
            return;
        }
        IsLoading = true;
        Error = null;
        try
        {
            await _load(this);
            _loaded = true;
        }
        catch (OperationCanceledException)
        {
            // 取消（切换仓库 / 刷新）后保持未加载，下次展开时重试
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            _loaded = true;
        }
        finally
        {
            IsLoading = false;
        }
    }

    internal void SetDiff(GitFileDiff diff)
    {
        Lines = diff.Lines;
        IsRenameOnly = diff.IsRenameOnly;
        IsDiffTruncated = diff.Truncated;
        Error = null;
    }

    internal void Fail(string? error) => Error = error;
}

/// <summary>
/// 工作台的「历史」标签页：当前分支的提交列表（两阶段加载，先元数据，展开时再取正文 / 文件 / 单个文件的 patch）。
/// 标签激活、刷新按钮与 <see cref="GitBranchWatcher"/>（选中仓库的 refs 变化）都会重新加载；
/// 同一时间只展开一条提交。
/// </summary>
public sealed partial class HistoryTabViewModel : WorkbenchTabViewModel
{
    /// <summary>每页显示的提交数；向 git 多要一条只用来判断是否还有下一页。</summary>
    private const int PageSize = 50;

    private readonly IGitHistory _history;
    private readonly Action<Action> _post;
    private CancellationTokenSource? _loadCts;
    private CancellationTokenSource? _detailCts;
    private GitBranchWatcher? _watcher;
    private int _generation;
    private int _watcherGeneration;
    private bool _settingRepositories;

    /// <param name="history">数据来源；为 null 时调用本机 git。</param>
    /// <param name="post">把 watcher 的回调派发到 UI 线程；测试里直接执行。</param>
    public HistoryTabViewModel(IGitHistory? history = null, Action<Action>? post = null) : base(WorkbenchTabKind.History)
    {
        _history = history ?? new GitHistoryService();
        _post = post ?? (action => Dispatcher.UIThread.Post(action));
        Commits.CollectionChanged += (_, _) => OnPropertyChanged(nameof(IsEmpty));
    }

    public override string Title => WorkbenchTexts.Title(Kind);

    /// <summary>项目里的 Git 仓库（主文件夹的在前）；只有一个时不显示仓库选择器。</summary>
    public ObservableCollection<RepositoryItemViewModel> Repositories { get; } = [];

    public ObservableCollection<GitCommitViewModel> Commits { get; } = [];

    /// <summary>当前列出提交的仓库；null 表示工作区里没有 Git 仓库。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRepository), nameof(HasNoRepository), nameof(IsEmpty), nameof(HasMultipleRepositories))]
    public partial RepositoryItemViewModel? SelectedRepository { get; set; }

    public bool HasRepository => SelectedRepository is not null;
    public bool HasNoRepository => SelectedRepository is null;
    public bool HasMultipleRepositories => Repositories.Count > 1;

    /// <summary>选中仓库的当前分支（detached 时为短哈希；读不到分支名时为“无分支”）。</summary>
    [ObservableProperty]
    public partial string BranchText { get; private set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    public partial bool IsLoading { get; private set; }

    [ObservableProperty]
    public partial bool IsLoadingMore { get; private set; }

    /// <summary>还有更早的提交（下一页）。</summary>
    [ObservableProperty]
    public partial bool HasMore { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty), nameof(HasError))]
    public partial string? Error { get; private set; }

    public bool HasError => Error is not null;

    /// <summary>是仓库、读完了，但没有提交（空分支 / unborn HEAD）。</summary>
    public bool IsEmpty => HasRepository && Commits.Count == 0 && !IsLoading && Error is null;

    /// <summary>标签不可见时数据可能已过期（watcher / 仓库列表变化）：下次激活时重新加载。</summary>
    [ObservableProperty]
    public partial bool IsStale { get; private set; } = true;

    /// <summary>复制文本（写剪贴板），由主窗口提供；为 null 时复制命令什么也不做。</summary>
    public Func<string, Task>? CopyTextAsync { get; set; }

    /// <summary>选中仓库变了：更新分支文字与 watcher；路径真的变了才重新加载（arc:Select 会重设选中项）。</summary>
    partial void OnSelectedRepositoryChanged(RepositoryItemViewModel? oldValue, RepositoryItemViewModel? newValue)
    {
        var changed = !SamePath(oldValue?.Path, newValue?.Path);
        UpdateBranchText();
        if (changed)
        {
            RebuildWatcher(newValue?.Path);
        }
        if (_settingRepositories || !changed)
        {
            return;
        }
        ReloadOrMarkStale();
    }

    protected override void OnActivated()
    {
        // 关闭标签时 watcher 被释放：再次打开时重建（否则之后在终端里提交就不会自动刷新）
        if (_watcher is null)
        {
            RebuildWatcher(SelectedRepository?.Path);
        }
        ReloadOrMarkStale();
    }

    /// <summary>
    /// 设置项目里的仓库（路径与显示名）。路径列表不变时只刷新分支文字；否则保留仍存在的选中仓库，
    /// 不再存在时选第一个。标签可见时重新加载，不可见时只标记过期。
    /// </summary>
    public void SetRepositories(IReadOnlyList<(string Path, string Name)> repositories)
    {
        var samePaths = repositories.Count == Repositories.Count
            && repositories.Select(r => r.Path).SequenceEqual(Repositories.Select(r => r.Path), PathRules.Comparer);
        if (samePaths)
        {
            UpdateBranchText();
            return;
        }

        var selected = SelectedRepository?.Path;
        _settingRepositories = true;
        try
        {
            Repositories.Clear();
            foreach (var (path, name) in repositories)
            {
                Repositories.Add(new RepositoryItemViewModel(path, name));
            }
            SelectedRepository = selected is null
                ? Repositories.FirstOrDefault()
                : Repositories.FirstOrDefault(r => PathRules.SamePath(r.Path, selected)) ?? Repositories.FirstOrDefault();
        }
        finally
        {
            _settingRepositories = false;
        }
        // 仓库 record 按值相等：选中项与之前完全相同时不会触发 OnSelectedRepositoryChanged，这里显式刷新
        UpdateBranchText();
        NotifyState();
        ReloadOrMarkStale();
    }

    /// <summary>重新加载第一页（保留仍展开的提交）；切换仓库 / 刷新 / 激活都经过这里。</summary>
    public async Task LoadAsync()
    {
        UpdateBranchText();
        _detailCts?.Cancel();
        _detailCts?.Dispose();
        _detailCts = null;

        _loadCts?.Cancel();
        _loadCts?.Dispose();
        var cts = new CancellationTokenSource();
        _loadCts = cts;
        var generation = ++_generation;
        var expanded = Commits.FirstOrDefault(c => c.IsExpanded)?.Sha;

        IsStale = false;
        Error = null;
        IsLoadingMore = false;

        if (SelectedRepository is not { } repository)
        {
            IsLoading = false;
            HasMore = false;
            Commits.Clear();
            NotifyState();
            return;
        }

        IsLoading = true;
        try
        {
            var result = await _history.LogAsync(repository.Path, 0, PageSize + 1, cts.Token);
            if (generation != _generation)
            {
                return;
            }
            if (result.Value is { } commits)
            {
                ApplyPage(commits, append: false);
                RestoreExpanded(expanded);
            }
            else
            {
                Commits.Clear();
                HasMore = false;
                Error = result.Error;
            }
        }
        catch (OperationCanceledException)
        {
            // 被更新的一轮取代（切换仓库 / 刷新 / 关闭）
        }
        catch (Exception ex)
        {
            if (generation == _generation)
            {
                Error = ex.Message;
            }
        }
        finally
        {
            if (generation == _generation)
            {
                IsLoading = false;
                NotifyState();
            }
        }
    }

    /// <summary>刷新：重新读取当前分支并加载第一页（保留仍展开的提交）。</summary>
    public Task RefreshAsync() => LoadAsync();

    /// <summary>下一页：从已显示的条数继续，多取一条判断是否还有更多。</summary>
    [RelayCommand]
    private async Task LoadMoreAsync()
    {
        if (IsLoading || IsLoadingMore || !HasMore || SelectedRepository is not { } repository)
        {
            return;
        }
        var generation = _generation;
        IsLoadingMore = true;
        try
        {
            var result = await _history.LogAsync(repository.Path, Commits.Count, PageSize + 1, _loadCts?.Token ?? CancellationToken.None);
            if (generation != _generation)
            {
                return;
            }
            if (result.Value is { } commits)
            {
                ApplyPage(commits, append: true);
                Error = null;
            }
            else if (result.Error is { } error)
            {
                Error = error;
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (generation == _generation)
            {
                Error = ex.Message;
            }
        }
        finally
        {
            IsLoadingMore = false;
        }
    }

    /// <summary>刷新按钮：仓库与分支可能已在应用外变化。</summary>
    [RelayCommand]
    private Task Refresh() => RefreshAsync();

    /// <summary>复制完整哈希；剪贴板不可用时静默失败。</summary>
    [RelayCommand]
    private async Task CopyHash(string sha)
    {
        if (CopyTextAsync is not { } copy)
        {
            return;
        }
        try
        {
            await copy(sha);
        }
        catch (Exception)
        {
            // 剪贴板被占用 / 不可用：复制失败不值得打断阅读
        }
    }

    /// <summary>点击一条提交：已展开的折叠，否则折叠其他所有提交并展开这一条（手风琴），首次展开时取详情。</summary>
    public void ToggleCommit(GitCommitViewModel commit)
    {
        if (commit.IsExpanded)
        {
            commit.IsExpanded = false;
            return;
        }
        foreach (var other in Commits)
        {
            if (!ReferenceEquals(other, commit))
            {
                other.IsExpanded = false;
            }
        }
        commit.IsExpanded = true;
        if (!commit.IsDetailLoaded)
        {
            _ = LoadDetailAsync(commit);
        }
    }

    /// <summary>标签关闭：取消加载、释放 watcher、把 IsActive 置回 false（否则再次打开时不会触发 OnActivated）。</summary>
    public void OnClosed()
    {
        _watcher?.Dispose();
        _watcher = null;
        _watcherGeneration++;
        _loadCts?.Cancel();
        _loadCts?.Dispose();
        _loadCts = null;
        _detailCts?.Cancel();
        _detailCts?.Dispose();
        _detailCts = null;
        _generation++;
        IsLoading = false;
        IsLoadingMore = false;
        IsStale = true;
        IsActive = false;
    }

    private async Task LoadDetailAsync(GitCommitViewModel commit)
    {
        if (SelectedRepository is not { } repository || commit.IsDetailLoaded || commit.IsLoading)
        {
            return;
        }
        _detailCts?.Cancel();
        _detailCts?.Dispose();
        var cts = new CancellationTokenSource();
        _detailCts = cts;
        commit.BeginLoad();
        try
        {
            var result = await _history.ShowAsync(repository.Path, commit.Sha, cts.Token);
            if (!ReferenceEquals(_detailCts, cts))
            {
                return;
            }
            if (result.Value is { } detail)
            {
                var sha = detail.Sha.Length > 0 ? detail.Sha : commit.Sha;
                commit.SetDetail(detail.Message, detail.Files, file => LoadFileAsync(repository.Path, sha, file));
            }
            else
            {
                commit.FailLoad(result.Error);
            }
        }
        catch (OperationCanceledException)
        {
            // 另一条提交被展开：这一条的文字留在加载前的状态
        }
        catch (Exception ex)
        {
            if (ReferenceEquals(_detailCts, cts))
            {
                commit.FailLoad(ex.Message);
            }
        }
        finally
        {
            // 被后一次展开取代时也要收尾，否则这条提交再次展开会一直停在加载中
            commit.EndLoad();
            if (ReferenceEquals(_detailCts, cts))
            {
                _detailCts = null;
            }
            cts.Dispose();
        }
    }

    private async Task LoadFileAsync(string repository, string sha, GitFileViewModel file)
    {
        var result = await _history.FileDiffAsync(repository, sha, file.Change, _loadCts?.Token ?? CancellationToken.None);
        if (result.Value is { } diff)
        {
            file.SetDiff(diff);
        }
        else
        {
            file.Fail(result.Error);
        }
    }

    private void ApplyPage(IReadOnlyList<GitCommitInfo> commits, bool append)
    {
        if (!append)
        {
            Commits.Clear();
        }
        foreach (var info in commits.Take(PageSize))
        {
            Commits.Add(new GitCommitViewModel(info, ToggleCommit));
        }
        HasMore = commits.Count > PageSize;
    }

    /// <summary>刷新后重新展开刷新前展开的那条提交（还在第一页里时），并重新读取它的详情。</summary>
    private void RestoreExpanded(string? sha)
    {
        if (sha is null || Commits.FirstOrDefault(c => c.Sha == sha) is not { } commit)
        {
            return;
        }
        foreach (var other in Commits)
        {
            other.IsExpanded = ReferenceEquals(other, commit);
        }
        _ = LoadDetailAsync(commit);
    }

    private void ReloadOrMarkStale()
    {
        if (IsActive)
        {
            _ = SafeLoadAsync();
        }
        else
        {
            IsStale = true;
        }
    }

    /// <summary>fire-and-forget 的加载：LoadAsync 自己会把失败写进 Error，这里再兜一层避免未观察的异常。</summary>
    private async Task SafeLoadAsync()
    {
        try
        {
            await LoadAsync();
        }
        catch (Exception)
        {
        }
    }

    private void UpdateBranchText()
    {
        BranchText = SelectedRepository is { } repository
            ? GitRepository.ReadBranch(repository.Path) ?? Strings.Get(TKCodeLangResourceKind.NoBranch)
            : "";
    }

    /// <summary>选中仓库的 refs / HEAD 变化（终端里 commit、switch）：可见时刷新，不可见时只标记过期。</summary>
    private void RebuildWatcher(string? path)
    {
        _watcher?.Dispose();
        _watcher = null;
        var generation = ++_watcherGeneration;
        if (path is null)
        {
            return;
        }
        _watcher = new GitBranchWatcher(path, () => _post(() => OnRepositoryChanged(generation)));
    }

    private void OnRepositoryChanged(int generation)
    {
        if (generation != _watcherGeneration)
        {
            // 回调在 watcher 释放之后才到：丢弃
            return;
        }
        if (!IsActive)
        {
            IsStale = true;
            return;
        }
        _ = SafeLoadAsync();
    }

    private void NotifyState()
    {
        OnPropertyChanged(nameof(HasRepository));
        OnPropertyChanged(nameof(HasNoRepository));
        OnPropertyChanged(nameof(HasMultipleRepositories));
        OnPropertyChanged(nameof(IsEmpty));
    }

    private static bool SamePath(string? left, string? right) =>
        left is null ? right is null : right is not null && PathRules.SamePath(left, right);
}
