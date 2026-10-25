using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TKCode.Desktop.Services;
using TKCode.Desktop.ViewModels;
using TKCode.Desktop.Views;
using Xunit;

namespace TKCode.Desktop.Tests;

/// <summary>
/// 侧栏会话列表：分页加载（默认 20 条，底部“展开更多”每次再加 20 条）、虚拟化（只创建可见行），
/// 以及右键菜单的重命名 / 置顶 / 删除。会话文件直接写到临时数据目录。
/// </summary>
public sealed class SessionSidebarTests
{
    [Fact]
    public async Task ManySessions_ArePagedAndVirtualized_AndCanBeRenamedPinnedDeleted()
    {
        var home = Directory.CreateTempSubdirectory("tkcode-ui-home-").FullName;
        var project = Directory.CreateTempSubdirectory("tkcode-ui-project-").FullName;
        var dir = Path.Combine(home, "sessions", "--project--");
        Directory.CreateDirectory(dir);
        var start = DateTimeOffset.UtcNow.AddHours(-5);
        for (var i = 0; i < 120; i++)
        {
            var id = $"{i:D32}";
            var file = Path.Combine(dir, $"20261016T0000{i:D3}_{id}.jsonl");
            File.WriteAllText(file,
                $$"""{"type":"session","sessionId":"{{id}}","cwd":"{{project}}","version":1}""" + "\n" +
                $$$"""{"type":"message","id":"m{{{i}}}","message":{"role":"user","content":[{"type":"text","text":"会话 {{{i}}}"}]}}""" + "\n");
            File.SetLastWriteTimeUtc(file, start.AddMinutes(i).UtcDateTime);
        }
        Environment.SetEnvironmentVariable("TKCODE_HOME", home);
        Environment.SetEnvironmentVariable("TKCODE_AGENTHOST_DIR", Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "../../../../../src/TKCode.AgentHost/bin/Debug/net11.0")));
        Environment.SetEnvironmentVariable("TKCODE_FAKE_PROVIDER", "1");
        Environment.SetEnvironmentVariable("TKCODE_EXTERNAL_RUNTIMES", "0");
        var settings = new StateService(Path.Combine(home, "state.json"));
        settings.Current.Workspaces.Add(project);
        settings.Save();

        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        Program.ConfigureServices(services);
        await using var provider = services.BuildServiceProvider();

        await HeadlessApp.Session.Dispatch(async () =>
        {
            var vm = provider.GetRequiredService<MainWindowViewModel>();
            var window = new MainWindow { DataContext = vm, Width = 1100, Height = 720 };
            window.Show();
            var conversation = vm.Conversation;
            await WaitUntil(() => vm.Sidebar.VisibleSessions.Count >= 20, "first page");

            // 分页：默认 20 条，多返回一条说明还有下一页；最新的在前
            Assert.Equal(20, conversation.Sessions.Count);
            Assert.True(conversation.HasMoreSessions);
            Assert.Equal("会话 119", conversation.Sessions[0].DisplayTitle);
            // 虚拟化：只为可见区域创建行
            var realized = window.GetVisualDescendants().OfType<Button>().Count(b => b.DataContext is SessionItemViewModel);
            Assert.InRange(realized, 1, 40);
            HeadlessApp.Save(window, "sidebar-paged");

            // “展开更多”：每次多显示 20 条（20 → 40 → 60），按钮仍显示
            var loadMore = window.FindControl<Button>("SessionLoadMore")!;
            Assert.True(loadMore.IsVisible);
            await conversation.LoadMoreSessionsCommand.ExecuteAsync(null);
            await WaitUntil(() => conversation.Sessions.Count == 40, "second page");
            Assert.True(loadMore.IsVisible);
            await conversation.LoadMoreSessionsCommand.ExecuteAsync(null);
            await WaitUntil(() => conversation.Sessions.Count == 60, "third page");
            Assert.True(conversation.HasMoreSessions);
            HeadlessApp.Save(window, "sidebar-load-more");

            // 一直展开到最后一页：按钮隐藏
            while (conversation.HasMoreSessions)
            {
                await conversation.LoadMoreSessionsCommand.ExecuteAsync(null);
                await Settle();
            }
            Assert.Equal(120, conversation.Sessions.Count);
            Assert.False(loadMore.IsVisible);

            var viewer = window.FindControl<ItemsControl>("SessionList")!.GetVisualDescendants().OfType<ScrollViewer>().First();
            viewer.Offset = new Avalonia.Vector(0, 0);

            // 重命名（行内编辑）
            var target = conversation.Sessions.Single(s => s.DisplayTitle == "会话 100");
            target.BeginRenameCommand.Execute(null);
            Assert.True(target.IsRenaming);
            target.EditTitle = "重命名后的会话";
            await target.CommitRenameCommand.ExecuteAsync(null);
            Assert.False(target.IsRenaming);
            Assert.Equal("重命名后的会话", target.DisplayTitle);

            // 置顶：排到最前，刷新后仍在最前且保留新标题
            await target.TogglePinCommand.ExecuteAsync(null);
            Assert.Same(target, conversation.Sessions[0]);
            viewer.Offset = new Avalonia.Vector(0, 0);
            await conversation.RefreshSessionsAsync();
            await Settle();
            Assert.Equal("重命名后的会话", conversation.Sessions[0].DisplayTitle);
            Assert.True(conversation.Sessions[0].IsPinned);
            HeadlessApp.Save(window, "sidebar-pinned");

            // 右键菜单与行内重命名的外观
            var menu = window.GetVisualDescendants().OfType<TKCode.Arc.ContextMenu>().First(m => m.DataContext == conversation.Sessions[1]);
            menu.Open(null, TKCode.Arc.MenuOpenFocus.Surface);
            await Settle();
            Assert.True(menu.IsOpen);
            var entries = menu.Items.OfType<TKCode.Arc.MenuEntry>().ToList();
            Assert.Equal(6, entries.Count);
            Assert.Same(conversation.Sessions[1].TogglePinCommand, entries.Single(e => Equals(e.Header, conversation.Sessions[1].PinMenuText)).Command);
            Assert.Same(conversation.Sessions[1].DeleteCommand, entries[^1].Command);
            HeadlessApp.Save(window, "sidebar-context-menu");
            menu.Close();
            conversation.Sessions[1].BeginRenameCommand.Execute(null);
            await Settle();
            HeadlessApp.Save(window, "sidebar-renaming");
            conversation.Sessions[1].CancelRenameCommand.Execute(null);
            Assert.Equal("会话 119", conversation.Sessions[1].DisplayTitle);

            // 删除：确认后永久删除文件；取消则不删
            var victim = conversation.Sessions.Single(s => s.DisplayTitle == "会话 119");
            conversation.ConfirmDangerAsync = (_, _) => Task.FromResult(false);
            await victim.DeleteCommand.ExecuteAsync(null);
            Assert.True(File.Exists(victim.FilePath));
            conversation.ConfirmDangerAsync = (_, text) => Task.FromResult(text.Contains("会话 119"));
            await victim.DeleteCommand.ExecuteAsync(null);
            Assert.False(File.Exists(victim.FilePath));
            Assert.DoesNotContain(victim, conversation.Sessions);

            // 复制会话 ID
            string? copied = null;
            conversation.SetClipboardAsync = text =>
            {
                copied = text;
                return Task.CompletedTask;
            };
            await target.CopyIdCommand.ExecuteAsync(null);
            Assert.Equal(target.SessionId, copied);
            window.Close();
            return true;
        }, TestContext.Current.CancellationToken);
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
        for (var i = 0; i < 6; i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(30);
        }
    }
}
