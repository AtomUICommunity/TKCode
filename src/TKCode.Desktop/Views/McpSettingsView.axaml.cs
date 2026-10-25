using System.ComponentModel;
using Avalonia.Controls;
using TKCode.Desktop.ViewModels;

namespace TKCode.Desktop.Views;

public partial class McpSettingsView : UserControl
{
    private McpSettingsViewModel? _vm;
    private McpImportDialog? _importDialog;
    private McpExportDialog? _exportDialog;

    public McpSettingsView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (_vm is not null)
            {
                _vm.PropertyChanged -= OnViewModelChanged;
            }
            _vm = DataContext as McpSettingsViewModel;
            if (_vm is not null)
            {
                _vm.PropertyChanged += OnViewModelChanged;
            }
        };
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_vm is not { } vm)
        {
            return;
        }
        if (e.PropertyName == nameof(McpSettingsViewModel.IsImporting))
        {
            if (vm.IsImporting)
            {
                _ = OpenImportAsync(vm);
            }
            else if (_importDialog is { } dialog)
            {
                // 视图模型关的（导入成功）：先放开引用，关闭动画期间可以再次打开
                _importDialog = null;
                dialog.Close();
            }
        }
        else if (e.PropertyName == nameof(McpSettingsViewModel.IsExporting))
        {
            if (vm.IsExporting)
            {
                _ = OpenExportAsync(vm);
            }
            else if (_exportDialog is { } dialog)
            {
                _exportDialog = null;
                dialog.Close();
            }
        }
    }

    /// <summary>
    /// 打开导入对话框。导入成功时视图模型先把 Import 置空（上面关闭对话框）；
    /// 用户自己取消（Esc、遮罩、关闭按钮）时对话框先关，这里再把视图模型的状态还原。
    /// </summary>
    private async Task OpenImportAsync(McpSettingsViewModel vm)
    {
        if (_importDialog is not null || vm.Import is not { } import)
        {
            return;
        }
        if (TopLevel.GetTopLevel(this) is not Window owner)
        {
            import.CloseCommand.Execute(null);
            return;
        }
        var dialog = _importDialog = new McpImportDialog { DataContext = import };
        await dialog.ShowAsync(owner);
        if (ReferenceEquals(_importDialog, dialog))
        {
            _importDialog = null;
            import.CloseCommand.Execute(null);
        }
    }

    private async Task OpenExportAsync(McpSettingsViewModel vm)
    {
        if (_exportDialog is not null || vm.Export is not { } export)
        {
            return;
        }
        if (TopLevel.GetTopLevel(this) is not Window owner)
        {
            export.CloseCommand.Execute(null);
            return;
        }
        var dialog = _exportDialog = new McpExportDialog { DataContext = export };
        await dialog.ShowAsync(owner);
        if (ReferenceEquals(_exportDialog, dialog))
        {
            _exportDialog = null;
            export.CloseCommand.Execute(null);
        }
    }
}
