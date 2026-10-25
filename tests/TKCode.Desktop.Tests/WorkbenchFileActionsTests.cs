using Avalonia.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using TKCode.Desktop.Icons;
using TKCode.Desktop.Localization;
using TKCode.Desktop.Previews;
using TKCode.Desktop.Services;
using TKCode.Desktop.ViewModels;
using Xunit;

namespace TKCode.Desktop.Tests;

/// <summary>Model-side explorer effects only: temporary file moves, no trash, file manager or terminal host.</summary>
public sealed class WorkbenchFileActionsTests
{
    [Fact]
    public Task DirectoryRetarget_PreservesTabIdentityOrderAndActiveFile_AndExcludesSiblingPrefix() => OnUi(async () =>
    {
        await using var files = new Workspace();
        var firstPath = files.File("src/first.cs", "first source");
        var deepPath = files.File("src/deep/second.json", "{\"second\":true}");
        var siblingPath = files.File("src-other/keep.cs", "keep this source");
        var first = files.Open(firstPath);
        files.Workbench.AddToolCommand.Execute(new WorkbenchToolViewModel(WorkbenchTabKind.Browser));
        var browser = files.Workbench.ActiveTab!;
        var sibling = files.Open(siblingPath);
        var deep = files.Open(deepPath);
        await WaitUntil(() => files.Workbench.Tabs.OfType<FileTabViewModel>().All(tab => tab.Preview is not null));
        var originalOrder = files.Workbench.Tabs.ToArray();
        var siblingPreview = sibling.Preview;
        var activePaths = new List<string?>();
        files.Workbench.ActiveFileChanged += activePaths.Add;

        Directory.Move(files.Path("src"), files.Path("renamed"));
        files.Workbench.RetargetFiles(files.Path("src"), files.Path("renamed"), isDirectory: true);

        AssertIdentityOrder(originalOrder, files.Workbench);
        Assert.Same(deep, files.Workbench.ActiveTab);
        Assert.Same(deep, files.Workbench.ActiveFile);
        Assert.True(deep.IsActive);
        Assert.All(files.Workbench.Tabs.Where(tab => tab != deep), tab => Assert.False(tab.IsActive));
        Assert.Equal(files.Path("renamed", "first.cs"), first.FilePath);
        Assert.Equal(files.Path("renamed", "deep", "second.json"), deep.FilePath);
        Assert.Equal(deep.FilePath, files.Workbench.ActiveFilePath);
        Assert.Equal([deep.FilePath], activePaths);
        Assert.Equal(siblingPath, sibling.FilePath);
        Assert.Same(siblingPreview, sibling.Preview);
        Assert.Contains(browser, files.Workbench.Tabs);
        Assert.Equal("first.cs", first.Title);
        Assert.Equal("second.json", deep.Title);
        Assert.Equal([new BreadcrumbItem("renamed", false), new BreadcrumbItem("first.cs", true)], first.Breadcrumbs);
        Assert.Equal([new BreadcrumbItem("renamed", false), new BreadcrumbItem("deep", true), new BreadcrumbItem("second.json", true)], deep.Breadcrumbs);
        Assert.Equal(FileIcons.For("first.cs", false), (first.Icon, first.IconColor));
        Assert.Equal(FileIcons.For("second.json", false), (deep.FileIcon, deep.FileColor));
        await WaitUntil(() => first.Preview is CodePreviewViewModel && deep.Preview is CodePreviewViewModel);
        Assert.Equal("first source", Assert.IsType<CodePreviewViewModel>(first.Preview).Text);
        Assert.Equal("{\"second\":true}", Assert.IsType<CodePreviewViewModel>(deep.Preview).Text);
        Assert.False(first.HasNotice);
        Assert.False(deep.HasNotice);

        files.Workbench.OpenFile(deep.FilePath);
        AssertIdentityOrder(originalOrder, files.Workbench);
        Assert.Same(deep, files.Workbench.ActiveTab);
    });

