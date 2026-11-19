using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Xunit;

namespace TKCode.Desktop.Tests;

/// <summary>
/// Windows 的托盘右键菜单由 Avalonia 用标准 MenuFlyoutPresenter 绘制（不是原生菜单）。
/// Arc 的主题不包含这些标准菜单控件，缺少主题时右键只会弹出一个空白小窗口；
/// 这里固定 MenuFlyoutPresenter / MenuItem / Separator 三者都有主题且能生成条目。
/// </summary>
public sealed class TrayMenuTests
{
    [Fact]
    public async Task Tray_menu_presenter_has_a_theme_and_realizes_items()
    {
        await HeadlessApp.Session.Dispatch(() =>
        {
            var menu = new NativeMenu();
            menu.Items.Add(new NativeMenuItem("显示 TKCode"));
            menu.Items.Add(new NativeMenuItem("新会话"));
            menu.Items.Add(new NativeMenuItemSeparator());
            menu.Items.Add(new NativeMenuItem("退出 TKCode"));

            var presenter = new MenuFlyoutPresenter { ItemsSource = menu.Items };
            var window = new Window
            {
                WindowDecorations = WindowDecorations.None,
                SizeToContent = SizeToContent.WidthAndHeight,
                Background = null,
                TransparencyLevelHint = [WindowTransparencyLevel.Transparent],
                Content = presenter,
            };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();

            Assert.True(window.TryFindResource(typeof(MenuFlyoutPresenter), out _), "MenuFlyoutPresenter has no ControlTheme");
            Assert.True(window.TryFindResource(typeof(MenuItem), out _), "MenuItem has no ControlTheme");
            Assert.True(window.TryFindResource(typeof(Separator), out _), "Separator has no ControlTheme");
            Assert.True(presenter.Template is not null, "MenuFlyoutPresenter has no template");
            Assert.Equal(4, presenter.ItemCount);
            Assert.NotNull(presenter.ContainerFromIndex(0));
            Assert.True(presenter.Bounds.Height > 40, $"presenter is {presenter.Bounds.Size}");
            window.Close();
        }, TestContext.Current.CancellationToken);
    }
}
