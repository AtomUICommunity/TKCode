using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using TKCode.Config;
using TKCode.Desktop.Localization;
using TKCode.Desktop.Services;
using TKCode.Desktop.Services.Notifications;
using Xunit;

namespace TKCode.Desktop.Tests;

/// <summary>
/// 系统通知的决策（docs/design/notifications.md §4、§6）：档位 × 前后台、回合耗时阈值、合并与提示音节流、未读与撤回、点击；
/// 正文提取、设置写回、角标图片。平台实现换成记录调用的假通知器。
/// </summary>
public sealed class NotificationTests
{
    internal sealed class FakeNotifier : ISystemNotifier
    {
        public List<SystemNotification> Shown { get; } = [];
        public List<string> Withdrawn { get; } = [];
        public int Badge { get; private set; }
        public bool Disposed { get; private set; }

        public event Action<string>? Activated;

        public void Show(SystemNotification notification) => Shown.Add(notification);
        public void Withdraw(string sessionId) => Withdrawn.Add(sessionId);
        public void SetBadge(int count) => Badge = count;
        public void Click(string sessionId) => Activated?.Invoke(sessionId);
        public void Dispose() => Disposed = true;
    }

    internal sealed class FakePresence : IAppPresence
    {
        public bool IsForeground { get; set; } = true;
        public string? VisibleSessionId { get; set; }
    }

    private sealed record Harness(NotificationCenter Center, FakeNotifier Notifier, FakePresence Presence, FakeTimeProvider Time);

    private static Harness Create(NotificationPreferences? preferences = null)
    {
        var notifier = new FakeNotifier();
        var presence = new FakePresence();
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-10-30T10:00:00Z"));
        var center = new NotificationCenter(notifier, () => preferences ?? NotificationPreferences.Default, presence, time);
        return new Harness(center, notifier, presence, time);
    }

    private static SessionAttention Approval(string session, string command = "npm test") => new(session, "Fix the build", AttentionKind.Approval, command);

    private static SessionAttention Completed(string session, double seconds, string? text = "All tests pass. Ready to merge.") =>
        new(session, "Fix the build", AttentionKind.TurnCompleted, text, TimeSpan.FromSeconds(seconds));

    [Fact]
    public Task Background_NotifiesOnlyForSessionsTheUserIsNotLookingAt() => HeadlessApp.Session.Dispatch(() =>
    {
        var (center, notifier, presence, time) = Create();
        presence.VisibleSessionId = "a";

        // 正在看的会话、窗口在前台：不通知也不算未读
        center.Report(Approval("a"));
        Assert.Empty(notifier.Shown);
        Assert.Equal(0, center.UnreadCount);

        // 另一个会话：通知 + 未读
        center.Report(Approval("b"));
        var shown = Assert.Single(notifier.Shown);
        Assert.Equal("b", shown.SessionId);
        Assert.Equal("Fix the build", shown.Title);
        Assert.Equal(Strings.Format(TKCodeLangResourceKind.NotifyApprovalFormat, "npm test"), shown.Body);
        Assert.True(shown.Sound);
        Assert.True(center.IsUnread("b"));
        Assert.Equal(1, notifier.Badge);

        // 窗口失焦（或隐藏到托盘）：正在看的会话也算后台
        time.Advance(TimeSpan.FromSeconds(5));
        presence.IsForeground = false;
        center.Report(new SessionAttention("a", "Fix the build", AttentionKind.Question));
        Assert.Equal(2, notifier.Shown.Count);
        Assert.Equal(Strings.Get(TKCodeLangResourceKind.NotifyQuestion), notifier.Shown[^1].Body);
        Assert.Equal(2, center.UnreadCount);

        // 设置页打开时没有显示任何会话
        presence.IsForeground = true;
        presence.VisibleSessionId = null;
        time.Advance(TimeSpan.FromSeconds(5));
        center.Report(new SessionAttention("c", "", AttentionKind.PlanReview));
        Assert.Equal("TKCode", notifier.Shown[^1].Title);
        Assert.Equal(Strings.Get(TKCodeLangResourceKind.NotifyPlanReview), notifier.Shown[^1].Body);
        return true;
    }, CancellationToken.None);

