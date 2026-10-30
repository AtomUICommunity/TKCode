using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using TKCode.Desktop.Motion;
using Xunit;

namespace TKCode.Desktop.Tests;

/// <summary>动效基础件：过程连续、可中途反向、结束状态与无动效时一致。</summary>
public sealed partial class MotionTests
{
    private static (Avalonia.Controls.Window Window, StackPanel Root) CreateWindow()
    {
        var root = new StackPanel { Width = 300, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top };
        var window = new Avalonia.Controls.Window { Width = 400, Height = 400, Content = root, Background = Brushes.Black };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, root);
    }

    [Fact]
    public async Task Reveal_AnimatesHeight_AndReversesMidway()
    {
        await HeadlessApp.Session.Dispatch(() => HeadlessApp.WithMotion(() =>
        {
            var (window, root) = CreateWindow();
            var reveal = new Reveal { IsOpen = false, Child = new Border { Height = 100, Background = Brushes.Purple } };
            root.Children.Add(reveal);
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Assert.Equal(0, reveal.Bounds.Height);
            Assert.False(reveal.Child!.IsVisible);

            reveal.IsOpen = true;
            HeadlessApp.Step(window, 60);
            var h1 = reveal.Bounds.Height;
            Assert.InRange(h1, 1, 99);
            HeadlessApp.Step(window, 60);
            Assert.True(reveal.Bounds.Height > h1);

            // 中途反向：从当前高度往回收，不跳到 0 或 100
            var before = reveal.Bounds.Height;
            reveal.IsOpen = false;
            HeadlessApp.Step(window, 16);
            Assert.InRange(reveal.Bounds.Height, 1, 99);
            Assert.True(Math.Abs(reveal.Bounds.Height - before) < 40);

            MotionTicker.FinishAll();
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Assert.Equal(0, reveal.Bounds.Height);
            Assert.False(reveal.Child.IsVisible);

            reveal.IsOpen = true;
            MotionTicker.FinishAll();
            window.UpdateLayout();
            Assert.Equal(100, reveal.Bounds.Height);
            Assert.Equal(1, reveal.Child.Opacity);
            window.Close();
        }), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Reveal_InitialValue_DoesNotAnimate()
    {
        await HeadlessApp.Session.Dispatch(() => HeadlessApp.WithMotion(() =>
        {
            var (window, root) = CreateWindow();
            var reveal = new Reveal { Child = new Border { Height = 50 } };
            root.Children.Add(reveal);
            reveal.IsOpen = false;
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Assert.Equal(0, MotionTicker.ActiveCount);
            Assert.Equal(0, reveal.Bounds.Height);
            window.Close();
        }), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Reveal_ContentTemplate_CreatedWhenOpened_ReleasedAfterCollapse()
    {
        await HeadlessApp.Session.Dispatch(() => HeadlessApp.WithMotion(() =>
        {
            var (window, root) = CreateWindow();
            var built = 0;
            var reveal = new Reveal
            {
                IsOpen = false,
                ContentTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<object?>((_, _) =>
                {
                    built++;
                    return new Border { Height = 60, Background = Brushes.Purple };
                }),
            };
            root.Children.Add(reveal);
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            // 收起时不创建内容
            Assert.Null(reveal.Child);

            reveal.IsOpen = true;
            HeadlessApp.Step(window, 40);
            Assert.NotNull(reveal.Child);
            Assert.InRange(reveal.Bounds.Height, 1, 59);
            MotionTicker.FinishAll();
            window.UpdateLayout();
            Assert.Equal(60, reveal.Bounds.Height);

            // 收起：动画期间内容还在（高度连续变小），播完才释放
            reveal.IsOpen = false;
            HeadlessApp.Step(window, 40);
            Assert.NotNull(reveal.Child);
            Assert.InRange(reveal.Bounds.Height, 1, 59);
            MotionTicker.FinishAll();
            window.UpdateLayout();
            Assert.Null(reveal.Child);
            Assert.Equal(0, reveal.Bounds.Height);
            Assert.Equal(1, built);
            window.Close();
        }), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Presence_HidesAfterExit_AndRestoresStyleValues()
    {
        await HeadlessApp.Session.Dispatch(() => HeadlessApp.WithMotion(() =>
        {
            var (window, root) = CreateWindow();
            var card = new Border { Height = 40, Background = Brushes.Teal };
            Presence.SetEffect(card, PresenceEffect.Pop);
            root.Children.Add(card);
            Dispatcher.UIThread.RunJobs();

            Presence.SetIsPresent(card, false);
            Assert.True(card.IsVisible);
            HeadlessApp.Step(window, 50);
            Assert.InRange(card.Opacity, 0.01, 0.99);
            Assert.NotNull(card.RenderTransform);
            MotionTicker.FinishAll();
            Assert.False(card.IsVisible);

            Presence.SetIsPresent(card, true);
            Assert.True(card.IsVisible);
            HeadlessApp.Step(window, 40);
            Assert.InRange(card.Opacity, 0.01, 0.99);
            MotionTicker.FinishAll();
            Assert.Equal(1, card.Opacity);
            Assert.Null(card.RenderTransform);
            Assert.False(card.IsSet(Visual.OpacityProperty));
            window.Close();
        }), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task MotionDisabled_SnapsImmediately()
    {
        await HeadlessApp.Session.Dispatch(() =>
        {
            var (window, root) = CreateWindow();
            var card = new Border { Height = 40 };
            var reveal = new Reveal { Child = new Border { Height = 30 } };
            root.Children.Add(card);
            root.Children.Add(reveal);
            Dispatcher.UIThread.RunJobs();
            Presence.SetIsPresent(card, false);
            reveal.IsOpen = false;
            window.UpdateLayout();
            Assert.False(card.IsVisible);
            Assert.Equal(0, reveal.Bounds.Height);
            Assert.Equal(0, MotionTicker.ActiveCount);
            window.Close();
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task SmoothSize_FollowsContentHeight()
    {
        await HeadlessApp.Session.Dispatch(() => HeadlessApp.WithMotion(() =>
        {
            var (window, root) = CreateWindow();
            var content = new Border { Height = 40 };
            var smooth = new SmoothSize { Child = content };
            root.Children.Add(smooth);
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Assert.Equal(40, smooth.Bounds.Height);

            content.Height = 120;
            window.UpdateLayout();
            HeadlessApp.Step(window, 50);
            Assert.InRange(smooth.Bounds.Height, 41, 119);
            MotionTicker.FinishAll();
            window.UpdateLayout();
            Assert.Equal(120, smooth.Bounds.Height);
            window.Close();
        }), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Composer_SingleLineToMultiline_MorphsParts()
    {
        await HeadlessApp.Session.Dispatch(() => HeadlessApp.WithMotion(() =>
        {
            var composer = new TKCode.Desktop.Views.ComposerView { Width = 640, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Bottom };
            composer.Classes.Add("compact");
            var window = new Avalonia.Controls.Window { Width = 700, Height = 400, Content = composer, Background = Brushes.Black };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            var input = composer.FindControl<TKCode.Desktop.Controls.Composer.ComposerEditor>("Input")!;
            var pickers = composer.FindControl<StackPanel>("Pickers")!;
            var oneLine = composer.Bounds.Height;
            HeadlessApp.Save(window, "motion-composer-0");

            input.Document.SetContent([new TKCode.Desktop.Controls.Composer.TextSegment(string.Concat(Enumerable.Repeat("您好", 120)))]);
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Assert.Contains("multiline", composer.Classes);
            HeadlessApp.Step(window, 40);
            // 中途：模型选择器带着反向位移从原位置滑过来，外框高度介于单行与两段之间
            Assert.IsType<TranslateTransform>(pickers.RenderTransform);
            HeadlessApp.Save(window, "motion-composer-mid");
            MotionTicker.FinishAll();
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Assert.Null(pickers.RenderTransform);
            Assert.True(composer.Bounds.Height > oneLine + 20);
            HeadlessApp.Save(window, "motion-composer-1");
            window.Close();
        }), TestContext.Current.CancellationToken);
    }

    private sealed partial class Expandable : CommunityToolkit.Mvvm.ComponentModel.ObservableObject
    {
        [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty]
        public partial bool IsExpanded { get; set; }
    }

    [Fact]
    public async Task Reveal_DataContextSwap_Snaps_UserToggle_Animates()
    {
        await HeadlessApp.Session.Dispatch(() => HeadlessApp.WithMotion(() =>
        {
            var (window, root) = CreateWindow();
            var reveal = new Reveal { Child = new Border { Height = 80 } };
            reveal.Bind(Reveal.IsOpenProperty, new Avalonia.Data.Binding(nameof(Expandable.IsExpanded)));
            var collapsed = new Expandable();
            var expanded = new Expandable { IsExpanded = true };
            root.DataContext = collapsed;
            root.Children.Add(reveal);
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Assert.Equal(0, reveal.Bounds.Height);

            // 虚拟化回收：换成另一条数据，直接到终态
            root.DataContext = expanded;
            Assert.Equal(0, MotionTicker.ActiveCount);
            window.UpdateLayout();
            Assert.Equal(80, reveal.Bounds.Height);
            Dispatcher.UIThread.RunJobs();

            // 用户切换：播动画
            expanded.IsExpanded = false;
            Assert.Equal(1, MotionTicker.ActiveCount);
            HeadlessApp.Step(window, 40);
            Assert.InRange(reveal.Bounds.Height, 1, 79);
            window.Close();
        }), TestContext.Current.CancellationToken);
    }

    [Fact]
    public void BrushMix_FromTransparent_KeepsTargetColor()
    {
        // 悬停：Transparent（#00FFFFFF）→ #222323，中途不应变成半透明的浅灰
        var mid = MotionBrushTransition.Mix(Colors.Transparent, Color.Parse("#222323"), 0.5);
        Assert.Equal((byte)0x22, mid.R);
        Assert.Equal((byte)0x23, mid.B);
        Assert.InRange(mid.A, 126, 129);
        // 两端都不透明时就是普通插值
        var opaque = MotionBrushTransition.Mix(Color.Parse("#000000"), Color.Parse("#FFFFFF"), 0.5);
        Assert.InRange(opaque.R, 127, 128);
        Assert.Equal(255, opaque.A);
        // 离开：颜色 → 透明，同样只降透明度
        var leaving = MotionBrushTransition.Mix(Color.Parse("#EFEFEF"), Colors.Transparent, 0.7);
        Assert.Equal((byte)0xEF, leaving.R);
    }

    [Fact]
    public async Task TranscriptAdapter_MarksOnlyAppendedEntries()
    {
        await HeadlessApp.Session.Dispatch(() => HeadlessApp.WithMotion(() =>
        {
            var entries = new System.Collections.ObjectModel.ObservableCollection<StreamMark.Avalonia.Transcript.TranscriptEntry>();
            var adapter = new TKCode.Desktop.Controls.TranscriptAdapter(entries);
            var session = new TKCode.Desktop.ViewModels.SessionViewModel(
                new TKCode.Rpc.SessionDto("s1", "/tmp", "m", "/tmp/s1.jsonl", new TKCode.Rpc.SessionOptionsDto()));
            var history = new TKCode.Desktop.ViewModels.UserMessageItemViewModel("历史消息");
            session.Items.Add(history);
            adapter.Attach(session);

            // 挂载时已有的消息不播入场；之后追加的播
            var fresh = new TKCode.Desktop.ViewModels.UserMessageItemViewModel("新消息");
            session.Items.Add(fresh);
            var oldView = new Border { DataContext = history };
            var newView = new Border { DataContext = fresh };
            Entrance.SetEffect(oldView, PresenceEffect.SlideUp);
            Entrance.SetEffect(newView, PresenceEffect.SlideUp);
            var root = new StackPanel { Children = { oldView, newView } };
            var window = new Avalonia.Controls.Window { Width = 300, Height = 200, Content = root };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            HeadlessApp.Step(window, 30);
            Assert.Equal(1, oldView.Opacity);
            Assert.InRange(newView.Opacity, 0.01, 0.99);
            window.Close();
        }), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task IndicatorPanel_GlidesBetweenItems()
    {
        await HeadlessApp.Session.Dispatch(() => HeadlessApp.WithMotion(() =>
        {
            var items = Enumerable.Range(0, 3).Select(_ => new Border { Width = 60, Height = 24 }).ToArray();
            var row = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal };
            foreach (var item in items)
            {
                row.Children.Add(item);
            }
            var panel = new IndicatorPanel { IndicatorBrush = Brushes.Purple, Child = row, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left };
            var window = new Avalonia.Controls.Window { Width = 300, Height = 120, Content = panel, Background = Brushes.Black };
            window.Show();
            IndicatorPanel.SetIsCurrent(items[0], true);
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            MotionTicker.FinishAll();
            Assert.Equal(0, panel.IndicatorRect.X);
            Assert.Equal(60, panel.IndicatorRect.Width);

            // 切到第三项：底板从第一项滑过去，中途位于两者之间
            IndicatorPanel.SetIsCurrent(items[0], false);
            IndicatorPanel.SetIsCurrent(items[2], true);
            HeadlessApp.Step(window, 50);
            Assert.InRange(panel.IndicatorRect.X, 1, 119);
            HeadlessApp.Save(window, "motion-indicator-mid");
            MotionTicker.FinishAll();
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(120, panel.IndicatorRect.X, 0.5);
            Assert.Same(items[2], panel.Current);
            window.Close();
        }), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Morph_Fly_StartsTargetAtSourcePosition()
    {
        await HeadlessApp.Session.Dispatch(() => HeadlessApp.WithMotion(() =>
        {
            var source = new Border { Width = 200, Height = 60, Margin = new Thickness(40, 100, 0, 0),
                                      HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top };
            var target = new Border { Width = 260, Height = 50, Margin = new Thickness(20, 0, 0, 20), IsVisible = false,
                                      HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Bottom };
            var container = new Grid { Children = { source, target } };
            var window = new Avalonia.Controls.Window { Width = 400, Height = 400, Content = container };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();

            Morph.Fly(source, target, container);
            source.IsVisible = false;
            target.IsVisible = true;
            window.UpdateLayout();
            // 起点：左边缘与源对齐，底边与源的底边对齐
            var start = Assert.IsType<TranslateTransform>(target.RenderTransform);
            Assert.Equal(20, start.X, 0.5);
            Assert.True(start.Y < -100);
            HeadlessApp.Step(window, 60);
            Assert.True(start.Y is < 0 and > -150);
            MotionTicker.FinishAll();
            Assert.Null(target.RenderTransform);
            window.Close();
        }), TestContext.Current.CancellationToken);
    }

    [Fact]
    public void Spring_SettlesAndOvershootsSlightly()
    {
        var ease = new SpringEase(0.72);
        Assert.Equal(0, ease.Ease(0));
        Assert.Equal(1, ease.Ease(1));
        var max = Enumerable.Range(0, 100).Select(i => ease.Ease(i / 100.0)).Max();
        Assert.InRange(max, 1.005, 1.08);
    }
}
