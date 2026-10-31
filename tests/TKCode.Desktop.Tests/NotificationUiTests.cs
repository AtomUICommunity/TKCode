using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TKCode.Desktop.Localization;
using TKCode.Desktop.Services;
using TKCode.Desktop.Services.Notifications;
using TKCode.Desktop.ViewModels;
using TKCode.Desktop.Views;
using Xunit;
using static TKCode.Desktop.Tests.NotificationTests;

namespace TKCode.Desktop.Tests;

/// <summary>
/// 系统通知端到端（docs/design/notifications.md §6）：真实 Host + Fake 模型，会话事件经 <see cref="DesktopNotifications.Attach"/> 进入通知中心；
/// 设置 › 通用的「通知」卡片截图（snapshots/settings-notifications.png）并写回 settings.json。
/// </summary>
public sealed class NotificationUiTests
{
    private const string Ask = """mcp:ask_user {"questions":[{"question":"Which database should we use?","header":"Database","options":[{"label":"Postgres","description":"Full featured"},{"label":"SQLite","description":"Zero setup"}]}]}""";

    [Fact]
    public async Task SessionEvents_ReachTheNotificationCenter()
    {
        var (_, provider) = await StartAsync("tkcode-notify-e2e-", """{ "notifications": { "minTurnSeconds": 0 } }""");
        await using var disposeProvider = provider;

        await HeadlessApp.Session.Dispatch(async () =>
        {
            var vm = provider.GetRequiredService<MainWindowViewModel>();
            var window = new MainWindow { DataContext = vm, Width = 1100, Height = 760 };
            window.Show();
            await WaitUntil(() => vm.Conversation.Picker.Selected is not null, "models loaded");
            var conversation = vm.Conversation;
            var settings = provider.GetRequiredService<UserSettingsService>();
            var notifier = new FakeNotifier();
            // TKCode 在后台（窗口失焦或隐藏到托盘）
            var presence = new FakePresence { IsForeground = false };
            using var center = new NotificationCenter(notifier, () => NotificationPreferences.From(settings.Current.Notifications), presence,
                post: action => Dispatcher.UIThread.Post(action));
            DesktopNotifications.Attach(center, conversation);

            // 1. 一轮正常结束（minTurnSeconds = 0）：通知正文是回复的最后一句，会话记为未读
            conversation.ComposerText = "hello there";
            conversation.SendCommand.Execute(null);
            await WaitUntil(() => notifier.Shown.Count == 1, "turn completed notification", 30_000);
            var session = conversation.Active!;
            Assert.False(session.IsRunning);
            var completed = notifier.Shown[0];
            Assert.Equal(session.SessionId, completed.SessionId);
            Assert.False(string.IsNullOrWhiteSpace(completed.Title));
            Assert.Equal(NotificationText.LastSentence(SessionViewModel.FinalText(session.Items)), completed.Body);
            Assert.True(center.IsUnread(session.SessionId));
            Assert.Equal(1, notifier.Badge);

            // 2. ask_user：「需要你处理」的通知（同一会话，2 秒内的先压着再补发）
            conversation.ComposerText = Ask;
            conversation.SendCommand.Execute(null);
            await WaitUntil(() => notifier.Shown.Any(n => n.Body == Strings.Get(TKCodeLangResourceKind.NotifyQuestion)), "question notification", 30_000);

            // 3. 用户回到窗口、正看着这个会话：未读清除，通知撤回
            presence.IsForeground = true;
            presence.VisibleSessionId = session.SessionId;
            center.PresenceChanged();
            Assert.Equal(0, center.UnreadCount);
            Assert.Contains(session.SessionId, notifier.Withdrawn);

            // 拒绝回答，结束这一轮
            session.CurrentQuestion!.Decline();
            await WaitUntil(() => !session.IsRunning, "declined turn finished", 30_000);

            // 4. 点击通知：打开会话（先切到新对话，再从通知回来）
            conversation.NewChat();
            await vm.ShowSessionAsync(session.SessionId);
            Assert.Same(session, conversation.Active);
            return true;
        }, CancellationToken.None);
    }