    [Fact]
    public Task Modes_NeverAndAlways_UnreadIsIndependentOfTheMode() => HeadlessApp.Session.Dispatch(() =>
    {
        var never = Create(NotificationPreferences.Default with { NeedsInput = NotifyWhen.Never });
        never.Presence.IsForeground = false;
        never.Center.Report(Approval("a"));
        Assert.Empty(never.Notifier.Shown);
        // 档位只管弹不弹通知：未读照样计数
        Assert.Equal(1, never.Center.UnreadCount);

        var always = Create(NotificationPreferences.Default with { TurnCompleted = NotifyWhen.Always });
        always.Presence.VisibleSessionId = "a";
        always.Center.Report(Completed("a", 45));
        Assert.Single(always.Notifier.Shown);
        // 用户正看着：通知照发，但不算未读
        Assert.Equal(0, always.Center.UnreadCount);

        // 「需要你处理」仍是默认的仅后台：正在看的会话不通知
        always.Time.Advance(TimeSpan.FromSeconds(5));
        always.Center.Report(Approval("a"));
        Assert.Single(always.Notifier.Shown);
        return true;
    }, CancellationToken.None);

    [Fact]
    public Task TurnEnd_ThresholdErrorsAndAborts() => HeadlessApp.Session.Dispatch(() =>
    {
        var (center, notifier, presence, time) = Create();
        presence.IsForeground = false;

        // 不到 30 秒的成功回合：不通知，但算未读
        center.Report(Completed("a", 12));
        Assert.Empty(notifier.Shown);
        Assert.True(center.IsUnread("a"));

        center.Report(Completed("b", 31));
        Assert.Equal("Ready to merge.", Assert.Single(notifier.Shown).Body);

        // 出错：不看耗时，正文是错误的第一行
        center.Report(new SessionAttention("c", "Refactor", AttentionKind.TurnFailed, "429 Too Many Requests\nretry later", TimeSpan.FromSeconds(1)));
        Assert.Equal(Strings.Format(TKCodeLangResourceKind.NotifyTurnFailedFormat, "429 Too Many Requests"), notifier.Shown[^1].Body);

        // 用户停止：不通知，也不算未读
        center.Report(new SessionAttention("d", "Refactor", AttentionKind.TurnAborted, null, TimeSpan.FromMinutes(5)));
        Assert.Equal(2, notifier.Shown.Count);
        Assert.False(center.IsUnread("d"));

        // 没有正文的回合
        time.Advance(TimeSpan.FromSeconds(5));
        center.Report(Completed("e", 40, text: "```\ncode only\n```"));
        Assert.Equal(Strings.Get(TKCodeLangResourceKind.NotifyTurnFinished), notifier.Shown[^1].Body);

        // minTurnSeconds 可配
        var custom = Create(NotificationPreferences.From(new NotificationSettings { MinTurnSeconds = 5 }));
        custom.Presence.IsForeground = false;
        custom.Center.Report(Completed("a", 6));
        Assert.Single(custom.Notifier.Shown);
        return true;
    }, CancellationToken.None);

    [Fact]
    public Task Coalesce_KeepsTheLatestPerSession_AndThrottlesSound() => HeadlessApp.Session.Dispatch(() =>
    {
        var (center, notifier, presence, time) = Create();
        presence.IsForeground = false;

        center.Report(Approval("a", "first"));
        Assert.Single(notifier.Shown);
        Assert.True(notifier.Shown[0].Sound);

        // 2 秒内同一会话的后续事件先压着，只保留最新一条
        time.Advance(TimeSpan.FromMilliseconds(500));
        center.Report(Approval("a", "second"));
        time.Advance(TimeSpan.FromMilliseconds(500));
        center.Report(Approval("a", "third"));
        Assert.Single(notifier.Shown);

        // 另一个会话立即投递，但提示音在 2 秒内只响一次
        center.Report(Approval("b", "other"));
        Assert.Equal(2, notifier.Shown.Count);
        Assert.False(notifier.Shown[1].Sound);

        // 到时间补发最新那条，替换刚才那条，不再出声
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(3, notifier.Shown.Count);
        Assert.Equal(Strings.Format(TKCodeLangResourceKind.NotifyApprovalFormat, "third"), notifier.Shown[^1].Body);
        Assert.False(notifier.Shown[^1].Sound);

        // 窗口外的提示音节流过后再响
        time.Advance(TimeSpan.FromSeconds(3));
        center.Report(Approval("c"));
        Assert.True(notifier.Shown[^1].Sound);

        // 待发期间用户打开了会话：丢弃
        center.Report(Approval("c", "pending"));
        presence.IsForeground = true;
        presence.VisibleSessionId = "c";
        center.PresenceChanged();
        time.Advance(TimeSpan.FromSeconds(3));
        Assert.Equal(4, notifier.Shown.Count);
        return true;
    }, CancellationToken.None);

