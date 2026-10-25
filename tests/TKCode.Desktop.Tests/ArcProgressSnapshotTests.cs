using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using TKCode.Arc;
using Xunit;

namespace TKCode.Desktop.Tests;

/// <summary>更新下载卡片用的 Arc Progress：0–1 的进度、不显示百分比读数。</summary>
public sealed class ArcProgressSnapshotTests
{
    [Fact]
    public async Task Progress_ShowsBarOnly_ForFractionValues()
    {
        await HeadlessApp.Session.Dispatch(() =>
        {
            var stack = new StackPanel { Spacing = 14, Width = 312 };
            var bars = new[] { 0.0, 0.4, 1.0 }.Select(v => new Progress { Maximum = 1, Value = v, IsValueVisible = false }).ToList();
            foreach (var bar in bars)
            {
                stack.Children.Add(bar);
            }
            var window = new Window { Width = 340, Height = 120, Content = new Border { Padding = new Thickness(14), Child = stack } };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();

            Assert.Equal([0, 40, 100], bars.Select(b => b.Percentage));
            Assert.All(bars, b => Assert.False(b.IsValueVisible));
            Assert.True(bars[2].IsComplete);
            HeadlessApp.Save(window, "arc-progress");
            window.Close();
        }, TestContext.Current.CancellationToken);
    }
}
