using CommunityToolkit.Mvvm.ComponentModel;
using TKCode.Desktop.Services;

namespace TKCode.Desktop.ViewModels;

public enum ExplorerAction
{
    Open, Reveal, NewFile, NewFolder, ToggleExpanded, ShowInFileManager, OpenTerminal,
    CopyFullPath, CopyRelativePath, Rename, Trash, OpenRoot, CopyRootPath, Refresh,
    CollapseAll, ToggleHidden, RemoveSessionRoot,
}

/// <summary>A menu captures a path, never the mutable selection or an obsolete tree node.</summary>
/// <param name="Root">路径所在的根目录（多文件夹项目中不一定是主文件夹）。</param>
/// <param name="IsRoot">空白处，或多个根目录时某个文件夹的顶层节点。</param>
public sealed record ExplorerContextTarget(string Root, int RootVersion, string Path,
    bool IsDirectory, bool IsSearch, bool IsRoot);

/// <summary>The host owns the dialog; submission failures stay in that dialog for correction/retry.</summary>
public sealed record ExplorerFileDialogRequest(ExplorerAction Action, string Path, bool IsDirectory,
    string InitialName, Func<string?, Task> SubmitAsync);

public sealed partial class FileExplorerViewModel
{
    private readonly IExplorerFileOperations? _fileOperations;
    private readonly IExplorerPlatformActions? _platformActions;

    public Func<string, Task>? CopyTextAsync { get; set; }
    public Func<ExplorerFileDialogRequest, Task>? FileDialogRequested { get; set; }

    [ObservableProperty]
    public partial bool IsBusy { get; private set; }

    /// <summary>Pass null only for genuine blank space, not for an unrecognized/placeholder row.</summary>
    public ExplorerContextTarget? CaptureContext(FileNodeViewModel? node, bool isSearch = false)
    {
        if (Root is not { } primary || node is { IsPlaceholder: true })
        {
            return null;
        }
        // 空白处作用于主文件夹；节点作用于它所在的根目录
        var root = primary;
        if (node is not null && (!IsCurrentNode(node) || !TryGetRoot(node.FullPath, out root, out _)))
        {
            return null;
        }
        return new ExplorerContextTarget(root, _rootVersion, node?.FullPath ?? root,
            node?.IsDirectory ?? true, isSearch, node is null || node.IsWorkspaceRoot);
    }

    public IReadOnlyList<ExplorerAction> GetContextActions(ExplorerContextTarget target)
    {
        if (!IsCurrentTarget(target))
        {
            return [];
        }
        if (target.IsRoot)
        {
            ExplorerAction[] root = [ExplorerAction.NewFile, ExplorerAction.NewFolder, ExplorerAction.OpenRoot,
                ExplorerAction.OpenTerminal, ExplorerAction.CopyRootPath, ExplorerAction.Refresh,
                ExplorerAction.CollapseAll, ExplorerAction.ToggleHidden];
            // 只属于本会话的文件夹可以从本会话移除（docs/design/projects.md §10.4）
            return IsSessionRoot(target.Root) ? [.. root, ExplorerAction.RemoveSessionRoot] : root;
        }
        var actions = new List<ExplorerAction>();
        if (target.IsDirectory)
        {
            actions.Add(ExplorerAction.NewFile);
            actions.Add(ExplorerAction.NewFolder);
            actions.Add(target.IsSearch ? ExplorerAction.Reveal : ExplorerAction.ToggleExpanded);
        }
        else
        {
            actions.Add(ExplorerAction.Open);
            if (target.IsSearch)
            {
                actions.Add(ExplorerAction.Reveal);
            }
        }
        actions.AddRange([ExplorerAction.ShowInFileManager, ExplorerAction.OpenTerminal,
            ExplorerAction.CopyFullPath, ExplorerAction.CopyRelativePath, ExplorerAction.Rename, ExplorerAction.Trash]);
        return actions;
    }