    [Fact]
    public Task Unread_ClearsWhenOpened_WithdrawsAndForgets() => HeadlessApp.Session.Dispatch(() =>
    {
        var (center, notifier, presence, time) = Create();
        var counts = new List<int>();
        center.UnreadChanged += counts.Add;
        presence.IsForeground = false;
        center.Report(Approval("a"));
        center.Report(Approval("b"));
        center.Report(Approval("a"));
        Assert.Equal(2, center.UnreadCount);
        Assert.Equal(2, notifier.Badge);

        // 切到 a 但窗口仍在后台：不清除
        presence.VisibleSessionId = "a";
        center.PresenceChanged();
        Assert.Equal(2, center.UnreadCount);

        // 窗口回到前台：a 已读，撤回它的通知
        presence.IsForeground = true;
        center.PresenceChanged();
        Assert.False(center.IsUnread("a"));
        Assert.Equal(["a"], notifier.Withdrawn);
        Assert.Equal(1, notifier.Badge);

        // 删除会话 b
        center.Forget("b");
        Assert.Equal(0, center.UnreadCount);
        Assert.Equal(0, notifier.Badge);
        Assert.Equal(["a", "b"], notifier.Withdrawn);
        Assert.Equal([1, 2, 1, 0], counts);
        return true;
    }, CancellationToken.None);

    [Fact]
    public Task Click_RaisesOpenRequested_AndTestNotificationBypassesRules() => HeadlessApp.Session.Dispatch(() =>
    {
        var (center, notifier, presence, _) = Create(NotificationPreferences.Default with { NeedsInput = NotifyWhen.Never, TurnCompleted = NotifyWhen.Never });
        var opened = new List<string>();
        center.OpenRequested += opened.Add;
        notifier.Click("a");
        Assert.Equal(["a"], opened);

        center.SendTest("TKCode", "hello");
        var test = Assert.Single(notifier.Shown);
        Assert.Equal(NotificationCenter.TestSessionId, test.SessionId);
        Assert.True(test.Sound);

        center.Dispose();
        Assert.True(notifier.Disposed);
        notifier.Click("b");
        Assert.Equal(["a"], opened);
        return true;
    }, CancellationToken.None);

    [Fact]
    public Task TurnSummary_CarriesStopReasonTextAndQueueContinuation() => HeadlessApp.Session.Dispatch(() =>
    {
        var session = new TKCode.Desktop.ViewModels.SessionViewModel(
            new TKCode.Rpc.SessionDto("s1", "/tmp/project", "m", "/tmp/s1.jsonl", new TKCode.Rpc.SessionOptionsDto()));
        var summaries = new List<TKCode.Desktop.ViewModels.TurnSummary>();
        session.TurnEnded += summaries.Add;

        void Turn(string reply, string stopReason, string? error = null)
        {
            session.Apply(new TKCode.Rpc.AgentStarted());
            session.Apply(new TKCode.Rpc.MessageStarted("u", "user", "do it"));
            session.Apply(new TKCode.Rpc.MessageStarted("a", "assistant", null));
            session.Apply(new TKCode.Rpc.TextDelta("a", 0, reply));
            session.Apply(new TKCode.Rpc.TextEnded("a", 0));
            session.Apply(new TKCode.Rpc.MessageEnded("a", "assistant", stopReason, error, null));
            session.Apply(new TKCode.Rpc.AgentEnded());
        }

        Turn("Looked around. All good.", "Stop");
        var done = Assert.Single(summaries);
        Assert.True(done.Completed);
        Assert.False(done.ContinuesWithQueue);
        Assert.Equal("Looked around. All good.", done.FinalText);
        Assert.True(done.Duration >= TimeSpan.Zero);

        Turn("partial", "Error", "rate limited");
        Assert.Equal("Error", summaries[^1].StopReason);
        Assert.Equal("rate limited", summaries[^1].ErrorMessage);

        // 正常结束、队列里还有消息：会自动发出下一条，不算「回合结束」
        session.QueuedPrompts.Add(new TKCode.Desktop.ViewModels.QueuedPromptViewModel("next", session));
        Turn("first part", "Stop");
        Assert.True(summaries[^1].ContinuesWithQueue);
        return true;
    }, CancellationToken.None);

