using Avalonia;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using TKCode.Desktop.ViewModels;
using TKCode.Desktop.Views;
using Xunit;

namespace TKCode.Desktop.Tests;

public sealed class PermissionPickerSnapshotTests
{
    [Fact]
    public async Task FullAccess_RequiresConfirmation_OthersApplyImmediately()
    {
        await HeadlessApp.Session.Dispatch(() =>
        {
            var picker = new PermissionPickerViewModel();
            var chosen = new List<string>();
            picker.ModeChosen += chosen.Add;

            var view = new PermissionPickerView
            {
                DataContext = picker,
                Margin = new Thickness(40),
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
            };
            var window = new Avalonia.Controls.Window { Width = 520, Height = 420, Content = view, Background = Avalonia.Media.Brushes.Black };
            window.Show();
            view.GetVisualDescendants().OfType<Popup>().Single().ShouldUseOverlayLayer = true;
            Dispatcher.UIThread.RunJobs();
            HeadlessApp.Save(window, "permission-chip-ask");

            view.Toggle();
            Dispatcher.UIThread.RunJobs();
            HeadlessApp.Save(window, "permission-popup");

            // 自动放行：立即生效并关闭弹层
            picker.ChooseCommand.Execute(PermissionPickerViewModel.AutoApprove);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(["autoApprove"], chosen);
            Assert.False(view.GetVisualDescendants().OfType<Popup>().Single().IsOpen);

            // 完全授权：先进入确认卡片，取消后保持原档位
            view.Toggle();
            picker.ChooseCommand.Execute(PermissionPickerViewModel.FullAccess);
            Dispatcher.UIThread.RunJobs();
            Assert.True(picker.IsConfirmingFullAccess);
            Assert.Equal(PermissionPickerViewModel.AutoApprove, picker.Mode);
            HeadlessApp.Save(window, "permission-confirm-full");
            picker.CancelConfirmCommand.Execute(null);
            Assert.Equal(["autoApprove"], chosen);

            // 确认后生效
            picker.ChooseCommand.Execute(PermissionPickerViewModel.FullAccess);
            picker.ConfirmFullAccessCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(["autoApprove", "fullAccess"], chosen);
            Assert.True(picker.IsFullAccess);
            HeadlessApp.Save(window, "permission-chip-full");

            // 外部显示（切换会话）不触发 ModeChosen
            picker.Show("ask");
            Assert.True(picker.IsAsk);
            Assert.Equal(2, chosen.Count);
            window.Close();
        }, TestContext.Current.CancellationToken);
    }
}
