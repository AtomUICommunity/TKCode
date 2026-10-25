using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TKCode.Desktop.Services;
using TKCode.Desktop.ViewModels;
using Xunit;

namespace TKCode.Desktop.Tests;

/// <summary>
/// 托盘 / macOS Dock / Windows 任务栏跳转列表：命令行指令、单实例转发、最近项目与菜单内容、从菜单进入项目。
/// 原生菜单本身无法在无头平台渲染，这里测它们的数据与点击后的行为；跳转列表的 COM 调用只能在 Windows 上验证。
/// </summary>
public sealed class SystemMenuTests
{
    [Fact]
    public void AppCommand_Parses_And_Quotes_Arguments()
    {
        Assert.Equal(AppCommandKind.Show, AppCommand.Parse([]).Kind);
        Assert.Equal(AppCommandKind.Show, AppCommand.Parse(["--something-else"]).Kind);
        Assert.Equal(AppCommandKind.NewSession, AppCommand.Parse(["--new-session"]).Kind);
        Assert.Equal(AppCommandKind.Quit, AppCommand.Parse(["--quit"]).Kind);
        var open = AppCommand.Parse(["--open-project", "/work/my project"]);
        Assert.Equal(AppCommandKind.OpenProject, open.Kind);
        Assert.Equal("/work/my project", open.Argument);
        // 缺少路径时不是有效指令
        Assert.Equal(AppCommandKind.Show, AppCommand.Parse(["--open-project"]).Kind);
        Assert.Equal(AppCommandKind.Show, AppCommand.Parse(["--open-project", ""]).Kind);

        Assert.Equal("--quit", AppCommand.Quit.ToCommandLine());
        Assert.Equal("--open-project C:\\code\\tkcode", AppCommand.OpenProject("C:\\code\\tkcode").ToCommandLine());
        Assert.Equal("--open-project \"C:\\my code\\tk code\"", AppCommand.OpenProject("C:\\my code\\tk code").ToCommandLine());
        // 末尾的反斜杠紧挨着结束引号，必须加倍
        Assert.Equal("--open-project \"D:\\my code\\\\\"", AppCommand.OpenProject("D:\\my code\\").ToCommandLine());
        Assert.Equal(["--open-project", "/a b"], AppCommand.OpenProject("/a b").ToArguments());
        Assert.Empty(AppCommand.Show.ToArguments());
    }

    [Fact]
    public void SingleInstance_Forwards_Commands_To_The_Primary_And_Hands_Over_On_Release()
    {
        var home = Directory.CreateTempSubdirectory("tkcode-single-instance-").FullName;
        Assert.True(SingleInstance.TryClaim(home, AppCommand.Show, out var primary));
        Assert.NotNull(primary);
        var received = new System.Collections.Concurrent.BlockingCollection<AppCommand>();
        primary.CommandReceived += received.Add;

        // 第二个进程：转发后返回 false（调用方退出）
        foreach (var command in new[] { AppCommand.OpenProject("/x/my project"), AppCommand.NewSession, AppCommand.Quit, AppCommand.Show })
        {
            Assert.False(SingleInstance.TryClaim(home, command, out var second));
            Assert.Null(second);
            Assert.True(received.TryTake(out var got, TimeSpan.FromSeconds(10)), $"{command.Kind} was not delivered");
            Assert.Equal(command, got);
        }

        // 主实例放弃（应用内更新启动新版本之前）：下一个进程成为主实例，而不是被转发
        SingleInstance.Release();
        Assert.True(SingleInstance.TryClaim(home, AppCommand.Show, out var next));
        Assert.NotNull(next);
        Assert.Empty(received);
        next.Dispose();
        primary.Dispose();

        // 不同的数据目录互不干扰
        var other = Directory.CreateTempSubdirectory("tkcode-single-instance-").FullName;
        Assert.True(SingleInstance.TryClaim(home, AppCommand.Show, out var a));
        Assert.True(SingleInstance.TryClaim(other, AppCommand.Show, out var b));
        a!.Dispose();
        b!.Dispose();
    }

