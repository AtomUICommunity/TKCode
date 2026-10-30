using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using TKCode.Arc;
using TKCode.Arc.Motion;
using TKCode.Arc.Primitives;
using TKCode.Desktop.Controls;
using TKCode.Desktop.Localization;
using TKCode.Desktop.Motion;
using TKCode.Desktop.Services;
using TKCode.Desktop.ViewModels;
using Xunit;
using static TKCode.Desktop.Tests.ArcFileExplorerTreeTests;

namespace TKCode.Desktop.Tests;

public sealed class ExplorerContextMenuTests
{
    [Fact]
    public async Task StaticArcSurface_CapturesTarget_AndClosesAndRestoresFocusBeforeExecuting()
    {
        await WithTree(["a.cs", "b.cs"], async fixture =>
        {
            var first = fixture.Explorer.RootNodes[0];
            var second = fixture.Explorer.RootNodes[1];
            using var menu = new ExplorerContextMenu(fixture.Explorer, fixture.Window, Fail);
            var target = Assert.IsType<ExplorerContextTarget>(fixture.Explorer.CaptureContext(first));
            fixture.Editor.Focus();
            menu.Show(target, fixture.Tree, new Point(30, 20), fixture.Editor);
            await Settle(fixture.Window);
            var surface = Surface(fixture.Window);
            Assert.Equal(MenuSurfaceVariant.Context, surface.Variant);
            Assert.False(surface.IsOpen); // This must never create MenuSurface's own nested popup.
            Assert.Single(surface.GetVisualAncestors().OfType<ZoomHost>());
            var popup = Assert.Single(surface.GetLogicalAncestors().OfType<ArcPopup>());
            Assert.False(popup.IsMotionEnabled);
            Assert.True(surface.Items.OfType<MenuEntry>().First().IsKeyboardFocusWithin);
            Assert.Contains(surface.Items, item => item is MenuEntrySeparator);
            var trash = surface.Items.OfType<MenuEntry>().Single(e => e.IsDestructive);
            Assert.Equal(Strings.Get(OperatingSystem.IsWindows() ? TKCodeLangResourceKind.ExplorerRecycle : TKCodeLangResourceKind.ExplorerTrash), trash.Header);
            Assert.All(surface.Items.OfType<MenuEntry>(), entry => Assert.NotNull(entry.Icon));
            fixture.Explorer.SelectedNode = second;
            var focusAtExecution = false;
            fixture.Workbench.OpenRequested += () => focusAtExecution = fixture.Editor.IsKeyboardFocusWithin && !menu.IsOpen;
            Press(fixture.Window, Key.Home);
            Press(fixture.Window, Key.Enter);
            await Settle(fixture.Window);
            Assert.True(focusAtExecution);
            Assert.False(menu.IsOpen);
            Assert.Equal(first.FullPath, fixture.Workbench.ActiveFilePath);
            Assert.Equal(1, fixture.OpenRequests);
        });
    }

