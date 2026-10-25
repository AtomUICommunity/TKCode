using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using TKCode.Desktop.ViewModels;

namespace TKCode.Desktop.Views;

public partial class ProjectPickerView : UserControl
{
    private ProjectPickerViewModel? _vm;

    public ProjectPickerView()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm is not null)
        {
            _vm.CloseRequested -= Close;
        }
        _vm = DataContext as ProjectPickerViewModel;
        if (_vm is not null)
        {
            _vm.CloseRequested += Close;
        }
    }

    public void Open() => Popup.IsOpen = true;

    private void Close() => Popup.IsOpen = false;

    private void OnTriggerClick(object? sender, RoutedEventArgs e) => Popup.IsOpen = !Popup.IsOpen;

    private void OnPopupOpened(object? sender, EventArgs e) =>
        Dispatcher.UIThread.Post(() => SearchBox.Focus(), DispatcherPriority.Input);

    private void OnPopupClosed(object? sender, EventArgs e) => _vm?.OnClosed();
}
