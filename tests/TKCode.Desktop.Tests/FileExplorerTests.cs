using Avalonia.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using TKCode.Desktop.Localization;
using TKCode.Desktop.Services;
using TKCode.Desktop.ViewModels;
using Xunit;

namespace TKCode.Desktop.Tests;

public sealed class FileExplorerTests
{
    [Fact]
    public Task Hierarchy_LoadsOnlyDirectChildren_AndCachesExpansion() => OnUi(async () =>
    {
        using var files = new Workspace();
        files.File("src/deep/App.cs");
        files.Directory("empty");
        files.File("readme.md");
        await files.LoadAsync(3);
        var explorer = files.Explorer;
        var src = explorer.RootNodes.Single(n => n.Name == "src");
        var empty = explorer.RootNodes.Single(n => n.Name == "empty");
        Assert.False(src.IsLoaded);
        Assert.False(src.IsExpanded);
        Assert.False(src.IsLoading);
        var placeholder = Assert.Single(src.Children);
        Assert.True(placeholder.IsPlaceholder);
        Assert.Equal(Strings.Get(TKCodeLangResourceKind.CompletionLoading), placeholder.Name);
        Assert.NotSame(placeholder, Assert.Single(empty.Children));
        Assert.Empty(explorer.RootNodes.Single(n => n.Name == "readme.md").Children);

        var transitions = new List<bool>();
        explorer.ExpansionChanged += node =>
        {
            if (node == src)
            {
                transitions.Add(node.IsExpanded);
            }
        };
        var loadedNodes = new List<FileNodeViewModel>();
        explorer.ChildrenLoaded += node =>
        {
            Dispatcher.UIThread.VerifyAccess();
            Assert.True(node.IsLoaded);
            Assert.DoesNotContain(node.Children, child => child.IsPlaceholder);
            loadedNodes.Add(node);
        };
        src.Children.CollectionChanged += (_, _) => Dispatcher.UIThread.VerifyAccess();
        var firstLoad = explorer.EnsureChildrenLoadedAsync(src);
        if (!firstLoad.IsCompleted)
        {
            Assert.Same(firstLoad, explorer.EnsureChildrenLoadedAsync(src));
        }
        await firstLoad;
        Assert.True(src.IsLoaded);
        Assert.False(src.IsLoading);
        Assert.False(src.IsExpanded); // Loading itself must not expand the node.
        var deep = Assert.Single(src.Children);
        Assert.Equal("deep", deep.Name);
        Assert.False(deep.IsLoaded);
        Assert.True(Assert.Single(deep.Children).IsPlaceholder);

        await explorer.ExpandAsync(src);
        await explorer.ExpandAsync(src); // Arc's callbacks may echo programmatic expansion.
        explorer.Collapse(src);
        explorer.Collapse(src);
        files.File("src/added-later.txt");
        await explorer.ExpandAsync(src);
        Assert.Same(deep, Assert.Single(src.Children));
        Assert.Equal([true, false, true], transitions);
        var emptyTransitions = new List<bool>();
        explorer.ExpansionChanged += node =>
        {
            if (node == empty)
            {
                emptyTransitions.Add(node.IsExpanded);
                if (!node.IsExpanded)
                {
                    Assert.True(Assert.Single(node.Children).IsPlaceholder);
                }
            }
        };
        await explorer.ExpandAsync(empty);
        Assert.True(empty.IsLoaded);
        Assert.False(empty.IsExpanded);
        Assert.Empty(empty.Children);
        await explorer.ExpandAsync(empty);
        Assert.False(empty.IsExpanded); // An already-loaded empty directory stays a leaf.
        Assert.Equal([true, false], emptyTransitions);
        Assert.Equal([src, empty], loadedNodes);
    });

