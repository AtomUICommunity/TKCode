using AtomUI;
using AtomUI.Theme;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using TKCode.Arc;
using TKCode.Arc.Motion;
using TKCode.Arc.Primitives;
using TKCode.Desktop.Controls;
using TKCode.Desktop.Services;
using TKCode.Desktop.ViewModels;
using TKCode.Desktop.Views;
using Xunit;
using ArcButton = TKCode.Arc.Button;

namespace TKCode.Desktop.Tests;

/// <summary>Real in-window modals with fake submissions: never invoke a recycling backend or platform launcher.</summary>
public sealed class ExplorerFileDialogTests
{
    [Theory]
    [InlineData("click")]
    [InlineData("escape")]
    [InlineData("enter")]
    public Task Trash_InitiallyFocusesCancel_AndCancellationDoesNotSubmit(string cancelWith) => OnUi(async () =>
    {
        var calls = 0;
        var path = Path.Combine(Path.GetTempPath(), "explorer-dialog-target.txt");
        using var modal = new DialogHarness(new ExplorerFileDialogRequest(
            ExplorerAction.Trash, path, false, "explorer-dialog-target.txt", _ =>
            {
                ++calls;
                return Task.CompletedTask;
            }));

        Assert.False(modal.Input.IsVisible);
        Assert.True(modal.Find<TextBlock>("TargetName").IsVisible);
        Assert.Equal(Path.GetFileName(path), modal.Find<TextBlock>("TargetName").Text);
        Assert.Equal(path, modal.Find<TextBlock>("TargetPath").Text);
        Assert.True(modal.Find<Alert>("WarningMessage").IsVisible);
        Assert.Same(modal.Owner, TopLevel.GetTopLevel(modal.Find<TextBlock>("TargetName")));
        Assert.Same(modal.Owner, TopLevel.GetTopLevel(modal.Find<Alert>("WarningMessage")));
        Assert.Equal(ButtonVariant.Danger, modal.Submit.Variant);
        Assert.True(modal.Cancel.IsKeyboardFocusWithin);
        Assert.False(modal.Submit.IsKeyboardFocusWithin);

        if (cancelWith == "click")
        {
            Click(modal.Cancel);
        }
        else
        {
            Press(modal.Owner, cancelWith == "escape" ? Key.Escape : Key.Enter);
        }
        await modal.WaitForCloseAsync();
        Assert.Equal(0, calls);
        Assert.True(modal.Opener.IsKeyboardFocusWithin);
    });

    [Theory]
    [InlineData("archive.tar.gz", false, "archive.tar")]
    [InlineData("Program.cs", false, "Program")]
    [InlineData("LICENSE", false, "LICENSE")]
    [InlineData("folder.with.dots", true, "folder.with.dots")]
    public Task Rename_SelectsFileStemOrWholeFolder_WithoutChangingCompleteName(
        string name, bool isDirectory, string selected) => OnUi(async () =>
    {
        using var modal = new DialogHarness(new ExplorerFileDialogRequest(
            ExplorerAction.Rename, Path.Combine(Path.GetTempPath(), name), isDirectory, name,
            _ => throw new InvalidOperationException("Selection must not submit.")));

        Assert.True(modal.Input.IsVisible);
        Assert.True(modal.Input.IsKeyboardFocusWithin);
        Assert.Equal(name, modal.Input.Text);
        Assert.Equal(0, modal.Input.SelectionStart);
        Assert.Equal(selected.Length, modal.Input.SelectionEnd);
        Assert.Equal(selected, modal.Input.SelectedText);
        Assert.False(modal.Find<Alert>("WarningMessage").IsVisible);
        Assert.Equal(ButtonVariant.Primary, modal.Submit.Variant);
        Click(modal.Cancel);
        await modal.WaitForCloseAsync();
    });

    [Theory]
    [InlineData(ExplorerAction.NewFile, false, "")]
    [InlineData(ExplorerAction.NewFolder, true, "")]
    [InlineData(ExplorerAction.Rename, false, "old.cs")]
    public Task InputSubmit_PassesExactText_AndSuccessCloses(
        ExplorerAction action, bool isDirectory, string initialName) => OnUi(async () =>
    {
        var submitted = new List<string?>();
        using var modal = new DialogHarness(new ExplorerFileDialogRequest(
            action, Path.Combine(Path.GetTempPath(), "dialog-parent"), isDirectory, initialName, text =>
            {
                submitted.Add(text);
                return Task.CompletedTask;
            }));
        modal.Input.Text = "new-name";
        modal.Input.Focus();

        Press(modal.Owner, Key.Enter);
        await modal.WaitForCloseAsync();
        // In particular a rename from old.cs does not silently append .cs to the submitted name.
        Assert.Equal(["new-name"], submitted);
    });

