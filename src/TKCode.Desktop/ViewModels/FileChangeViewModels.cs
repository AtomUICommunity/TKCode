using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TKCode.Desktop.Icons;
using TKCode.Desktop.Localization;
using TKCode.Rpc;

namespace TKCode.Desktop.ViewModels;

/// <summary>
/// 一个文件在若干次 Agent 改动中的合并结果（同一轮，或整个会话）。
/// 撤销回到第一次改动前的内容，并要求文件仍是最后一次改动后的样子（见 <see cref="RevertFileRequest"/>）。
/// </summary>
public sealed partial class FileChangeViewModel : ObservableObject
{
    private readonly Func<RevertFileRequest, Task<RevertFileResult>> _revert;

    public FileChangeViewModel(string path, string displayPath, bool created, string? before, string? afterSha256,
        IReadOnlyList<DiffLineViewModel> lines, int added, int removed, Func<RevertFileRequest, Task<RevertFileResult>> revert)
    {
        Path = path;
        DisplayPath = displayPath;
        Created = created;
        Before = before;
        AfterSha256 = afterSha256;
        Lines = lines;
        Added = added;
        Removed = removed;
        _revert = revert;
        (Icon, IconColor) = FileIcons.For(Name, isDirectory: false);
    }

    public string Path { get; }

    /// <summary>列表里显示的路径：工作区内相对工作目录，工作区外为完整路径（见 <see cref="ChangePaths"/>）。</summary>
    public string DisplayPath { get; }

    public string Name => System.IO.Path.GetFileName(Path);

    /// <summary>所在目录（用于列表中的次要文字）。</summary>
    public string Directory => System.IO.Path.GetDirectoryName(Path) ?? "";

    public LucideKind Icon { get; }
    public string IconColor { get; }

    public bool Created { get; }

    /// <summary>第一次改动前的完整文本；为 null 且非新建时不可撤销。</summary>
    public string? Before { get; }

    /// <summary>最后一次改动后的文件 SHA-256。</summary>
    public string? AfterSha256 { get; }

    public IReadOnlyList<DiffLineViewModel> Lines { get; }

    /// <summary>有 diff 行（二进制 / 过大的文件只有路径信息）。</summary>
    public bool HasDiff => Lines.Count > 0;

    public int Added { get; }
    public int Removed { get; }

    public string Stats => $"+{Added} −{Removed}";

    public string AddedText => $"+{Added}";
    public string RemovedText => $"−{Removed}";

    /// <summary>变更面板中是否展开显示 diff（默认只显示文件行）。</summary>
    [ObservableProperty]
    public partial bool IsExpanded { get; set; }

    [RelayCommand]
    private void ToggleExpanded() => IsExpanded = !IsExpanded;

    /// <summary>有快照（或是新建的文件）时才能撤销。</summary>
    public bool CanRevert => AfterSha256 is not null && (Created || Before is not null);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRevertNow))]
    public partial bool IsReverted { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? Error { get; private set; }

    public bool HasError => Error is not null;

    public bool CanRevertNow => CanRevert && !IsReverted;

    [RelayCommand]
    private Task RevertAsync() => RevertCoreAsync();

    /// <summary>撤销这个文件的改动；成功后标记为已撤销，失败时保留错误文字。</summary>
    public async Task RevertCoreAsync()
    {
        if (!CanRevertNow || AfterSha256 is null)
        {
            return;
        }
        var result = await _revert(new RevertFileRequest(Path, AfterSha256, Created ? null : Before));
        if (result.Error is null)
        {
            Error = null;
            IsReverted = true;
        }
        else
        {
            Error = Strings.Format(TKCodeLangResourceKind.ChangesRevertFailed, result.Error);
        }
    }
}

/// <summary>把若干工具改动（DiffDetails）按文件合并。</summary>
public static class FileChangeSet
{
    public static IReadOnlyList<FileChangeViewModel> Group(IEnumerable<ParsedToolDetails> edits,
        Func<RevertFileRequest, Task<RevertFileResult>> revert, string? cwd = null)
    {
        var order = new List<string>();
        var byPath = new Dictionary<string, List<ParsedToolDetails>>(PathComparer);
        foreach (var edit in edits)
        {
            if (edit.Path is not { } path)
            {
                continue;
            }
            if (!byPath.TryGetValue(path, out var list))
            {
                byPath[path] = list = [];
                order.Add(path);
            }
            list.Add(edit);
        }
        return order.Select(path => Build(path, byPath[path], revert, cwd)).ToArray();
    }

    private static FileChangeViewModel Build(string path, List<ParsedToolDetails> edits,
        Func<RevertFileRequest, Task<RevertFileResult>> revert, string? cwd)
    {
        var first = edits[0];
        var last = edits[^1];
        return new FileChangeViewModel(
            path,
            ChangePaths.Display(path, cwd),
            created: first.Created,
            before: first.Created ? null : first.Before,
            afterSha256: last.AfterSha256,
            lines: edits.SelectMany(e => e.DiffLines).ToArray(),
            added: edits.Sum(e => e.Added),
            removed: edits.Sum(e => e.Removed),
            revert);
    }

    private static StringComparer PathComparer => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;
}