    [Fact]
    public Task Hierarchy_SortsDirectoriesFirst_AndRebuildsHiddenFilter() => OnUi(async () =>
    {
        using var files = new Workspace();
        foreach (var name in new[] { "zebra", "Alpha", "bin", "obj", "node_modules", "dist", "build", "out", "target", "__pycache__", ".git", ".secret" })
        {
            files.Directory(name);
        }
        foreach (var name in new[] { "z.txt", "A.txt", ".env", ".DS_Store" })
        {
            files.File(name);
        }
        await files.LoadAsync(4);
        Assert.Equal(["Alpha", "zebra", "A.txt", "z.txt"], files.Explorer.RootNodes.Select(n => n.Name));
        var previous = files.Explorer.RootNodes[0];
        files.Explorer.ToggleHiddenCommand.Execute(null);
        await WaitUntil(() => files.Explorer.RootNodes.Count == 16);
        Assert.Contains(files.Explorer.RootNodes, n => n.Name == ".env");
        Assert.Contains(files.Explorer.RootNodes, n => n.Name == ".git");
        Assert.NotSame(previous, files.Explorer.RootNodes.Single(n => n.Name == "Alpha"));
        Assert.True(files.Explorer.RootNodes.Take(12).All(n => n.IsDirectory));
        Assert.Equal([".DS_Store", ".env", "A.txt", "z.txt"], files.Explorer.RootNodes.Skip(12).Select(n => n.Name));
        files.Explorer.ToggleHiddenCommand.Execute(null);
        await WaitUntil(() => files.Explorer.RootNodes.Count == 4);
        Assert.False(files.Explorer.ShowHidden);
    });

    [Fact]
    public Task CaseDistinctFiles_RetainSeparateTreeSearchAndEditorIdentities_WhenFileSystemSupportsThem() => OnUi(async () =>
    {
        using var files = new Workspace();
        var lowerPath = files.File("foo.cs");
        var upperPath = files.File("Foo.cs");
        var names = System.IO.Directory.EnumerateFiles(files.Root)
            .Select(System.IO.Path.GetFileName).ToHashSet(StringComparer.Ordinal);
        if (!names.Contains("foo.cs") || !names.Contains("Foo.cs"))
        {
            return; // Probe the actual volume rather than assuming a platform's case rules.
        }
        await files.LoadAsync(2);
        var explorer = files.Explorer;
        var lower = explorer.RootNodes.Single(node => node.Name == "foo.cs");
        var upper = explorer.RootNodes.Single(node => node.Name == "Foo.cs");
        Assert.NotSame(lower, upper);
        Assert.Equal(lowerPath, lower.FullPath);
        Assert.Equal(upperPath, upper.FullPath);
        explorer.Search = "foo";
        await WaitUntil(() => explorer.SearchResults.Count == 2);
        Assert.Same(lower, explorer.SearchResults.Single(node => node.Name == "foo.cs"));
        Assert.Same(upper, explorer.SearchResults.Single(node => node.Name == "Foo.cs"));
        await explorer.ActivateCommand.ExecuteAsync(lower);
        Assert.Equal(lowerPath, files.Workbench.ActiveFilePath);
        Assert.Same(lower, explorer.SelectedNode);
        await explorer.ActivateCommand.ExecuteAsync(upper);
        Assert.Equal(upperPath, files.Workbench.ActiveFilePath);
        Assert.Same(upper, explorer.SelectedNode);
        Assert.False(lower.IsSelected);
        Assert.True(upper.IsSelected);
        Assert.Equal(2, files.Workbench.Tabs.Count);
    });

    [Fact]
    public Task Search_IsSeparateFromLoadedHierarchy_AndRetainsRelativeHintAndIdentity() => OnUi(async () =>
    {
        using var files = new Workspace();
        files.File("src/deep/App.cs");
        files.File("App.md");
        await files.LoadAsync(2);
        var explorer = files.Explorer;
        var src = explorer.RootNodes.Single(n => n.Name == "src");
        await explorer.ExpandAsync(src);
        var deep = Assert.Single(src.Children);
        await explorer.ExpandAsync(deep);
        var app = Assert.Single(deep.Children);
        var roots = explorer.RootNodes.ToArray();
        var changes = new List<string?>();
        explorer.PropertyChanged += (_, args) => changes.Add(args.PropertyName);
        explorer.Search = "  aPP  ";
        Assert.True(explorer.IsSearching);
        await WaitUntil(() => explorer.SearchResults.Count == 2);
        Assert.Same(app, explorer.SearchResults.Single(n => n.Name == "App.cs"));
        Assert.Equal(Path.Combine("src", "deep"), app.Hint);
        Assert.Null(explorer.SearchResults.Single(n => n.Name == "App.md").Hint);
        Assert.Equal(roots, explorer.RootNodes);
        Assert.True(src.IsExpanded);
        Assert.True(deep.IsExpanded);
        explorer.Search = "   ";
        Assert.False(explorer.IsSearching);
        Assert.Empty(explorer.SearchResults);
        Assert.Equal(roots, explorer.RootNodes);
        Assert.Same(app, Assert.Single(deep.Children));
        Assert.Contains(nameof(FileExplorerViewModel.IsSearching), changes);
    });