    public bool CanExecuteAction(ExplorerContextTarget target, ExplorerAction action)
    {
        if (IsBusy || !GetContextActions(target).Contains(action))
        {
            return false;
        }
        return action switch
        {
            ExplorerAction.NewFile or ExplorerAction.NewFolder or ExplorerAction.Rename or ExplorerAction.Trash =>
                _fileOperations is not null && FileDialogRequested is not null,
            ExplorerAction.ShowInFileManager or ExplorerAction.OpenRoot => _platformActions is not null,
            ExplorerAction.CopyFullPath or ExplorerAction.CopyRelativePath or ExplorerAction.CopyRootPath => CopyTextAsync is not null,
            ExplorerAction.RemoveSessionRoot => RemoveSessionRootAsync is not null,
            _ => true,
        };
    }

    public async Task ExecuteActionAsync(ExplorerContextTarget target, ExplorerAction action)
    {
        if (!CanExecuteAction(target, action))
        {
            return;
        }
        switch (action)
        {
            case ExplorerAction.Open:
                _workbench.OpenFile(target.Path);
                break;
            case ExplorerAction.Reveal:
                await RevealAsync(target.Path, expandTarget: target.IsDirectory);
                break;
            case ExplorerAction.ToggleExpanded:
                if (_nodes.TryGetValue(target.Path, out var node))
                {
                    if (node.IsExpanded)
                    {
                        Collapse(node);
                    }
                    else
                    {
                        await ExpandAsync(node);
                    }
                }
                break;
            case ExplorerAction.ShowInFileManager:
                if (_platformActions is not null)
                {
                    await _platformActions.ShowAsync(target.Path, target.IsDirectory);
                }
                break;
            case ExplorerAction.OpenRoot:
                if (_platformActions is not null)
                {
                    await _platformActions.OpenDirectoryAsync(target.Root);
                }
                break;
            case ExplorerAction.OpenTerminal:
                _workbench.OpenTerminal(target.IsDirectory ? target.Path : System.IO.Path.GetDirectoryName(target.Path)!);
                break;
            case ExplorerAction.CopyRootPath:
            case ExplorerAction.CopyFullPath:
            case ExplorerAction.CopyRelativePath:
                if (CopyTextAsync is { } copy)
                {
                    await copy(action == ExplorerAction.CopyRelativePath
                        ? System.IO.Path.GetRelativePath(target.Root, target.Path) : target.Path);
                }
                break;
            case ExplorerAction.Refresh:
                await RefreshAsync();
                break;
            case ExplorerAction.CollapseAll:
                CollapseAll();
                break;
            case ExplorerAction.ToggleHidden:
                ShowHidden = !ShowHidden;
                await _rootLoadTask;
                await _searchTask;
                break;
            case ExplorerAction.NewFile:
            case ExplorerAction.NewFolder:
            case ExplorerAction.Rename:
            case ExplorerAction.Trash:
                await RequestFileDialogAsync(target, action);
                break;
            case ExplorerAction.RemoveSessionRoot:
                if (RemoveSessionRootAsync is { } remove)
                {
                    await remove(target.Root);
                }
                break;
        }
    }

    private bool IsCurrentTarget(ExplorerContextTarget target) =>
        target.RootVersion == _rootVersion && AllRoots.Contains(target.Root, PathComparer)
        && TryGetRoot(target.Path, out var root, out var relative) && PathComparer.Equals(root, target.Root)
        && (target.IsRoot ? target.IsDirectory && relative == "." : relative != ".");

    private void RequireCurrentTarget(ExplorerContextTarget target)
    {
        if (!IsCurrentTarget(target))
        {
            throw new InvalidOperationException("The workspace changed. Reopen the file action from the current workspace.");
        }
    }