    [Fact]
    public async Task MotionOn_QueuedGraceFocus_CannotStealEditorFocusDuringPopupExit()
    {
        await WithTree(["a.cs"], async fixture =>
        {
            Assert.SkipWhen(MotionSettings.SystemReduceMotion, "This regression requires the animated popup exit, disabled by the OS reduced-motion preference.");
            var forced = MotionSettings.ForceDisabled;
            var reduced = MotionSettings.UserReduceMotion;
            var clock = new ManualFrameClock();
            using var installation = clock.Install();
            try
            {
                MotionSettings.UserReduceMotion = false;
                MotionSettings.ForceDisabled = false;
                using var menu = new ExplorerContextMenu(fixture.Explorer, fixture.Window, Fail);
                menu.Show(fixture.Explorer.CaptureContext(fixture.Explorer.RootNodes[0])!, fixture.Tree, new Point(30, 20), fixture.Editor);
                await Task.Delay(240); // AtomUI's popup enter uses its own clock; Arc's highlight uses the manual clock.
                clock.Advance(TimeSpan.FromSeconds(1));
                await Settle(fixture.Window);
                var surface = Surface(fixture.Window);
                var popup = Assert.Single(surface.GetLogicalAncestors().OfType<ArcPopup>());
                Assert.True(popup.IsMotionEnabled);
                Assert.Equal(ArcMotionLevel.Full, ArcMotion.GetLevel(surface));
                var open = surface.Items.OfType<MenuEntry>().First();
                var point = open.TranslatePoint(new Point(open.Bounds.Width / 2, open.Bounds.Height / 2), fixture.Window)!.Value;
                fixture.Window.MouseMove(point);
                fixture.Window.MouseDown(point, MouseButton.Left);
                Assert.Same(open, surface.HighlightedEntry);

                var focusAttempted = false;
                var outgoingStillAttached = false;
                var outgoingWasDisabled = false;
                var outgoingAcceptedFocus = false;
                fixture.Workbench.OpenRequested += () =>
                {
                    fixture.Editor.Focus();
                    // Arc's 70ms pointer-exit grace ends with this same public Focus call. Queue it only after
                    // action dispatch handed focus to the editor: headless MouseUp can pump jobs before injecting
                    // the input event. Run during the 120ms animated exit without a flaky wall-clock timer race.
                    Dispatcher.UIThread.Post(() =>
                    {
                        focusAttempted = true;
                        outgoingStillAttached = ReferenceEquals(TopLevel.GetTopLevel(surface), fixture.Window);
                        outgoingWasDisabled = !surface.IsEnabled && !surface.Focusable && !menu.IsOpen;
                        outgoingAcceptedFocus = surface.Focus(NavigationMethod.Pointer);
                    }, DispatcherPriority.Input);
                };
                fixture.Window.MouseUp(point, MouseButton.Left);
                Assert.False(menu.IsOpen);
                Assert.True(fixture.Editor.IsKeyboardFocusWithin);
                Assert.False(surface.IsEnabled);
                Assert.False(surface.IsHitTestVisible);
                Assert.False(surface.Focusable);
                Dispatcher.UIThread.RunJobs();
                Assert.True(focusAttempted);
                Assert.True(outgoingStillAttached, "The queued grace request must run against the still-hosted outgoing menu.");
                Assert.True(outgoingWasDisabled, "The queued focus request must run after Close, never before input dispatch.");
                Assert.False(outgoingAcceptedFocus);
                Assert.False(surface.IsKeyboardFocusWithin);
                Assert.True(fixture.Editor.IsKeyboardFocusWithin);
                Assert.Equal(1, fixture.OpenRequests);
                clock.Advance(TimeSpan.FromSeconds(1));
                await Task.Delay(180);
                await Settle(fixture.Window);
                Assert.True(fixture.Editor.IsKeyboardFocusWithin);
            }
            finally
            {
                MotionSettings.UserReduceMotion = reduced;
                MotionSettings.ForceDisabled = forced;
                clock.Advance(TimeSpan.FromSeconds(2));
                Dispatcher.UIThread.RunJobs();
            }
        });
    }

    [Theory]
    [InlineData(Key.Escape)]
    [InlineData(Key.Tab)]
    public async Task EscapeAndTab_CloseAndRestoreTheExplicitFocusAnchor(Key key)
    {
        await WithTree(["a.cs"], async fixture =>
        {
            using var menu = new ExplorerContextMenu(fixture.Explorer, fixture.Window, Fail);
            menu.Show(fixture.Explorer.CaptureContext(fixture.Explorer.RootNodes[0])!, fixture.Tree, new Point(20, 20), fixture.Editor);
            await Settle(fixture.Window);
            Press(fixture.Window, key);
            Assert.False(menu.IsOpen);
            Assert.True(fixture.Editor.IsKeyboardFocusWithin);
            Assert.Equal(0, fixture.OpenRequests);
        });
    }

    [Fact]
    public async Task Reopen_IsFresh_OutsidePressScrollResizeAndOwnerDetachDismiss()
    {
        await WithTree(Enumerable.Range(0, 100).Select(i => $"f{i:000}.cs").ToArray(), async fixture =>
        {
            using var menu = new ExplorerContextMenu(fixture.Explorer, fixture.Window, Fail);
            var target = fixture.Explorer.CaptureContext(fixture.Explorer.RootNodes[0])!;
            async Task<MenuSurface> Open()
            {
                menu.Show(target, fixture.Tree, new Point(20, 30), fixture.Editor);
                await Settle(fixture.Window);
                Assert.True(menu.IsOpen);
                return Surface(fixture.Window);
            }
            var first = await Open();
            Click(fixture.Window, fixture.Editor);
            Assert.False(menu.IsOpen);
            var second = await Open();
            Assert.NotSame(first, second);
            Assert.NotSame(first.Items[0], second.Items[0]);
            var scroll = fixture.Tree.GetVisualDescendants().OfType<ScrollViewer>().Single();
            scroll.Offset = new Vector(0, 260);
            await Settle(fixture.Window);
            Assert.False(menu.IsOpen);
            await Open();
            fixture.Window.Width += 20;
            await Settle(fixture.Window);
            Assert.False(menu.IsOpen);
            await Open();
            fixture.Window.Content = null;
            await Settle(fixture.Window);
            Assert.False(menu.IsOpen);
        });
    }

