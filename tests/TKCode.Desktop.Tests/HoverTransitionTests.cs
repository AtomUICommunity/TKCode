using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Threading;
using Xunit;

namespace TKCode.Desktop.Tests;

/// <summary>
/// 悬停 / 选中换色走真实的 Avalonia 过渡管线（推进渲染计时器）：中间帧始终是目标色，只有透明度渐变。
/// Avalonia 自带的 BrushTransition 从 Transparent（透明的白色）过渡时会先出现一层半透明的浅灰，这里作为对照。
/// </summary>
public sealed class HoverTransitionTests
{
    [Fact]
    public async Task RowButtonHover_NeverPassesThroughGray()
    {
        await HeadlessApp.Session.Dispatch(() =>
        {
            TKCode.Desktop.Motion.MotionSettings.ForceDisabled = false;
            try
            {
                var target = Color.Parse("#272828");
                List<Color> ours = [], theirs = [];
                // 无头环境的过渡时钟偶尔不推进（Avalonia 自带的 BrushTransition 同样会停住）：换新窗口重试，直到采到中间帧
                for (var attempt = 0; attempt < 5 && !ours.Any(c => c.A is > 0 and < 255); attempt++)
                {
                    (ours, theirs) = Sample(target);
                }
                var midway = ours.Where(c => c.A is > 0 and < 255).ToList();
                Assert.NotEmpty(midway);
                Assert.All(midway, c => Assert.Equal((target.R, target.G, target.B), (c.R, c.G, c.B)));
                // 对照：Avalonia 自带插值的中间帧明显偏亮（浅灰）
                var builtInMidway = theirs.Where(c => c.A is > 0 and < 255).ToList();
                if (builtInMidway.Count > 0)
                {
                    Assert.Contains(builtInMidway, c => c.R > target.R + 40);
                }
            }
            finally
            {
                TKCode.Desktop.Motion.MotionSettings.ForceDisabled = true;
            }
        }, TestContext.Current.CancellationToken);
    }

    private static (List<Color> Ours, List<Color> Theirs) Sample(Color target)
    {
        var button = new Button { Width = 120, Height = 30, Content = "row" };
        button.Theme = (Avalonia.Styling.ControlTheme)Avalonia.Application.Current!.FindResource("TKCode.RowButton")!;
        var builtIn = new Border
        {
            Width = 50, Height = 20, Background = Brushes.Transparent,
            Transitions = [new Avalonia.Animation.BrushTransition { Property = Border.BackgroundProperty, Duration = TimeSpan.FromMilliseconds(140) }],
        };
        var window = new Avalonia.Controls.Window { Width = 300, Height = 200, Content = new StackPanel { Children = { button, builtIn } } };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        button.Classes.Add("selected");
        builtIn.Background = new SolidColorBrush(target);
        var ours = new List<Color>();
        var theirs = new List<Color>();
        for (var i = 0; i < 60 && (ours.Count == 0 || ours[^1] != target || theirs[^1] != target); i++)
        {
            Thread.Sleep(16);
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
            ours.Add(((ISolidColorBrush)button.Background!).Color);
            theirs.Add(((ISolidColorBrush)builtIn.Background!).Color);
        }
        window.Close();
        return (ours, theirs);
    }
}