    private async Task RequestFileDialogAsync(ExplorerContextTarget target, ExplorerAction action)
    {
        if (_fileOperations is null || FileDialogRequested is not { } showDialog)
        {
            return; // Read-only callers deliberately have no write service by default.
        }
        IsBusy = true;
        var dialogOpen = true;
        var submitting = false;
        var submitted = false;
        try
        {
            await showDialog(new ExplorerFileDialogRequest(action, target.Path,
                action is ExplorerAction.NewFolder || (action is not ExplorerAction.NewFile && target.IsDirectory),
                action == ExplorerAction.Rename ? System.IO.Path.GetFileName(target.Path) : "",
                async name =>
                {
                    if (!dialogOpen || submitting || submitted)
                    {
                        throw new InvalidOperationException("This file action is already being submitted or has completed.");
                    }
                    RequireCurrentTarget(target);
                    submitting = true;
                    try
                    {
                        await SubmitFileActionAsync(target, action, name);
                        submitted = true;
                    }
                    finally
                    {
                        submitting = false;
                    }
                }));
        }
        finally
        {
            dialogOpen = false;
            IsBusy = false;
        }
    }

    private async Task SubmitFileActionAsync(ExplorerContextTarget target, ExplorerAction action, string? name)
    {
        RequireCurrentTarget(target);
        var context = new ExplorerOperationContext(target.Root, () => IsCurrentTarget(target));
        string? destination = null;
        try
        {
            if (action is ExplorerAction.NewFile or ExplorerAction.NewFolder)
            {
                destination = await _fileOperations!.CreateAsync(context, target.Path, name ?? "",
                    action == ExplorerAction.NewFolder);
            }
            else if (action == ExplorerAction.Rename)
            {
                destination = await _fileOperations!.RenameAsync(context, target.Path, name ?? "", target.IsDirectory);
            }
            else
            {
                await _fileOperations!.TrashAsync(context, target.Path, target.IsDirectory);
            }
        }
        catch (ExplorerRenameException ex)
        {
            // A failed case-only rollback can leave the item at an intermediate path.
            if (IsCurrentTarget(target))
            {
                var recoveredExpanded = RetargetPaths(CaptureExpandedPaths(), target.Path, ex.ActualPath, target.IsDirectory);
                var recoveredSelection = RetargetPath(_selectedPath, target.Path, ex.ActualPath, target.IsDirectory);
                _workbench.RetargetFiles(target.Path, ex.ActualPath, target.IsDirectory);
                await RefreshCoreAsync(recoveredExpanded, recoveredSelection);
            }
            throw;
        }
        if (!IsCurrentTarget(target))
        {
            return; // The service may have completed just before the workspace switched.
        }
        // Preserve the latest UI state, including changes made while the service was awaiting IO.
        var expanded = CaptureExpandedPaths();
        var selected = _selectedPath;
        if (action == ExplorerAction.Rename)
        {
            _workbench.RetargetFiles(target.Path, destination!, target.IsDirectory);
            expanded = RetargetPaths(expanded, target.Path, destination!, target.IsDirectory);
            selected = RetargetPath(selected, target.Path, destination!, target.IsDirectory);
        }
        else if (action == ExplorerAction.Trash)
        {
            _workbench.CloseFiles(target.Path, target.IsDirectory);
            expanded = expanded.Where(path => !IsSameOrDescendant(path, target.Path, target.IsDirectory)).ToArray();
            if (selected is not null && IsSameOrDescendant(selected, target.Path, target.IsDirectory))
            {
                selected = _workbench.ActiveFilePath;
            }
        }
        else
        {
            selected = destination;
        }
        if (destination is not null && IsFilteredPath(destination))
        {
            ShowHidden = true;
        }
        var refreshing = RefreshCoreAsync(expanded, selected);
        var generation = _generation;
        var navigationVersion = _navigationVersion;
        await refreshing;
        if (!IsCurrentTarget(target) || generation != _generation || navigationVersion != _navigationVersion)
        {
            return;
        }
        if ((action is ExplorerAction.NewFile or ExplorerAction.NewFolder) && destination is not null
            && TryGetRoot(destination, out var destinationRoot, out var relative))
        {
            // Creation explicitly locates its result; ordinary refresh never opens collapsed branches.
            navigationVersion = ++_navigationVersion;
            await RevealCoreAsync(destination, destinationRoot, relative, action == ExplorerAction.NewFolder,
                generation, navigationVersion, requestScroll: true);
            if (IsCurrentTarget(target) && generation == _generation && navigationVersion == _navigationVersion
                && action == ExplorerAction.NewFile)
            {
                _workbench.OpenFile(destination);
            }
        }
    }

