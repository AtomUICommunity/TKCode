using Avalonia.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using TKCode.Desktop.Services;
using TKCode.Desktop.ViewModels;
using Xunit;

namespace TKCode.Desktop.Tests;

public sealed class ExplorerActionsTests
{
    [Fact]
    public Task ContextPolicy_DistinguishesFileDirectorySearchAndGenuineBlankSpace() => OnUi(async () =>
    {
        using var files = new Workspace();
        files.File("src/app.cs");
        files.File("readme.md");
        await files.LoadAsync();
        var explorer = files.Explorer;
        var file = explorer.RootNodes.Single(node => !node.IsDirectory);
        var directory = explorer.RootNodes.Single(node => node.IsDirectory);
        var fileActions = explorer.GetContextActions(explorer.CaptureContext(file)!);
        Assert.Contains(ExplorerAction.Open, fileActions);
        Assert.DoesNotContain(ExplorerAction.NewFile, fileActions);
        Assert.DoesNotContain(ExplorerAction.ToggleExpanded, fileActions);
        Assert.DoesNotContain(ExplorerAction.Reveal, fileActions);
        Assert.Contains(ExplorerAction.Rename, fileActions);
        Assert.Contains(ExplorerAction.Trash, fileActions);
        var directoryActions = explorer.GetContextActions(explorer.CaptureContext(directory)!);
        Assert.Contains(ExplorerAction.NewFile, directoryActions);
        Assert.Contains(ExplorerAction.NewFolder, directoryActions);
        Assert.Contains(ExplorerAction.ToggleExpanded, directoryActions);
        Assert.DoesNotContain(ExplorerAction.Open, directoryActions);
        Assert.Equal([ExplorerAction.NewFile, ExplorerAction.NewFolder, ExplorerAction.ToggleExpanded], directoryActions.Take(3));
        var searchActions = explorer.GetContextActions(explorer.CaptureContext(directory, isSearch: true)!);
        Assert.Contains(ExplorerAction.Reveal, searchActions);
        Assert.DoesNotContain(ExplorerAction.ToggleExpanded, searchActions);
        var blank = explorer.CaptureContext(null)!;
        Assert.True(blank.IsRoot);
        Assert.Equal(files.Root, blank.Path);
        var blankActions = explorer.GetContextActions(blank);
        Assert.Contains(ExplorerAction.OpenRoot, blankActions);
        Assert.Contains(ExplorerAction.Refresh, blankActions);
        Assert.Contains(ExplorerAction.CollapseAll, blankActions);
        Assert.DoesNotContain(ExplorerAction.Rename, blankActions);
        Assert.DoesNotContain(ExplorerAction.Trash, blankActions);
        Assert.Equal([ExplorerAction.NewFile, ExplorerAction.NewFolder, ExplorerAction.OpenRoot], blankActions.Take(3));
        Assert.False(explorer.CanExecuteAction(blank, ExplorerAction.NewFile)); // No dialog host yet.
        Assert.False(explorer.CanExecuteAction(blank, ExplorerAction.CopyRootPath));
        Assert.True(explorer.CanExecuteAction(blank, ExplorerAction.OpenRoot));
        Assert.Null(explorer.CaptureContext(Assert.Single(directory.Children)));
        Assert.Null(explorer.CaptureContext(new FileNodeViewModel(file.FullPath, file.Name, false)));
        explorer.SetRoot(null);
        Assert.Null(explorer.CaptureContext(null));
    });

    [Fact]
    public Task CapturedTarget_DoesNotFollowSelection_AndClipboardUsesCapturedRelativePath() => OnUi(async () =>
    {
        using var files = new Workspace();
        var firstPath = files.File("src/first.cs");
        files.File("second.cs");
        await files.LoadAsync();
        var explorer = files.Explorer;
        await explorer.RevealAsync(firstPath);
        var target = explorer.CaptureContext(explorer.SelectedNode)!;
        explorer.SelectedNode = explorer.RootNodes.Single(node => node.Name == "second.cs");
        var copied = new List<string>();
        explorer.CopyTextAsync = text => { copied.Add(text); return Task.CompletedTask; };
        await explorer.ExecuteActionAsync(target, ExplorerAction.CopyFullPath);
        await explorer.ExecuteActionAsync(target, ExplorerAction.CopyRelativePath);
        await explorer.ExecuteActionAsync(target, ExplorerAction.Open);
        Assert.Equal([firstPath, Path.Combine("src", "first.cs")], copied);
        Assert.Equal(firstPath, files.Workbench.ActiveFilePath);
    });