    [Theory]
    [InlineData(ExplorerAction.NewFile)]
    [InlineData(ExplorerAction.Rename)]
    [InlineData(ExplorerAction.Trash)]
    public Task FailedSubmit_KeepsTextAndErrorVisible_AndAllowsSuccessfulRetry(ExplorerAction action) => OnUi(async () =>
    {
        var releaseRetry = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var submitted = new List<string?>();
        using var modal = new DialogHarness(new ExplorerFileDialogRequest(
            action, Path.Combine(Path.GetTempPath(), "old.cs"), false, "old.cs", text =>
            {
                submitted.Add(text);
                return submitted.Count == 1
                    ? Task.FromException(new IOException("A target with that name already exists."))
                    : releaseRetry.Task;
            }));
        modal.Input.Text = "keep-this.json";
        try
        {
            Click(modal.Submit);
            await WaitUntil(() => modal.Error.IsVisible && !modal.Submit.IsLoading);

            Assert.False(modal.Completion.IsCompleted);
            Assert.True(modal.Presence.IsOpen);
            Assert.Equal("keep-this.json", modal.Input.Text);
            Assert.Equal("A target with that name already exists.", modal.Error.Content);
            Assert.Same(modal.Owner, TopLevel.GetTopLevel(modal.Error));
            if (action != ExplorerAction.Trash)
            {
                Assert.Equal(modal.Error.Content, modal.Input.ErrorMessage);
            }
            Assert.True(modal.Input.IsEnabled);
            Assert.True(modal.Cancel.IsEnabled);
            Assert.True(modal.Submit.IsEnabled);
            Assert.True(modal.Dialog.ShowCloseButton);

            Click(modal.Submit);
            Assert.Equal(2, submitted.Count);
            Assert.False(modal.Error.IsVisible);
            Assert.Null(modal.Input.ErrorMessage);
            Assert.False(modal.Completion.IsCompleted);
            releaseRetry.SetResult();
            await modal.WaitForCloseAsync();
            Assert.All(submitted, text => Assert.Equal(action == ExplorerAction.Trash ? null : "keep-this.json", text));
        }
        finally
        {
            releaseRetry.TrySetResult();
        }
    });

