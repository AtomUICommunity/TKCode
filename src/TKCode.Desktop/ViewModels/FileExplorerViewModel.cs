using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TKCode.Desktop.Icons;
using TKCode.Desktop.Localization;
using TKCode.Desktop.Services;

namespace TKCode.Desktop.ViewModels;

/// <summary>A cached file-system node. Reading Children never performs file IO.</summary>
public sealed partial class FileNodeViewModel : ObservableObject
{
    public FileNodeViewModel(string fullPath, string name, bool isDirectory)
        : this(fullPath, name, isDirectory, false)
    {
    }

    private FileNodeViewModel(string fullPath, string name, bool isDirectory, bool isPlaceholder)
    {
        FullPath = fullPath;
        Name = name;
        IsDirectory = isDirectory;
        IsPlaceholder = isPlaceholder;
        (Icon, IconColor) = FileIcons.For(name, isDirectory);
        IsLoaded = !isDirectory;
        if (isDirectory)
        {
            // Each directory needs its own placeholder identity for the virtualized tree.
            Children.Add(new FileNodeViewModel("", Strings.Get(TKCodeLangResourceKind.CompletionLoading), false, true));
        }
    }

    public string FullPath { get; }
    public string Name { get; }
    public bool IsDirectory { get; }

    /// <summary>多文件夹项目中某个文件夹的顶层节点（docs/design/projects.md §10.1）。</summary>
    public bool IsWorkspaceRoot { get; init; }
    public LucideKind Icon { get; }
    public string IconColor { get; }
    public string? Hint { get; init; }
    public bool IsPlaceholder { get; }
    public ObservableCollection<FileNodeViewModel> Children { get; } = [];

    [ObservableProperty]
    public partial bool IsExpanded { get; internal set; }

    [ObservableProperty]
    public partial bool IsLoaded { get; internal set; }

    [ObservableProperty]
    public partial bool IsLoading { get; internal set; }

    [ObservableProperty]
    public partial bool IsSelected { get; set; }
}

