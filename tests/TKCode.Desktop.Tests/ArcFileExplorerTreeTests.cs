using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.Logging.Abstractions;
using TKCode.Arc.Motion;
using TKCode.Desktop.Controls;
using TKCode.Desktop.Motion;
using TKCode.Desktop.Services;
using TKCode.Desktop.ViewModels;
using Xunit;

namespace TKCode.Desktop.Tests;

/// <summary>Exercise the real virtualized Arc tree through public APIs and headless input, not its private row types.</summary>
public sealed class ArcFileExplorerTreeTests
{
    [Fact]
    public async Task Mouse_InvokesFilesRepeatedly_AndTogglesDirectoriesOnlyOnce()
    {
        await WithTree(["src/a.cs", "src/b.cs", "README.md"], async fixture =>
        {
            var tree = fixture.Tree;
            var explorer = fixture.Explorer;
            var src = explorer.RootNodes.Single(n => n.Name == "src");
            var expansions = 0;
            var collapses = 0;
            tree.ItemExpanded += (_, _) => expansions++;
            tree.ItemCollapsed += (_, _) => collapses++;

            Click(fixture.Window, Row(tree, src));
            await WaitUntil(fixture.Window, () => src.IsLoaded, "directory mouse expansion");
            Assert.True(src.IsExpanded);
            Assert.True(tree.IsItemExpanded(src));
            Assert.Equal(1, expansions);
            Assert.Equal(0, collapses);
            Assert.Equal(0, fixture.OpenRequests);

            var file = src.Children.Single(n => n.Name == "a.cs");
            Click(fixture.Window, Row(tree, file));
            await Settle(fixture.Window);
            Assert.Same(file, explorer.SelectedNode);
            Assert.Equal(file.FullPath, fixture.Workbench.ActiveFilePath);
            Assert.Equal(1, fixture.OpenRequests);
            Click(fixture.Window, Row(tree, file));
            await Settle(fixture.Window);
            Assert.Equal(2, fixture.OpenRequests);
            Assert.Single(fixture.Workbench.Tabs);

            Click(fixture.Window, Row(tree, src));
            await Settle(fixture.Window);
            Assert.False(src.IsExpanded);
            Assert.False(tree.IsItemExpanded(src));
            Assert.Equal(1, expansions);
            Assert.Equal(1, collapses);
            Assert.Equal(2, fixture.OpenRequests);

            tree.FocusItem(src);
            Press(fixture.Window, Key.Enter);
            await Settle(fixture.Window);
            Assert.True(src.IsExpanded);
            Assert.True(tree.IsItemExpanded(src));
            Assert.Equal(2, expansions);
            Assert.Equal(1, collapses);
            Assert.Equal(2, fixture.OpenRequests);
        });
    }

    [Fact]
    public async Task Keyboard_FocusAndSelectionDoNotOpen_EnterAndSpaceDoEvenWhenAlreadySelected()
    {
        await WithTree(["a.cs", "b.cs"], async fixture =>
        {
            var first = fixture.Explorer.RootNodes[0];
            var second = fixture.Explorer.RootNodes[1];
            fixture.Tree.FocusItem(first);
            Press(fixture.Window, Key.Down);
            await Settle(fixture.Window);
            Assert.True(Row(fixture.Tree, second).IsKeyboardFocusWithin);
            Assert.Null(fixture.Explorer.SelectedNode);
            Assert.Empty(fixture.Workbench.Tabs);

            fixture.Tree.SelectedItem = first;
            Assert.Same(first, fixture.Explorer.SelectedNode);
            fixture.Explorer.SelectedNode = second;
            Assert.Same(second, fixture.Tree.SelectedItem);
            Assert.Equal(0, fixture.OpenRequests);
            Assert.Empty(fixture.Workbench.Tabs);

            Press(fixture.Window, Key.Enter);
            await Settle(fixture.Window);
            Assert.Same(second, fixture.Tree.SelectedItem);
            Assert.Equal(second.FullPath, fixture.Workbench.ActiveFilePath);
            Assert.Equal(1, fixture.OpenRequests);
            Press(fixture.Window, Key.Space);
            await Settle(fixture.Window);
            Assert.Equal(2, fixture.OpenRequests);
            Assert.Single(fixture.Workbench.Tabs);
        });
    }