    [Fact]
    public Task FileRetarget_UpdatesTitleBreadcrumbsIconAndNotifications_WithoutMatchingFilenamePrefix() => OnUi(async () =>
    {
        await using var files = new Workspace();
        var path = files.File("folder/report.cs", "old content");
        var siblingPath = files.File("folder/report.cs.bak", "backup content");
        var sibling = files.Open(siblingPath);
        var tab = files.Open(path);
        await WaitUntil(() => tab.Preview is not null && sibling.Preview is not null);
        var order = files.Workbench.Tabs.ToArray();
        var oldIcon = tab.Icon;
        var oldColor = tab.IconColor;
        var changed = new HashSet<string?>();
        var workbenchChanged = new HashSet<string?>();
        tab.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        files.Workbench.PropertyChanged += (_, e) => workbenchChanged.Add(e.PropertyName);
        var activePaths = new List<string?>();
        files.Workbench.ActiveFileChanged += activePaths.Add;
        var renamed = files.Path("folder", "renamed.json");

        System.IO.File.Move(path, renamed);
        System.IO.File.WriteAllText(renamed, "{\"renamed\":true}");
        files.Workbench.RetargetFiles(path, renamed, isDirectory: false);

        AssertIdentityOrder(order, files.Workbench);
        Assert.Same(tab, files.Workbench.ActiveTab);
        Assert.Equal(renamed, tab.FilePath);
        Assert.Equal("renamed.json", tab.Name);
        Assert.Equal("renamed.json", tab.Title);
        Assert.Equal([new BreadcrumbItem("folder", false), new BreadcrumbItem("renamed.json", true)], tab.Breadcrumbs);
        Assert.Equal(FileIcons.For("renamed.json", false), (tab.FileIcon, tab.FileColor));
        Assert.Equal((tab.FileIcon, tab.FileColor), (tab.Icon, tab.IconColor));
        Assert.NotEqual(oldIcon, tab.Icon);
        Assert.NotEqual(oldColor, tab.IconColor);
        Assert.Equal(siblingPath, sibling.FilePath);
        Assert.Equal([renamed], activePaths);
        foreach (var property in new[] { "FilePath", "Name", "Title", "FileIcon", "FileColor", "Icon", "IconColor", "Breadcrumbs" })
        {
            Assert.Contains(property, changed);
        }
        Assert.Contains(nameof(WorkbenchViewModel.ActiveFile), workbenchChanged);
        Assert.Contains(nameof(WorkbenchViewModel.ActiveFilePath), workbenchChanged);
        await WaitUntil(() => tab.Preview is CodePreviewViewModel);
        Assert.Equal("json", Assert.IsType<CodePreviewViewModel>(tab.Preview).Language);
        Assert.Equal("{\"renamed\":true}", Assert.IsType<CodePreviewViewModel>(tab.Preview).Text);
    });

    [Fact]
    public Task DirectoryClose_OnlyRemovesRelatedFiles_KeepsToolsAndSelectsSurvivingNeighbour() => OnUi(async () =>
    {
        await using var files = new Workspace();
        var first = files.Open(files.File("src/first.cs"));
        var sibling = files.Open(files.File("src-other/keep.cs"));
        var deep = files.Open(files.File("src/deep/second.cs"));
        files.Workbench.AddToolCommand.Execute(new WorkbenchToolViewModel(WorkbenchTabKind.Browser));
        var browser = files.Workbench.ActiveTab!;
        files.Workbench.ActivateCommand.Execute(deep);
        var emptied = 0;
        files.Workbench.Emptied += () => ++emptied;

        files.Workbench.CloseFiles(files.Path("src"), isDirectory: true);

        AssertIdentityOrder([sibling, browser], files.Workbench);
        Assert.DoesNotContain(first, files.Workbench.Tabs);
        Assert.DoesNotContain(deep, files.Workbench.Tabs);
        Assert.Same(browser, files.Workbench.ActiveTab);
        Assert.True(browser.IsActive);
        Assert.False(sibling.IsActive);
        Assert.Null(files.Workbench.ActiveFile);
        Assert.Null(files.Workbench.ActiveFilePath);
        Assert.True(files.Workbench.HasTabs);
        Assert.Equal(0, emptied);
        // CloseFiles is post-success bookkeeping; it must never delete anything itself.
        Assert.True(System.IO.File.Exists(first.FilePath));
        Assert.True(System.IO.File.Exists(deep.FilePath));
        Assert.True(System.IO.File.Exists(sibling.FilePath));
    });

    [Fact]
    public Task ExactFileClose_ExcludesSiblingPrefix_AndLastCloseResetsMaximizeAndNotifies() => OnUi(async () =>
    {
        await using var files = new Workspace();
        var exact = files.Open(files.File("notes.txt"));
        var sibling = files.Open(files.File("notes.txt.bak"));
        var emptied = 0;
        var activePaths = new List<string?>();
        files.Workbench.Emptied += () => ++emptied;
        files.Workbench.ActiveFileChanged += activePaths.Add;
        files.Workbench.IsMaximized = true;

        files.Workbench.CloseFiles(exact.FilePath, isDirectory: false);
        Assert.Same(sibling, Assert.Single(files.Workbench.Tabs));
        Assert.Same(sibling, files.Workbench.ActiveTab);
        Assert.Empty(activePaths);
        Assert.Equal(0, emptied);
        Assert.True(files.Workbench.IsMaximized);

        files.Workbench.CloseFiles(sibling.FilePath, isDirectory: false);
        Assert.Empty(files.Workbench.Tabs);
        Assert.False(files.Workbench.HasTabs);
        Assert.Null(files.Workbench.ActiveTab);
        Assert.Null(files.Workbench.ActiveFile);
        Assert.False(files.Workbench.IsMaximized);
        Assert.Equal(1, emptied);
        Assert.Equal(new string?[] { null }, activePaths);
        files.Workbench.CloseFiles(sibling.FilePath, isDirectory: false);
        Assert.Equal(1, emptied);
    });

