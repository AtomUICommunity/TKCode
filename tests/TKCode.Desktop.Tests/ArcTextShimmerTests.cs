using TKCode.Arc.Localization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using TKCode.Arc;
using TKCode.Arc.Motion;
using TKCode.Desktop.Controls;
using TKCode.Desktop.Localization;
using TKCode.Desktop.Motion;
using TKCode.Desktop.Services;
using TKCode.Desktop.ViewModels;
using TKCode.Desktop.Views;
using TKCode.Rpc;
using Xunit;

namespace TKCode.Desktop.Tests;

public sealed class ArcTextShimmerTests
{
    [Fact]
    public async Task StatusShimmer_UsesReleasedArcTheme_AndAccessibleText()
    {
        await HeadlessApp.Session.Dispatch(() =>
        {
            var shimmer = new StatusShimmer { Text = "Thinking", FontSize = 13 };
            var window = Show(shimmer);
            try
            {
                Assert.Equal(typeof(TextShimmer), shimmer.StyleKey);
                Assert.True(shimmer.Bounds.Width > 0);
                Assert.True(shimmer.Bounds.Height > 0);
                Assert.NotNull(shimmer.Foreground);
                Assert.Equal("Thinking", AutomationProperties.GetName(shimmer));
                Assert.Equal("Busy", AutomationProperties.GetItemStatus(shimmer));
                shimmer.IsActive = false;
                Assert.Equal(string.Empty, AutomationProperties.GetItemStatus(shimmer));
                Assert.Equal("Thinking", AutomationProperties.GetName(shimmer));
                HeadlessApp.Save(window, "thinking-shimmer-motion-off");
            }
            finally
            {
                Close(window);
            }
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task StatusShimmer_PublicClock_StopsWhenInactiveHiddenOrUnloaded_AndResumes()
    {
        await HeadlessApp.Session.Dispatch(() =>
        {
            using var fixture = new MotionFixture();
            var shimmer = new StatusShimmer { Text = "Thinking" };
            var host = new StackPanel { Children = { shimmer } };
            var window = Show(host);
            try
            {
                // Exercise Arc's lifecycle gates even on machines whose OS requests reduced motion.
                ArcMotion.SetLevel(shimmer, ArcMotionLevel.Full);
                AssertRequestsFrames(fixture.Clock, window);

                shimmer.IsActive = false;
                AssertStopsRequestingFrames(fixture.Clock, window);
                Assert.Equal(string.Empty, AutomationProperties.GetItemStatus(shimmer));
                shimmer.IsActive = true;
                AssertRequestsFrames(fixture.Clock, window);

                shimmer.IsVisible = false;
                AssertStopsRequestingFrames(fixture.Clock, window);
                shimmer.IsVisible = true;
                Flush(window);
                AssertRequestsFrames(fixture.Clock, window);

                host.IsVisible = false;
                AssertStopsRequestingFrames(fixture.Clock, window);
                host.IsVisible = true;
                Flush(window);
                AssertRequestsFrames(fixture.Clock, window);

                host.Children.Remove(shimmer);
                Flush(window);
                Assert.False(shimmer.IsLoaded);
                AssertStopsRequestingFrames(fixture.Clock, window);
                host.Children.Add(shimmer);
                Flush(window);
                ArcMotion.SetLevel(shimmer, ArcMotionLevel.Full);
                Assert.True(shimmer.IsLoaded);
                AssertRequestsFrames(fixture.Clock, window);
            }
            finally
            {
                Close(window);
                AssertStopsRequestingFrames(fixture.Clock, window);
            }
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task StatusShimmer_PublicClock_PausesOutsideTheTranscriptViewport()
    {
        await HeadlessApp.Session.Dispatch(() =>
        {
            using var fixture = new MotionFixture();
            var shimmer = new StatusShimmer { Text = "Thinking" };
            var scroll = new Avalonia.Controls.ScrollViewer
            {
                Content = new StackPanel
                {
                    Children = { shimmer, new Border { Height = 1400 } }
                }
            };
            var window = Show(scroll);
            try
            {
                ArcMotion.SetLevel(shimmer, ArcMotionLevel.Full);
                AssertRequestsFrames(fixture.Clock, window);
                scroll.Offset = new Vector(0, 900);
                Flush(window);
                AssertStopsRequestingFrames(fixture.Clock, window);
                scroll.Offset = default;
                Flush(window);
                AssertRequestsFrames(fixture.Clock, window);
            }
            finally
            {
                Close(window);
            }
        }, TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData(ArcMotionLevel.Off)]
    [InlineData(ArcMotionLevel.Reduced)]
    public async Task StatusShimmer_PublicClock_IsStillBelowFullMotion(ArcMotionLevel level)
    {
        await HeadlessApp.Session.Dispatch(() =>
        {
            using var fixture = new MotionFixture();
            var shimmer = new StatusShimmer { Text = "Thinking" };
            var window = Show(shimmer);
            try
            {
                ArcMotion.SetLevel(shimmer, level);
                AssertStopsRequestingFrames(fixture.Clock, window);
                Assert.Equal("Thinking", shimmer.Text);
                Assert.True(shimmer.Bounds.Width > 0);
                Assert.Equal("Thinking", AutomationProperties.GetName(shimmer));
            }
            finally
            {
                Close(window);
            }
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task StatusShimmer_BridgesMotionSettingsOnlyWhileAttached()
    {
        await HeadlessApp.Session.Dispatch(() =>
        {
            using var fixture = new MotionFixture();
            MotionSettings.ForceDisabled = true;
            var shimmer = new StatusShimmer { Text = "Thinking" };
            Assert.Equal(ArcMotionLevel.Off, ArcMotion.GetLevel(shimmer));
            var host = new StackPanel { Children = { shimmer } };
            var window = Show(host);
            try
            {
                AssertStopsRequestingFrames(fixture.Clock, window);
                MotionSettings.ForceDisabled = false;
                Assert.Equal(MotionSettings.IsEnabled ? ArcMotionLevel.Full : ArcMotionLevel.Off,
                    ArcMotion.GetLevel(shimmer));
                MotionSettings.UserReduceMotion = true;
                Assert.Equal(ArcMotionLevel.Off, ArcMotion.GetLevel(shimmer));
                AssertStopsRequestingFrames(fixture.Clock, window);
                MotionSettings.UserReduceMotion = false;
                MotionSettings.ForceDisabled = true;
                Assert.Equal(ArcMotionLevel.Off, ArcMotion.GetLevel(shimmer));

                host.Children.Remove(shimmer);
                ArcMotion.SetLevel(shimmer, ArcMotionLevel.Reduced);
                MotionSettings.ForceDisabled = false;
                MotionSettings.ForceDisabled = true;
                Assert.Equal(ArcMotionLevel.Reduced, ArcMotion.GetLevel(shimmer));
                host.Children.Add(shimmer);
                Flush(window);
                Assert.Equal(ArcMotionLevel.Off, ArcMotion.GetLevel(shimmer));
                AssertStopsRequestingFrames(fixture.Clock, window);
            }
            finally
            {
                Close(window);
            }
        }, TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ThinkingTemplates_FollowBlockStreaming_AndPreserveExpansionStatsAndContent(bool inActivityGroup)
    {
        await HeadlessApp.Session.Dispatch(() =>
        {
            var thought = new ThinkingBlockViewModel(1250) { Text = "Keep the original reasoning." };
            object model = thought;
            if (inActivityGroup)
            {
                var group = new ActivityGroupViewModel { IsActive = true, IsExpanded = true };
                group.Steps.Add(thought);
                model = group;
            }
            var root = BuildTemplate(model);
            var window = Show(root);
            try
            {
                var shimmer = Assert.Single(root.GetVisualDescendants().OfType<StatusShimmer>());
                Assert.True(shimmer.IsActive);
                Assert.Equal("Thinking", shimmer.Text);
                Assert.Equal(thought.Header, shimmer.Text);
                // 流式输出中自动展开
                Assert.True(thought.IsExpanded);
                var button = shimmer.GetVisualAncestors().OfType<Avalonia.Controls.Button>().First();
                button.Foreground = Brushes.Magenta;
                Flush(window);
                Assert.Same(button.Foreground, shimmer.Foreground);
                Assert.Contains(root.GetVisualDescendants().OfType<SelectableTextBlock>(),
                    block => block.Text == thought.Text && block.IsEffectivelyVisible);
                var stats = Assert.Single(root.GetVisualDescendants().OfType<Avalonia.Controls.TextBlock>(),
                    block => block.Text == thought.Stats);
                Assert.Equal(1, Assert.IsType<TranslateTransform>(stats.RenderTransform).Y);

                // 用户手动收起再展开后不再自动改变：流式结束后保持展开
                thought.ToggleCommand.Execute(null);
                Assert.False(thought.IsExpanded);
                thought.ToggleCommand.Execute(null);
                Flush(window);
                Assert.True(thought.IsExpanded);

                thought.IsStreaming = false;
                Flush(window);
                Assert.False(shimmer.IsActive);
                Assert.Equal(Strings.Get(TKCodeLangResourceKind.ThoughtProcess), shimmer.Text);
                Assert.True(thought.IsExpanded);
                Assert.Equal("Keep the original reasoning.", thought.Text);
                Assert.Equal(string.Empty, AutomationProperties.GetItemStatus(shimmer));
            }
            finally
            {
                Close(window);
            }
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task WaitingTemplate_UsesThinkingInBothLanguages_KeepsElapsedAndHasNoSpinner()
    {
        await HeadlessApp.Session.Dispatch(() =>
        {
            var manager = ArcLanguageManager.Current!;
            var language = manager.Language;
            var session = new SessionViewModel(new SessionDto("shimmer", "/tmp/project", "test", "/tmp/session.jsonl", new SessionOptionsDto(), "codex"));
            session.Apply(new AgentStarted());
            var root = BuildTemplate(session);
            var window = Show(root);
            try
            {
                var shimmer = Assert.Single(root.GetVisualDescendants().OfType<StatusShimmer>());
                Assert.Empty(root.GetVisualDescendants().OfType<DotSpinner>());
                Assert.True(shimmer.IsActive);
                Assert.Matches(@"^\d+s$", session.PonderText);
                var elapsed = Assert.Single(root.GetVisualDescendants().OfType<Avalonia.Controls.TextBlock>(),
                    block => block.Text == session.PonderText);
                Assert.Equal(12, elapsed.FontSize);
                Assert.Equal(1, Assert.IsType<TranslateTransform>(elapsed.RenderTransform).Y);
                foreach (var tag in new[] { LanguageTags.EnUS, LanguageTags.ZhCN })
                {
                    manager.Set(tag);
                    Flush(window);
                    Assert.Equal("Thinking", Strings.Get(TKCodeLangResourceKind.Thinking));
                    Assert.Equal("Thinking", shimmer.Text);
                }
                session.Apply(new MessageStarted("m1", "assistant", null));
                session.Apply(new ThinkingDelta("m1", 0, "Now reasoning."));
                Flush(window);
                Assert.False(session.IsPondering);
                Assert.False(shimmer.IsActive);
                Assert.True(session.IsRunning);
            }
            finally
            {
                session.Apply(new AgentEnded());
                Close(window);
                manager.Set(language);
            }
        }, TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData(false, "thinking-templates-dark")]
    [InlineData(true, "thinking-templates-light")]
    public async Task ThinkingTemplates_RenderOnTheActualPalette_InBothThemes(bool light, string snapshot)
    {
        await HeadlessApp.Session.Dispatch(async () =>
        {
            await TestTheme.ApplyAsync(light ? TestTheme.LightId : TestTheme.DarkId);
            var session = new SessionViewModel(new SessionDto("preview", "/tmp/project", "test", "/tmp/session.jsonl", new SessionOptionsDto(), "codex"));
            session.Apply(new AgentStarted());
            var standalone = new ThinkingBlockViewModel(2100)
            {
                Text = "Checking the request and preserving the original reasoning text.",
                IsExpanded = true
            };
            var completed = new ThinkingBlockViewModel(1250)
            {
                Text = "The completed reasoning remains selectable and expandable.",
                IsStreaming = false,
                IsExpanded = true
            };
            var group = new ActivityGroupViewModel();
            group.SetSteps([completed]);
            group.IsExpanded = true;
            var stack = new StackPanel { Spacing = 18 };
            stack.Children.Add(BuildTemplate(session));
            stack.Children.Add(BuildTemplate(standalone));
            stack.Children.Add(BuildTemplate(group));
            var window = Show(new Border { Padding = new Thickness(28), Child = stack });
            var clock = new ManualFrameClock();
            using var installation = clock.Install();
            try
            {
                var shimmers = stack.GetVisualDescendants().OfType<StatusShimmer>().ToArray();
                Assert.Equal(3, shimmers.Length);
                // Desktop entrance/reveal motion stays disabled; opt only Arc's status sweep into this manual clock.
                foreach (var shimmer in shimmers)
                {
                    ArcMotion.SetLevel(shimmer, ArcMotionLevel.Full);
                }
                clock.Advance(TimeSpan.FromMilliseconds(800));
                Flush(window);
                Assert.Equal(light ? Color.Parse("#FFFFFF") : Color.Parse("#141414"),
                    Assert.IsAssignableFrom<ISolidColorBrush>(window.Background).Color);
                Assert.Equal(2, shimmers.Count(shimmer => shimmer.IsActive));
                HeadlessApp.Save(window, snapshot);
            }
            finally
            {
                session.Apply(new AgentEnded());
                Close(window);
                clock.Advance(TimeSpan.FromSeconds(2));
                await TestTheme.ApplyAsync(TestTheme.DarkId);
            }
            return true;
        }, TestContext.Current.CancellationToken);
    }

    private static Control BuildTemplate(object model)
    {
        var template = new TranscriptTemplates().Single(item => item.Match(model));
        var root = template.Build(model)!;
        root.DataContext = model;
        return root;
    }

    private static Window Show(Control content)
    {
        var window = new Window { Width = 560, Height = 340, Content = content };
        window.Bind(Window.BackgroundProperty, new DynamicResourceExtension("TKCode.WindowBg"));
        window.Bind(Window.ForegroundProperty, new DynamicResourceExtension("TKCode.TextPrimary"));
        window.Show();
        Flush(window);
        return window;
    }

    private static void Close(Window window)
    {
        // A headless closed Window retains its visual content (and its children's effective visibility).
        // Explicitly detach it so the externally driven Arc clock observes an actual visual-tree unload.
        window.Content = null;
        window.Close();
    }

    private static void Flush(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }

    private static void AssertRequestsFrames(ManualFrameClock clock, Window window)
    {
        Flush(window);
        var before = clock.RequestCount;
        clock.Advance(TimeSpan.FromMilliseconds(250));
        Flush(window);
        Assert.True(clock.RequestCount > before, "Active visible shimmer should request Arc frames.");
    }

    private static void AssertStopsRequestingFrames(ManualFrameClock clock, Window window)
    {
        // Completion can glide/settle; a scheduler callback already in flight must also be drained.
        clock.Advance(TimeSpan.FromSeconds(2));
        Flush(window);
        var settled = clock.RequestCount;
        clock.Advance(TimeSpan.FromMilliseconds(250));
        Flush(window);
        Assert.Equal(settled, clock.RequestCount);
    }

    private sealed class MotionFixture : IDisposable
    {
        private readonly bool _forced = MotionSettings.ForceDisabled;
        private readonly bool _reduced = MotionSettings.UserReduceMotion;
        private readonly IDisposable _installation;

        public ManualFrameClock Clock { get; } = new();

        public MotionFixture()
        {
            _installation = Clock.Install();
            MotionSettings.UserReduceMotion = false;
            MotionSettings.ForceDisabled = false;
        }

        public void Dispose()
        {
            MotionSettings.UserReduceMotion = _reduced;
            MotionSettings.ForceDisabled = _forced;
            Clock.Advance(TimeSpan.FromSeconds(2));
            _installation.Dispose();
        }
    }
}