    [Fact]
    public Task OldNodesAndRootTargets_AreRejectedEvenAfterSwitchingBackToSameRoot() => OnUi(async () =>
    {
        using var files = new Workspace();
        using var other = new Workspace();
        files.File("first.cs");
        await files.LoadAsync();
        var explorer = files.Explorer;
        var oldNode = Assert.Single(explorer.RootNodes);
        var target = explorer.CaptureContext(oldNode)!;
        var blank = explorer.CaptureContext(null)!;
        explorer.SetRoot(other.Root);
        explorer.SetRoot(files.Root);
        await explorer.RefreshAsync();
        Assert.Null(explorer.CaptureContext(oldNode));
        Assert.Empty(explorer.GetContextActions(target));
        Assert.Empty(explorer.GetContextActions(blank));
        await explorer.ExecuteActionAsync(target, ExplorerAction.Open);
        Assert.Empty(files.Workbench.Tabs);
        Assert.Empty(explorer.GetContextActions(explorer.CaptureContext(null)! with
            { IsRoot = false, Path = Path.Combine(files.Root + "-sibling", "outside.cs") }));
    });

    [Fact]
    public Task DialogRootSwitch_InvalidatesSubmission_AndBusyPreventsDuplicateDialogs() => OnUi(async () =>
    {
        using var files = new Workspace();
        using var other = new Workspace();
        files.File("app.cs");
        await files.LoadAsync();
        var explorer = files.Explorer;
        var target = explorer.CaptureContext(Assert.Single(explorer.RootNodes))!;
        ExplorerFileDialogRequest? dialog = null;
        var closed = new TaskCompletionSource();
        var count = 0;
        explorer.FileDialogRequested = request => { dialog = request; ++count; return closed.Task; };
        var pending = explorer.ExecuteActionAsync(target, ExplorerAction.Rename);
        Assert.True(explorer.IsBusy);
        Assert.False(explorer.CanExecuteAction(target, ExplorerAction.Rename));
        Assert.NotNull(dialog);
        Assert.Equal("app.cs", dialog.InitialName);
        await explorer.ExecuteActionAsync(target, ExplorerAction.Rename);
        Assert.Equal(1, count);
        explorer.SetRoot(other.Root);
        explorer.SetRoot(files.Root);
        await Assert.ThrowsAsync<InvalidOperationException>(() => dialog.SubmitAsync("new.cs"));
        Assert.Empty(files.Operations.Calls);
        closed.SetResult();
        await pending;
        Assert.False(explorer.IsBusy);
        await Assert.ThrowsAsync<InvalidOperationException>(() => dialog.SubmitAsync("late.cs"));
    });

    [Fact]
    public Task DialogSubmission_RejectsConcurrentOrRepeatedSubmit_ButAllowsRetryAfterError() => OnUi(async () =>
    {
        using var files = new Workspace();
        files.File("app.cs");
        await files.LoadAsync();
        var explorer = files.Explorer;
        var target = explorer.CaptureContext(Assert.Single(explorer.RootNodes))!;
        var release = new TaskCompletionSource();
        var attempt = 0;
        files.Operations.Rename = async (_, _, _, _) =>
        {
            if (++attempt == 1)
            {
                throw new IOException("Already exists");
            }
            await release.Task;
            System.IO.File.Move(files.Path("app.cs"), files.Path("new.cs"));
            return files.Path("new.cs");
        };
        explorer.FileDialogRequested = async dialog =>
        {
            var error = await Assert.ThrowsAsync<IOException>(() => dialog.SubmitAsync("new.cs"));
            Assert.Equal("Already exists", error.Message);
            var submitting = dialog.SubmitAsync("new.cs");
            await Assert.ThrowsAsync<InvalidOperationException>(() => dialog.SubmitAsync("again.cs"));
            release.SetResult();
            await submitting;
            await Assert.ThrowsAsync<InvalidOperationException>(() => dialog.SubmitAsync("again.cs"));
        };
        await explorer.ExecuteActionAsync(target, ExplorerAction.Rename);
        Assert.Equal(2, attempt);
        Assert.False(explorer.IsBusy);
        Assert.Equal("new.cs", Assert.Single(explorer.RootNodes).Name);
    });

