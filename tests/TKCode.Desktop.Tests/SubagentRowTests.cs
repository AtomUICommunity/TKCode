using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using TKCode.Desktop.Controls;
using TKCode.Desktop.Icons;
using TKCode.Desktop.Services;
using TKCode.Desktop.ViewModels;
using TKCode.Desktop.Views;
using Xunit;

namespace TKCode.Desktop.Tests;

public sealed class SubagentRowTests
{
    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 1)]
    [InlineData(false, 1.5)]
    [InlineData(true, 1.5)]
    public Task SubagentGroup_ListsAgentsCompactly_AndRowsAreClickable(bool light, double scale) =>
        HeadlessApp.Session.Dispatch(async () =>
        {
            var previousTheme = TestTheme.CurrentId;
            var previousScale = UiZoom.Scale;
            Window? window = null;
            try
            {
                await TestTheme.ApplyAsync(light ? TestTheme.LightId : TestTheme.DarkId);
                UiZoom.Scale = scale;
                var call = new ToolCallBlockViewModel("actual-tool-call", "agent")
                {
                    Arguments = """{"name":"thinking-performance","model":"deepseek-flash","run_in_background":true}""",
                    IsStreaming = false,
                };
                ToolCallBlockViewModel? opened = null;
                var openCount = 0;
                call.OpenSubagentRequested = target => { opened = target; ++openCount; };
                var other = new ToolCallBlockViewModel("other-tool-call", "agent")
                {
                    Arguments = """{"name":"data-analyst"}""",
                    IsStreaming = false,
                    Status = ToolCallStatus.Running,
                };
                var group = new SubagentGroupViewModel();
                group.SetAgents([new SubagentCardViewModel(call), new SubagentCardViewModel(other)]);
                var view = new SubagentGroupView { DataContext = group };
                var content = new StackPanel
                {
                    Spacing = 8,
                    Children = { new TextBlock { Text = "Adjacent transcript text" }, view }
                };
                window = new Avalonia.Controls.Window
                {
                    Width = 760,
                    Height = 260,
                    Content = new ZoomHost { Child = new Border { Padding = new Thickness(24), Child = content } }
                };
                window.Bind(Window.BackgroundProperty, new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("TKCode.WindowBg"));
                window.Show();
                Flush(window);

                var rows = view.GetVisualDescendants().OfType<SubagentCardView>().ToList();
                Assert.Equal(2, rows.Count);
                var row = rows[0];
                var button = Assert.Single(row.GetVisualDescendants().OfType<Button>());
                var name = row.GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == "thinking-performance");
                Assert.Equal(13, name.FontSize);
                // 紧凑：行高 28，相邻两行之间没有转录条目的间距
                Assert.Equal(28, button.Bounds.Height, 2);
                Assert.True(rows[1].TranslatePoint(default, view)!.Value.Y - row.TranslatePoint(default, view)!.Value.Y <= 32);
                Assert.Equal(row.Bounds.Width, button.Bounds.Width, 2);
                // 名称左对齐（状态列之后），不随 Arc 按钮的标签居中
                Assert.True(name.TranslatePoint(default, row)!.Value.X < 40);
                // 模型与后台标记；第二个子 Agent 是前台运行中
                var texts = row.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text).ToList();
                Assert.Contains("deepseek-flash", texts);
                Assert.Contains(Localization.Strings.Get(Localization.TKCodeLangResourceKind.SubagentBackground), texts);
                Assert.DoesNotContain(row.GetVisualDescendants().OfType<DotSpinner>(), s => s.IsEffectivelyVisible);
                Assert.Contains(rows[1].GetVisualDescendants().OfType<DotSpinner>(), s => s.IsEffectivelyVisible);
                Assert.Contains(view.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == group.Header);
                var point = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window)!.Value;
                window.MouseDown(point, MouseButton.Left);
                window.MouseUp(point, MouseButton.Left);
                Assert.Same(call, opened);
                Assert.Equal(1, openCount);
                button.Focus(NavigationMethod.Tab);
                window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
                window.KeyRelease(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
                Assert.Equal(2, openCount);
                HeadlessApp.Save(window, $"subagent-group-{(light ? "light" : "dark")}-{scale:0.0}");
            }
            finally
            {
                window?.Close();
                UiZoom.Scale = previousScale;
                await TestTheme.ApplyAsync(previousTheme);
            }
            return true;
        }, TestContext.Current.CancellationToken);

    private static void Flush(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }
}