    [Fact]
    public Task Search_HasStrict300ResultLimit_AndHonorsHiddenAndGitFilters() => OnUi(async () =>
    {
        using var files = new Workspace();
        for (var i = 0; i < 350; ++i)
        {
            files.File($"match-{i:D3}.txt");
        }
        files.File("nested/match-child.txt");
        files.File(".hidden/secret-hidden.txt");
        files.File("bin/secret-build.txt");
        files.File(".git/secret-git.txt");
        await files.LoadAsync(351);
        files.Explorer.Search = "match";
        await WaitUntil(() => files.Explorer.SearchResults.Count == 300);
        await Task.Delay(50);
        Assert.Equal(300, files.Explorer.SearchResults.Count);
        Assert.All(files.Explorer.SearchResults, node => Assert.Contains("match", node.Name));
        files.Explorer.Search = "secret";
        await Task.Delay(100);
        Assert.Empty(files.Explorer.SearchResults);
        files.Explorer.ShowHidden = true;
        await WaitUntil(() => files.Explorer.SearchResults.Count == 2);
        Assert.Equal(["secret-build.txt", "secret-hidden.txt"], files.Explorer.SearchResults.Select(n => n.Name));
        Assert.DoesNotContain(files.Explorer.SearchResults, n => n.Name == "secret-git.txt");
    });

    [Fact]
    public Task Search_RapidReplacementAndRootSwitchCannotPublishObsoleteResults() => OnUi(async () =>
    {
        using var files = new Workspace();
        using var other = new Workspace();
        for (var i = 0; i < 400; ++i)
        {
            files.File($"old/match-{i:D3}.txt");
        }
        files.File("latest.txt");
        other.File("new-root.txt");
        await files.LoadAsync(2);
        var explorer = files.Explorer;
        for (var i = 0; i < 12; ++i)
        {
            explorer.Search = i % 2 == 0 ? "match" : "missing";
        }
        explorer.Search = "latest";
        await WaitUntil(() => explorer.SearchResults.Count == 1);
        Assert.Equal("latest.txt", explorer.SearchResults[0].Name);
        explorer.Search = "match";
        explorer.Search = "";
        await Task.Delay(50);
        Assert.Empty(explorer.SearchResults);
        explorer.Search = "match";
        explorer.SetRoot(other.Root);
        Assert.Equal("", explorer.Search);
        explorer.Search = "new-root";
        await WaitUntil(() => explorer.RootNodes.Count == 1 && explorer.SearchResults.Count == 1);
        await Task.Delay(100);
        Assert.Equal("new-root.txt", Assert.Single(explorer.RootNodes).Name);
        Assert.Equal(other.Path("new-root.txt"), Assert.Single(explorer.SearchResults).FullPath);
        // Invalidate a root enumeration before it can return to the UI thread, too.
        explorer.SetRoot(files.Root);
        explorer.SetRoot(other.Root);
        await WaitUntil(() => explorer.RootNodes.Count == 1);
        await Task.Delay(50);
        Assert.Equal("new-root.txt", Assert.Single(explorer.RootNodes).Name);
        explorer.SetRoot(null);
        await Task.Delay(30);
        Assert.False(explorer.HasRoot);
        Assert.Empty(explorer.RootNodes);
        Assert.Empty(explorer.SearchResults);
    });

