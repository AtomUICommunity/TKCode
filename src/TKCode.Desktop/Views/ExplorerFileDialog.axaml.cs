using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using TKCode.Arc;
using TKCode.Desktop.Controls;
using TKCode.Desktop.Localization;
using TKCode.Desktop.ViewModels;

namespace TKCode.Desktop.Views;

/// <summary>A scaled Arc modal (<see cref="ScaledModal"/>). A failed submit never discards the input or closes the surface.</summary>
public partial class ExplorerFileDialog : DialogSurface
{
    private readonly ScaledModal _modal;
    private ExplorerFileDialogRequest? _request;

    protected override Type StyleKeyOverride => typeof(DialogSurface);

    public ExplorerFileDialog()
    {
        InitializeComponent();
        CloseButtonName = Strings.Get(TKCodeLangResourceKind.Close);
        _modal = new ScaledModal(this);
        AddHandler(KeyDownEvent, OnDialogKeyDown, RoutingStrategies.Tunnel);
    }

    public static Task ShowAsync(Window owner, ExplorerFileDialogRequest request)
    {
        var dialog = new ExplorerFileDialog { _request = request };
        var trash = request.Action == ExplorerAction.Trash;
        dialog.Title = Strings.Get(request.Action switch
        {
            ExplorerAction.NewFile => TKCodeLangResourceKind.ExplorerNewFile,
            ExplorerAction.NewFolder => TKCodeLangResourceKind.ExplorerNewFolder,
            ExplorerAction.Rename => TKCodeLangResourceKind.ExplorerRename,
            _ => OperatingSystem.IsWindows() ? TKCodeLangResourceKind.ExplorerRecycle : TKCodeLangResourceKind.ExplorerTrash,
        });
        dialog.TargetName.Text = trash ? Path.GetFileName(request.Path) : null;
        dialog.TargetName.IsVisible = trash;
        dialog.TargetPath.Text = request.Path;
        dialog.NameInput.IsVisible = !trash;
        dialog.NameInput.Label = Strings.Get(TKCodeLangResourceKind.ExplorerFileName);
        dialog.NameInput.Text = request.InitialName;
        dialog.CancelButton.Content = Strings.Get(TKCodeLangResourceKind.Cancel);
        dialog.SubmitButton.Content = Strings.Get(request.Action switch
        {
            ExplorerAction.Rename => TKCodeLangResourceKind.ExplorerRenameAction,
            ExplorerAction.Trash => OperatingSystem.IsWindows() ? TKCodeLangResourceKind.ExplorerRecycleAction : TKCodeLangResourceKind.ExplorerTrashAction,
            _ => TKCodeLangResourceKind.ExplorerCreateAction,
        });
        dialog.SubmitButton.Variant = trash ? ButtonVariant.Danger : ButtonVariant.Primary;
        dialog.WarningMessage.IsVisible = trash;
        dialog.WarningMessage.Content = Strings.Get(OperatingSystem.IsWindows()
            ? TKCodeLangResourceKind.ExplorerRecycleConfirm : TKCodeLangResourceKind.ExplorerTrashConfirm);
        dialog._modal.Show(owner, trash ? dialog.CancelButton : dialog.NameInput);
        if (!trash)
        {
            // Do not append an extension: only preselect the stem of the existing complete filename.
            var selectedLength = request.Action == ExplorerAction.Rename && !request.IsDirectory
                ? Path.GetFileNameWithoutExtension(request.InitialName).Length : request.InitialName.Length;
            dialog.NameInput.SelectionStart = 0;
            dialog.NameInput.SelectionEnd = selectedLength;
        }
        return dialog._modal.Completion;
    }

    public static Task ShowErrorAsync(Window owner, Exception error)
    {
        var dialog = new ExplorerFileDialog
        {
            Title = Strings.Get(TKCodeLangResourceKind.ExplorerOperationFailed),
        };
        dialog.TargetName.IsVisible = false;
        dialog.TargetPath.IsVisible = false;
        dialog.NameInput.IsVisible = false;
        dialog.SubmitButton.IsVisible = false;
        dialog.CancelButton.Content = Strings.Get(TKCodeLangResourceKind.Close);
        dialog.ErrorMessage.Content = error.Message;
        dialog.ErrorMessage.IsVisible = true;
        dialog._modal.Show(owner, dialog.CancelButton);
        return dialog._modal.Completion;
    }

    private void OnDialogKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && e.KeyModifiers == KeyModifiers.None && _request is { Action: not ExplorerAction.Trash }
            && ReferenceEquals(e.Source, NameInput))
        {
            e.Handled = true;
            _ = SubmitAsync();
        }
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => _modal.Cancel();
    private async void OnSubmitClick(object? sender, RoutedEventArgs e) => await SubmitAsync();

    private async Task SubmitAsync()
    {
        if (_modal.IsBusy || _request is not { } request)
        {
            return;
        }
        _modal.IsBusy = true;
        SubmitButton.IsLoading = true;
        SubmitButton.IsEnabled = CancelButton.IsEnabled = NameInput.IsEnabled = false;
        ShowCloseButton = false;
        NameInput.ErrorMessage = null;
        ErrorMessage.IsVisible = false;
        try
        {
            await request.SubmitAsync(request.Action == ExplorerAction.Trash ? null : NameInput.Text);
            // Close only after success; no Dialog.Result on async submit buttons.
            _modal.Close();
        }
        catch (Exception error)
        {
            if (NameInput.IsVisible)
            {
                NameInput.ErrorMessage = error.Message;
            }
            ErrorMessage.Content = error.Message;
            ErrorMessage.IsVisible = true;
        }
        finally
        {
            _modal.IsBusy = false;
            SubmitButton.IsLoading = false;
            SubmitButton.IsEnabled = CancelButton.IsEnabled = NameInput.IsEnabled = true;
            ShowCloseButton = true;
        }
    }
}
