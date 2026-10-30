using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using StreamMark.Avalonia;
using StreamMark.Avalonia.Extensions;
using StreamMark.Avalonia.Rendering;
using TKCode.Desktop.Controls;
using TKCode.Desktop.Services;
using Xunit;

namespace TKCode.Desktop.Tests;

/// <summary>
/// 会话里的 Mermaid 图表在 TKCode 暗 / 亮两套主题下清晰可读。
/// AtomUI 的文字与代码块底色 token 是半透明的（暗色：85% 的白 / 4% 的白），传给图表渲染器时必须叠到真实底色上，
/// 否则图表是白底白字（亮色下是黑底黑字）。截图写到 snapshots/（或 $TKCODE_SNAPSHOT_DIR）供人工查看。
/// </summary>
public sealed class MermaidThemeSnapshotTests
{
    private const string Gateway = """
        flowchart LR
          client["客户端<br/>CLI / 桌面端"] --> check{"请求合法?"}
          check -->|是| route["路由<br/>选择 Provider"]
          check -->|否| reject["拒绝请求"]
          subgraph server["网关服务"]
            route --> codec["协议编解码"]
            codec --> guard["隐私检查"]
          end
          subgraph upstream["上游"]
            llm["模型服务"]
            plugin["插件 Provider"]
          end
          guard --> llm
          guard -->|插件| plugin
          llm --> store[("SQLite<br/>用量统计")]
        """;

    private const string Tall = """
        flowchart TD
          a[开始] --> b{校验}
          b -->|通过| c[解析]
          b -->|失败| z[结束]
          c --> d[路由]
          d --> e{鉴权}
          e -->|通过| f[转发]
          e -->|失败| z
          f --> g[编码]
          g --> h[响应]
          h --> z
        """;

    /// <summary>记录收到的渲染环境，返回一个小的假图表。</summary>
    private sealed class RecordingRenderer : IDiagramRenderer
    {
        private readonly List<DiagramRenderContext> _contexts = [];

        public IReadOnlyList<DiagramRenderContext> Contexts
        {
            get
            {
                lock (_contexts)
                {
                    return [.. _contexts];
                }
            }
        }

        public bool CanRender(string? language) => language == "mermaid";

        public Task<DiagramResult?> RenderAsync(string language, string code, DiagramRenderContext context, CancellationToken cancellationToken)
        {
            lock (_contexts)
            {
                _contexts.Add(context);
            }

            return Task.FromResult<DiagramResult?>(new FakeDiagram());
        }
    }

    private sealed class FakeDiagram : DiagramResult
    {
        public override Size Size => new(300, 120);

        public override void Draw(DrawingContext context, Rect destination) => context.DrawRectangle(Brushes.SteelBlue, null, destination);
    }

    /// <summary>其它测试默认运行在暗色主题下，用完恢复。</summary>
    private static Task RestoreDarkTheme() =>
        TestTheme.ApplyAsync(TestTheme.DarkId);

    private static async Task<Avalonia.Controls.Window> Show(bool dark, MarkdownServices services, string source)
    {
        await TestTheme.ApplyAsync(dark ? TestTheme.DarkId : TestTheme.LightId);
        var view = new MarkdownView();
        MarkdownServices.SetServices(view, services);
        view.Markdown = "```mermaid\n" + source + "\n```\n";
        var window = new Avalonia.Controls.Window
        {
            Width = 1100,
            Height = 720,
            Content = new ScrollViewer { Content = new Border { Padding = new Thickness(32), Child = view } },
        };
        window[!TemplatedControl.BackgroundProperty] = window.GetResourceObservable("TKCode.WindowBg").ToBinding();
        window.Show();

        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!window.GetVisualDescendants().OfType<DiagramBlockView>().Any(v => v.IsShowingDiagram) && DateTime.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(20);
        }

        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        return window;
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DiagramColors_AreOpaqueAndReadable_WithTheRealAppTheme(bool dark)
    {
        await HeadlessApp.Session.Dispatch(async () =>
        {
            try
            {
                var renderer = new RecordingRenderer();
                var window = await Show(dark, new MarkdownServices { Diagrams = [renderer] }, Tall);

                var context = renderer.Contexts.Last();
                Assert.Equal(dark, context.IsDark);
                Assert.Equal(byte.MaxValue, context.Foreground.A);
                Assert.Equal(byte.MaxValue, context.Background.A);
                Assert.Equal(byte.MaxValue, context.Accent.A);
                // 丢掉透明度时前景和背景是同一个颜色
                Assert.NotEqual(context.Foreground, context.Background);
                var contrast = ColorContrast.Ratio(context.Foreground, context.Background);
                Assert.True(contrast > 7, $"dark={dark}: foreground {context.Foreground} on {context.Background} has contrast {contrast:F2}");
                // 底色保持明暗方向
                Assert.Equal(dark, ColorContrast.Luminance(context.Background) < 0.2);
                window.Close();
            }
            finally
            {
                await RestoreDarkTheme();
            }

            return true;
        }, TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Diagrams_RenderInBothThemes(bool dark)
    {
        foreach (var (name, source) in new[] { ("gateway", Gateway), ("tall", Tall) })
        {
            await HeadlessApp.Session.Dispatch(async () =>
            {
                try
                {
                    var window = await Show(dark, MarkdownSetup.Services, source);
                    var block = window.GetVisualDescendants().OfType<DiagramBlockView>().Single();
                    Assert.True(block.IsShowingDiagram, $"{name}: diagram was not rendered");
                    HeadlessApp.Save(window, $"mermaid-{name}-{(dark ? "dark" : "light")}");
                    window.Close();
                }
                finally
                {
                    await RestoreDarkTheme();
                }

                return true;
            }, TestContext.Current.CancellationToken);
        }
    }
}
