using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using StreamMark.Avalonia;
using StreamMark.Avalonia.Extensions;
using StreamMark.Avalonia.Rendering;
using StreamMark.Avalonia.Transcript;
using TKCode.Desktop.Controls;
using TKCode.Desktop.Localization;
using TKCode.Desktop.Previews;
using TKCode.Desktop.Services;
using TKCode.Desktop.ViewModels;
using TKCode.Desktop.Views;
using Xunit;
using TKCode.Config;

namespace TKCode.Desktop.Tests;

public sealed class MarkdownLinkTests
{
    [Fact]
    public void FileLinks_ResolveExistingPaths_WithoutTreatingLocationsAsFileNames()
    {
        var root = Directory.CreateTempSubdirectory("tkcode-markdown-links-").FullName;
        try
        {
            var source = Path.Combine(Directory.CreateDirectory(Path.Combine(root, "src")).FullName, "App.cs");
            var encoded = Path.Combine(root, "中文 # %20.cs");
            File.WriteAllText(source, "class App {}\n");
            File.WriteAllText(encoded, "// encoded path\n");

            foreach (var url in new[]
                     {
                         source, new Uri(source).AbsoluteUri, "src/App.cs", "./src/../src/App.cs",
                         "src/App.cs#L12-L15", source + ":12", source + ":12:3",
                         new Uri(source).AbsoluteUri + "#L12", "src/App.cs?view=source#L12",
                     })
            {
                Assert.Equal(source, MarkdownLinks.ResolveFile(url, root));
            }
            Assert.Equal(source, MarkdownLinks.ResolveFile(new Uri(source).AbsoluteUri, null));
            Assert.Equal(encoded, MarkdownLinks.ResolveFile(Uri.EscapeDataString(Path.GetFileName(encoded)), root));
            var encodedFileUri = new Uri(root + Path.DirectorySeparatorChar).AbsoluteUri + Uri.EscapeDataString(Path.GetFileName(encoded));
            Assert.Equal(encoded, MarkdownLinks.ResolveFile(encodedFileUri, root));
            Assert.Null(MarkdownLinks.ResolveFile("src/missing.cs", root));
            Assert.Null(MarkdownLinks.ResolveFile("src", root));
            Assert.Null(MarkdownLinks.ResolveFile("src/App.cs", null));
            Assert.Null(MarkdownLinks.ResolveFile("#heading", root));
            Assert.Null(MarkdownLinks.ResolveFile("https://example.com/src/App.cs", root));
            Assert.Null(MarkdownLinks.ResolveFile("javascript:alert(1)", root));
            Assert.Null(MarkdownLinks.ResolveFile("invalid\0path", root));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task LinkPolicy_AllowsFileAndBrowserLinks()
    {
        await HeadlessApp.Session.Dispatch(() =>
        {
            var policy = MarkdownSetup.Services.Links;
            foreach (var url in new[] { "src/App.cs", Path.Combine(Path.GetTempPath(), "App.cs"), "file:///tmp/App.cs", "https://example.com", "http://example.com", "mailto:hello@example.com" })
            {
                Assert.NotEqual(LinkDecision.Block, policy.Evaluate(url));
            }
            Assert.Equal(LinkDecision.Block, policy.Evaluate("javascript:alert(1)"));
            Assert.Equal(LinkDecision.Block, policy.Evaluate("data:text/html,hello"));
            Assert.Equal(LinkDecision.Confirm, policy.Evaluate("https://example.com", "https://github.com"));
            return true;
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task MainWindow_OpensFileLinksInWorkbench_UsingTheMarkdownDirectory()
    {
        var home = Directory.CreateTempSubdirectory("tkcode-link-home-").FullName;
        var root = Directory.CreateTempSubdirectory("tkcode-link-project-").FullName;
        var source = Path.Combine(root, "App.cs");
        var docs = Directory.CreateDirectory(Path.Combine(root, "docs")).FullName;
        var linked = Path.Combine(docs, "App.cs");
        var readme = Path.Combine(docs, "README.md");
        File.WriteAllText(source, "// project file\n");
        File.WriteAllText(linked, "// file next to the Markdown document\n");
        File.WriteAllText(readme, "[open](App.cs#L2)\n");
        var previousHome = Environment.GetEnvironmentVariable("TKCODE_HOME");
        var previousRuntimes = Environment.GetEnvironmentVariable("TKCODE_EXTERNAL_RUNTIMES");
        Environment.SetEnvironmentVariable("TKCODE_HOME", home);
        Environment.SetEnvironmentVariable("TKCODE_EXTERNAL_RUNTIMES", "0");
        try
        {
            var services = new ServiceCollection();
            services.AddLogging(b => b.SetMinimumLevel(LogLevel.Critical));
            Program.ConfigureServices(services);
            await using var provider = services.BuildServiceProvider();
            await HeadlessApp.Session.Dispatch(async () =>
            {
                var vm = provider.GetRequiredService<MainWindowViewModel>();
                vm.Conversation.SetWorkspace(Project.Create("project", [root]));
                var window = new MainWindow { DataContext = vm, Width = 1280, Height = 760 };
                try
                {
                    window.Show();
                    var transcript = window.FindControl<TranscriptView>("Transcript")!;
                    Click(transcript, "App.cs");
                    var first = Assert.IsType<FileTabViewModel>(vm.Workbench.ActiveTab);
                    Assert.Equal(source, first.FilePath);
                    Assert.True(vm.IsWorkbenchOpen);
                    Dispatcher.UIThread.RunJobs();
                    window.UpdateLayout();
                    Assert.True(window.FindControl<DockPanel>("EditorColumn")!.Bounds.Width > 0);

                    vm.IsWorkbenchOpen = false;
                    Click(transcript, new Uri(source).AbsoluteUri + "#L2");
                    Assert.Same(first, vm.Workbench.ActiveTab);
                    Assert.Single(vm.Workbench.Tabs);
                    Assert.True(vm.IsWorkbenchOpen);

                    vm.Workbench.OpenFile(readme);
                    var deadline = DateTime.UtcNow.AddSeconds(20);
                    MarkdownView? markdown;
                    while ((markdown = window.GetVisualDescendants().OfType<MarkdownView>().SingleOrDefault(v => v.Markdown == "[open](App.cs#L2)")) is null && DateTime.UtcNow < deadline)
                    {
                        Dispatcher.UIThread.RunJobs();
                        await Task.Delay(20);
                    }
                    Assert.NotNull(markdown);
                    markdown.ApplyLatest();
                    Dispatcher.UIThread.RunJobs();
                    var segment = markdown.GetVisualDescendants().OfType<TextSegmentView>().Single();
                    Click(segment, "App.cs#L2");
                    Assert.Equal(linked, vm.Workbench.ActiveFilePath);
                    Assert.Equal(3, vm.Workbench.Tabs.Count);

                    var active = vm.Workbench.ActiveTab;
                    vm.IsWorkbenchOpen = false;
                    Click(transcript, "missing.cs", handled: false);
                    Click(transcript, "#missing-heading", handled: false);
                    Assert.Same(active, vm.Workbench.ActiveTab);
                    Assert.Equal(3, vm.Workbench.Tabs.Count);
                    Assert.False(vm.IsWorkbenchOpen);
                }
                finally
                {
                    window.Close();
                    foreach (var tab in vm.Workbench.Tabs.OfType<FileTabViewModel>())
                    {
                        tab.Dispose();
                    }
                }
                return true;
            }, TestContext.Current.CancellationToken);
        }
        finally
        {
            Environment.SetEnvironmentVariable("TKCODE_HOME", previousHome);
            Environment.SetEnvironmentVariable("TKCODE_EXTERNAL_RUNTIMES", previousRuntimes);
        }
    }

    [Fact]
    public void CodeSpans_ResolveExistingFiles_AndIgnoreSnippets()
    {
        var root = Directory.CreateTempSubdirectory("tkcode-code-span-").FullName;
        try
        {
            var docs = Directory.CreateDirectory(Path.Combine(root, "docs")).FullName;
            var note = Path.Combine(docs, "616对线2_日志实证流程汇总_20261019.md");
            var source = Path.Combine(root, "App.cs");
            File.WriteAllText(note, "# 标题\n");
            File.WriteAllText(source, "class App {}\n");
            var name = "616对线2_日志实证流程汇总_20261019.md";
            var relative = "docs/" + name;

            Assert.Equal(note, MarkdownLinks.ResolveCodeSpan(relative, root));
            Assert.Equal(note, MarkdownLinks.ResolveCodeSpan(" " + relative + " ", root));
            Assert.Equal(note, MarkdownLinks.ResolveCodeSpan(relative + ":12", root));
            Assert.Equal(note, MarkdownLinks.ResolveCodeSpan(relative + "#L10", root));
            Assert.Equal(note, MarkdownLinks.ResolveCodeSpan("markdown:" + relative, root));
            Assert.Equal(note, MarkdownLinks.ResolveCodeSpan("12:40:" + relative, root));
            Assert.Equal(note, MarkdownLinks.ResolveCodeSpan(name, docs));
            Assert.Equal(source, MarkdownLinks.ResolveCodeSpan(source, null));
            Assert.Equal(relative, MarkdownLinks.FenceTarget("md " + relative + "#L10"));
            Assert.Equal("src/App.cs", MarkdownLinks.FenceTarget("csharp src/App.cs#L10-20"));
            Assert.Equal(relative, MarkdownLinks.FenceTarget(relative));

            Assert.Null(MarkdownLinks.ResolveCodeSpan("missing.md", root));
            Assert.Null(MarkdownLinks.ResolveCodeSpan("Console.WriteLine", root));
            Assert.Null(MarkdownLinks.ResolveCodeSpan("null", root));
            Assert.Null(MarkdownLinks.ResolveCodeSpan("https://example.com/" + relative, root));
            Assert.Null(MarkdownLinks.ResolveCodeSpan("*.md", root));
            Assert.Null(MarkdownLinks.ResolveCodeSpan("a\nb.md", root));
            Assert.Null(MarkdownLinks.FenceTarget("cs"));
            Assert.Equal("App.cs", MarkdownLinks.FenceTarget("csharp App.cs"));
            Assert.False(MarkdownLinks.IsFileReference("https://example.com/a.md"));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task CodeSpanAndFence_OpenMarkdownPreview()
    {
        var home = Directory.CreateTempSubdirectory("tkcode-code-span-home-").FullName;
        var root = Directory.CreateTempSubdirectory("tkcode-code-span-project-").FullName;
        var docs = Directory.CreateDirectory(Path.Combine(root, "docs")).FullName;
        var note = Path.Combine(docs, "616对线2_日志实证流程汇总_20261019.md");
        File.WriteAllText(note, "# 标题\n\n正文\n");
        var relative = "docs/616对线2_日志实证流程汇总_20261019.md";
        var previousHome = Environment.GetEnvironmentVariable("TKCODE_HOME");
        var previousRuntimes = Environment.GetEnvironmentVariable("TKCODE_EXTERNAL_RUNTIMES");
        Environment.SetEnvironmentVariable("TKCODE_HOME", home);
        Environment.SetEnvironmentVariable("TKCODE_EXTERNAL_RUNTIMES", "0");
        try
        {
            var services = new ServiceCollection();
            services.AddLogging(b => b.SetMinimumLevel(LogLevel.Critical));
            Program.ConfigureServices(services);
            await using var provider = services.BuildServiceProvider();
            await HeadlessApp.Session.Dispatch(async () =>
            {
                var vm = provider.GetRequiredService<MainWindowViewModel>();
                vm.Conversation.SetWorkspace(Project.Create("project", [root]));
                var window = new MainWindow { DataContext = vm, Width = 1280, Height = 760 };
                try
                {
                    window.Show();
                    var markdown = new MarkdownView
                    {
                        Width = 720,
                        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
                        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top,
                    };
                    MarkdownServices.SetServices(markdown, MarkdownSetup.Services);
                    MarkdownServices.SetImageBaseUri(markdown, root);
                    markdown.Markdown = "已把案例追加为 `" + relative + "` 的第 10 节，不是 `null`。\n\n```md " + relative + "\n# 标题\n```\n\n```cs\nclass A {}\n```\n";
                    window.FindControl<Grid>("MainGrid")!.Children.Add(markdown);

                    var deadline = DateTime.UtcNow.AddSeconds(15);
                    TextSegmentView? segment = null;
                    while (segment is null && DateTime.UtcNow < deadline)
                    {
                        markdown.ApplyLatest();
                        Dispatcher.UIThread.RunJobs();
                        window.UpdateLayout();
                        segment = markdown.GetVisualDescendants().OfType<TextSegmentView>().FirstOrDefault(v => v.Content.Text.Contains(relative));
                        if (segment is null)
                        {
                            await Task.Delay(20);
                        }
                    }
                    Assert.NotNull(segment);

                    var span = segment.Content.Spans.Single(s => segment.Content.Text.Substring(s.Start, s.Length) == relative);
                    var bounds = segment.GetCharacterBounds(span.Start);
                    Assert.True(bounds.Width > 0);
                    Assert.True(window.TryOpenCodeSpan(segment, new Point(bounds.X + 1, bounds.Y + bounds.Height / 2)));
                    Assert.True(vm.IsWorkbenchOpen);
                    var tab = Assert.IsType<FileTabViewModel>(vm.Workbench.ActiveTab);
                    Assert.Equal(note, tab.FilePath);
                    while (tab.Preview is null && DateTime.UtcNow < deadline)
                    {
                        Dispatcher.UIThread.RunJobs();
                        await Task.Delay(20);
                    }
                    var preview = Assert.IsType<MarkdownPreviewViewModel>(tab.Preview);
                    Assert.Equal("# 标题\n\n正文", preview.Markdown);

                    var plain = segment.Content.Spans.Single(s => segment.Content.Text.Substring(s.Start, s.Length) == "null");
                    var plainBounds = segment.GetCharacterBounds(plain.Start);
                    Assert.False(window.TryOpenCodeSpan(segment, new Point(plainBounds.X + 1, plainBounds.Y + plainBounds.Height / 2)));
                    Assert.Single(vm.Workbench.Tabs);

                    var open = markdown.GetVisualDescendants().OfType<IconButton>()
                        .Single(b => ToolTip.GetTip(b) as string == Strings.Get(TKCodeLangResourceKind.OpenFilePreview));
                    open.PerformClick();
                    Assert.Same(tab, vm.Workbench.ActiveTab);
                    Assert.Single(vm.Workbench.Tabs);
                }
                finally
                {
                    window.Close();
                    foreach (var tab in vm.Workbench.Tabs.OfType<FileTabViewModel>())
                    {
                        tab.Dispose();
                    }
                }
                return true;
            }, TestContext.Current.CancellationToken);
        }
        finally
        {
            Environment.SetEnvironmentVariable("TKCODE_HOME", previousHome);
            Environment.SetEnvironmentVariable("TKCODE_EXTERNAL_RUNTIMES", previousRuntimes);
            Directory.Delete(root, true);
            Directory.Delete(home, true);
        }
    }

    private static void Click(Control source, string url, bool handled = true)
    {
        var args = new LinkClickedEventArgs(MarkdownView.LinkClickedEvent, source, url, null);
        source.RaiseEvent(args);
        Assert.Equal(handled, args.Handled);
    }
}