    [Fact]
    public async Task SettingsCard_RendersAndWritesBack()
    {
        var (home, provider) = await StartAsync("tkcode-notify-settings-", null);
        await using var disposeProvider = provider;

        await HeadlessApp.Session.Dispatch(async () =>
        {
            var vm = provider.GetRequiredService<MainWindowViewModel>();
            var window = new MainWindow { DataContext = vm, Width = 1100, Height = 900 };
            window.Show();
            var tested = 0;
            vm.TestNotification = () => tested++;
            await vm.OpenSettingsCommand.ExecuteAsync(null);
            await Settle();
            var card = window.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "NotificationsCard");
            card.BringIntoView();
            await Settle();

            // 默认：两项都是「仅在后台」，提示音开启
            var turn = window.GetVisualDescendants().OfType<TKCode.Arc.SegmentedControl>().Single(c => c.Name == "NotifyTurnModes");
            var input = window.GetVisualDescendants().OfType<TKCode.Arc.SegmentedControl>().Single(c => c.Name == "NotifyInputModes");
            Assert.Equal(1, turn.SelectedIndex);
            Assert.Equal(1, input.SelectedIndex);
            Assert.True(vm.Settings.NotificationSound);
            Assert.Contains("30", vm.Settings.NotifyTurnHint);
            HeadlessApp.Save(window, "settings-notifications");

            // 改档位、关提示音：写回 settings.json，分段控件跟着变
            vm.Settings.ChooseNotifyTurnCommand.Execute(vm.Settings.NotifyTurnModes.Single(c => c.Value == "always"));
            vm.Settings.ChooseNotifyInputCommand.Execute(vm.Settings.NotifyInputModes.Single(c => c.Value == "never"));
            vm.Settings.NotificationSound = false;
            await Settle();
            var text = await File.ReadAllTextAsync(Path.Combine(home, "settings.json"));
            Assert.Contains("\"turnCompleted\": \"always\"", text);
            Assert.Contains("\"needsInput\": \"never\"", text);
            Assert.Contains("\"sound\": false", text);
            Assert.Equal(2, turn.SelectedIndex);
            Assert.Equal(0, input.SelectedIndex);
            HeadlessApp.Save(window, "settings-notifications-changed");

            // 「发送测试通知」交给主窗口接上的通知中心
            vm.Settings.TestNotificationCommand.Execute(null);
            Assert.Equal(1, tested);
            return true;
        }, CancellationToken.None);
    }

    private static Task<(string Home, ServiceProvider Provider)> StartAsync(string prefix, string? settingsJson)
    {
        var home = Directory.CreateTempSubdirectory(prefix + "home-").FullName;
        var project = Directory.CreateTempSubdirectory(prefix + "project-").FullName;
        Environment.SetEnvironmentVariable("TKCODE_HOME", home);
        Environment.SetEnvironmentVariable("TKCODE_FAKE_PROVIDER", "1");
        Environment.SetEnvironmentVariable("TKCODE_EXTERNAL_RUNTIMES", "0");
        Environment.SetEnvironmentVariable("TKCODE_AGENTHOST_DIR", Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "../../../../../src/TKCode.AgentHost/bin/Debug/net11.0")));
        if (settingsJson is not null)
        {
            File.WriteAllText(Path.Combine(home, "settings.json"), settingsJson);
        }
        var state = new StateService(Path.Combine(home, "state.json"));
        state.Current.Workspaces.Add(project);
        state.Current.LastWorkspace = project;
        state.Save();
        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        Program.ConfigureServices(services);
        return Task.FromResult((home, services.BuildServiceProvider()));
    }

    private static async Task WaitUntil(Func<bool> condition, string what, int timeoutMs = 20_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"Timed out waiting for {what}.");
            }
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(50);
        }
        await Settle();
    }

    private static async Task Settle()
    {
        for (var i = 0; i < 8; i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(30);
        }
    }
}