/// <summary>
/// 一轮结束后的改动卡片：“已编辑 N 个文件 +X −Y”，可整轮撤销、打开变更面板；
/// 点击文件行在编辑器打开该文件，悬停看 diff（由视图实现）。
/// 文件超过 <see cref="PreviewCount"/> 个时默认只列出前面几个，其余收起，避免长列表把对话顶出视口。
/// </summary>
public sealed partial class TurnChangesItemViewModel : TranscriptItemViewModel
{
    /// <summary>卡片里默认直接列出的文件数。</summary>
    public const int PreviewCount = 5;

    private readonly Action<string> _open;
    private readonly Action? _viewChanges;

    public TurnChangesItemViewModel(IReadOnlyList<FileChangeViewModel> files, Action<string> open, Action? viewChanges = null)
    {
        _open = open;
        _viewChanges = viewChanges;
        foreach (var file in files)
        {
            Files.Add(file);
        }
    }

    public ObservableCollection<FileChangeViewModel> Files { get; } = [];

    public int Added => Files.Sum(f => f.Added);
    public int Removed => Files.Sum(f => f.Removed);

    public string Header => Strings.Format(TKCodeLangResourceKind.ChangesFilesChanged, Files.Count);

    public string Stats => $"+{Added} −{Removed}";

    public string AddedText => $"+{Added}";

    public string RemovedText => $"−{Removed}";

    /// <summary>还有可以撤销的文件（整轮撤销按钮的可用状态）。</summary>
    public bool CanRevertAll => Files.Any(f => f.CanRevertNow);

    [ObservableProperty]
    public partial bool IsExpanded { get; set; } = true;

    /// <summary>超出预览数量的文件是否展开。默认收起。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VisibleFiles))]
    [NotifyPropertyChangedFor(nameof(RestLabel))]
    public partial bool ShowAll { get; set; }

    /// <summary>当前应显示的文件：未展开时只取前 <see cref="PreviewCount"/> 个。</summary>
    public IEnumerable<FileChangeViewModel> VisibleFiles => ShowAll ? Files : Files.Take(PreviewCount);

    /// <summary>还有没列出的文件（显示“还有 N 个文件”）。</summary>
    public bool HasOverflow => Files.Count > PreviewCount;

    /// <summary>“还有 N 个文件”，展开后变成“收起”。</summary>
    public string RestLabel => ShowAll
        ? Strings.Get(TKCodeLangResourceKind.ChangesShowLess)
        : Strings.Format(TKCodeLangResourceKind.ChangesShowMore, Math.Max(0, Files.Count - PreviewCount));

    [RelayCommand]
    private void Toggle() => IsExpanded = !IsExpanded;

    [RelayCommand]
    private void ToggleRest() => ShowAll = !ShowAll;

    public void RefreshLanguage()
    {
        OnPropertyChanged(nameof(Header));
        OnPropertyChanged(nameof(RestLabel));
    }

    [RelayCommand]
    private async Task RevertAllAsync()
    {
        foreach (var file in Files.ToArray())
        {
            await file.RevertCoreAsync();
        }
        NotifyRevertState();
    }

    /// <summary>文件在别处被撤销后（回退到某条消息）刷新整轮撤销按钮。</summary>
    public void NotifyRevertState() => OnPropertyChanged(nameof(CanRevertAll));

    [RelayCommand]
    private void ViewChanges() => _viewChanges?.Invoke();

    [RelayCommand]
    private void Open(FileChangeViewModel file) => _open(file.Path);
}

/// <summary>工作台的“变更”标签页：当前会话全部文件的改动。默认只列文件与增删行数，点击某行在其下展开 diff。</summary>
public sealed partial class ChangesTabViewModel : WorkbenchTabViewModel
{
    private SessionViewModel? _session;

    public ChangesTabViewModel() : base(WorkbenchTabKind.Diffs)
    {
    }

    public override string Title => WorkbenchTexts.Title(Kind);

    public ObservableCollection<FileChangeViewModel> Files { get; } = [];

    public bool IsEmpty => Files.Count == 0;

    public string EmptyText => Strings.Get(TKCodeLangResourceKind.ChangesEmpty);

    /// <summary>请求把某个文件滚动到可见处（<see cref="Select"/> 选中文件时）。</summary>
    public event Action<FileChangeViewModel>? RevealRequested;

    /// <summary>切换会话（或会话有新改动）时重新汇总。</summary>
    public void SetSession(SessionViewModel? session)
    {
        if (_session is not null)
        {
            _session.ChangesChanged -= Refresh;
        }
        _session = session;
        if (session is not null)
        {
            session.ChangesChanged += Refresh;
        }
        Refresh();
    }

    /// <summary>重新汇总；已展开的文件保持展开。</summary>
    public void Refresh()
    {
        var expanded = Files.Where(f => f.IsExpanded).Select(f => f.Path).ToHashSet();
        Files.Clear();
        foreach (var file in _session?.FileChanges() ?? [])
        {
            file.IsExpanded = expanded.Contains(file.Path);
            Files.Add(file);
        }
        OnPropertyChanged(nameof(IsEmpty));
    }

    /// <summary>展开某个文件的 diff 并滚动到它。</summary>
    public void Select(string path)
    {
        Refresh();
        if (Files.FirstOrDefault(f => f.Path == path) is { } file)
        {
            file.IsExpanded = true;
            RevealRequested?.Invoke(file);
        }
    }
}