    [Theory]
    [InlineData("All tests pass. Ready to merge.", "Ready to merge.")]
    [InlineData("I fixed the bug.\n\n```csharp\nvar x = 1;\n```", "I fixed the bug.")]
    [InlineData("## Summary\n- **Fixed** the `parser` crash\n- Updated [docs](https://example.com)", "Updated docs")]
    [InlineData("修改了三个文件。\n全部测试已经通过，可以合并到主分支了！", "全部测试已经通过，可以合并到主分支了！")]
    [InlineData("修改了三个文件。测试通过！", "修改了三个文件。测试通过！")]
    [InlineData("Refactored the settings page and the tray icon. Done.", "Refactored the settings page and the tray icon. Done.")]
    [InlineData("| a | b |\n|---|---|\n| 1 | 2 |", "1 2")]
    [InlineData("> quoted reply line", "quoted reply line")]
    [InlineData("   \n```\nonly code\n```\n", null)]
    public void LastSentence_SkipsCodeAndMarkdown(string markdown, string? expected) =>
        Assert.Equal(expected, NotificationText.LastSentence(markdown));

    [Fact]
    public void LastSentence_TruncatesWithoutSplittingSurrogates()
    {
        var text = new string('a', 118) + "😀😀😀";
        var result = NotificationText.LastSentence(text)!;
        Assert.True(result.Length <= NotificationText.MaxBodyLength);
        Assert.EndsWith("…", result);
        Assert.False(char.IsHighSurrogate(result[^2]));
    }

    [Fact]
    public void Settings_WriteBackRemovesDefaults_AndValidates()
    {
        var path = Path.Combine(Directory.CreateTempSubdirectory("tkcode-notify-settings-").FullName, "settings.json");
        using var settings = new UserSettingsService(NullLogger<UserSettingsService>.Instance, path);
        Assert.Equal(NotificationPreferences.Default, NotificationPreferences.From(settings.Current.Notifications));

        settings.SetNotifyTurnCompleted("always");
        settings.SetNotifyNeedsInput("never");
        settings.SetNotificationSound(false);
        var section = JsonNode.Parse(File.ReadAllText(path))!["notifications"]!;
        Assert.Equal("always", section["turnCompleted"]!.GetValue<string>());
        Assert.Equal("never", section["needsInput"]!.GetValue<string>());
        Assert.False(section["sound"]!.GetValue<bool>());
        var preferences = NotificationPreferences.From(settings.Current.Notifications);
        Assert.Equal(new NotificationPreferences(NotifyWhen.Always, NotifyWhen.Never, false, TimeSpan.FromSeconds(30)), preferences);

        // 默认值不落盘：全部恢复默认后整个节被删除
        settings.SetNotifyTurnCompleted("background");
        settings.SetNotifyNeedsInput("background");
        settings.SetNotificationSound(true);
        Assert.Null(JsonNode.Parse(File.ReadAllText(path))!["notifications"]);

        // 取值不对只警告（按默认处理），类型不对报错
        var warned = SettingsFile.Parse("""{ "notifications": { "turnCompleted": "sometimes", "minTurnSeconds": -3, "colour": 1 } }""");
        Assert.True(warned.IsValid);
        Assert.Contains(warned.Warnings, w => w.StartsWith("notifications.turnCompleted", StringComparison.Ordinal));
        Assert.Contains(warned.Warnings, w => w.StartsWith("notifications.minTurnSeconds", StringComparison.Ordinal));
        Assert.Contains(warned.Warnings, w => w.Contains("notifications.colour", StringComparison.Ordinal));
        var fallback = NotificationPreferences.From(warned.Settings!.Notifications);
        Assert.Equal(NotifyWhen.Background, fallback.TurnCompleted);
        Assert.Equal(TimeSpan.Zero, fallback.MinTurnDuration);
        var broken = SettingsFile.Parse("""{ "notifications": { "sound": "yes" } }""");
        Assert.False(broken.IsValid);
        Assert.Contains(broken.Errors, e => e.StartsWith("notifications.sound", StringComparison.Ordinal));
    }

    [Fact]
    public Task Badge_RendersOverlayAndTrayIcon() => HeadlessApp.Session.Dispatch(() =>
    {
        Assert.Equal("3", BadgeRenderer.CountText(3));
        Assert.Equal("9+", BadgeRenderer.CountText(12));
        foreach (var count in (int[])[1, 12])
        {
            var png = BadgeRenderer.OverlayPng(count);
            Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, png[..4]);
            File.WriteAllBytes(HeadlessApp.SnapshotPath($"notification-badge-{count}"), png);
        }
        using var tray = BadgeRenderer.TrayIcon(dot: true);
        Assert.Equal(64, tray.PixelSize.Width);
        tray.Save(HeadlessApp.SnapshotPath("notification-tray-dot"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
        Assert.Equal("TKCode", AppTray.ToolTip(0));
        Assert.Contains(Strings.Format(TKCodeLangResourceKind.TrayUnreadFormat, 2), AppTray.ToolTip(2));
        return true;
    }, CancellationToken.None);
}