/// <summary>
/// Lazy workspace hierarchy and an independent, cancellable filename search. All public
/// operations run on the UI context; background workers only return file-system metadata.
/// Selection never opens a file: opening is an explicit activation operation.
/// </summary>
public sealed partial class FileExplorerViewModel : ObservableObject
{
    private const int MaxSearchResults = 300;
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    private static readonly HashSet<string> BuildDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "obj", "node_modules", "dist", "build", "out", "target", "__pycache__", ".git",
    };

    private readonly WorkbenchViewModel _workbench;
    private readonly Dictionary<string, FileNodeViewModel> _nodes = new(PathComparer);
    private readonly Dictionary<FileNodeViewModel, Task> _loads = [];
    private CancellationTokenSource _rootCts = new();
    private CancellationTokenSource? _searchCts;
    private Task _rootLoadTask = Task.CompletedTask;
    private Task _searchTask = Task.CompletedTask;
    private int _rootVersion;
    private int _generation;
    private int _searchGeneration;
    private int _navigationVersion;
    private FileNodeViewModel? _selectedNode;
    private string? _selectedPath;

    public FileExplorerViewModel(WorkbenchViewModel workbench,
        IExplorerFileOperations? fileOperations = null, IExplorerPlatformActions? platformActions = null)
    {
        _workbench = workbench;
        _fileOperations = fileOperations;
        _platformActions = platformActions;
        workbench.ActiveFileChanged += SetSelectedPath;
        RootNodes.CollectionChanged += (_, _) => OnPropertyChanged(nameof(IsEmpty));
        SearchResults.CollectionChanged += (_, _) => OnPropertyChanged(nameof(IsEmpty));
    }

    public ObservableCollection<FileNodeViewModel> RootNodes { get; } = [];
    public ObservableCollection<FileNodeViewModel> SearchResults { get; } = [];

    /// <summary>主文件夹（会话的 cwd，或所选项目的主文件夹）。</summary>
    [ObservableProperty]
    public partial string? Root { get; private set; }

    /// <summary>主文件夹以外的根目录（项目的其他文件夹、本会话追加的文件夹）；有时每个文件夹一个顶层节点（docs/design/projects.md §10.1）。</summary>
    public IReadOnlyList<string> ExtraRoots => _extraRoots;

    private string[] _extraRoots = [];
    private HashSet<string> _sessionRoots = new(PathComparer);

    public bool IsMultiRoot => Root is not null && _extraRoots.Length > 0;

    /// <summary>全部根目录，主文件夹在前。</summary>
    public IReadOnlyList<string> AllRoots => Root is { } root ? [root, .. _extraRoots] : [];

    /// <summary>只属于本会话的根目录（根节点的右键菜单可以把它从本会话移除）。</summary>
    public bool IsSessionRoot(string path) => _sessionRoots.Contains(path);

    /// <summary>根节点的「从本会话移除」。</summary>
    public Func<string, Task>? RemoveSessionRootAsync { get; set; }

    [ObservableProperty]
    public partial string Search { get; set; } = "";

    [ObservableProperty]
    public partial bool ShowHidden { get; set; }

    public bool HasRoot => Root is not null;
    public bool IsSearching => !string.IsNullOrWhiteSpace(Search);
    public bool IsEmpty => IsSearching ? SearchResults.Count == 0 : RootNodes.Count == 0;

    public FileNodeViewModel? SelectedNode
    {
        get => _selectedNode;
        set
        {
            if (value is { IsPlaceholder: true } || ReferenceEquals(value, _selectedNode))
            {
                return;
            }
            ++_navigationVersion;
            _selectedPath = value?.FullPath;
            SetSelection(value);
        }
    }

    /// <summary>Synchronizes programmatic expansion with the tree's public expansion API.</summary>
    public event Action<FileNodeViewModel>? ExpansionChanged;

    /// <summary>A directory's placeholder has been replaced with its loaded children.</summary>
    public event Action<FileNodeViewModel>? ChildrenLoaded;

    /// <summary>Ancestors are loaded and expanded, and the target is ready to scroll into view.</summary>
    public event Action<FileNodeViewModel>? RevealRequested;

    partial void OnSearchChanged(string value)
    {
        ++_navigationVersion;
        OnPropertyChanged(nameof(IsSearching));
        OnPropertyChanged(nameof(IsEmpty));
        StartSearch();
        if (!IsSearching)
        {
            _ = ObserveAsync(RestoreSelectionAsync(_selectedPath, _generation, _navigationVersion));
        }
    }

    partial void OnShowHiddenChanged(bool value) => RebuildRoot();

    partial void OnRootChanged(string? value)
    {
        ++_rootVersion;
        OnPropertyChanged(nameof(HasRoot));
        OnPropertyChanged(nameof(ExtraRoots));
        OnPropertyChanged(nameof(IsMultiRoot));
        _workbench.Root = value;
        Search = "";
        RebuildRoot();
    }

    public void SetRoot(string? root) => SetRoots(root, []);

    /// <summary>设置主文件夹与其他根目录；不存在的、重复的与等于主文件夹的跳过。根目录不变时不重建。</summary>
    public void SetRoots(string? root, IReadOnlyList<string> extra, IEnumerable<string>? sessionOnly = null)
    {
        var normalized = NormalizePath(root);
        var primary = normalized is not null && Directory.Exists(normalized) ? normalized : null;
        var roots = new List<string>();
        foreach (var path in primary is null ? [] : extra)
        {
            if (NormalizePath(path) is { } full && Directory.Exists(full) && !PathComparer.Equals(full, primary)
                && !roots.Contains(full, PathComparer))
            {
                roots.Add(full);
            }
        }
        var session = new HashSet<string>((sessionOnly ?? []).Select(NormalizePath).OfType<string>(), PathComparer);
        session.IntersectWith(roots);
        var extraChanged = !roots.SequenceEqual(_extraRoots, PathComparer) || !session.SetEquals(_sessionRoots);
        _extraRoots = [.. roots];
        _sessionRoots = session;
        if (!PathComparer.Equals(Root ?? "", primary ?? ""))
        {
            Root = primary;
        }
        else if (extraChanged)
        {
            ++_rootVersion;
            OnPropertyChanged(nameof(ExtraRoots));
            OnPropertyChanged(nameof(IsMultiRoot));
            Search = "";
            RebuildRoot();
        }
    }

    [RelayCommand]
    private void ToggleHidden() => ShowHidden = !ShowHidden;

    [RelayCommand]
    private async Task ActivateAsync(FileNodeViewModel? node)
    {
        if (node is null || node.IsPlaceholder || !IsCurrentNode(node))
        {
            return;
        }
        if (!node.IsDirectory)
        {
            _workbench.OpenFile(node.FullPath);
        }
        else if (IsSearching)
        {
            await RevealAsync(node.FullPath, expandTarget: true);
        }
        else if (node.IsExpanded)
        {
            Collapse(node);
        }
        else
        {
            await ExpandAsync(node);
        }
    }

    public Task EnsureChildrenLoadedAsync(FileNodeViewModel node)
    {
        if (!node.IsDirectory || node.IsPlaceholder || node.IsLoaded || !IsCurrentNode(node))
        {
            return Task.CompletedTask;
        }
        if (_loads.TryGetValue(node, out var loading))
        {
            return loading;
        }
        var task = LoadChildrenAsync(node, _generation, ShowHidden, _rootCts.Token);
        _loads[node] = task;
        return task;
    }

    public async Task ExpandAsync(FileNodeViewModel node)
    {
        if (!node.IsDirectory || node.IsPlaceholder || !IsCurrentNode(node)
            || (node.IsLoaded && node.Children.Count == 0))
        {
            return;
        }
        SetExpanded(node, true);
        await EnsureChildrenLoadedAsync(node);
        // Do not expand again: the user may have collapsed the node during the read.
    }

    public void Collapse(FileNodeViewModel node)
    {
        if (node.IsDirectory && !node.IsPlaceholder && node.IsExpanded && IsCurrentNode(node))
        {
            ++_navigationVersion;
            SetExpanded(node, false);
        }
    }

    private void SetExpanded(FileNodeViewModel node, bool expanded)
    {
        if (node.IsExpanded != expanded)
        {
            node.IsExpanded = expanded;
            ExpansionChanged?.Invoke(node);
        }
    }

    /// <summary>Loads only the target's ancestry; directories expand only when explicitly requested.</summary>
    public Task RevealAsync(string path, bool expandTarget = false)
    {
        var fullPath = NormalizePath(path);
        if (fullPath is null || !TryGetRoot(fullPath, out var root, out var relative) || relative == ".")
        {
            return Task.CompletedTask;
        }
        Search = "";
        return RevealCoreAsync(fullPath, root, relative, expandTarget, _generation, ++_navigationVersion, requestScroll: true);
    }

    /// <summary>Safe fire-and-forget entry point for the editor's reveal command.</summary>
    public void Reveal(string path) => _ = ObserveAsync(RevealAsync(path));

    private async Task RevealCoreAsync(string path, string root, string relative, bool expandTarget, int generation,
        int navigationVersion, bool requestScroll)
    {
        await _rootLoadTask;
        var children = RootNodes;
        if (IsMultiRoot)
        {
            // 多个根目录：先展开路径所在的根节点
            if (RootNodes.FirstOrDefault(n => PathComparer.Equals(n.FullPath, root)) is not { } rootNode)
            {
                return;
            }
            await ExpandAsync(rootNode);
            if (generation != _generation || navigationVersion != _navigationVersion)
            {
                return;
            }
            children = rootNode.Children;
        }
        var parts = relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        var current = root;
        for (var i = 0; i < parts.Length; ++i)
        {
            if (generation != _generation || navigationVersion != _navigationVersion)
            {
                return;
            }
            current = Path.Combine(current, parts[i]);
            var node = children.FirstOrDefault(n => PathComparer.Equals(n.FullPath, current));
            if (node is null)
            {
                return; // Disappeared, inaccessible, or filtered out.
            }
            var isTarget = i == parts.Length - 1;
            if (!isTarget || (expandTarget && node.IsDirectory))
            {
                if (!node.IsDirectory)
                {
                    return;
                }
                await ExpandAsync(node);
                if (generation != _generation || navigationVersion != _navigationVersion)
                {
                    return;
                }
            }
            if (isTarget)
            {
                _selectedPath = path;
                SetSelection(node);
                if (requestScroll)
                {
                    RevealRequested?.Invoke(node);
                }
                return;
            }
            children = node.Children;
        }
    }

    private void SetSelectedPath(string? path)
    {
        ++_navigationVersion;
        _selectedPath = NormalizePath(path);
        SyncCachedSelection();
    }

    private void SyncCachedSelection() =>
        SetSelection(_selectedPath is { } path && _nodes.TryGetValue(path, out var node) ? node : null);

    private void SetSelection(FileNodeViewModel? node)
    {
        if (ReferenceEquals(_selectedNode, node))
        {
            return;
        }
        if (_selectedNode is { } previous)
        {
            previous.IsSelected = false;
        }
        if (node is not null)
        {
            node.IsSelected = true;
        }
        SetProperty(ref _selectedNode, node, nameof(SelectedNode));
    }

    private void RebuildRoot(bool restoreSelection = true)
    {
        _rootCts.Cancel();
        _rootCts.Dispose();
        _rootCts = new CancellationTokenSource();
        var generation = ++_generation;
        var navigationVersion = ++_navigationVersion;
        // Remember the path, not a node from an obsolete hierarchy.
        var selectedPath = _workbench.ActiveFilePath is { } active ? NormalizePath(active) : _selectedPath;
        SetSelection(null);
        _selectedPath = selectedPath;
        _nodes.Clear();
        _loads.Clear();
        RootNodes.Clear();
        _rootLoadTask = Root is { } root
            ? IsMultiRoot ? LoadRootsAsync(generation) : LoadRootAsync(root, generation, ShowHidden, _rootCts.Token)
            : Task.CompletedTask;
        StartSearch();
        if (restoreSelection)
        {
            _ = ObserveAsync(RestoreSelectionAsync(selectedPath, generation, navigationVersion));
        }
    }

    private async Task LoadRootAsync(string root, int generation, bool showHidden, CancellationToken token)
    {
        try
        {
            var entries = await Task.Run(() => ReadChildren(root, showHidden, token), token);
            if (generation != _generation || token.IsCancellationRequested)
            {
                return;
            }
            foreach (var entry in entries)
            {
                RootNodes.Add(GetOrCreate(entry));
            }
            SyncCachedSelection();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
    }

    /// <summary>
    /// 多个根目录：每个文件夹一个顶层节点（名称后注明「主要」或「本会话」），主文件夹默认展开。
    /// </summary>
    private async Task LoadRootsAsync(int generation)
    {
        foreach (var root in AllRoots)
        {
            var label = PathComparer.Equals(root, Root) ? Strings.Get(TKCodeLangResourceKind.PrimaryFolder)
                : _sessionRoots.Contains(root) ? Strings.Get(TKCodeLangResourceKind.SessionFolderSuffix) : null;
            var name = TKCode.Config.Project.FolderName(root);
            var node = new FileNodeViewModel(root, label is null ? name : $"{name} · {label}", true) { IsWorkspaceRoot = true };
            _nodes[root] = node;
            RootNodes.Add(node);
        }
        if (RootNodes.FirstOrDefault() is { } primary && generation == _generation)
        {
            await ExpandAsync(primary);
        }
        SyncCachedSelection();
    }

    private async Task RestoreSelectionAsync(string? path, int generation, int navigationVersion)
    {
        await _rootLoadTask;
        if (path is not null && generation == _generation && navigationVersion == _navigationVersion && !IsSearching
            && TryGetRoot(path, out var root, out var relative) && relative != ".")
        {
            await RevealCoreAsync(path, root, relative, false, generation, navigationVersion, requestScroll: false);
        }
    }

    private async Task LoadChildrenAsync(FileNodeViewModel node, int generation, bool showHidden, CancellationToken token)
    {
        node.IsLoading = true;
        try
        {
            var entries = await Task.Run(() => ReadChildren(node.FullPath, showHidden, token), token);
            if (generation != _generation || token.IsCancellationRequested || !IsCurrentNode(node))
            {
                return;
            }
            if (entries.Count == 0)
            {
                // Arc can only collapse a branch while it still has children. Remove its
                // expansion before replacing the placeholder with an empty leaf collection.
                SetExpanded(node, false);
            }
            node.Children.Clear();
            foreach (var entry in entries)
            {
                node.Children.Add(GetOrCreate(entry));
            }
            node.IsLoaded = true;
            ChildrenLoaded?.Invoke(node);
            SyncCachedSelection();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        finally
        {
            node.IsLoading = false;
            if (generation == _generation)
            {
                _loads.Remove(node);
            }
        }
    }

    private void StartSearch()
    {
        _searchCts?.Cancel();
        _searchCts?.Dispose();
        _searchCts = null;
        var searchGeneration = ++_searchGeneration;
        SearchResults.Clear();
        _searchTask = Task.CompletedTask;
        if (Root is null || !IsSearching)
        {
            return;
        }
        _searchCts = CancellationTokenSource.CreateLinkedTokenSource(_rootCts.Token);
        _searchTask = SearchAsync(AllRoots, Search.Trim(), ShowHidden, _generation, searchGeneration, _searchCts.Token);
        _ = ObserveAsync(_searchTask);
    }

    private async Task SearchAsync(IReadOnlyList<string> roots, string query, bool showHidden, int generation,
        int searchGeneration, CancellationToken token)
    {
        try
        {
            // 多个根目录依次搜索，合计不超过上限；嵌套的根目录按路径去重
            var entries = await Task.Run(() => roots.SelectMany(root => SearchFiles(root, query, showHidden, token))
                .DistinctBy(e => e.FullPath, PathComparer).Take(MaxSearchResults).ToList(), token);
            if (generation != _generation || searchGeneration != _searchGeneration || token.IsCancellationRequested)
            {
                return;
            }
            foreach (var entry in entries)
            {
                SearchResults.Add(GetOrCreate(entry));
            }
            SyncCachedSelection();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
    }

    private FileNodeViewModel GetOrCreate(FileEntry entry)
    {
        if (_nodes.TryGetValue(entry.FullPath, out var node))
        {
            return node;
        }
        // 提示文字：相对于所在根目录的上级目录；多个根目录时加上文件夹名
        var root = TryGetRoot(entry.FullPath, out var containing, out _) ? containing : Root!;
        var parent = Path.GetRelativePath(root, Path.GetDirectoryName(entry.FullPath)!);
        var folder = IsMultiRoot ? TKCode.Config.Project.FolderName(root) : null;
        var hint = parent == "." ? folder : folder is null ? parent : Path.Combine(folder, parent);
        node = new FileNodeViewModel(entry.FullPath, entry.Name, entry.IsDirectory) { Hint = hint };
        _nodes.Add(entry.FullPath, node);
        return node;
    }

    private bool IsCurrentNode(FileNodeViewModel node) =>
        _nodes.TryGetValue(node.FullPath, out var current) && ReferenceEquals(current, node);

    /// <summary>路径所在的根目录（嵌套时取最深的一个）与相对于它的路径。</summary>
    private bool TryGetRoot(string path, out string root, out string relative)
    {
        root = "";
        relative = "..";
        foreach (var candidate in AllRoots.OrderByDescending(r => r.Length))
        {
            if (TryGetRelativePath(candidate, path, out relative))
            {
                root = candidate;
                return true;
            }
        }
        return false;
    }

    private bool TryGetRelativePath(string path, out string relative) => TryGetRoot(path, out _, out relative);

    private static bool TryGetRelativePath(string root, string path, out string relative)
    {
        relative = "..";
        if (PathComparer.Equals(root, path))
        {
            relative = ".";
            return true;
        }
        // GetRelativePath itself ignores case on macOS, including case-sensitive APFS.
        // Validate the normalized boundary with the same rules as our node cache first.
        var prefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!path.StartsWith(prefix, comparison))
        {
            return false;
        }
        relative = Path.GetRelativePath(root, path);
        return !Path.IsPathRooted(relative) && relative != ".."
            && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && !relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal);
    }

    private static string? NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }
        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static bool IsHidden(string name, bool isDirectory, bool showHidden) =>
        !showHidden && (name.StartsWith('.') || (isDirectory && BuildDirectories.Contains(name)));

    private sealed record FileEntry(string FullPath, string Name, bool IsDirectory, bool IsLink);

    private static List<FileEntry> ReadChildren(string directory, bool showHidden, CancellationToken token)
    {
        var entries = new List<FileEntry>();
        try
        {
            foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos())
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    var attributes = entry.Attributes;
                    var isDirectory = (attributes & FileAttributes.Directory) != 0;
                    if (!IsHidden(entry.Name, isDirectory, showHidden))
                    {
                        entries.Add(new FileEntry(entry.FullName, entry.Name, isDirectory, (attributes & FileAttributes.ReparsePoint) != 0));
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // A single entry can vanish while the directory is being enumerated.
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
        token.ThrowIfCancellationRequested();
        return entries.OrderByDescending(e => e.IsDirectory).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(e => e.Name, StringComparer.Ordinal).ToList();
    }

    private static List<FileEntry> SearchFiles(string root, string query, bool showHidden, CancellationToken token)
    {
        var results = new List<FileEntry>();
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.TryPop(out var directory))
        {
            token.ThrowIfCancellationRequested();
            foreach (var entry in ReadChildren(directory, showHidden, token))
            {
                token.ThrowIfCancellationRequested();
                // Never search repository internals, even when hidden items are shown.
                if (entry.Name == ".git")
                {
                    continue;
                }
                if (entry.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
                {
                    results.Add(entry);
                    if (results.Count == MaxSearchResults)
                    {
                        return SortSearchResults(results);
                    }
                }
                // Links remain visible/matchable, but must not lead outside the root or into cycles.
                if (entry.IsDirectory && !entry.IsLink)
                {
                    pending.Push(entry.FullPath);
                }
            }
        }
        return SortSearchResults(results);
    }

    private static List<FileEntry> SortSearchResults(List<FileEntry> results) =>
        results.OrderBy(e => e.IsDirectory).ThenBy(e => e.Name.Length)
            .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();

    private static async Task ObserveAsync(Task task)
    {
        try
        {
            await task;
        }
        catch (Exception ex)
        {
            // Property changes and the editor's void Reveal API cannot propagate async failures.
            System.Diagnostics.Debug.WriteLine($"File explorer: {ex}");
        }
    }
}