    [Theory]
    [InlineData(ExplorerAction.Trash, true)]
    [InlineData(ExplorerAction.Trash, false)]
    [InlineData(ExplorerAction.Rename, true)]
    [InlineData(ExplorerAction.Rename, false)]
    public Task BusySubmit_BlocksDuplicatesEscapeCancelAndOwnerClose_UntilExplicitResult(ExplorerAction action, bool succeeds) => OnUi(async () =>
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        using var modal = new DialogHarness(new ExplorerFileDialogRequest(
            action, Path.Combine(Path.GetTempPath(), "never-trash-this.txt"), false, "never-trash-this.txt", text =>
            {
                Assert.Equal(action == ExplorerAction.Trash ? null : "new-name", text);
                ++calls;
                return release.Task;
            }));
        modal.Input.Text = "new-name";
        try
        {
            Click(modal.Submit);
            Assert.Equal(1, calls);
            Assert.True(modal.Submit.IsLoading);
            Assert.False(modal.Submit.IsEnabled);
            Assert.False(modal.Cancel.IsEnabled);
            Assert.False(modal.Input.IsEnabled);
            Assert.False(modal.Dialog.ShowCloseButton);

            // Raise the routed clicks even though the buttons are disabled: the handlers must also guard re-entry.
            Click(modal.Submit);
            Click(modal.Cancel);
            Press(modal.Owner, Key.Escape);
            Assert.False(modal.Presence.Dismiss(DismissReason.PressOutside));
            modal.Owner.Close();
            Pump(modal.Owner);
            Assert.Equal(1, calls);
            Assert.True(modal.Owner.IsVisible);
            Assert.True(modal.Presence.IsOpen);
            Assert.False(modal.Completion.IsCompleted);

            if (succeeds)
            {
                release.SetResult();
            }
            else
            {
                release.SetException(new IOException("Recycling was declined."));
                await WaitUntil(() => modal.Error.IsVisible && !modal.Submit.IsLoading);
                Assert.False(modal.Completion.IsCompleted);
                Assert.True(modal.Presence.IsOpen);
                Assert.Equal("Recycling was declined.", modal.Error.Content);
                Assert.True(modal.Cancel.IsEnabled);
                Assert.True(modal.Dialog.ShowCloseButton);
                Click(modal.Cancel);
            }
            await modal.WaitForCloseAsync();
            Assert.Equal(1, calls);
        }
        finally
        {
            release.TrySetResult();
        }
    });

    [Fact]
    public Task DialogZoomHost_TracksLiveScaleAndOwnerBounds() => OnUi(async () =>
    {
        var previousScale = UiZoom.Scale;
        try
        {
            UiZoom.Scale = 1;
            using var modal = new DialogHarness(new ExplorerFileDialogRequest(
                ExplorerAction.NewFile, Path.GetTempPath(), false, "", _ => Task.CompletedTask));
            Assert.Same(modal.Owner, TopLevel.GetTopLevel(modal.Dialog));
            Assert.Same(modal.Zoom, modal.Dialog.GetVisualAncestors().OfType<ZoomHost>().Single());
            var normalWidth = RenderedDistance(modal.Input, modal.Owner);
            Assert.Equal(80, normalWidth, 2);

            UiZoom.Scale = 2;
            Pump(modal.Owner);
            Assert.Equal(normalWidth * 2, RenderedDistance(modal.Input, modal.Owner), 2);
            // 宽度取 Arc 的预设（Medium 440）与窗口可用宽度中较小的一个
            Assert.Equal(Math.Min(440, modal.Owner.ClientSize.Width / 2 - 32), modal.Dialog.MaxWidth, 2);
            Assert.Equal(modal.Owner.ClientSize.Height / 2 - 32, modal.Dialog.MaxHeight, 2);
            Assert.False(modal.Completion.IsCompleted);

            modal.Owner.Width = 800;
            modal.Owner.Height = 600;
            Pump(modal.Owner);
            Assert.Equal(modal.Owner.ClientSize.Width / 2 - 32, modal.Dialog.MaxWidth, 2);
            Assert.Equal(modal.Owner.ClientSize.Height / 2 - 32, modal.Dialog.MaxHeight, 2);
            Click(modal.Cancel);
            await modal.WaitForCloseAsync();
        }
        finally
        {
            UiZoom.Scale = previousScale;
        }
    });

    [Fact]
    public Task StandaloneError_ShowsMessageAndClosesWithoutSubmit() => OnUi(async () =>
    {
        using var modal = new DialogHarness(owner => ExplorerFileDialog.ShowErrorAsync(owner, new IOException("File manager unavailable.")));
        Assert.True(modal.Cancel.IsKeyboardFocusWithin);
        Assert.False(modal.Input.IsVisible);
        Assert.False(modal.Submit.IsVisible);
        Assert.False(modal.Find<TextBlock>("TargetPath").IsVisible);
        Assert.True(modal.Error.IsVisible);
        Assert.Equal("File manager unavailable.", modal.Error.Content);
        Assert.Same(modal.Owner, TopLevel.GetTopLevel(modal.Error));
        Press(modal.Owner, Key.Escape);
        await modal.WaitForCloseAsync();
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task Dialogs_RenderInputValidationAndTrashWarning_InDarkAndLightThemes(bool light) => OnUi(async () =>
    {
        var suffix = light ? "light" : "dark";
        var manager = Application.Current!.GetThemeManager()!;
        var previousTheme = manager.CurrentTheme!.ThemeId;
        try
        {
            var transition = await manager.ApplyThemeAsync(new ThemeRequest(
                light ? ThemeService.LightThemeId : ThemeService.DarkThemeId, null, ThemeTransitionReason.UserRequest));
            Assert.NotEqual(ThemeTransitionStatus.Failed, transition.Status);
            using (var rename = new DialogHarness(new ExplorerFileDialogRequest(
                ExplorerAction.Rename, Path.Combine(Path.GetTempPath(), "project", "Agent.cs"), false, "Agent.cs",
                _ => Task.FromException(new IOException("A file with this name already exists."))), light))
            {
                rename.Owner.Width = 760;
                rename.Owner.Height = 560;
                Pump(rename.Owner);
                Assert.True(rename.Input.IsKeyboardFocusWithin);
                Assert.True(rename.Input.Bounds.Width > 0);
                HeadlessApp.Save(rename.Owner, $"explorer-dialog-rename-{suffix}");
                rename.Input.Text = "Existing.cs";
                Click(rename.Submit);
                await WaitUntil(() => rename.Error.IsVisible && !rename.Submit.IsLoading);
                Pump(rename.Owner);
                Assert.Same(rename.Owner, TopLevel.GetTopLevel(rename.Error));
                Assert.Equal("Existing.cs", rename.Input.Text);
                HeadlessApp.Save(rename.Owner, $"explorer-dialog-error-{suffix}");
                Click(rename.Cancel);
                await rename.WaitForCloseAsync();
            }

            using var trash = new DialogHarness(new ExplorerFileDialogRequest(
                ExplorerAction.Trash, Path.Combine(Path.GetTempPath(), "project", "Agent.cs"), false, "Agent.cs",
                _ => throw new InvalidOperationException("The snapshot must never submit trash.")), light);
            trash.Owner.Width = 760;
            trash.Owner.Height = 560;
            Pump(trash.Owner);
            Assert.True(trash.Cancel.IsKeyboardFocusWithin);
            Assert.Same(trash.Owner, TopLevel.GetTopLevel(trash.Find<Alert>("WarningMessage")));
            HeadlessApp.Save(trash.Owner, $"explorer-dialog-trash-{suffix}");
            Click(trash.Cancel);
            await trash.WaitForCloseAsync();
        }
        finally
        {
            await manager.ApplyThemeAsync(new ThemeRequest(previousTheme, null, ThemeTransitionReason.UserRequest));
        }
    });

    private static double RenderedDistance(Control control, Window owner)
    {
        var origin = control.TranslatePoint(default, owner);
        var corner = control.TranslatePoint(new Point(80, 0), owner);
        Assert.NotNull(origin);
        Assert.NotNull(corner);
        return corner.Value.X - origin.Value.X;
    }

    private static void Click(ArcButton button) => button.RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));

    private static void Press(Window owner, Key key)
    {
        owner.KeyPress(key, RawInputModifiers.None, PhysicalKey.None, null);
        owner.KeyRelease(key, RawInputModifiers.None, PhysicalKey.None, null);
        Pump(owner);
    }

    private static void Pump(Window owner)
    {
        Dispatcher.UIThread.RunJobs();
        owner.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
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
                throw new TimeoutException("Explorer dialog did not reach the expected state.");
            }
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }

    private sealed class DialogHarness : IDisposable
    {
        public Window Owner { get; }
        public Avalonia.Controls.Button Opener { get; } = new() { Content = "Open explorer action" };
        public Task Completion { get; }
        public ModalPresence Presence { get; }
        public ZoomHost Zoom { get; }
        public ExplorerFileDialog Dialog { get; }
        public Input Input => Find<Input>("NameInput");
        public ArcButton Cancel => Find<ArcButton>("CancelButton");
        public ArcButton Submit => Find<ArcButton>("SubmitButton");
        public Alert Error => Find<Alert>("ErrorMessage");

        public DialogHarness(ExplorerFileDialogRequest request, bool light = false) : this(owner => ExplorerFileDialog.ShowAsync(owner, request), light)
        {
        }

        public DialogHarness(Func<Window, Task> show, bool light = false)
        {
            Owner = new AtomUI.Desktop.Controls.Window
            {
                Width = 1400,
                Height = 1000,
                Content = Opener,
                RequestedThemeVariant = light ? Avalonia.Styling.ThemeVariant.Light : Avalonia.Styling.ThemeVariant.Dark,
            };
            ArcMotion.SetLevel(Owner, ArcMotionLevel.Off);
            Owner.Show();
            Pump(Owner);
            Opener.Focus();
            try
            {
                Completion = show(Owner);
                Pump(Owner);
                Presence = Assert.Single(ModalPresence.GetModals(Owner));
                Zoom = Assert.IsType<ZoomHost>(Presence.Panel);
                Dialog = Assert.IsType<ExplorerFileDialog>(Zoom.Child);
            }
            catch
            {
                foreach (var presence in ModalPresence.GetModals(Owner).ToArray())
                {
                    presence.Close();
                }
                Owner.Close();
                Pump(Owner);
                throw;
            }
        }

        public T Find<T>(string name) where T : Control => Assert.IsType<T>(Dialog.FindControl<T>(name));

        public async Task WaitForCloseAsync()
        {
            await WaitUntil(() => Completion.IsCompleted);
            await Completion;
            Pump(Owner);
            Assert.Empty(ModalPresence.GetModals(Owner));
            Assert.Null(TopLevel.GetTopLevel(Dialog));
        }

        public void Dispose()
        {
            // This bypasses busy dismissal only for cleanup after a failed assertion.
            Presence.Close();
            Pump(Owner);
            Owner.Close();
            Pump(Owner);
        }
    }
}