    [Fact]
    public async Task StaleWorkspaceTarget_CannotExecuteOrReopen_EvenAtTheSameRootPath()
    {
        await WithTree(["a.cs"], async fixture =>
        {
            using var menu = new ExplorerContextMenu(fixture.Explorer, fixture.Window, Fail);
            var target = fixture.Explorer.CaptureContext(fixture.Explorer.RootNodes[0])!;
            menu.Show(target, fixture.Tree, new Point(30, 20), fixture.Editor);
            await Settle(fixture.Window);
            fixture.Explorer.SetRoot(null);
            fixture.Explorer.SetRoot(fixture.Root);
            await WaitUntil(fixture.Window, () => fixture.Explorer.RootNodes.Count == 1, "same-path replacement root");
            Press(fixture.Window, Key.Home);
            Press(fixture.Window, Key.Enter);
            await Settle(fixture.Window);
            Assert.Equal(0, fixture.OpenRequests);
            Assert.Empty(fixture.Workbench.Tabs);
            menu.Show(target, fixture.Tree, new Point(30, 20), fixture.Editor);
            Assert.False(menu.IsOpen);
        });
    }

    [Fact]
    public async Task ActionFailure_IsReportedVisibly_AfterMenuCloseAndFocusRestore()
    {
        await WithTree(["a.cs"], async fixture =>
        {
            Exception? reported = null;
            var restoredBeforeCallback = false;
            ExplorerContextMenu? host = null;
            fixture.Explorer.CopyTextAsync = _ => throw new IOException("clipboard unavailable");
            using var menu = host = new ExplorerContextMenu(fixture.Explorer, fixture.Window, ex =>
            {
                reported = ex;
                restoredBeforeCallback = fixture.Editor.IsKeyboardFocusWithin && !host!.IsOpen;
                return Task.CompletedTask;
            });
            menu.Show(fixture.Explorer.CaptureContext(fixture.Explorer.RootNodes[0])!, fixture.Tree, new Point(30, 20), fixture.Editor);
            await Settle(fixture.Window);
            var surface = Surface(fixture.Window);
            var copy = surface.Items.OfType<MenuEntry>().Single(e => Equals(e.Header, Strings.Get(TKCodeLangResourceKind.ExplorerCopyFullPath)));
            copy.BringIntoView();
            await Settle(fixture.Window);
            Click(fixture.Window, copy);
            await Settle(fixture.Window);
            Assert.IsType<IOException>(reported);
            Assert.True(restoredBeforeCallback);
        });
    }

    [Theory]
    [InlineData(false, 0.8, "explorer-context-dark-080")]
    [InlineData(true, 0.8, "explorer-context-light-080")]
    [InlineData(false, 1.0, "explorer-context-dark")]
    [InlineData(true, 1.0, "explorer-context-light")]
    [InlineData(false, 1.5, "explorer-context-dark-150")]
    [InlineData(true, 1.5, "explorer-context-light-150")]
    [InlineData(false, 2.0, "explorer-context-dark-200")]
    [InlineData(true, 2.0, "explorer-context-light-200")]
    public async Task Menu_FitsViewportAndScrolls_InLightDarkAndZoom(bool light, double scale, string snapshot)
    {
        await WithTree(["folder/a.cs"], async fixture =>
        {
            var oldScale = UiZoom.Scale;
            try
            {
                await TestTheme.ApplyAsync(light ? TestTheme.LightId : TestTheme.DarkId);
                UiZoom.Scale = scale;
                await Settle(fixture.Window);
                using var menu = new ExplorerContextMenu(fixture.Explorer, fixture.Window, Fail);
                menu.Show(fixture.Explorer.CaptureContext(fixture.Explorer.RootNodes[0])!, fixture.Tree,
                    new Point(fixture.Tree.Bounds.Width - 4, fixture.Tree.Bounds.Height - 4), fixture.Editor);
                await Settle(fixture.Window);
                var surface = Surface(fixture.Window);
                Assert.Equal(ArcMotionLevel.Off, ArcMotion.GetLevel(surface));
                Assert.False(surface.IsOpen);
                var origin = surface.TranslatePoint(default, fixture.Window)!.Value;
                Assert.InRange(origin.X, 0, fixture.Window.ClientSize.Width);
                Assert.InRange(origin.Y, 0, fixture.Window.ClientSize.Height);
                Assert.True(origin.X + surface.Bounds.Width * scale <= fixture.Window.ClientSize.Width + 1);
                Assert.True(origin.Y + surface.Bounds.Height * scale <= fixture.Window.ClientSize.Height + 1);
                var scroll = surface.GetVisualDescendants().OfType<ScrollViewer>().Single();
                Assert.True(scroll.Extent.Height > scroll.Viewport.Height);
                HeadlessApp.Save(fixture.Window, snapshot);
            }
            finally
            {
                UiZoom.Scale = oldScale;
                await TestTheme.ApplyAsync(TestTheme.DarkId);
            }
        });
    }

    private static MenuSurface Surface(Window window) => Assert.Single(window.GetVisualDescendants().OfType<MenuSurface>(), s => s.IsEffectivelyVisible);
    private static Task Fail(Exception exception) => Task.FromException(new Xunit.Sdk.XunitException(exception.ToString()));
}