    [Fact]
    public Task Search_DoesNotFollowDirectoryLinksOrCycles() => OnUi(async () =>
    {
        if (OperatingSystem.IsWindows())
        {
            return; // Creating links is privilege-dependent on Windows.
        }
        using var files = new Workspace();
        using var outside = new Workspace();
        outside.File("secret-outside.txt");
        files.File("src/secret-inside.txt");
        System.IO.Directory.CreateSymbolicLink(files.Path("external"), outside.Root);
        System.IO.Directory.CreateSymbolicLink(files.Path("src", "cycle"), files.Root);
        await files.LoadAsync(2);
        files.Explorer.Search = "secret";
        await WaitUntil(() => files.Explorer.SearchResults.Count == 1);
        await Task.Delay(50);
        Assert.Equal("secret-inside.txt", Assert.Single(files.Explorer.SearchResults).Name);
        files.Explorer.Search = "external";
        await WaitUntil(() => files.Explorer.SearchResults.Count == 1);
        Assert.True(Assert.Single(files.Explorer.SearchResults).IsDirectory);
    });

    [Fact]
    public Task Reveal_LoadsOnlyAncestors_SelectsAndNotifiesWithoutOpeningFile() => OnUi(async () =>
    {
        using var files = new Workspace();
        files.File("src/deep/App.cs");
        files.File("unrelated/never-loaded.txt");
        await files.LoadAsync(2);
        var explorer = files.Explorer;
        var reveals = new List<FileNodeViewModel>();
        explorer.RevealRequested += node =>
        {
            Assert.Same(node, explorer.SelectedNode);
            Assert.True(node.IsSelected);
            reveals.Add(node);
        };
        await explorer.RevealAsync(files.Path("src", "deep", "App.cs"));
        var src = explorer.RootNodes.Single(n => n.Name == "src");
        var deep = Assert.Single(src.Children);
        var app = Assert.Single(deep.Children);
        Assert.True(src.IsExpanded);
        Assert.True(deep.IsExpanded);
        Assert.Same(app, Assert.Single(reveals));
        Assert.False(explorer.RootNodes.Single(n => n.Name == "unrelated").IsLoaded);
        Assert.Empty(files.Workbench.Tabs);
    });

    [Fact]
    public Task Reveal_ValidatesRootBoundaryAndDoesNotClearSearchForOutsideTargets() => OnUi(async () =>
    {
        using var files = new Workspace();
        var root = files.Directory("project");
        var sibling = files.File("project-other/outside.txt");
        var target = files.File("project/inside.txt");
        files.Explorer.SetRoot(root + System.IO.Path.DirectorySeparatorChar);
        await WaitUntil(() => files.Explorer.RootNodes.Count == 1);
        Assert.Equal(root, files.Explorer.Root);
        var reveals = 0;
        files.Explorer.RevealRequested += _ => ++reveals;
        await files.Explorer.RevealAsync(target);
        var selected = files.Explorer.SelectedNode;
        files.Explorer.Search = "inside";
        await WaitUntil(() => files.Explorer.SearchResults.Count == 1);
        await files.Explorer.RevealAsync(sibling);
        await files.Explorer.RevealAsync(files.Path("project", "..", "project-other", "outside.txt"));
        await files.Explorer.RevealAsync(root);
        Assert.Equal("inside", files.Explorer.Search);
        Assert.Same(selected, files.Explorer.SelectedNode);
        Assert.Equal(1, reveals);
        if (OperatingSystem.IsLinux())
        {
            await files.Explorer.RevealAsync(System.IO.Path.Combine(root.ToUpperInvariant(), "inside.txt"));
            Assert.Equal(1, reveals);
        }
    });

    [Fact]
    public Task Reveal_RejectsCaseDistinctSiblingRoot_WhenFileSystemSupportsIt() => OnUi(async () =>
    {
        using var files = new Workspace();
        var target = files.File("Project/item.cs");
        var outside = files.File("project/item.cs");
        var names = System.IO.Directory.EnumerateDirectories(files.Root)
            .Select(System.IO.Path.GetFileName).ToHashSet(StringComparer.Ordinal);
        if (!names.Contains("Project") || !names.Contains("project"))
        {
            return;
        }
        var explorer = files.Explorer;
        explorer.SetRoot(files.Path("Project"));
        await WaitUntil(() => explorer.RootNodes.Count == 1);
        explorer.Search = "item";
        await WaitUntil(() => explorer.SearchResults.Count == 1);
        var reveals = 0;
        explorer.RevealRequested += _ => ++reveals;
        await explorer.RevealAsync(outside);
        Assert.Equal("item", explorer.Search);
        Assert.Null(explorer.SelectedNode);
        Assert.Equal(0, reveals);
        await explorer.RevealAsync(target);
        Assert.Equal(target, explorer.SelectedNode?.FullPath);
        Assert.Equal(1, reveals);
    });