    [Fact]
    public async Task RightClick_ResolvesTheActualPeerIdentity_WithDuplicateNames_WithoutInvokingOrToggling()
    {
        await WithTree(["left/same.cs", "right/same.cs"], async fixture =>
        {
            foreach (var directory in fixture.Explorer.RootNodes)
            {
                await fixture.Explorer.ExpandAsync(directory);
            }
            await Settle(fixture.Window);
            var left = fixture.Explorer.RootNodes.Single(n => n.Name == "left");
            var right = fixture.Explorer.RootNodes.Single(n => n.Name == "right");
            var first = Assert.Single(left.Children);
            var second = Assert.Single(right.Children);
            ExplorerContextRequestedEventArgs? request = null;
            fixture.Tree.ContextRequested += (_, e) => request = e;
            fixture.Tree.SelectedItem = first;
            var row = PeerRow(fixture.Tree, second);
            Click(fixture.Window, row, MouseButton.Right);
            Assert.Same(second, request?.Node);
            Assert.Same(second, fixture.Tree.SelectedItem);
            Assert.Same(second, fixture.Explorer.SelectedNode);
            Assert.Same(row, request?.FocusTarget);
            Assert.True(row.IsKeyboardFocusWithin);
            Assert.Equal(0, fixture.OpenRequests);
            Click(fixture.Window, Row(fixture.Tree, right), MouseButton.Right);
            Assert.Same(right, request?.Node);
            Assert.True(right.IsExpanded);
            Assert.True(fixture.Tree.IsItemExpanded(right));
            Assert.Empty(fixture.Workbench.Tabs);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ContextKeyboard_UsesFocusedRow_NotTheSelectedRow(bool shiftF10)
    {
        await WithTree(["a.cs", "b.cs"], async fixture =>
        {
            var first = fixture.Explorer.RootNodes[0];
            var second = fixture.Explorer.RootNodes[1];
            fixture.Tree.SelectedItem = first;
            fixture.Tree.FocusItem(second);
            await Settle(fixture.Window);
            Assert.Same(first, fixture.Tree.SelectedItem);
            ExplorerContextRequestedEventArgs? request = null;
            fixture.Tree.ContextRequested += (_, e) => request = e;
            Press(fixture.Window, shiftF10 ? Key.F10 : Key.Apps, shiftF10 ? RawInputModifiers.Shift : RawInputModifiers.None);
            Assert.Same(second, request?.Node);
            Assert.Same(second, fixture.Explorer.SelectedNode);
            Assert.True(request?.FocusTarget.IsKeyboardFocusWithin);
            Assert.Equal(0, fixture.OpenRequests);
        });
    }

    [Fact]
    public async Task ContextBlankSpace_IsRoot_ButScrollbarAndRecycledRowsAreNot()
    {
        await WithTree(["a.cs"], fixture =>
        {
            ExplorerContextRequestedEventArgs? request = null;
            var requests = 0;
            fixture.Tree.ContextRequested += (_, e) => { request = e; requests++; };
            var blank = fixture.Tree.TranslatePoint(new Point(40, fixture.Tree.Bounds.Height - 20), fixture.Window)!.Value;
            fixture.Window.MouseDown(blank, MouseButton.Right);
            fixture.Window.MouseUp(blank, MouseButton.Right);
            Assert.Equal(1, requests);
            Assert.Null(request!.Node);
            Assert.Equal(0, fixture.OpenRequests);
            return Task.CompletedTask;
        });
        await WithTree(Enumerable.Range(0, 240).Select(i => $"f{i:000}.cs").ToArray(), async fixture =>
        {
            var requests = 0;
            fixture.Tree.ContextRequested += (_, _) => requests++;
            fixture.Tree.ScrollIntoView(fixture.Explorer.RootNodes[^1]);
            await Settle(fixture.Window);
            var scrollbar = fixture.Tree.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.ScrollBar>()
                .Single(c => c.IsEffectivelyVisible && c.Bounds.Width > 0 && c.Bounds.Height > 0);
            Click(fixture.Window, scrollbar, MouseButton.Right);
            Assert.Equal(0, requests);
            Assert.Equal(0, fixture.OpenRequests);
        });
    }

    [Fact]
    public async Task PlaceholderContext_IsIgnored_NotMistakenForBlankRoot_AndPreservesSelection()
    {
        await WithTree(["a.cs"], async fixture =>
        {
            var file = Assert.Single(fixture.Explorer.RootNodes);
            // An uncached synthetic directory keeps its public placeholder without racing filesystem IO.
            var pending = new FileNodeViewModel(Path.Combine(fixture.Root, "pending"), "pending", true);
            var placeholder = Assert.Single(pending.Children);
            fixture.Explorer.RootNodes.Add(pending);
            await Settle(fixture.Window);
            fixture.Tree.Expand(pending);
            await Settle(fixture.Window);
            fixture.Tree.SelectedItem = file;
            var requests = 0;
            fixture.Tree.ContextRequested += (_, _) => requests++;
            Click(fixture.Window, Row(fixture.Tree, placeholder), MouseButton.Right);
            Assert.Equal(0, requests);
            Assert.Same(file, fixture.Tree.SelectedItem);
            Assert.Same(file, fixture.Explorer.SelectedNode);
            Assert.Equal(0, fixture.OpenRequests);
        });
    }

    [Fact]
    public async Task ExplorerDensity_IsScoped_AndRealRowsUse26PixelSlots()
    {
        await WithTree(["a.cs", "b.cs", "c.cs"], fixture =>
        {
            Assert.Equal(12, fixture.Tree.FontSize);
            Assert.Equal(new Thickness(2, 0, 2, 2), fixture.Tree.Padding);
            Assert.Equal(26d, fixture.Tree.Resources[TKCode.Arc.DesignTokens.TreeViewTokenKind.RowSlotHeight]);
            Assert.Equal(14d, fixture.Tree.Resources[TKCode.Arc.DesignTokens.TreeViewTokenKind.IndentWidth]);
            var first = Row(fixture.Tree, fixture.Explorer.RootNodes[0]);
            var second = Row(fixture.Tree, fixture.Explorer.RootNodes[1]);
            Assert.Equal(26, first.Bounds.Height);
            Assert.Equal(26, second.TranslatePoint(default, fixture.Tree)!.Value.Y - first.TranslatePoint(default, fixture.Tree)!.Value.Y);
            Assert.False(new TKCode.Arc.TreeView().Resources.ContainsKey(TKCode.Arc.DesignTokens.TreeViewTokenKind.RowSlotHeight));
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task ExplicitReveal_ScrollsSelectedFile_WithoutTakingEditorFocus()
    {
        var files = Enumerable.Range(0, 240).Select(i => $"file-{i:000}.cs").ToArray();
        await WithTree(files, async fixture =>
        {
            var target = fixture.Explorer.RootNodes[^1];
            var scroll = fixture.Tree.GetVisualDescendants().OfType<ScrollViewer>().Single();
            fixture.Explorer.SelectedNode = target;
            await Settle(fixture.Window);
            Assert.Same(target, fixture.Tree.SelectedItem);
            Assert.Equal(0, scroll.Offset.Y);
            Assert.Equal(0, fixture.OpenRequests);
            Assert.True(fixture.Editor.Focus());

            await fixture.Explorer.RevealAsync(target.FullPath);
            await WaitUntil(fixture.Window, () => scroll.Offset.Y > 0, "explicit reveal scroll");
            var row = Row(fixture.Tree, target);
            var point = row.TranslatePoint(new Point(0, row.Bounds.Height / 2), fixture.Tree)!.Value;
            Assert.InRange(point.Y, 0, fixture.Tree.Bounds.Height);
            Assert.True(fixture.Editor.IsKeyboardFocusWithin);
            Assert.Same(target, fixture.Tree.SelectedItem);
            Assert.Equal(0, fixture.OpenRequests);
        });
    }

    [Fact]
    public async Task CollapseDuringLoad_StaysCollapsed_AndFastReexpandKeepsOneSetOfChildren()
    {
        var entries = Enumerable.Range(0, 120).SelectMany(i => new[] { $"src/file-{i:000}.cs", $"pending/pending-{i:000}.cs" }).ToArray();
        await WithTree(entries, async fixture =>
        {
            var src = fixture.Explorer.RootNodes.Single(n => n.Name == "src");
            Assert.False(src.IsLoaded);
            Assert.True(Assert.Single(src.Children).IsPlaceholder);

            // Do not yield between expanding and collapsing: the pending IO completion must not reopen the row.
            fixture.Tree.Expand(src);
            fixture.Tree.Collapse(src);
            await fixture.Explorer.EnsureChildrenLoadedAsync(src);
            await Settle(fixture.Window);
            Assert.True(src.IsLoaded);
            Assert.False(src.IsLoading);
            Assert.False(src.IsExpanded);
            Assert.False(fixture.Tree.IsItemExpanded(src));
            Assert.Equal(120, src.Children.Count);
            Assert.DoesNotContain(src.Children, n => n.IsPlaceholder);
            Assert.DoesNotContain(fixture.Tree.GetVisualDescendants().OfType<Control>(),
                c => c.IsEffectivelyVisible && AutomationProperties.GetName(c) == "file-000.cs");

            fixture.Tree.Expand(src);
            fixture.Tree.Collapse(src);
            fixture.Tree.Expand(src);
            await Settle(fixture.Window);
            Assert.True(src.IsExpanded);
            Assert.True(fixture.Tree.IsItemExpanded(src));
            Assert.Equal(120, src.Children.Count);
            Assert.Equal(120, src.Children.Distinct().Count());
            Assert.Single(fixture.Tree.GetVisualDescendants().OfType<Control>(),
                c => c.IsEffectivelyVisible && AutomationProperties.GetName(c) == "file-000.cs");

            // Reverse again while a different directory's first load is still awaiting its UI continuation.
            var pending = fixture.Explorer.RootNodes.Single(n => n.Name == "pending");
            fixture.Tree.Expand(pending);
            fixture.Tree.Collapse(pending);
            fixture.Tree.Expand(pending);
            await fixture.Explorer.EnsureChildrenLoadedAsync(pending);
            await Settle(fixture.Window);
            Assert.True(pending.IsExpanded);
            Assert.True(fixture.Tree.IsItemExpanded(pending));
            Assert.Equal(120, pending.Children.Count);
            Assert.Equal(120, pending.Children.Distinct().Count());
            Assert.Equal(0, fixture.OpenRequests);
        });
    }

    [Fact]
    public async Task ActiveEditorFile_LoadCompletionDoesNotReopenCollapsedAncestor()
    {
        await WithTree(["src/a.cs"], async fixture =>
        {
            var src = Assert.Single(fixture.Explorer.RootNodes);
            var path = Path.Combine(fixture.Root, "src", "a.cs");
            Assert.False(src.IsLoaded);
            fixture.Workbench.OpenFile(path);
            Assert.Equal(1, fixture.OpenRequests);
            Assert.Null(fixture.Explorer.SelectedNode);

            // Editor selection becomes resolvable during this first load, after the user has already collapsed src.
            fixture.Tree.Expand(src);
            fixture.Tree.Collapse(src);
            await fixture.Explorer.EnsureChildrenLoadedAsync(src);
            await Settle(fixture.Window);
            var file = Assert.Single(src.Children);
            Assert.True(src.IsLoaded);
            Assert.False(src.IsExpanded);
            Assert.False(fixture.Tree.IsItemExpanded(src));
            Assert.Same(file, fixture.Explorer.SelectedNode);
            Assert.True(file.IsSelected);
            Assert.Null(fixture.Tree.SelectedItem);
            Assert.Equal(path, fixture.Workbench.ActiveFilePath);
            Assert.Equal(1, fixture.OpenRequests);
            Assert.Single(fixture.Workbench.Tabs);

            await fixture.Explorer.ExpandAsync(src);
            await Settle(fixture.Window);
            Assert.Same(file, fixture.Tree.SelectedItem);
            Assert.Equal(1, fixture.OpenRequests);
        });
    }

    [Fact]
    public async Task WorkspaceReplacementDuringLoad_DoesNotRestoreOldRowsOrSelection()
    {
        await WithTree(Enumerable.Range(0, 120).Select(i => $"old/file-{i:000}.cs").ToArray(), async fixture =>
        {
            var oldDirectory = Assert.Single(fixture.Explorer.RootNodes);
            var oldLoad = fixture.Explorer.ExpandAsync(oldDirectory);
            var replacement = Directory.CreateDirectory(Path.Combine(fixture.Root, "replacement")).FullName;
            File.WriteAllText(Path.Combine(replacement, "new.cs"), "// new workspace");
            fixture.Explorer.SetRoot(replacement);
            await oldLoad;
            await WaitUntil(fixture.Window, () => fixture.Explorer.RootNodes.Count == 1, "replacement workspace roots");
            Assert.Equal("new.cs", Assert.Single(fixture.Explorer.RootNodes).Name);
            Assert.Equal(fixture.Explorer.RootNodes, fixture.Tree.ItemsSource!.Cast<FileNodeViewModel>());
            Assert.Null(fixture.Tree.SelectedItem);
            Assert.False(fixture.Tree.IsItemExpanded(oldDirectory));
            Assert.DoesNotContain(fixture.Tree.GetVisualDescendants().OfType<Control>(),
                c => c.IsEffectivelyVisible && AutomationProperties.GetName(c) == "old");
            Assert.Equal(0, fixture.OpenRequests);
        });
    }

    [Fact]
    public async Task EmptyDirectory_UsesPublicFolderIcon_AndPlaceholderCannotOpen()
    {
        await WithTree(["empty/", "a.cs"], async fixture =>
        {
            var directory = fixture.Explorer.RootNodes.Single(n => n.IsDirectory);
            var placeholder = Assert.Single(directory.Children);
            Assert.True(placeholder.IsPlaceholder);
            Assert.Null(fixture.Tree.IconSelector!(placeholder));
            Assert.Null(fixture.Tree.IconSelector(directory)); // Arc supplies the animated branch glyph.
            var branch = Assert.IsAssignableFrom<IExpandCollapseProvider>(
                ControlAutomationPeer.CreatePeerForElement(Row(fixture.Tree, directory)));
            Assert.Equal(ExpandCollapseState.Collapsed, branch.ExpandCollapseState);
            await fixture.Explorer.ActivateCommand.ExecuteAsync(placeholder);
            fixture.Tree.SelectedItem = placeholder;
            Assert.Null(fixture.Explorer.SelectedNode);
            Assert.Equal(0, fixture.OpenRequests);

            await fixture.Explorer.ExpandAsync(directory);
            await Settle(fixture.Window);
            Assert.True(directory.IsLoaded);
            Assert.Empty(directory.Children);
            var folder = Assert.IsAssignableFrom<Geometry>(fixture.Tree.IconSelector(directory));
            Assert.True(folder.Bounds.Width > 0);
            var file = fixture.Explorer.RootNodes.Single(n => !n.IsDirectory);
            Assert.NotNull(fixture.Tree.IconSelector(file));
            var leaf = Assert.IsAssignableFrom<IExpandCollapseProvider>(
                ControlAutomationPeer.CreatePeerForElement(Row(fixture.Tree, directory)));
            Assert.Equal(ExpandCollapseState.LeafNode, leaf.ExpandCollapseState);
            Assert.False(directory.IsExpanded);
            Assert.False(fixture.Tree.IsItemExpanded(directory));
            Assert.Equal(0, fixture.OpenRequests);
        });
    }

    [Fact]
    public async Task ModelReplacementAndUnload_DisconnectOldModelAndMotionHandlers()
    {
        await WithTree(["old/a.cs", "b.cs"], async fixture =>
        {
            var previous = fixture.Explorer;
            var oldDirectory = previous.RootNodes.Single(n => n.IsDirectory);
            var oldFile = previous.RootNodes.Single(n => !n.IsDirectory);
            var replacement = new FileExplorerViewModel(fixture.Workbench);
            replacement.SetRoot(fixture.Root);
            await WaitUntil(fixture.Window, () => replacement.RootNodes.Count == 2, "replacement model roots");
            fixture.Tree.Explorer = replacement;
            await Settle(fixture.Window);
            var current = replacement.RootNodes.Single(n => !n.IsDirectory);
            replacement.SelectedNode = current;
            Assert.Same(current, fixture.Tree.SelectedItem);

            await previous.ExpandAsync(oldDirectory);
            await previous.RevealAsync(oldFile.FullPath);
            await Settle(fixture.Window);
            Assert.Equal(replacement.RootNodes, fixture.Tree.ItemsSource!.Cast<FileNodeViewModel>());
            Assert.Same(current, fixture.Tree.SelectedItem);
            Assert.False(fixture.Tree.IsItemExpanded(oldDirectory));

            var content = fixture.Window.Content;
            fixture.Window.Content = null;
            await Settle(fixture.Window);
            Assert.Null(fixture.Tree.ItemsSource);
            Assert.Null(fixture.Tree.SelectedItem);
            await replacement.RevealAsync(current.FullPath);
            previous.SelectedNode = null;
            Assert.Null(fixture.Tree.SelectedItem);
            var wasDisabled = MotionSettings.ForceDisabled;
            try
            {
                ArcMotion.SetLevel(fixture.Tree, ArcMotionLevel.Full);
                MotionSettings.ForceDisabled = false;
                MotionSettings.ForceDisabled = true;
                Assert.Equal(ArcMotionLevel.Full, ArcMotion.GetLevel(fixture.Tree));
            }
            finally
            {
                MotionSettings.ForceDisabled = wasDisabled;
            }

            fixture.Window.Content = content;
            await Settle(fixture.Window);
            Assert.Equal(replacement.RootNodes, fixture.Tree.ItemsSource!.Cast<FileNodeViewModel>());
            Assert.Same(current, fixture.Tree.SelectedItem);
            Assert.Equal(0, fixture.OpenRequests);
            replacement.SetRoot(null);
        });
    }

    [Fact]
    public async Task CachedNestedSelection_StaysHiddenUntilAncestorExpanded_WithoutOpeningFile()
    {
        await WithTree(["src/deep/a.cs"], async fixture =>
        {
            await fixture.Explorer.RevealAsync(Path.Combine(fixture.Root, "src", "deep", "a.cs"));
            var src = Assert.Single(fixture.Explorer.RootNodes);
            var deep = Assert.Single(src.Children);
            var file = Assert.Single(deep.Children);
            fixture.Explorer.Collapse(src);
            fixture.Explorer.SelectedNode = null;
            await Settle(fixture.Window);
            Assert.False(src.IsExpanded);
            Assert.False(fixture.Tree.IsItemExpanded(src));

            fixture.Explorer.SelectedNode = file;
            await Settle(fixture.Window);
            Assert.Same(file, fixture.Explorer.SelectedNode);
            Assert.True(file.IsSelected);
            Assert.Null(fixture.Tree.SelectedItem);
            Assert.False(src.IsExpanded);
            Assert.False(fixture.Tree.IsItemExpanded(src));
            Assert.Equal(0, fixture.OpenRequests);
            Assert.Empty(fixture.Workbench.Tabs);

            await fixture.Explorer.ExpandAsync(src);
            await Settle(fixture.Window);
            Assert.True(src.IsExpanded);
            Assert.True(deep.IsExpanded);
            Assert.Same(file, fixture.Tree.SelectedItem);
            Assert.Equal(0, fixture.OpenRequests);
        });
    }

    [Fact]
    public async Task CollapseAfterReveal_WinsOverDeferredScroll()
    {
        await WithTree(["src/deep/a.cs"], async fixture =>
        {
            var path = Path.Combine(fixture.Root, "src", "deep", "a.cs");
            await fixture.Explorer.RevealAsync(path);
            await Settle(fixture.Window);
            var src = Assert.Single(fixture.Explorer.RootNodes);
            fixture.Explorer.Collapse(src);
            await Settle(fixture.Window);

            // Cached reveal completes in this UI turn, but its ScrollIntoView is queued at Background priority.
            await fixture.Explorer.RevealAsync(path);
            Assert.True(src.IsExpanded);
            fixture.Explorer.Collapse(src);
            await Settle(fixture.Window);
            Assert.False(src.IsExpanded);
            Assert.False(fixture.Tree.IsItemExpanded(src));
            Assert.DoesNotContain(fixture.Tree.GetVisualDescendants().OfType<Control>(),
                c => c.IsEffectivelyVisible && AutomationProperties.GetName(c) == "a.cs");
            Assert.Equal(0, fixture.OpenRequests);
            Assert.Empty(fixture.Workbench.Tabs);
        });
    }

    [Fact]
    public async Task Reattach_RespectsDirectoryCollapsedWhileUnloaded()
    {
        await WithTree(["src/a.cs"], async fixture =>
        {
            var src = Assert.Single(fixture.Explorer.RootNodes);
            await fixture.Explorer.ExpandAsync(src);
            await Settle(fixture.Window);
            Assert.True(fixture.Tree.IsItemExpanded(src));
            var content = fixture.Window.Content;
            fixture.Window.Content = null;
            fixture.Explorer.Collapse(src);
            fixture.Window.Content = content;
            await Settle(fixture.Window);

            Assert.False(src.IsExpanded);
            Assert.False(fixture.Tree.IsItemExpanded(src));
            Assert.DoesNotContain(fixture.Tree.GetVisualDescendants().OfType<Control>(),
                c => c.IsEffectivelyVisible && AutomationProperties.GetName(c) == "a.cs");
            Assert.Equal(0, fixture.OpenRequests);
        });
    }

    [Fact]
    public async Task ArcMotion_FollowsProjectMotionSettings_WithoutUsingTheManualMotionClock()
    {
        await WithTree(["src/a.cs"], fixture =>
        {
            var forced = MotionSettings.ForceDisabled;
            var reduced = MotionSettings.UserReduceMotion;
            try
            {
                Assert.Equal(ArcMotionLevel.Off, ArcMotion.GetLevel(fixture.Tree));
                MotionSettings.UserReduceMotion = false;
                MotionSettings.ForceDisabled = false;
                Assert.Equal(MotionSettings.IsEnabled ? ArcMotionLevel.Full : ArcMotionLevel.Off,
                    ArcMotion.GetLevel(fixture.Tree));
                MotionSettings.UserReduceMotion = true;
                Assert.Equal(ArcMotionLevel.Off, ArcMotion.GetLevel(fixture.Tree));
                MotionSettings.UserReduceMotion = false;
                MotionSettings.ForceDisabled = true;
                Assert.Equal(ArcMotionLevel.Off, ArcMotion.GetLevel(fixture.Tree));
            }
            finally
            {
                MotionSettings.UserReduceMotion = reduced;
                MotionSettings.ForceDisabled = forced;
            }
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task LargeTree_RealizedPublicVisualDescendantsStayBounded_WhenScrolling()
    {
        await WithTree(["seed.cs"], async fixture =>
        {
            var initialCount = fixture.Tree.GetVisualDescendants().OfType<Control>().Count();
            // No filesystem cost is needed to exercise Arc's public ItemsSource virtualization contract.
            for (var i = 0; i < 10_000; i++)
            {
                fixture.Explorer.RootNodes.Add(new FileNodeViewModel(Path.Combine(fixture.Root, $"large-{i:00000}.cs"), $"large-{i:00000}.cs", false));
            }
            await Settle(fixture.Window);
            var beforeScroll = fixture.Tree.GetVisualDescendants().OfType<Control>().Count();
            Assert.InRange(beforeScroll, 1, initialCount + 600);
            Assert.True(beforeScroll < fixture.Explorer.RootNodes.Count / 10,
                $"A bounded viewport realized {beforeScroll} controls for {fixture.Explorer.RootNodes.Count} nodes.");

            var last = fixture.Explorer.RootNodes[^1];
            fixture.Tree.ScrollIntoView(last);
            await Settle(fixture.Window);
            Assert.NotNull(Row(fixture.Tree, last));
            Assert.InRange(fixture.Tree.GetVisualDescendants().OfType<Control>().Count(), 1, initialCount + 600);
        });
    }

    [Theory]
    [InlineData(false, 1.0, "explorer-tree-dark")]
    [InlineData(true, 1.0, "explorer-tree-light")]
    [InlineData(false, 1.3, "explorer-tree-dark-zoom")]
    [InlineData(true, 1.3, "explorer-tree-light-zoom")]
    public async Task BrowserTree_RendersLightDarkAndZoomHost(bool light, double scale, string snapshot)
    {
        await WithTree(["src/Controls/Button.cs", "src/App.cs", "assets/logo.svg", "empty/", "README.md", "package.json", ".env"], async fixture =>
        {
            var oldScale = UiZoom.Scale;
            try
            {
                await TestTheme.ApplyAsync(light ? TestTheme.LightId : TestTheme.DarkId);
                UiZoom.Scale = scale;
                await fixture.Explorer.RevealAsync(Path.Combine(fixture.Root, "src", "Controls", "Button.cs"));
                await fixture.Explorer.ExpandAsync(fixture.Explorer.RootNodes.Single(n => n.Name == "empty"));
                await Settle(fixture.Window);
                Assert.Equal(ArcMotionLevel.Off, ArcMotion.GetLevel(fixture.Tree));
                Assert.Equal("Button.cs", ((FileNodeViewModel)fixture.Tree.SelectedItem!).Name);
                Assert.Equal(scale, fixture.Zoom.LayoutTransform is ScaleTransform transform ? transform.ScaleX : 1.0);
                Assert.InRange(fixture.Tree.Bounds.Height, 1, fixture.Window.Bounds.Height / scale);
                HeadlessApp.Save(fixture.Window, snapshot);
            }
            finally
            {
                UiZoom.Scale = oldScale;
                await TestTheme.ApplyAsync(TestTheme.DarkId);
            }
        });
    }

    internal static Task WithTree(string[] entries, Func<TreeFixture, Task> body) =>
        HeadlessApp.Session.Dispatch(async () =>
        {
            using var fixture = new TreeFixture(entries);
            var rootCount = entries.Select(p => p.Split('/')[0]).Where(name => !name.StartsWith('.')).Distinct().Count();
            await WaitUntil(fixture.Window, () => fixture.Explorer.RootNodes.Count == rootCount, "initial explorer roots");
            await body(fixture);
            return true;
        }, TestContext.Current.CancellationToken);

    private static Control Row(FileExplorerTree tree, FileNodeViewModel node) =>
        // Recycled Arc controls retain their automation names while hidden in the bounded pool.
        Assert.Single(tree.GetVisualDescendants().OfType<Control>(),
            c => c.IsEffectivelyVisible && AutomationProperties.GetName(c) == node.Name);

    private static Control PeerRow(FileExplorerTree tree, FileNodeViewModel node)
    {
        var previous = tree.SelectedItem;
        try
        {
            foreach (var control in tree.GetVisualDescendants().OfType<Control>().Where(c => c.IsEffectivelyVisible))
            {
                var peer = ControlAutomationPeer.CreatePeerForElement(control);
                if (peer?.GetAutomationControlType() == AutomationControlType.TreeItem && peer is ISelectionItemProvider selection)
                {
                    selection.Select();
                    if (ReferenceEquals(node, tree.SelectedItem))
                    {
                        return control;
                    }
                }
            }
            throw new InvalidOperationException("Expected a realized TreeItem peer for the requested node identity.");
        }
        finally
        {
            tree.SelectedItem = previous;
        }
    }

    internal static void Click(Window window, Control row, MouseButton button = MouseButton.Left)
    {
        var point = row.TranslatePoint(new Point(Math.Min(row.Bounds.Width - 4, 120), row.Bounds.Height / 2), window)!.Value;
        window.MouseMove(point);
        window.MouseDown(point, button);
        window.MouseUp(point, button);
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
    }

    internal static void Press(Window window, Key key, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        window.KeyPress(key, modifiers, PhysicalKey.None, null);
        window.KeyRelease(key, modifiers, PhysicalKey.None, null);
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
    }

    internal static async Task WaitUntil(Window window, Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, $"Timed out waiting for {what}.");
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            await Task.Delay(20);
        }
        await Settle(window);
    }

    internal static async Task Settle(Window window)
    {
        // Arc rebuilds on Background priority and has its own frame clock. All deterministic tests use motion Off.
        for (var i = 0; i < 3; i++)
        {
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            await Task.Delay(20);
        }
    }

    internal sealed class TreeFixture : IDisposable
    {
        private readonly PtyHostService _hosts = new(NullLogger<PtyHostService>.Instance);
        private readonly bool _wasMotionDisabled;

        public TreeFixture(string[] entries)
        {
            _wasMotionDisabled = MotionSettings.ForceDisabled;
            MotionSettings.ForceDisabled = true;
            Root = Directory.CreateTempSubdirectory("tkcode-arc-tree-").FullName;
            foreach (var entry in entries)
            {
                var path = Path.Combine(Root, entry.Replace('/', Path.DirectorySeparatorChar));
                if (entry.EndsWith('/'))
                {
                    Directory.CreateDirectory(path);
                }
                else
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    File.WriteAllText(path, "// file tree test\n");
                }
            }
            Workbench = new WorkbenchViewModel(_hosts);
            Workbench.OpenRequested += () => OpenRequests++;
            Explorer = new FileExplorerViewModel(Workbench);
            Explorer.SetRoot(Root);
            Tree = new FileExplorerTree { Explorer = Explorer };
            Editor = new TextBox { Text = "Editor keeps keyboard focus during Reveal", Height = 32 };
            var layout = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
            layout.Children.Add(Editor);
            Grid.SetRow(Tree, 1);
            layout.Children.Add(Tree);
            Zoom = new ZoomHost { Child = layout };
            Window = new Avalonia.Controls.Window { Width = 420, Height = 460, Content = Zoom };
            Window.Show();
            Dispatcher.UIThread.RunJobs();
            Window.UpdateLayout();
        }

        public string Root { get; }
        public WorkbenchViewModel Workbench { get; }
        public FileExplorerViewModel Explorer { get; }
        public FileExplorerTree Tree { get; }
        public TextBox Editor { get; }
        public ZoomHost Zoom { get; }
        public Window Window { get; }
        public int OpenRequests { get; private set; }

        public void Dispose()
        {
            Window.Close();
            Explorer.SetRoot(null);
            foreach (var tab in Workbench.Tabs.OfType<IDisposable>())
            {
                tab.Dispose();
            }
            _hosts.Dispose();
            MotionSettings.ForceDisabled = _wasMotionDisabled;
            Directory.Delete(Root, recursive: true);
        }
    }
}