    [Fact]
    public Task ErrorsPropagate_AndBusyIsResetWithoutClosingOrRetargetingTabs() => OnUi(async () =>
    {
        using var files = new Workspace();
        var path = files.File("app.cs");
        await files.LoadAsync();
        files.Workbench.OpenFile(path);
        files.Operations.Trash = (_, _, _) => throw new IOException("Trash unavailable");
        var explorer = files.Explorer;
        explorer.FileDialogRequested = dialog => dialog.SubmitAsync(null);
        var error = await Assert.ThrowsAsync<IOException>(() =>
            explorer.ExecuteActionAsync(explorer.CaptureContext(Assert.Single(explorer.RootNodes))!, ExplorerAction.Trash));
        Assert.Equal("Trash unavailable", error.Message);
        Assert.False(explorer.IsBusy);
        Assert.Equal(path, files.Workbench.ActiveFilePath);
        Assert.Single(files.Workbench.Tabs);
        Assert.True(System.IO.File.Exists(path));
    });

    [Fact]
    public Task OptionalServices_AreReadOnlyByDefault() => OnUi(async () =>
    {
        using var files = new Workspace();
        files.File("app.cs");
        var explorer = new FileExplorerViewModel(files.Workbench);
        explorer.SetRoot(files.Root);
        await explorer.RefreshAsync();
        var dialogs = 0;
        explorer.FileDialogRequested = _ => { ++dialogs; return Task.CompletedTask; };
        var target = explorer.CaptureContext(Assert.Single(explorer.RootNodes))!;
        Assert.False(explorer.CanExecuteAction(target, ExplorerAction.Trash));
        Assert.False(explorer.CanExecuteAction(target, ExplorerAction.ShowInFileManager));
        Assert.True(explorer.CanExecuteAction(target, ExplorerAction.Open));
        await explorer.ExecuteActionAsync(target, ExplorerAction.Trash);
        await explorer.ExecuteActionAsync(explorer.CaptureContext(null)!, ExplorerAction.NewFile);
        Assert.Equal(0, dialogs);
        Assert.True(System.IO.File.Exists(files.Path("app.cs")));
        Assert.False(explorer.IsBusy);
        explorer.SetRoot(null);
    });

    [Fact]
    public Task CreateHiddenFile_LocatesAndOpensResult_WithoutLoadingUnrelatedBranch() => OnUi(async () =>
    {
        using var files = new Workspace();
        files.File("src/existing.cs");
        files.File("unrelated/deep/file.cs");
        await files.LoadAsync();
        var explorer = files.Explorer;
        explorer.FileDialogRequested = dialog =>
        {
            Assert.Equal(ExplorerAction.NewFile, dialog.Action);
            Assert.False(dialog.IsDirectory);
            return dialog.SubmitAsync(".env");
        };
        var target = explorer.CaptureContext(explorer.RootNodes.Single(node => node.Name == "src"))!;
        await explorer.ExecuteActionAsync(target, ExplorerAction.NewFile);
        Assert.True(explorer.ShowHidden);
        Assert.Equal(files.Path("src", ".env"), explorer.SelectedNode?.FullPath);
        Assert.Equal(files.Path("src", ".env"), files.Workbench.ActiveFilePath);
        Assert.True(explorer.RootNodes.Single(node => node.Name == "src").IsExpanded);
        Assert.False(explorer.RootNodes.Single(node => node.Name == "unrelated").IsLoaded);
        Assert.Equal(("create", files.Path("src"), ".env", false), Assert.Single(files.Operations.Calls));
    });

    [Fact]
    public Task CreateFolderAtRoot_SelectsWithoutOpeningPreview_AndPreservesSearchQuery() => OnUi(async () =>
    {
        using var files = new Workspace();
        files.File("old.txt");
        await files.LoadAsync();
        var explorer = files.Explorer;
        explorer.Search = "new";
        explorer.FileDialogRequested = dialog =>
        {
            Assert.True(dialog.IsDirectory);
            Assert.Equal(files.Root, dialog.Path);
            return dialog.SubmitAsync("new-folder");
        };
        await explorer.ExecuteActionAsync(explorer.CaptureContext(null, isSearch: true)!, ExplorerAction.NewFolder);
        Assert.Equal("new", explorer.Search);
        Assert.Equal("new-folder", Assert.Single(explorer.SearchResults).Name);
        Assert.Equal("new-folder", explorer.SelectedNode?.Name);
        Assert.False(explorer.SelectedNode!.IsExpanded);
        Assert.Empty(files.Workbench.Tabs);
    });