    [Fact]
    public Task Reveal_DirectoryExpandsOnlyWhenExplicit_AndDirectorySearchActivationExpandsIt() => OnUi(async () =>
    {
        using var files = new Workspace();
        files.File("src/deep/inside.txt");
        await files.LoadAsync(1);
        var explorer = files.Explorer;
        await explorer.RevealAsync(files.Path("src", "deep"));
        var src = Assert.Single(explorer.RootNodes);
        var deep = Assert.Single(src.Children);
        Assert.True(src.IsExpanded);
        Assert.False(deep.IsExpanded);
        Assert.False(deep.IsLoaded);
        Assert.Same(deep, explorer.SelectedNode);
        await explorer.RevealAsync(deep.FullPath, expandTarget: true);
        Assert.True(deep.IsExpanded);
        Assert.True(deep.IsLoaded);
        explorer.Collapse(src);
        explorer.Collapse(deep);
        explorer.Search = "deep";
        await WaitUntil(() => explorer.SearchResults.Count == 1);
        Assert.Same(deep, Assert.Single(explorer.SearchResults));
        await explorer.ActivateCommand.ExecuteAsync(deep);
        Assert.False(explorer.IsSearching);
        Assert.True(src.IsExpanded);
        Assert.True(deep.IsExpanded);
        Assert.Same(deep, explorer.SelectedNode);
        Assert.Empty(files.Workbench.Tabs);
    });

    [Fact]
    public Task Reveal_UserCollapseCancelsPendingHiddenTraversalAndSelection() => OnUi(async () =>
    {
        using var files = new Workspace();
        files.File("src/deep/nested/App.cs");
        for (var i = 0; i < 100; ++i)
        {
            files.File($"src/file-{i:D3}.txt");
        }
        await files.LoadAsync(1);
        var explorer = files.Explorer;
        var src = Assert.Single(explorer.RootNodes);
        var reveals = 0;
        explorer.RevealRequested += _ => ++reveals;
        var revealing = explorer.RevealAsync(files.Path("src", "deep", "nested", "App.cs"));
        var wasPending = !revealing.IsCompleted;
        var ancestorWasLoading = !src.IsLoaded;
        explorer.Collapse(src);
        await revealing;
        Assert.False(src.IsExpanded);
        if (wasPending)
        {
            Assert.Null(explorer.SelectedNode);
            Assert.Equal(0, reveals);
            if (ancestorWasLoading)
            {
                var deep = src.Children.Single(node => node.Name == "deep");
                Assert.False(deep.IsLoaded);
                Assert.False(deep.IsExpanded);
                Assert.True(Assert.Single(deep.Children).IsPlaceholder);
            }
        }
    });

