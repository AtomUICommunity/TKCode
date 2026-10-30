using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TKCode.Arc;
using TKCode.Arc.Motion;
using TKCode.Arc.Primitives;
using TKCode.Config;
using TKCode.Desktop.Controls;
using TKCode.Desktop.Services;
using TKCode.Desktop.ViewModels;
using TKCode.Desktop.Views;
using Xunit;

namespace TKCode.Desktop.Tests;

/// <summary>
/// 侧栏项目列表与创建项目弹窗（docs/design/projects.md §8、§9）：项目常驻显示，点击项目筛选会话，
/// 行内操作在悬停时出现；创建 / 编辑项目是跟随界面缩放的 Arc 弹窗。
/// </summary>
public sealed class ProjectSidebarTests
{
    [Fact]
    public async Task Sidebar_ListsProjects_FiltersSessions_AndShowsRowActions()
    {
        var home = Directory.CreateTempSubdirectory("tkcode-ui-home-").FullName;
        var root = Directory.CreateTempSubdirectory("tkcode-ui-projects-").FullName;
        string Make(string name) => Directory.CreateDirectory(Path.Combine(root, name)).FullName;
        var alpha = Make("routin-fax");
        var cowork = Make("CoWork");
        var coworkLib = Make("cowork-lib");
        var gone = Path.Combine(root, "DataFoundry");
        var next = Make("NextCoWork");
        var coworkProject = Project.Create("CoWork", [cowork, coworkLib]);
        Environment.SetEnvironmentVariable("TKCODE_HOME", home);
        ProjectsFile.Save([
            Project.Create(null, [alpha]),
            coworkProject,
            Project.Create(null, [next]) with { Pinned = true },
            Project.Create(null, [gone]),
        ]);
        // 旧会话（没有 projectId，按 cwd 归属）、记录了项目的会话、不在项目中的会话
        var sessions = Path.Combine(home, "sessions", "--x--");
        Directory.CreateDirectory(sessions);
        var now = DateTimeOffset.UtcNow;
        var n = 0;
        void Session(string cwd, string? projectId, string title)
        {
            var id = $"{++n:D32}";
            var file = Path.Combine(sessions, $"20261022T0000{n:D2}_{id}.jsonl");
            var project = projectId is null ? "" : $",\"projectId\":\"{projectId}\"";
            File.WriteAllText(file,
                $$"""{"type":"session","sessionId":"{{id}}","cwd":"{{cwd}}","version":1""" + project + "}\n" +
                $$$"""{"type":"message","id":"m{{{n}}}","message":{"role":"user","content":[{"type":"text","text":"{{{title}}}"}]}}""" + "\n");
            File.SetLastWriteTimeUtc(file, now.AddMinutes(-n).UtcDateTime);
        }
        Session(alpha, null, "修复登录超时");
        Session(alpha, null, "重构 HttpClient");
        Session(cowork, coworkProject.Id, "梳理多仓库构建");
        Session(coworkLib, coworkProject.Id, "共享库升级");
        Session(home, null, "随便聊聊");
        Environment.SetEnvironmentVariable("TKCODE_AGENTHOST_DIR", Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "../../../../../src/TKCode.AgentHost/bin/Debug/net11.0")));
        Environment.SetEnvironmentVariable("TKCODE_FAKE_PROVIDER", "1");
        Environment.SetEnvironmentVariable("TKCODE_EXTERNAL_RUNTIMES", "0");

        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        Program.ConfigureServices(services);
        await using var provider = services.BuildServiceProvider();

        await HeadlessApp.Session.Dispatch(async () =>
        {
            var vm = provider.GetRequiredService<MainWindowViewModel>();
            var window = new MainWindow { DataContext = vm, Width = 1100, Height = 720 };
            ArcMotion.SetLevel(window, ArcMotionLevel.Off);
            window.Show();
            var sidebar = vm.Sidebar;
            await WaitUntil(() => sidebar.VisibleSessions.Count == 5, "all sessions");

            // 置顶的在前，其余保持项目表顺序；主文件夹不存在的项目照常显示（带警告）
            Assert.Equal(["NextCoWork", "routin-fax", "CoWork", "DataFoundry"], sidebar.Projects.Select(p => p.Name));
            Assert.True(sidebar.Projects.Single(p => p.Name == "DataFoundry").IsMissing);
            Assert.Equal("会话", sidebar.SessionsTitle);
            Assert.False(sidebar.IsFiltered);
            var badges = sidebar.VisibleSessions.ToDictionary(s => s.DisplayTitle, s => s.HasProject ? s.ProjectName : null);
            Assert.Equal("routin-fax", badges["修复登录超时"]);
            Assert.Equal("CoWork", badges["共享库升级"]);
            Assert.Null(badges["随便聊聊"]);
            HeadlessApp.Save(window, "projects-sidebar");

            // 选中项目：只显示它的会话（旧会话按 cwd 归属；记录了项目的会话即使 cwd 是其他文件夹也归它）
            var cowork = sidebar.Projects.Single(p => p.Name == "CoWork");
            sidebar.SelectProjectCommand.Execute(cowork);
            await WaitUntil(() => sidebar.VisibleSessions.Count == 2, "CoWork sessions");
            Assert.Equal(["梳理多仓库构建", "共享库升级"], sidebar.VisibleSessions.Select(s => s.DisplayTitle));
            Assert.Equal("会话 · CoWork", sidebar.SessionsTitle);
            Assert.True(cowork.IsSelected);
            Assert.Equal(coworkProject.Id, vm.Conversation.Workspace?.Id);
            Assert.True(sidebar.IsFiltered);

            // 悬停在项目行上：显示「…」和「新建会话」
            var row = window.GetVisualDescendants().OfType<Grid>().Single(g => g.Classes.Contains("projectRow") && g.DataContext == cowork);
            var actions = row.GetVisualDescendants().OfType<StackPanel>().Single(p => p.Classes.Contains("rowActions"));
            Assert.Equal(0, actions.Opacity);
            var center = row.TranslatePoint(new Point(row.Bounds.Width / 2, row.Bounds.Height / 2), window)!.Value;
            window.MouseMove(center);
            Pump(window);
            Assert.Equal(1, actions.Opacity);
            HeadlessApp.Save(window, "projects-sidebar-filtered-hover");

            // 「…」菜单：编辑 / 在 Finder 中显示 / 复制路径 / 置顶 / 移除
            var menu = row.GetVisualDescendants().OfType<DropdownMenu>().Single();
            menu.Open(focusFirst: false);
            Pump(window);
            Assert.True(menu.IsOpen);
            HeadlessApp.Save(window, "projects-row-menu");
            menu.Close();
            Pump(window);

            // 右键菜单：同样带置顶项，绑定到侧栏的 TogglePinCommand
            var contextMenu = row.GetVisualDescendants().OfType<TKCode.Arc.ContextMenu>().Single();
            contextMenu.Open(null, MenuOpenFocus.Surface);
            Pump(window);
            Assert.True(contextMenu.IsOpen);
            var pinEntry = contextMenu.Items.OfType<MenuEntry>().Single(e => Equals(e.Header, cowork.PinText));
            Assert.Same(sidebar.TogglePinCommand, pinEntry.Command);
            Assert.Same(cowork, pinEntry.CommandParameter);
            HeadlessApp.Save(window, "projects-row-context-menu");
            contextMenu.Close();
            Pump(window);

            // 再次点击选中的项目：回到全部会话
            sidebar.SelectProjectCommand.Execute(cowork);
            await WaitUntil(() => sidebar.VisibleSessions.Count == 5, "all sessions again");
            Assert.False(sidebar.IsFiltered);

            // 旧会话也能按项目筛选
            sidebar.SelectProjectCommand.Execute(sidebar.Projects.Single(p => p.Name == "routin-fax"));
            await WaitUntil(() => sidebar.VisibleSessions.Count == 2, "legacy sessions");

            // 置顶：排到最前；移除：会话保留，变为不属于任何项目
            sidebar.TogglePinCommand.Execute(cowork);
            Assert.Equal("CoWork", sidebar.Projects[0].Name);
            sidebar.RemoveProject(coworkProject);
            Assert.DoesNotContain(sidebar.Projects, p => p.Name == "CoWork");
            sidebar.ClearFilter();
            await WaitUntil(() => sidebar.VisibleSessions.Count == 5, "sessions after removing a project");
            Assert.False(sidebar.VisibleSessions.Single(s => s.DisplayTitle == "共享库升级").HasProject);
            window.Close();
            return true;
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task CreateProjectDialog_AddsFolders_ChoosesPrimary_AndWarnsAboutNesting()
    {
        var root = Directory.CreateTempSubdirectory("tkcode-ui-dialog-").FullName;
        var oneAi = Directory.CreateDirectory(Path.Combine(root, "one-ai")).FullName;
        var cowork = Directory.CreateDirectory(Path.Combine(root, "CoWork")).FullName;
        var nested = Directory.CreateDirectory(Path.Combine(oneAi, "packages")).FullName;

        await HeadlessApp.Session.Dispatch(async () =>
        {
            var previousTheme = TestTheme.CurrentId;
            foreach (var light in new[] { false, true })
            {
                await TestTheme.ApplyAsync(light ? TestTheme.LightId : TestTheme.DarkId);
                var owner = new Avalonia.Controls.Window
                {
                    Width = 1100,
                    Height = 720,
                    RequestedThemeVariant = light ? Avalonia.Styling.ThemeVariant.Light : Avalonia.Styling.ThemeVariant.Dark,
                };
                ArcMotion.SetLevel(owner, ArcMotionLevel.Off);
                owner.Show();
                Pump(owner);
                ProjectDialogViewModel? submitted = null;
                var completion = ProjectDialog.ShowAsync(owner, null, dialog =>
                {
                    submitted = dialog;
                    return Task.CompletedTask;
                });
                Pump(owner);
                var presence = Assert.Single(ModalPresence.GetModals(owner));
                var dialog = Assert.IsType<ProjectDialog>(Assert.IsType<ZoomHost>(presence.Panel).Child);
                var model = dialog.ViewModel;
                Assert.False(model.CanSubmit);
                Assert.Equal("新项目", model.NamePlaceholder);
                HeadlessApp.Save(owner, light ? "project-dialog-empty-light" : "project-dialog-empty");

                // 第一个加入的是主文件夹；重复的不加入
                model.AddFolders([oneAi, cowork, oneAi + Path.DirectorySeparatorChar]);
                Assert.Equal([oneAi, cowork], model.ResultFolders);
                Assert.True(model.Folders[0].IsPrimary);
                Assert.True(model.CanSubmit);
                Assert.Null(model.Warning);
                Pump(owner);
                HeadlessApp.Save(owner, light ? "project-dialog-light" : "project-dialog");

                // 设为主要；嵌套的文件夹给出警告但仍可提交
                model.MakePrimaryCommand.Execute(model.Folders[1]);
                Assert.Equal([cowork, oneAi], model.ResultFolders);
                model.AddFolders([nested]);
                Assert.Contains("packages 位于 one-ai 之内", model.Warning);
                Assert.True(model.CanSubmit);
                Pump(owner);
                HeadlessApp.Save(owner, light ? "project-dialog-nested-light" : "project-dialog-nested");

                model.RemoveCommand.Execute(model.Folders[2]);
                model.Name = "  多仓库  ";
                var submit = Assert.IsType<Arc.Button>(dialog.FindControl<Arc.Button>("SubmitButton"));
                submit.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
                await WaitUntil(() => completion.IsCompleted);
                Assert.Equal(ProjectDialogOutcome.Saved, await completion);
                Assert.Equal("多仓库", submitted!.ResultName);
                Assert.Equal([cowork, oneAi], submitted.ResultFolders);
                Assert.Empty(ModalPresence.GetModals(owner));
                owner.Close();
            }
            await TestTheme.ApplyAsync(previousTheme);
            return true;
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task EditProjectDialog_KeepsInputOnFailure_AndNotesPrimaryChange()
    {
        var root = Directory.CreateTempSubdirectory("tkcode-ui-dialog-").FullName;
        var a = Directory.CreateDirectory(Path.Combine(root, "app")).FullName;
        var b = Directory.CreateDirectory(Path.Combine(root, "lib")).FullName;
        var project = Project.Create("App", [a, b]);

        await HeadlessApp.Session.Dispatch(async () =>
        {
            var owner = new Avalonia.Controls.Window { Width = 1100, Height = 720 };
            ArcMotion.SetLevel(owner, ArcMotionLevel.Off);
            owner.Show();
            Pump(owner);
            var attempts = 0;
            var completion = ProjectDialog.ShowAsync(owner, project, _ =>
            {
                attempts++;
                return attempts == 1 ? Task.FromException(new IOException("disk full")) : Task.CompletedTask;
            });
            Pump(owner);
            var dialog = Assert.IsType<ProjectDialog>(Assert.IsType<ZoomHost>(Assert.Single(ModalPresence.GetModals(owner)).Panel).Child);
            var model = dialog.ViewModel;
            Assert.True(model.IsEdit);
            Assert.Equal("编辑项目", model.Title);
            Assert.Equal("App", model.Name);
            Assert.Equal([a, b], model.ResultFolders);

            model.MakePrimaryCommand.Execute(model.Folders[1]);
            Assert.Contains("新会话从新的主文件夹开始", model.Warning);

            var submit = Assert.IsType<Arc.Button>(dialog.FindControl<Arc.Button>("SubmitButton"));
            submit.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
            await WaitUntil(() => model.Error is not null);
            Assert.Contains("disk full", model.Error);
            Assert.False(completion.IsCompleted);
            Assert.Equal([b, a], model.ResultFolders);
            Pump(owner);
            HeadlessApp.Save(owner, "project-dialog-edit-error");

            submit.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
            await WaitUntil(() => completion.IsCompleted);
            Assert.Equal(ProjectDialogOutcome.Saved, await completion);
            owner.Close();
            return true;
        }, TestContext.Current.CancellationToken);
    }

    private static void Pump(TopLevel topLevel)
    {
        for (var i = 0; i < 4; i++)
        {
            Dispatcher.UIThread.RunJobs();
            topLevel.UpdateLayout();
        }
    }

    private static async Task WaitUntil(Func<bool> condition, string what = "condition", int timeoutMs = 20_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"Timed out waiting for {what}.");
            }
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(30);
        }
        for (var i = 0; i < 4; i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(20);
        }
    }
}