    [Fact]
    public Task CreateFolder_ExpandsItsResultWhenNotEmpty() => OnUi(async () =>
    {
        using var files = new Workspace();
        await files.LoadAsync();
        files.Operations.Create = (_, parent, name, _) =>
        {
            var destination = Path.Combine(parent, name);
            Directory.CreateDirectory(destination);
            System.IO.File.WriteAllText(Path.Combine(destination, "child.txt"), "test");
            return Task.FromResult(destination);
        };
        var explorer = files.Explorer;
        explorer.FileDialogRequested = dialog => dialog.SubmitAsync("created");
        await explorer.ExecuteActionAsync(explorer.CaptureContext(null)!, ExplorerAction.NewFolder);
        var created = Assert.Single(explorer.RootNodes);
        Assert.True(created.IsExpanded);
        Assert.True(created.IsLoaded);
        Assert.Equal("child.txt", Assert.Single(created.Children).Name);
        Assert.Same(created, explorer.SelectedNode);
        Assert.Empty(files.Workbench.Tabs);
    });

    [Fact]
    public Task RenameDirectory_RetargetsOpenDescendantsExpandedBranchesAndSelection() => OnUi(async () =>
    {
        using var files = new Workspace();
        var oldFile = files.File("src/deep/app.cs");
        var unrelated = files.File("src-other/keep.cs");
        await files.LoadAsync();
        var explorer = files.Explorer;
        await explorer.RevealAsync(oldFile);
        files.Workbench.OpenFile(unrelated);
        files.Workbench.OpenFile(oldFile);
        explorer.FileDialogRequested = dialog => dialog.SubmitAsync("renamed");
        await explorer.ExecuteActionAsync(explorer.CaptureContext(explorer.RootNodes.Single(node => node.Name == "src"))!, ExplorerAction.Rename);
        var renamed = explorer.RootNodes.Single(node => node.Name == "renamed");
        Assert.True(renamed.IsExpanded);
        var deep = Assert.Single(renamed.Children);
        Assert.True(deep.IsExpanded);
        Assert.Equal(files.Path("renamed", "deep", "app.cs"), explorer.SelectedNode?.FullPath);
        Assert.Equal(files.Path("renamed", "deep", "app.cs"), files.Workbench.ActiveFilePath);
        Assert.Contains(files.Workbench.Tabs.OfType<FileTabViewModel>(), tab => tab.FilePath == unrelated);
        Assert.DoesNotContain(files.Workbench.Tabs.OfType<FileTabViewModel>(), tab => tab.FilePath == oldFile);
    });

    [Fact]
    public Task TrashDirectory_ClosesOnlyDescendants_AndRefreshesActiveSearch() => OnUi(async () =>
    {
        using var files = new Workspace();
        var removed = files.File("src/app.cs");
        var kept = files.File("src-other/app.cs");
        await files.LoadAsync();
        files.Workbench.OpenFile(kept);
        files.Workbench.OpenFile(removed);
        var explorer = files.Explorer;
        explorer.Search = "src";
        await WaitUntil(() => explorer.SearchResults.Count == 2);
        var target = explorer.CaptureContext(explorer.SearchResults.Single(node => node.Name == "src"), true)!;
        explorer.FileDialogRequested = dialog =>
        {
            Assert.True(dialog.IsDirectory);
            return dialog.SubmitAsync(null);
        };
        await explorer.ExecuteActionAsync(target, ExplorerAction.Trash);
        Assert.Equal("src", explorer.Search);
        Assert.Equal("src-other", Assert.Single(explorer.SearchResults).Name);
        Assert.Equal(kept, Assert.Single(files.Workbench.Tabs.OfType<FileTabViewModel>()).FilePath);
        Assert.DoesNotContain(explorer.RootNodes, node => node.Name == "src");
    });

