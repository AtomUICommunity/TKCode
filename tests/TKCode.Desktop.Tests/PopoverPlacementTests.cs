using TKCode.Arc.Primitives;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using TKCode.Desktop.Motion;
using Xunit;

namespace TKCode.Desktop.Tests;

/// <summary>
/// 弹层打开动效在真实的过渡管线里（推进渲染计时器）运行：
/// - “打开下拉闪几下”：透明度曾随弹簧缓动过冲到 1.04，渲染时按字节换算溢出成接近 0，弹层快结束时几乎消失几帧。
///   透明度必须单调上升且不超过 1（缩放 / 位移仍可过冲）。
/// - 弹层按触发按钮当时的变换后边界定位：按下缩放回弹的按钮会让弹层固定偏几个像素，弹层触发按钮（ChipButton）不缩放。
/// </summary>
public sealed class PopoverPlacementTests
{
    private static (Avalonia.Controls.Window Window, Button Trigger, Popover Popover) Create(string theme)
    {
        var trigger = new Button
        {
            Content = "全部项目", Margin = new Thickness(40),
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top,
            Theme = (Avalonia.Styling.ControlTheme)Application.Current!.FindResource(theme)!,
        };
        var popover = new Popover
        {
            PlacementTarget = trigger, Placement = PlacementMode.BottomEdgeAlignedLeft, VerticalOffset = 6,
            Child = new Border { Classes = { "menu" }, Width = 200, Height = 120 },
            ShouldUseOverlayLayer = true,
        };
        var window = new Avalonia.Controls.Window
        {
            Width = 400, Height = 300, Background = Brushes.Black, Content = new Panel { Children = { trigger, popover } },
        };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, trigger, popover);
    }

    [Fact]
    public async Task OpenMotion_OpacityRisesMonotonically_NeverAboveOne()
    {
        await HeadlessApp.Session.Dispatch(() =>
        {
            MotionSettings.ForceDisabled = false;
            try
            {
                // 无头环境的过渡时钟偶尔不推进：换新窗口重试，直到采到中间帧
                var opacities = new List<double>();
                for (var attempt = 0; attempt < 5 && !opacities.Any(o => o is > 0.05 and < 0.95); attempt++)
                {
                    opacities = SampleOpenOpacity();
                }
                Assert.Contains(opacities, o => o is > 0.05 and < 0.95);
                Assert.All(opacities, o => Assert.True(o <= 1.0, $"opacity {o:F3} > 1"));
                for (var i = 1; i < opacities.Count; i++)
                {
                    Assert.True(opacities[i] >= opacities[i - 1] - 1e-6, $"opacity dropped {opacities[i - 1]:F3} → {opacities[i]:F3}");
                }
            }
            finally
            {
                MotionSettings.ForceDisabled = true;
            }
        }, TestContext.Current.CancellationToken);
    }

    private static List<double> SampleOpenOpacity()
    {
        var (window, _, popover) = Create("TKCode.ChipButton");
        popover.IsMotionEnabled = true;
        popover.IsOpen = true;
        Dispatcher.UIThread.RunJobs();
        var opacities = new List<double>();
        for (var i = 0; i < 24; i++)
        {
            Tick(1);
            if (popover.Child is ArcPopupFrame actor)
            {
                opacities.Add(actor.Opacity);
            }
        }
        popover.IsOpen = false;
        window.Close();
        return opacities;
    }

    [Fact]
    public async Task ChipTrigger_PressedThenOpened_PopupAlignsWithTrigger()
    {
        await HeadlessApp.Session.Dispatch(() =>
        {
            MotionSettings.ForceDisabled = false;
            try
            {
                var (window, trigger, popover) = Create("TKCode.ChipButton");
                // 与真实点击顺序一致：按下 → 松开 → 打开
                ((IPseudoClasses)trigger.Classes).Set(":pressed", true);
                Tick(4);
                ((IPseudoClasses)trigger.Classes).Set(":pressed", false);
                popover.IsOpen = true;
                Dispatcher.UIThread.RunJobs();
                Tick(2);
                var host = popover.Child!.FindAncestorOfType<OverlayPopupHost>()!;
                Assert.Equal(trigger.Bounds.X, host.Bounds.X, 0.5);
                Assert.Null(trigger.RenderTransform);
                popover.IsOpen = false;
                window.Close();
            }
            finally
            {
                MotionSettings.ForceDisabled = true;
            }
        }, TestContext.Current.CancellationToken);
    }

    private static void Tick(int frames)
    {
        for (var i = 0; i < frames; i++)
        {
            Thread.Sleep(16);
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
        }
    }
}