    [Fact]
    public Task Loading_CollapseAndRootOrFilterChangesCannotReexpandOrPublishOldNodes() => OnUi(async () =>
    {
        using var files = new Workspace();
        using var other = new Workspace();
        for (var i = 0; i < 100; ++i)
        {
            files.File($"src/file-{i:D3}.txt");
        }
        other.File("other.txt");
        await files.LoadAsync(1);
        var explorer = files.Explorer;
        var src = Assert.Single(explorer.RootNodes);
        var expanding = explorer.ExpandAsync(src);
        explorer.Collapse(src);
        await expanding;
        Assert.True(src.IsLoaded);
        Assert.False(src.IsExpanded);
        explorer.ShowHidden = true;
        await WaitUntil(() => explorer.RootNodes.Count == 1);
        var rebuilt = Assert.Single(explorer.RootNodes);
        Assert.NotSame(src, rebuilt);
        var loading = explorer.ExpandAsync(rebuilt);
        var wasPending = !loading.IsCompleted;
        var revealing = explorer.RevealAsync(files.Path("src", "file-099.txt"));
        explorer.SetRoot(other.Root);
        await Task.WhenAll(loading, revealing);
        await WaitUntil(() => explorer.RootNodes.Count == 1);
        Assert.Equal("other.txt", Assert.Single(explorer.RootNodes).Name);
        Assert.Null(explorer.SelectedNode);
        Assert.False(rebuilt.IsLoading);
        if (wasPending)
        {
            Assert.False(rebuilt.IsLoaded);
            Assert.True(Assert.Single(rebuilt.Children).IsPlaceholder);
        }
        await explorer.ExpandAsync(src); // Stale node callbacks cannot resurrect an old hierarchy.
        Assert.False(src.IsExpanded);
    });

