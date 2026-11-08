using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace TKCode.Desktop.Views;

public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
        if (!OperatingSystem.IsMacOS())
        {
            // Windows / Linux 的窗口按钮在右上角，左上角没有红绿灯：只留一条很窄的拖拽区，导航贴着窗口顶部开始。
            NavHeader.Height = 8;
            NavMenu.Margin = new Thickness(0, 0, 0, 8);
        }
    }

    /// <summary>macOS 红绿灯落在导航栏顶部，留出位置。</summary>
    public void SetTitleBarInset(double left) => NavHeader.Margin = new Thickness(left, 0, 0, 0);

    private void OnDefaultModelClick(object? sender, RoutedEventArgs e) => DefaultModelPopup.IsOpen = !DefaultModelPopup.IsOpen;

    private void OnDefaultModelChosen(object? sender, RoutedEventArgs e) => DefaultModelPopup.IsOpen = false;

    private void OnFastModelClick(object? sender, RoutedEventArgs e) => FastModelPopup.IsOpen = !FastModelPopup.IsOpen;

    private void OnFastModelChosen(object? sender, RoutedEventArgs e) => FastModelPopup.IsOpen = false;
}