    [Fact]
    public void RecentWorkspaces_AreMostRecentFirst_AndPaddedWithSidebarOrder()
    {
        var home = Directory.CreateTempSubdirectory("tkcode-recent-").FullName;
        var dirs = Enumerable.Range(0, 10).Select(i => Directory.CreateDirectory(Path.Combine(home, $"p{i}")).FullName).ToList();
        var state = new StateService(Path.Combine(home, "state.json"));
        // 旧版 state.json 的项目路径迁移成单文件夹项目，保持侧栏顺序
        state.Current.Workspaces.AddRange(dirs);
        var projects = new ProjectService(state, Path.Combine(home, "projects.json"));
        string Id(string dir) => projects.FindByPath(dir)!.Id;

        // 没用过任何项目：按侧栏顺序，最多 8 个
        Assert.Equal(dirs.Take(8), projects.Recent(8).Select(p => p.Primary));

        state.Current.TouchRecentProject(Id(dirs[5]));
        state.Current.TouchRecentProject(Id(dirs[9]));
        state.Current.TouchRecentProject(Id(dirs[5]));
        // 最近使用的在前、不重复，其余按侧栏顺序补齐
        Assert.Equal([dirs[5], dirs[9], dirs[0], dirs[1], dirs[2], dirs[3], dirs[4], dirs[6]], projects.Recent(8).Select(p => p.Primary));

        // 主文件夹已经不存在的项目不会出现在菜单里
        Directory.Delete(dirs[5]);
        Assert.DoesNotContain(dirs[5], projects.Recent(8).Select(p => p.Primary));
        Assert.Equal(dirs[9], projects.Recent(8)[0].Primary);
    }

