using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using StreamMark.Avalonia;
using StreamMark.Avalonia.Extensions;
using StreamMark.Avalonia.Selection;
using TKCode.Desktop.Controls;
using TKCode.Desktop.ViewModels;
using TKCode.Desktop.Views;
using Xunit;

namespace TKCode.Desktop.Tests;

/// <summary>
/// StreamMark 文本区右键菜单：文字使用 TKCode 的界面语言；在 StreamMark 支持 AtomUI 菜单之前保持关闭
/// （原生 MenuFlyoutPresenter 在 AtomUI 主题下没有模板，弹出的是空菜单）。
/// </summary>
public sealed class MarkdownContextMenuTests
{
    [Fact]
    public async Task MenuText_UsesInterfaceLanguage()
    {
        // 语言目录随应用加载，需在无头应用中取文字
        var texts = await HeadlessApp.Session.Dispatch(() =>
            new[] { MarkdownMenuCommand.Copy, MarkdownMenuCommand.CopyAsMarkdown, MarkdownMenuCommand.SelectAll, MarkdownMenuCommand.Find }
                .Select(MarkdownSetup.Services.MenuText).ToArray(), TestContext.Current.CancellationToken);
        Assert.Equal(["复制", "复制为 Markdown", "全选", "查找…"], texts);
    }

    [Fact]
    public async Task ContextMenu_IsDisabledUntilAtomUiMenuIsSupported()
    {
        var root = Directory.CreateTempSubdirectory("tkcode-menu-").FullName;
        File.WriteAllText(Path.Combine(root, "README.md"), "# 右键菜单\n\n一段文字。\n");

        await HeadlessApp.Session.Dispatch(async () =>
        {
            var tab = new FileTabViewModel(Path.Combine(root, "README.md"), root);
            var window = new Avalonia.Controls.Window { Width = 760, Height = 480, Content = new FileTabView { DataContext = tab } };
            // 与 MainWindow 相同：整个窗口共用 TKCode 的 Markdown 服务
            MarkdownServices.SetServices(window, MarkdownSetup.Services);
            window.Show();
            var deadline = DateTime.UtcNow.AddSeconds(20);
            while (!window.GetVisualDescendants().OfType<MarkdownView>().Any() && DateTime.UtcNow < deadline)
            {
                Dispatcher.UIThread.RunJobs();
                await Task.Delay(20);
            }
            var view = window.GetVisualDescendants().OfType<MarkdownView>().Single();
            // StreamMark 仍会挂上 flyout，但关闭时在隧道阶段拦下右键请求，菜单不会打开
            view.RaiseEvent(new ContextRequestedEventArgs());
            Dispatcher.UIThread.RunJobs();
            Assert.False(view.ContextFlyout?.IsOpen ?? false);
            window.Close();
            tab.Dispose();
            return true;
        }, TestContext.Current.CancellationToken);
    }
}
