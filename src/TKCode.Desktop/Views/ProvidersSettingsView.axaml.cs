using Avalonia.Controls;
using TKCode.Desktop.ViewModels;

namespace TKCode.Desktop.Views;

public partial class ProvidersSettingsView : UserControl
{
    private ProviderTemplateDialog? _templateDialog;

    public ProvidersSettingsView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is SettingsViewModel vm)
            {
                vm.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(SettingsViewModel.IsPickingTemplate))
                    {
                        if (vm.IsPickingTemplate)
                        {
                            _ = OpenTemplateDialogAsync(vm);
                        }
                        else if (_templateDialog is { } dialog)
                        {
                            // 视图模型关的（选中了模板）：先放开引用，关闭动画期间可以再次打开
                            _templateDialog = null;
                            dialog.Close();
                        }
                    }
                };
            }
        };
    }

    /// <summary>
    /// 打开“添加厂商”选择框。选中模板时视图模型先把 IsPickingTemplate 置回 false（上面关闭对话框）；
    /// 用户自己取消（Esc、遮罩、关闭按钮）时对话框先关，这里再把视图模型的状态还原。
    /// </summary>
    private async Task OpenTemplateDialogAsync(SettingsViewModel vm)
    {
        if (_templateDialog is not null)
        {
            return;
        }
        if (TopLevel.GetTopLevel(this) is not Window owner)
        {
            vm.CloseTemplatePickerCommand.Execute(null);
            return;
        }
        var dialog = _templateDialog = new ProviderTemplateDialog { DataContext = vm };
        await dialog.ShowAsync(owner);
        if (ReferenceEquals(_templateDialog, dialog))
        {
            _templateDialog = null;
            vm.CloseTemplatePickerCommand.Execute(null);
        }
    }
}
