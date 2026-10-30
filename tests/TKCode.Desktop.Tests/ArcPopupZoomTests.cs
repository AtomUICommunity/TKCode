using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using TKCode.Arc;
using TKCode.Desktop.Controls;
using Xunit;

namespace TKCode.Desktop.Tests;

/// <summary>Arc 弹层与界面缩放（uiScale）：弹层内容在 ZoomHost 放大 2 倍时，是否也放大 2 倍。</summary>
public sealed class ArcPopupZoomTests
{
    private static double PopupContentWidthAt(double scale, out bool inWindowOverlay)
    {
        UiZoom.Scale = scale;
        try
        {
            var probe = new Border { Name = "Probe", Width = 120, Height = 40, Background = Avalonia.Media.Brushes.Gray };
            var popover = new Popover { Trigger = new Avalonia.Controls.Button { Content = "Open" }, Content = probe };
            var window = new Avalonia.Controls.Window { Width = 500, Height = 400, Content = new ZoomHost { Child = new Border { Padding = new Thickness(20), Child = popover } } };
            TKCode.Arc.Motion.ArcMotion.SetLevel(window, TKCode.Arc.Motion.ArcMotionLevel.Off);
            window.Show();
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();

            popover.IsOpen = true;
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();

            var root = TopLevel.GetTopLevel(probe)!;
            inWindowOverlay = ReferenceEquals(root, window);
            var origin = probe.TranslatePoint(new Point(0, 0), root) ?? default;
            var corner = probe.TranslatePoint(new Point(probe.Bounds.Width, 0), root) ?? default;
            var width = corner.X - origin.X;
            HeadlessApp.Save(window, $"arc-popover-zoom-{scale:0.##}");
            window.Close();
            return width;
        }
        finally
        {
            UiZoom.Scale = 1.0;
        }
    }

    // 2026-10-21 实测：uiScale=2 时 Arc 弹层内容仍是 120px（应为 240px）。Arc 的弹层画在窗口覆盖层里，在 ZoomHost 之外。
    // 换任何 Arc 弹层类控件（Popover、Select、DropdownMenu、Tooltip、HoverCard、ContextMenu）之前，先让 Arc 支持缩放并去掉 Skip。
    [Fact]
    public async Task Popover_Content_FollowsUiScale()
    {
        await HeadlessApp.Session.Dispatch(() =>
        {
            var normal = PopupContentWidthAt(1.0, out var overlayAt1);
            var doubled = PopupContentWidthAt(2.0, out var overlayAt2);
            Assert.True(overlayAt1 && overlayAt2, $"popup lives in its own window (overlay at 1x: {overlayAt1}, at 2x: {overlayAt2})");
            Assert.Equal(120, normal, 1);
            Assert.True(Math.Abs(doubled - 240) < 1, $"popup content width at 2x was {doubled} (expected 240 if it follows uiScale; {normal} means it does not)");
        }, TestContext.Current.CancellationToken);
    }
}