    [Fact]
    public Task DirectoryRetarget_CancelsOldPreviewLoad_AndLateOldResultCannotReplaceReloadedPreview() => OnUi(async () =>
    {
        await using var files = new Workspace();
        var oldPath = files.File("src/slow.txt");
        var provider = new DelayedProvider();
        var tab = files.AddCustom(oldPath, provider);
        await WaitUntil(() => provider.Calls.Count == 1);
        var oldLoad = provider.Calls[0];
        try
        {
            Directory.Move(files.Path("src"), files.Path("renamed"));
            var newPath = files.Path("renamed", "slow.txt");
            files.Workbench.RetargetFiles(files.Path("src"), files.Path("renamed"), isDirectory: true);
            await WaitUntil(() => provider.Calls.Count == 2);
            var reload = provider.Calls[1];
            Assert.True(oldLoad.Token.IsCancellationRequested);
            Assert.False(reload.Token.IsCancellationRequested);
            Assert.Equal(oldPath, oldLoad.Path);
            Assert.Equal(newPath, reload.Path);
            Assert.Same(tab, files.Workbench.ActiveTab);

            reload.Complete();
            await WaitUntil(() => ReferenceEquals(tab.Preview, reload.Preview));
            oldLoad.Complete();
            await WaitUntil(() => oldLoad.Preview.IsDisposed);
            Assert.Same(reload.Preview, tab.Preview);
            Assert.False(reload.Preview.IsDisposed);
            Assert.False(tab.HasNotice);
        }
        finally
        {
            provider.CompleteAll();
        }
    });

    [Fact]
    public Task CloseFiles_CancelsPendingPreview_AndDisposesItsLateResult() => OnUi(async () =>
    {
        await using var files = new Workspace();
        var provider = new DelayedProvider();
        var tab = files.AddCustom(files.File("slow.txt"), provider);
        await WaitUntil(() => provider.Calls.Count == 1);
        var load = provider.Calls[0];
        try
        {
            files.Workbench.CloseFiles(tab.FilePath, isDirectory: false);
            Assert.True(load.Token.IsCancellationRequested);
            Assert.Empty(files.Workbench.Tabs);
            Assert.Null(files.Workbench.ActiveTab);
            load.Complete();
            await WaitUntil(() => load.Preview.IsDisposed);
            Assert.Null(tab.Preview);
        }
        finally
        {
            provider.CompleteAll();
        }
    });

    [Fact]
    public Task Retarget_ReloadsSelectedSourceViewInsteadOfReturningToMarkdownDefault() => OnUi(async () =>
    {
        await using var files = new Workspace();
        var oldPath = files.File("notes.md", "# before");
        var tab = files.Open(oldPath);
        await WaitUntil(() => tab.Preview is MarkdownPreviewViewModel);
        var source = tab.Views.Single(view => view.Provider is CodePreviewProvider);
        await tab.SelectViewCommand.ExecuteAsync(source);
        Assert.IsType<CodePreviewViewModel>(tab.Preview);
        var newPath = files.Path("renamed.mdx");
        System.IO.File.Move(oldPath, newPath);
        System.IO.File.WriteAllText(newPath, "# after");

        files.Workbench.RetargetFiles(oldPath, newPath, isDirectory: false);
        await WaitUntil(() => tab.Preview is CodePreviewViewModel);

        Assert.Same(tab, Assert.Single(files.Workbench.Tabs));
        Assert.Same(source.Provider, tab.Views.Single(view => view.IsSelected).Provider);
        Assert.True(tab.HasViewSwitch);
        Assert.Equal("# after", Assert.IsType<CodePreviewViewModel>(tab.Preview).Text);
        Assert.False(tab.HasNotice);
    });

