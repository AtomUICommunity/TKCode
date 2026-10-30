using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Media;
using Avalonia.Threading;
using StreamMark.Avalonia;
using StreamMark.Avalonia.Extensions;
using StreamMark.Avalonia.Rendering;
using TKCode.Desktop.Controls;
using TKCode.Desktop.Services;
using Xunit;

namespace TKCode.Desktop.Tests;

/// <summary>
/// 会话里选中的文字在 TKCode 暗 / 亮两套主题下清晰可读（issue #3）。
/// StreamMark 的 AtomUI 映射把选区底色指向 ColorPrimaryBgHover；TKCode 的主色是近黑 / 近白的中性色，
/// 亮色下推导出的是深灰，选中后黑底黑字，所以选区底色由 TKCode 调色板给出。截图写到 snapshots/ 供人工查看。
/// </summary>
public sealed class MarkdownSelectionThemeTests
{
    private const string Source = """
        坐标公式那行日志打印的 `y_found` 用的是 `FirstAiResponseOcrY`：

        | 运行/轮次 | 日志打印 y_found | 实际参与计算 |
        |---|---|---|
        | 66/1 | 89.0287 | 88.7778 |
        | 66/2 | 89.7578 | 89.7613 |

        ```csharp
        var y = GetPointCutLineOcrY();
        ```

        本次恰好不改变取整结果。
        """;

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SelectedText_IsReadable_WithTheRealAppTheme(bool dark)
    {
        await HeadlessApp.Session.Dispatch(async () =>
        {
            try
            {
                await TestTheme.ApplyAsync(dark ? TestTheme.DarkId : TestTheme.LightId);
                var view = new MarkdownView();
                MarkdownServices.SetServices(view, MarkdownSetup.Services);
                view.Markdown = Source;
                var window = new Avalonia.Controls.Window
                {
                    Width = 760,
                    Height = 420,
                    Content = new Border { Padding = new Thickness(32), Child = view },
                };
                window[!TemplatedControl.BackgroundProperty] = window.GetResourceObservable("TKCode.WindowBg").ToBinding();
                window.Show();
                Dispatcher.UIThread.RunJobs();
                view.SelectionController.SelectAll();
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
                HeadlessApp.Save(window, $"markdown-selection-{(dark ? "dark" : "light")}");

                var style = MarkdownStyle.Resolve(view);
                Assert.True(window.TryFindResource("TKCode.WindowBg", window.ActualThemeVariant, out var windowBg));
                var background = ColorOf((IBrush)windowBg!);
                var selection = ColorContrast.Over(ColorOf(style.SelectionBackground), background);
                var text = ColorContrast.Over(ColorOf(style.Foreground), selection);
                var readable = ColorContrast.Ratio(text, selection);
                Assert.True(readable > 4.5, $"dark={dark}: text {text} on selection {selection} has contrast {readable:F2}");
                // 选区本身也要和底色区分开
                var visible = ColorContrast.Ratio(selection, background);
                Assert.True(visible > 1.3, $"dark={dark}: selection {selection} on background {background} has contrast {visible:F2}");
                window.Close();
            }
            finally
            {
                // 其它测试默认运行在暗色主题下
                await TestTheme.ApplyAsync(TestTheme.DarkId);
            }

            return true;
        }, TestContext.Current.CancellationToken);
    }

    private static Color ColorOf(IBrush brush)
    {
        var color = Assert.IsAssignableFrom<ISolidColorBrush>(brush).Color;
        return Color.FromArgb((byte)Math.Round(color.A * brush.Opacity), color.R, color.G, color.B);
    }
}