    [Fact]
    public Task Refresh_PreservesExpandedBranchesQueryAndSelection_WithoutRevealingCollapsedSelection() => OnUi(async () =>
    {
        using var files = new Workspace();
        var path = files.File("src/deep/app.cs");
        files.File("untouched/nested/hidden.cs");
        await files.LoadAsync();
        var explorer = files.Explorer;
        await explorer.RevealAsync(path);
        files.File("src/deep/added.cs");
        await explorer.RefreshAsync();
        var src = explorer.RootNodes.Single(node => node.Name == "src");
        var deep = Assert.Single(src.Children);
        Assert.True(src.IsExpanded);
        Assert.True(deep.IsExpanded);
        Assert.Equal(2, deep.Children.Count);
        Assert.Equal(path, explorer.SelectedNode?.FullPath);
        Assert.False(explorer.RootNodes.Single(node => node.Name == "untouched").IsLoaded);
        explorer.Collapse(src);
        explorer.Search = "app";
        await WaitUntil(() => explorer.SearchResults.Count == 1);
        files.File("app-root.cs");
        await explorer.RefreshAsync();
        Assert.Equal("app", explorer.Search);
        Assert.Equal(2, explorer.SearchResults.Count);
        src = explorer.RootNodes.Single(node => node.Name == "src");
        Assert.False(src.IsExpanded);
        Assert.False(src.IsLoaded);
        Assert.Equal(path, explorer.SelectedNode?.FullPath);
    });

    [Fact]
    public Task CollapseAll_CancelsPendingRefreshExpansion_WithoutClearingSelectionOrQuery() => OnUi(async () =>
    {
        using var files = new Workspace();
        var path = files.File("src/deep/app.cs");
        await files.LoadAsync();
        var explorer = files.Explorer;
        await explorer.RevealAsync(path);
        explorer.Search = "app";
        await WaitUntil(() => explorer.SearchResults.Count == 1);
        var refresh = explorer.RefreshAsync();
        explorer.CollapseAll();
        await refresh;
        Assert.Equal("app", explorer.Search);
        Assert.False(Assert.Single(explorer.RootNodes).IsExpanded);
        await explorer.RefreshAsync();
        Assert.False(Assert.Single(explorer.RootNodes).IsLoaded);
    });

    [Fact]
    public Task PlatformActions_AreInjected_AndFailuresReachCaller() => OnUi(async () =>
    {
        using var files = new Workspace();
        var path = files.File("app.cs");
        await files.LoadAsync();
        var explorer = files.Explorer;
        await explorer.ExecuteActionAsync(explorer.CaptureContext(Assert.Single(explorer.RootNodes))!, ExplorerAction.ShowInFileManager);
        await explorer.ExecuteActionAsync(explorer.CaptureContext(null)!, ExplorerAction.OpenRoot);
        Assert.Equal([(path, false)], files.Platform.Shown);
        Assert.Equal([files.Root], files.Platform.Opened);
        files.Platform.Error = new IOException("File manager failed");
        var error = await Assert.ThrowsAsync<IOException>(() =>
            explorer.ExecuteActionAsync(explorer.CaptureContext(null)!, ExplorerAction.OpenRoot));
        Assert.Equal("File manager failed", error.Message);
    });

    [Fact]
    public Task ServiceCompletionFromOldRoot_CannotRefreshOrOpenFilesInNewRoot() => OnUi(async () =>
    {
        using var files = new Workspace();
        using var other = new Workspace();
        files.File("app.cs");
        other.File("other.cs");
        await files.LoadAsync();
        var explorer = files.Explorer;
        var release = new TaskCompletionSource();
        ExplorerOperationContext? capturedContext = null;
        files.Operations.Create = async (context, _, _, _) =>
        {
            capturedContext = context;
            await release.Task;
            return files.Path("new.cs");
        };
        explorer.FileDialogRequested = dialog => dialog.SubmitAsync("new.cs");
        var pending = explorer.ExecuteActionAsync(explorer.CaptureContext(null)!, ExplorerAction.NewFile);
        Assert.True(capturedContext!.IsCurrent());
        explorer.SetRoot(other.Root);
        Assert.False(capturedContext.IsCurrent());
        release.SetResult();
        await pending;
        await explorer.RefreshAsync();
        Assert.Equal(other.Root, explorer.Root);
        Assert.Equal("other.cs", Assert.Single(explorer.RootNodes).Name);
        Assert.Empty(files.Workbench.Tabs);
        Assert.False(explorer.IsBusy);
    });