    /// <summary>Re-reads only the root and expanded branches, preserving the query and path selection.</summary>
    public Task RefreshAsync() => RefreshCoreAsync(CaptureExpandedPaths(), _selectedPath);

    private string[] CaptureExpandedPaths() => _nodes.Values
        .Where(node => node.IsDirectory && node.IsExpanded).Select(node => node.FullPath)
        .OrderBy(path => path.Length).ToArray();

    private async Task RefreshCoreAsync(string[] expanded, string? selected)
    {
        RebuildRoot(restoreSelection: false);
        _selectedPath = selected;
        var generation = _generation;
        var navigationVersion = _navigationVersion;
        await _rootLoadTask;
        foreach (var path in expanded)
        {
            if (generation != _generation || navigationVersion != _navigationVersion)
            {
                return;
            }
            if (FindVisibleTreeNode(path) is { } node)
            {
                await ExpandAsync(node);
            }
        }
        await _searchTask;
        if (generation == _generation && navigationVersion == _navigationVersion)
        {
            SyncCachedSelection();
        }
    }

    private FileNodeViewModel? FindVisibleTreeNode(string path)
    {
        if (!TryGetRoot(path, out var root, out var relative))
        {
            return null;
        }
        var children = RootNodes;
        if (IsMultiRoot)
        {
            var rootNode = RootNodes.FirstOrDefault(n => PathComparer.Equals(n.FullPath, root));
            if (relative == "." || rootNode is null)
            {
                return rootNode;
            }
            if (!rootNode.IsExpanded || !rootNode.IsLoaded)
            {
                return null;
            }
            children = rootNode.Children;
        }
        else if (relative == ".")
        {
            return null;
        }
        var current = root;
        var parts = relative.Split([System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < parts.Length; ++i)
        {
            current = System.IO.Path.Combine(current, parts[i]);
            var node = children.FirstOrDefault(child => PathComparer.Equals(child.FullPath, current));
            if (node is null || i == parts.Length - 1)
            {
                return node;
            }
            if (!node.IsExpanded || !node.IsLoaded)
            {
                return null;
            }
            children = node.Children;
        }
        return null;
    }

    public void CollapseAll()
    {
        ++_navigationVersion; // Invalidate reveals/refreshes that are awaiting a directory read.
        foreach (var node in _nodes.Values.Where(node => node.IsExpanded).OrderByDescending(node => node.FullPath.Length).ToArray())
        {
            SetExpanded(node, false);
        }
    }

    private bool IsFilteredPath(string path)
    {
        if (ShowHidden || !TryGetRelativePath(path, out var relative))
        {
            return false;
        }
        var parts = relative.Split([System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
        return parts.Where((part, index) => IsHidden(part, index < parts.Length - 1 || Directory.Exists(path), false)).Any();
    }

    private static bool IsSameOrDescendant(string path, string ancestor, bool isDirectory)
    {
        if (PathComparer.Equals(path, ancestor))
        {
            return true;
        }
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var prefix = System.IO.Path.EndsInDirectorySeparator(ancestor) ? ancestor : ancestor + System.IO.Path.DirectorySeparatorChar;
        return isDirectory && path.StartsWith(prefix, comparison);
    }

    private static string? RetargetPath(string? path, string oldPath, string newPath, bool isDirectory) =>
        path is not null && IsSameOrDescendant(path, oldPath, isDirectory)
            ? newPath + path[oldPath.Length..] : path;

    private static string[] RetargetPaths(string[] paths, string oldPath, string newPath, bool isDirectory) =>
        paths.Select(path => RetargetPath(path, oldPath, newPath, isDirectory)!).ToArray();
}
