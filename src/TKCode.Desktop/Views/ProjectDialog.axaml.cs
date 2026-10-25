using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using TKCode.Arc;
using TKCode.Config;
using TKCode.Desktop.Controls;
using TKCode.Desktop.Localization;
using TKCode.Desktop.ViewModels;

namespace TKCode.Desktop.Views;

public enum ProjectDialogOutcome
{
    Cancelled,
    Saved,
    /// <summary>编辑弹窗里点了「移除项目」：由调用方确认后移除。</summary>
    RemoveRequested,
}

/// <summary>
/// 创建 / 编辑项目弹窗（docs/design/projects.md §9），经 <see cref="ScaledModal"/> 显示。
/// 提交交给调用方（写入项目表）；失败时显示原因、保留输入，弹窗不关闭。
/// </summary>
public partial class ProjectDialog : DialogSurface
{
    private readonly ScaledModal _modal;
    private Func<ProjectDialogViewModel, Task>? _submit;
    private ProjectDialogOutcome _outcome = ProjectDialogOutcome.Cancelled;

    protected override Type StyleKeyOverride => typeof(DialogSurface);

    public ProjectDialog()
    {
        InitializeComponent();
        CloseButtonName = Strings.Get(TKCodeLangResourceKind.Close);
        _modal = new ScaledModal(this);
        AddHandler(KeyDownEvent, OnDialogKeyDown, RoutingStrategies.Tunnel);
        FolderList.AddHandler(DragDrop.DragOverEvent, OnDragOver);
        FolderList.AddHandler(DragDrop.DropEvent, OnDrop);
    }

    public ProjectDialogViewModel ViewModel => (ProjectDialogViewModel)DataContext!;

    /// <param name="project">要编辑的项目；null 表示新建。</param>
    /// <param name="submit">提交：写入项目表；抛出异常时弹窗显示原因并保持打开。</param>
    /// <param name="initialFolders">新建时预先加入的文件夹（例如拖放到侧栏的文件夹）。</param>
    public static async Task<ProjectDialogOutcome> ShowAsync(Window owner, Project? project, Func<ProjectDialogViewModel, Task> submit,
        IEnumerable<string>? initialFolders = null)
    {
        var viewModel = new ProjectDialogViewModel(project);
        viewModel.AddFolders(initialFolders ?? []);
        var dialog = new ProjectDialog { DataContext = viewModel, _submit = submit };
        dialog._modal.Show(owner, dialog.NameInput);
        await dialog._modal.Completion;
        return dialog._outcome;
    }

    private async void OnAddFolderClick(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage)
        {
            return;
        }
        IStorageFolder? start = null;
        if (ViewModel.Folders.LastOrDefault()?.Path is { } last && Path.GetDirectoryName(last) is { } parent)
        {
            start = await storage.TryGetFolderFromPathAsync(parent);
        }
        var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = Strings.Get(TKCodeLangResourceKind.SelectFolderTitle),
            AllowMultiple = true,
            SuggestedStartLocation = start,
        });
        AddAndName(folders.Select(f => f.TryGetLocalPath()).OfType<string>());
    }

    /// <summary>加入第一个文件夹时，名称还空着就用它的文件夹名。</summary>
    private void AddAndName(IEnumerable<string> paths)
    {
        var vm = ViewModel;
        var wasEmpty = vm.Folders.Count == 0;
        vm.AddFolders(paths);
        if (wasEmpty && vm.Folders.Count > 0 && string.IsNullOrWhiteSpace(vm.Name))
        {
            vm.Name = vm.Folders[0].Name;
        }
    }

    /// <summary>拖放到文件夹列表上：只接受文件夹。</summary>
    private static string[] DroppedFolders(DragEventArgs e) =>
        e.DataTransfer.TryGetFiles()?.Select(f => f.TryGetLocalPath()).OfType<string>().Where(Directory.Exists).ToArray() ?? [];

    private static void OnDragOver(object? sender, DragEventArgs e) =>
        e.DragEffects = DroppedFolders(e).Length > 0 ? DragDropEffects.Copy : DragDropEffects.None;

    private void OnDrop(object? sender, DragEventArgs e) => AddAndName(DroppedFolders(e));

    private void OnDialogKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && e.KeyModifiers == KeyModifiers.None && ReferenceEquals(e.Source, NameInput))
        {
            e.Handled = true;
            _ = SubmitAsync();
        }
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => _modal.Cancel();

    private void OnRemoveClick(object? sender, RoutedEventArgs e)
    {
        if (_modal.IsBusy)
        {
            return;
        }
        _outcome = ProjectDialogOutcome.RemoveRequested;
        _modal.Close();
    }

    private async void OnSubmitClick(object? sender, RoutedEventArgs e) => await SubmitAsync();

    private async Task SubmitAsync()
    {
        var vm = ViewModel;
        if (_modal.IsBusy || !vm.CanSubmit || _submit is not { } submit)
        {
            return;
        }
        _modal.IsBusy = vm.IsBusy = true;
        ShowCloseButton = false;
        vm.Error = null;
        try
        {
            await submit(vm);
            _outcome = ProjectDialogOutcome.Saved;
            _modal.Close();
        }
        catch (Exception error)
        {
            vm.Error = Strings.Format(TKCodeLangResourceKind.ProjectSaveFailedFormat, error.Message);
        }
        finally
        {
            _modal.IsBusy = vm.IsBusy = false;
            ShowCloseButton = true;
        }
    }
}