    [Fact]
    public async Task Menus_Are_Built_From_Recent_Projects_And_Entries_Open_The_Project()
    {
        var home = Directory.CreateTempSubdirectory("tkcode-menus-").FullName;
        var projects = Directory.CreateDirectory(Path.Combine(home, "projects")).FullName;
        string Make(params string[] parts) => Directory.CreateDirectory(Path.Combine([projects, .. parts])).FullName;
        var alpha = Make("alpha");
        var beta = Make("beta");
        var appOne = Make("one", "app");
        var appTwo = Make("two", "app");
        var elsewhere = Make("elsewhere");
        Environment.SetEnvironmentVariable("TKCODE_HOME", home);
        Environment.SetEnvironmentVariable("TKCODE_FAKE_PROVIDER", "1");
        Environment.SetEnvironmentVariable("TKCODE_EXTERNAL_RUNTIMES", "0");
        Environment.SetEnvironmentVariable("TKCODE_AGENTHOST_DIR", Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "../../../../../src/TKCode.AgentHost/bin/Debug/net11.0")));
        var state = new StateService(Path.Combine(home, "state.json"));
        state.Current.Workspaces.AddRange([alpha, beta, appOne, appTwo]);
        state.Save();

        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        Program.ConfigureServices(services);
        await using var provider = services.BuildServiceProvider();

        await HeadlessApp.Session.Dispatch(async () =>
        {
            var vm = provider.GetRequiredService<MainWindowViewModel>();
            var workspaces = provider.GetRequiredService<ProjectService>();
            var updates = provider.GetRequiredService<UpdatesViewModel>();

            // 启动时选中的项目（侧栏第一个）已算作最近使用
            Assert.Equal(alpha, workspaces.Recent(8)[0].Primary);

            // 从菜单进入项目 = 在侧栏项目选择器里选中它：筛选会话 + 新会话默认在该项目中，并离开设置页
            await vm.OpenSettingsCommand.ExecuteAsync(null);
            Assert.True(vm.IsSettingsOpen);
            vm.OpenProject(beta);
            Assert.False(vm.IsSettingsOpen);
            Assert.Equal(beta, vm.Sidebar.Filter?.Primary);
            Assert.Equal(beta, vm.Conversation.Workspace?.Primary);
            Assert.Equal([beta, alpha], workspaces.Recent(2).Select(w => w.Primary));

            // 不在列表里的目录先加入再进入；不存在的目录忽略
            vm.OpenProject(elsewhere);
            Assert.Equal(elsewhere, vm.Sidebar.Filter?.Primary);
            Assert.Contains(vm.Sidebar.Projects, p => p.Primary == elsewhere);
            vm.OpenProject(Path.Combine(home, "does-not-exist"));
            Assert.Equal(elsewhere, vm.Sidebar.Filter?.Primary);
            Assert.DoesNotContain(vm.Sidebar.Projects, p => p.Primary.EndsWith("does-not-exist"));

            // 新会话：离开设置页，在当前项目中开始
            await vm.OpenSettingsCommand.ExecuteAsync(null);
            vm.StartNewSession();
            Assert.False(vm.IsSettingsOpen);

            var recent = workspaces.Recent(AppTray.ProjectLimit);
            Assert.Equal(5, recent.Count);
            Assert.Equal(elsewhere, recent[0].Primary);

            // 托盘：显示 / 新会话 / 项目▸ / —— / 更新 / 设置 / —— / 退出
            var tray = AppTray.BuildItems(updates, recent);
            Assert.Equal([TrayAction.Show, TrayAction.NewSession, TrayAction.None, TrayAction.None],
                tray.Take(4).Select(i => i.Action));
            Assert.Null(tray[3].Text);
            Assert.Equal(TrayAction.Quit, tray[^1].Action);
            Assert.Null(tray[^2].Text);
            Assert.Contains(tray, i => i.Action == TrayAction.Settings);
            Assert.Contains(tray, i => i.Action == TrayAction.CheckUpdates);
            var submenu = Assert.IsAssignableFrom<IReadOnlyList<TrayItem>>(tray[2].Children);
            Assert.Equal(recent.Select(p => p.Primary), submenu.Select(i => i.Argument));
            Assert.All(submenu, i => Assert.Equal(TrayAction.OpenProject, i.Action));
            // 同名项目附上上级目录名
            Assert.Contains(submenu, i => i.Argument == appOne && i.Text!.EndsWith("(one)"));
            Assert.Contains(submenu, i => i.Argument == appTwo && i.Text!.EndsWith("(two)"));
            Assert.Contains(submenu, i => i.Argument == alpha && !i.Text!.Contains('('));
            // 没有项目时子菜单里只有一条灰色占位
            var empty = Assert.IsAssignableFrom<IReadOnlyList<TrayItem>>(AppTray.BuildItems(updates, [])[2].Children);
            Assert.Equal(TrayAction.None, Assert.Single(empty).Action);

            // Dock：新会话 + 平铺的项目；没有项目时只有新会话
            var dock = AppTray.BuildDockItems(recent);
            Assert.Equal(TrayAction.NewSession, dock[0].Action);
            Assert.Null(dock[1].Text);
            Assert.Equal(recent.Select(p => p.Primary), dock.Skip(3).Select(i => i.Argument));
            Assert.Single(AppTray.BuildDockItems([]));

            // 跳转列表：任务是「新会话」「退出」，项目类别的条目参数能被主实例解析回来
            var (tasks, entries) = AppTray.BuildJumpList(recent);
            Assert.Equal([AppCommand.NewSession.ToCommandLine(), AppCommand.Quit.ToCommandLine()], tasks.Select(t => t.Arguments));
            Assert.Equal(recent.Select(p => p.Name), entries.Select(e => e.Title));
            Assert.Equal(recent.Select(p => p.Primary), entries.Select(e => e.Description));
            Assert.Equal(AppCommand.OpenProject(beta), AppCommand.Parse(["--open-project", beta]));
            return true;
        }, CancellationToken.None);
    }
}