    [Fact]
    public Task OpenTerminal_AlwaysAddsAndActivatesFreshUnstartedTab_WithoutRenderingOrLaunchingHost() => OnUi(async () =>
    {
        await using var files = new Workspace();
        var file = files.Open(files.File("keep.txt"));
        var opened = 0;
        var activePaths = new List<string?>();
        files.Workbench.OpenRequested += () => ++opened;
        files.Workbench.ActiveFileChanged += activePaths.Add;

        // Construction is deliberately lazy; do not attach a TerminalTabView or call EnsureStarted/Resize here.
        files.Workbench.OpenTerminal(files.Root);
        var first = Assert.IsType<TerminalTabViewModel>(files.Workbench.ActiveTab);
        files.Workbench.OpenTerminal(files.Root);
        var second = Assert.IsType<TerminalTabViewModel>(files.Workbench.ActiveTab);
        files.Workbench.OpenTerminal(null);
        var third = Assert.IsType<TerminalTabViewModel>(files.Workbench.ActiveTab);

        AssertIdentityOrder([file, first, second, third], files.Workbench);
        Assert.NotSame(first, second);
        Assert.NotSame(second, third);
        Assert.True(third.IsActive);
        Assert.False(first.IsActive);
        Assert.False(second.IsActive);
        Assert.False(file.IsActive);
        Assert.Null(files.Workbench.ActiveFile);
        Assert.Null(files.Workbench.ActiveFilePath);
        Assert.Equal(3, opened);
        Assert.Equal(new string?[] { null, null, null }, activePaths);
        foreach (var terminal in new[] { first, second, third })
        {
            Assert.Equal(WorkbenchTabKind.Terminal, terminal.Kind);
            Assert.Null(terminal.Frame);
            Assert.Null(terminal.Status);
            Assert.False(terminal.CanRestart);
        }
        Assert.True(await third.CloseAsync());
        Assert.DoesNotContain(third, files.Workbench.Tabs);
        Assert.Same(second, files.Workbench.ActiveTab);
    });

    private static void AssertIdentityOrder(IReadOnlyList<WorkbenchTabViewModel> expected, WorkbenchViewModel workbench)
    {
        Assert.Equal(expected.Count, workbench.Tabs.Count);
        for (var i = 0; i < expected.Count; ++i)
        {
            Assert.Same(expected[i], workbench.Tabs[i]);
        }
    }

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
                throw new TimeoutException("Workbench file action did not reach the expected state.");
            }
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }

    private sealed class Workspace : IAsyncDisposable
    {
        private readonly PtyHostService _hosts = new(NullLogger<PtyHostService>.Instance);
        public string Root { get; } = Directory.CreateTempSubdirectory("tkcode-workbench-files-").FullName;
        public WorkbenchViewModel Workbench { get; }

        public Workspace() => Workbench = new WorkbenchViewModel(_hosts) { Root = Root };

        public string Path(params string[] parts) => System.IO.Path.Combine([Root, .. parts]);

        public string File(string relative, string content = "workbench test content")
        {
            var path = Path(relative.Replace('/', System.IO.Path.DirectorySeparatorChar));
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            System.IO.File.WriteAllText(path, content);
            return path;
        }

        public FileTabViewModel Open(string path)
        {
            Workbench.OpenFile(path);
            return Assert.IsType<FileTabViewModel>(Workbench.ActiveTab);
        }

        public FileTabViewModel AddCustom(string path, IFilePreviewProvider provider)
        {
            var tab = new FileTabViewModel(path, Root, new FilePreviewRegistry([provider]));
            Workbench.Tabs.Add(tab);
            Workbench.ActivateCommand.Execute(tab);
            return tab;
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var tab in Workbench.Tabs.ToArray())
            {
                if (tab is IAsyncDisposable asyncDisposable)
                {
                    await asyncDisposable.DisposeAsync();
                }
                else if (tab is IDisposable disposable)
                {
                    disposable.Dispose();
                }
            }
            _hosts.Dispose();
            Directory.Delete(Root, recursive: true);
        }
    }

    private sealed class DelayedProvider : IFilePreviewProvider
    {
        public LucideKind Icon => LucideKind.File;
        public TKCodeLangResourceKind Title => TKCodeLangResourceKind.PreviewSource;
        public List<PendingPreview> Calls { get; } = [];
        public bool CanPreview(FileProbe probe) => true;

        public Task<FilePreviewViewModel> CreateAsync(FileProbe probe, CancellationToken cancellationToken)
        {
            var call = new PendingPreview(probe.Path, cancellationToken);
            Calls.Add(call);
            // Intentionally ignore cancellation so we can prove that late results are discarded and disposed.
            return call.Completion.Task;
        }

        public void CompleteAll()
        {
            foreach (var call in Calls)
            {
                call.Complete();
            }
        }
    }

    private sealed class PendingPreview(string path, CancellationToken token)
    {
        public string Path { get; } = path;
        public CancellationToken Token { get; } = token;
        public TrackingPreview Preview { get; } = new();
        public TaskCompletionSource<FilePreviewViewModel> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Complete() => Completion.TrySetResult(Preview);
    }

    private sealed class TrackingPreview : FilePreviewViewModel
    {
        public bool IsDisposed { get; private set; }
        public override void Dispose() => IsDisposed = true;
    }
}