    private static Task OnUi(Func<Task> test) => HeadlessApp.Session.Dispatch(async () =>
    {
        await test();
        return true;
    }, TestContext.Current.CancellationToken);

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException("Explorer action did not reach the expected state.");
            }
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(10);
        }
    }

    private sealed class Workspace : IDisposable
    {
        private readonly PtyHostService _hosts = new(NullLogger<PtyHostService>.Instance);
        public string Root { get; } = Directory.CreateTempSubdirectory("tkcode-explorer-actions-").FullName;
        public FakeOperations Operations { get; } = new();
        public FakePlatform Platform { get; } = new();
        public WorkbenchViewModel Workbench { get; }
        public FileExplorerViewModel Explorer { get; }

        public Workspace()
        {
            Workbench = new WorkbenchViewModel(_hosts);
            Explorer = new FileExplorerViewModel(Workbench, Operations, Platform);
        }

        public string Path(params string[] parts) => System.IO.Path.Combine([Root, .. parts]);
        public string File(string relative)
        {
            var path = Path(relative);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            System.IO.File.WriteAllText(path, "// explorer action test\n");
            return path;
        }

        public async Task LoadAsync()
        {
            Explorer.SetRoot(Root);
            await Explorer.RefreshAsync();
        }

        public void Dispose()
        {
            Explorer.SetRoot(null);
            foreach (var tab in Workbench.Tabs.OfType<IDisposable>())
            {
                tab.Dispose();
            }
            _hosts.Dispose();
            Directory.Delete(Root, recursive: true);
        }
    }

    private sealed class FakeOperations : IExplorerFileOperations
    {
        public List<(string Action, string Path, string? Name, bool IsDirectory)> Calls { get; } = [];
        public Func<ExplorerOperationContext, string, string, bool, Task<string>>? Create { get; set; }
        public Func<ExplorerOperationContext, string, string, bool, Task<string>>? Rename { get; set; }
        public Func<ExplorerOperationContext, string, bool, Task>? Trash { get; set; }

        public Task<string> CreateAsync(ExplorerOperationContext context, string parent, string name, bool isDirectory)
        {
            Calls.Add(("create", parent, name, isDirectory));
            if (Create is not null)
            {
                return Create(context, parent, name, isDirectory);
            }
            var path = System.IO.Path.Combine(parent, name);
            if (isDirectory)
            {
                Directory.CreateDirectory(path);
            }
            else
            {
                using var stream = System.IO.File.Create(path);
            }
            return Task.FromResult(path);
        }

        public Task<string> RenameAsync(ExplorerOperationContext context, string path, string name, bool isDirectory)
        {
            Calls.Add(("rename", path, name, isDirectory));
            if (Rename is not null)
            {
                return Rename(context, path, name, isDirectory);
            }
            var destination = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(path)!, name);
            if (isDirectory)
            {
                Directory.Move(path, destination);
            }
            else
            {
                System.IO.File.Move(path, destination);
            }
            return Task.FromResult(destination);
        }

        public Task TrashAsync(ExplorerOperationContext context, string path, bool isDirectory)
        {
            Calls.Add(("trash", path, null, isDirectory));
            if (Trash is not null)
            {
                return Trash(context, path, isDirectory);
            }
            if (isDirectory)
            {
                Directory.Delete(path, recursive: true);
            }
            else
            {
                System.IO.File.Delete(path);
            }
            return Task.CompletedTask;
        }
    }

    private sealed class FakePlatform : IExplorerPlatformActions
    {
        public List<(string Path, bool IsDirectory)> Shown { get; } = [];
        public List<string> Opened { get; } = [];
        public Exception? Error { get; set; }

        public Task ShowAsync(string path, bool isDirectory)
        {
            if (Error is not null)
            {
                throw Error;
            }
            Shown.Add((path, isDirectory));
            return Task.CompletedTask;
        }

        public Task OpenDirectoryAsync(string path)
        {
            if (Error is not null)
            {
                throw Error;
            }
            Opened.Add(path);
            return Task.CompletedTask;
        }
    }
}