    [Fact]
    public Task MissingOrInaccessibleDirectoriesBecomeLoadedEmptyNodes_AndPlaceholdersCannotActivate() => OnUi(async () =>
    {
        using var files = new Workspace();
        var gone = files.Directory("gone");
        files.File("locked/secret.txt");
        await files.LoadAsync(2);
        var explorer = files.Explorer;
        var missing = explorer.RootNodes.Single(n => n.Name == "gone");
        var placeholder = Assert.Single(missing.Children);
        await explorer.ActivateCommand.ExecuteAsync(placeholder);
        explorer.SelectedNode = placeholder;
        await explorer.ExpandAsync(placeholder);
        Assert.Null(explorer.SelectedNode);
        Assert.Empty(files.Workbench.Tabs);
        System.IO.Directory.Delete(gone);
        await explorer.ExpandAsync(missing);
        Assert.True(missing.IsLoaded);
        Assert.False(missing.IsLoading);
        Assert.Empty(missing.Children);
        await explorer.RevealAsync(files.Path("gone", "missing.txt"));
        Assert.Null(explorer.SelectedNode);

        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            var lockedPath = files.Path("locked");
            var mode = System.IO.File.GetUnixFileMode(lockedPath);
            try
            {
                System.IO.File.SetUnixFileMode(lockedPath, UnixFileMode.None);
                var locked = explorer.RootNodes.Single(n => n.Name == "locked");
                await explorer.ExpandAsync(locked);
                Assert.True(locked.IsLoaded);
                Assert.False(locked.IsLoading);
                // A privileged test process can still read the directory; neither case may throw.
                Assert.DoesNotContain(locked.Children, n => n.IsPlaceholder);
            }
            finally
            {
                System.IO.File.SetUnixFileMode(lockedPath, mode);
            }
        }
    });

    [Fact]
    public Task SelectionNeverOpensFiles_ActiveEditorAndRefreshRestoreSelectionByPath() => OnUi(async () =>
    {
        using var files = new Workspace();
        files.File("src/deep/App.cs");
        files.File("readme.txt");
        await files.LoadAsync(2);
        var explorer = files.Explorer;
        var readme = explorer.RootNodes.Single(n => n.Name == "readme.txt");
        var notifications = new List<string?>();
        explorer.PropertyChanged += (_, args) => notifications.Add(args.PropertyName);
        explorer.SelectedNode = readme;
        Assert.True(readme.IsSelected);
        Assert.Empty(files.Workbench.Tabs);
        Assert.Contains(nameof(FileExplorerViewModel.SelectedNode), notifications);
        await explorer.RevealAsync(files.Path("src", "deep", "App.cs"));
        var app = explorer.SelectedNode!;
        Assert.False(readme.IsSelected);
        Assert.Empty(files.Workbench.Tabs);
        var openRequests = 0;
        files.Workbench.OpenRequested += () => ++openRequests;
        await explorer.ActivateCommand.ExecuteAsync(app);
        await explorer.ActivateCommand.ExecuteAsync(app);
        Assert.Equal(2, openRequests);
        Assert.Single(files.Workbench.Tabs);
        Assert.Same(app, explorer.SelectedNode);
        files.Workbench.OpenFile(readme.FullPath);
        Assert.Same(readme, explorer.SelectedNode);
        Assert.True(readme.IsSelected);
        Assert.False(app.IsSelected);
        files.Workbench.OpenFile(app.FullPath);
        explorer.ShowHidden = true;
        await WaitUntil(() => explorer.SelectedNode is { Name: "App.cs" } selected && !ReferenceEquals(app, selected));
        var refreshed = explorer.SelectedNode!;
        Assert.Equal(app.FullPath, refreshed.FullPath);
        Assert.True(refreshed.IsSelected);
        Assert.True(explorer.RootNodes.Single(n => n.Name == "src").IsExpanded);
        Assert.False(app.IsSelected);
        Assert.Equal(2, files.Workbench.Tabs.Count);

        // Search selection and editor activation still synchronize during a filter rebuild.
        explorer.ShowHidden = false;
        explorer.Search = "readme";
        await WaitUntil(() => explorer.SearchResults.Count == 1);
        await explorer.ActivateCommand.ExecuteAsync(explorer.SearchResults[0]);
        explorer.Search = "";
        await WaitUntil(() => explorer.SelectedNode?.Name == "readme.txt");
        Assert.True(explorer.SelectedNode!.IsSelected);
        Assert.Equal(2, files.Workbench.Tabs.Count);
    });

    [Fact]
    public Task ActivatingUnloadedFileSearchResult_RevealsItsAncestryWhenLeavingSearch() => OnUi(async () =>
    {
        using var files = new Workspace();
        files.File("src/deep/App.cs");
        await files.LoadAsync(1);
        var explorer = files.Explorer;
        var src = Assert.Single(explorer.RootNodes);
        explorer.Search = "app";
        await WaitUntil(() => explorer.SearchResults.Count == 1);
        var app = Assert.Single(explorer.SearchResults);
        await explorer.ActivateCommand.ExecuteAsync(app);
        Assert.Same(app, explorer.SelectedNode);
        Assert.False(src.IsLoaded);
        explorer.Search = "";
        await WaitUntil(() => src.IsLoaded && src.Children.Single() is { IsLoaded: true });
        Assert.Same(src, Assert.Single(explorer.RootNodes));
        Assert.True(src.IsExpanded);
        var deep = Assert.Single(src.Children);
        Assert.True(deep.IsExpanded);
        Assert.Same(app, Assert.Single(deep.Children));
        Assert.Same(app, explorer.SelectedNode);
        Assert.Single(files.Workbench.Tabs);
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
                throw new TimeoutException("File explorer did not reach the expected state.");
            }
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(10);
        }
    }

    private sealed class Workspace : IDisposable
    {
        private readonly PtyHostService _hosts = new(NullLogger<PtyHostService>.Instance);
        public string Root { get; } = System.IO.Directory.CreateTempSubdirectory("tkcode-explorer-").FullName;
        public WorkbenchViewModel Workbench { get; }
        public FileExplorerViewModel Explorer { get; }

        public Workspace()
        {
            Workbench = new WorkbenchViewModel(_hosts);
            Explorer = new FileExplorerViewModel(Workbench);
            Explorer.RootNodes.CollectionChanged += (_, _) => Dispatcher.UIThread.VerifyAccess();
            Explorer.SearchResults.CollectionChanged += (_, _) => Dispatcher.UIThread.VerifyAccess();
        }

        public string Path(params string[] parts) => System.IO.Path.Combine([Root, .. parts]);

        public string Directory(string relative) => System.IO.Directory.CreateDirectory(Path(relative)).FullName;

        public string File(string relative)
        {
            var path = Path(relative);
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            System.IO.File.WriteAllText(path, "// explorer test\n");
            return path;
        }

        public async Task LoadAsync(int count)
        {
            Explorer.SetRoot(Root);
            await WaitUntil(() => Explorer.RootNodes.Count == count);
        }

        public void Dispose()
        {
            Explorer.SetRoot(null);
            foreach (var tab in Workbench.Tabs.OfType<IDisposable>())
            {
                tab.Dispose();
            }
            _hosts.Dispose();
            System.IO.Directory.Delete(Root, recursive: true);
        }
    }
}
