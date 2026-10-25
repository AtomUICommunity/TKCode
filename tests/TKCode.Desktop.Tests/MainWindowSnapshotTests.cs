using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
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
/// 端到端界面测试：启动真实的 Agent Host（Fake 模型），在无头窗口中完成一轮带工具调用的对话并截图。
/// 数据目录通过 TKCODE_HOME 指向临时目录，不读写用户的 ~/.tkcode。
/// </summary>
public sealed class MainWindowSnapshotTests
{
    [Fact]
    public async Task EmptyState_ThenConversation_RendersAndListsSession()
    {
        var home = Directory.CreateTempSubdirectory("tkcode-ui-home-").FullName;
        var project = Directory.CreateTempSubdirectory("tkcode-ui-project-").FullName;
        File.WriteAllText(Path.Combine(project, "README.md"), "# demo\n\nA **sample** project with `code`.\n\n- one\n- two\n");
        Directory.CreateDirectory(Path.Combine(project, "src"));
        File.WriteAllText(Path.Combine(project, "src", "App.cs"), "namespace Demo;\n\npublic static class App\n{\n    public static int Main() => 0;\n}\n");
        File.WriteAllText(Path.Combine(project, ".env.example"), "KEY=value\n");
        // 准备一个有两个分支的 Git 仓库，用于验证切换分支
        Git(project, "init", "-q", "-b", "main");
        Git(project, "-c", "user.email=t@example.com", "-c", "user.name=t", "commit", "-q", "--allow-empty", "-m", "init");
        Git(project, "branch", "feature");
        Environment.SetEnvironmentVariable("TKCODE_HOME", home);
        Environment.SetEnvironmentVariable("TKCODE_AGENTHOST_DIR", Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "../../../../../src/TKCode.AgentHost/bin/Debug/net11.0")));
        // 预置一个项目，避免首次启动时扫描 ~/code
        Environment.SetEnvironmentVariable("TKCODE_FAKE_PROVIDER", "1");
        Environment.SetEnvironmentVariable("TKCODE_EXTERNAL_RUNTIMES", "0"); // 不探测本机的 claude / codex CLI
        var settings = new StateService(Path.Combine(home, "state.json"));
        settings.Current.Workspaces.Add(project);
        settings.Save();

        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        Program.ConfigureServices(services);
        await using var provider = services.BuildServiceProvider();

        // 返回值让 Dispatch 选中 Func<Task<T>> 重载，等待异步流程完成（否则 async lambda 会被当作 Action）。
        await HeadlessApp.Session.Dispatch(async () =>
        {
            var vm = provider.GetRequiredService<MainWindowViewModel>();
            var window = new MainWindow { DataContext = vm, Width = 1100, Height = 720 };
            window.Show();
            window.UpdateLayout();
            // Host 尚在加载时，主界面和歇后语已经可见，无首屏遮罩或最短展示时间。
            var welcome = window.FindControl<TextBlock>("WelcomeQuip")!;
            Assert.True(welcome.IsEffectivelyVisible);
            Assert.False(string.IsNullOrWhiteSpace(welcome.Text));
            Assert.Equal(1, window.FindControl<Grid>("AppPage")!.Opacity);
            Assert.True(window.FindControl<ComposerView>("EmptyComposer")!.IsEffectivelyVisible);
            await WaitUntil(() => vm.Conversation.Picker.Selected is not null, "models loaded");
            HeadlessApp.Save(window, "main-empty");

            vm.Conversation.ComposerText = "看看目录里有哪些文件";
            vm.Conversation.SendCommand.Execute(null);
            await WaitUntil(() => vm.Conversation.Active is { IsRunning: false, Items.Count: >= 2 }, "conversation finished");
            await WaitUntil(() => vm.Sidebar.VisibleSessions.Count == 1, "session listed");
            Assert.Equal("看看目录里有哪些文件", vm.Sidebar.VisibleSessions[0].DisplayTitle);
            HeadlessApp.Save(window, "main-conversation");

            // 底部输入框：内容放不下一行时切到上下两段布局，变短后回到单行胶囊
            var composer = window.GetVisualDescendants().OfType<ComposerView>().Single(c => c.Classes.Contains("compact"));
            vm.Conversation.ComposerText = string.Concat(Enumerable.Repeat("您好", 160));
            await Settle();
            Assert.Contains("multiline", composer.Classes);
            HeadlessApp.Save(window, "composer-multiline");
            vm.Conversation.ComposerText = "第一行\n第二行";
            await Settle();
            Assert.Contains("multiline", composer.Classes);
            vm.Conversation.ComposerText = "短消息";
            await Settle();
            Assert.DoesNotContain("multiline", composer.Classes);
            vm.Conversation.ComposerText = "";
            await Settle();

            // 展开思考过程
            var assistant = vm.Conversation.Active!.Items.OfType<AssistantMessageItemViewModel>().First();
            assistant.Blocks.OfType<ThinkingBlockViewModel>().FirstOrDefault()?.ToggleCommand.Execute(null);
            await Settle();
            HeadlessApp.Save(window, "main-thinking");

            // 文件树：默认隐藏 . 开头的文件，目录在前
            vm.ToggleExplorerCommand.Execute(null);
            await Settle();
            await WaitUntil(() => vm.Explorer.RootNodes.Count == 2, "explorer roots loaded");
            Assert.Equal(["src", "README.md"], vm.Explorer.RootNodes.Select(n => n.Name));
            vm.Explorer.ToggleHiddenCommand.Execute(null);
            await WaitUntil(() => vm.Explorer.RootNodes.Any(n => n.Name == ".env.example"), "hidden explorer roots loaded");
            Assert.Contains(vm.Explorer.RootNodes, n => n.Name == ".env.example");
            vm.Explorer.ToggleHiddenCommand.Execute(null);
            await WaitUntil(() => vm.Explorer.RootNodes.Count == 2, "filtered explorer roots loaded");
            HeadlessApp.Save(window, "explorer");

            // 打开 Markdown（默认预览）与代码文件
            await vm.Explorer.ActivateCommand.ExecuteAsync(vm.Explorer.RootNodes.Single(n => n.Name == "README.md"));
            await Settle();
            Assert.True(vm.IsWorkbenchOpen);
            await WaitUntil(() => vm.Workbench.ActiveFile?.Preview is not null, "markdown loaded");
            Assert.IsType<TKCode.Desktop.Previews.MarkdownPreviewViewModel>(vm.Workbench.ActiveFile!.Preview);
            HeadlessApp.Save(window, "editor-markdown");
            var src = vm.Explorer.RootNodes.Single(n => n.Name == "src");
            await vm.Explorer.ExpandAsync(src);
            await Settle();
            var app = src.Children.Single(n => n.Name == "App.cs");
            await vm.Explorer.ActivateCommand.ExecuteAsync(app);
            await WaitUntil(() => vm.Workbench.ActiveFile?.Preview is TKCode.Desktop.Previews.CodePreviewViewModel, "file loaded");
            Assert.Equal(6, window.GetVisualDescendants().OfType<TKCode.Desktop.Controls.CodeView>().Single().LineCount);
            Assert.Equal(["src", "App.cs"], vm.Workbench.ActiveFile!.Breadcrumbs.Select(b => b.Text));
            Assert.True(app.IsSelected);
            Assert.Same(app, vm.Explorer.SelectedNode);
            HeadlessApp.Save(window, "editor-code");
            // 收起文件树：编辑区成为最右一栏
            vm.ToggleExplorerCommand.Execute(null);
            await Settle();
            HeadlessApp.Save(window, "editor-explorer-collapsed");
            vm.ToggleExplorerCommand.Execute(null);
            await Settle();

            // 搜索文件
            vm.Explorer.Search = "app";
            await WaitUntil(() => vm.Explorer.SearchResults.Any(n => n.Name == "App.cs"), "file search");
            Assert.True(vm.Explorer.IsSearching);
            Assert.Equal("src", vm.Explorer.SearchResults.Single(n => n.Name == "App.cs").Hint);
            HeadlessApp.Save(window, "explorer-search");
            // Search rows pass their actual template parameter; blank viewport space always targets the root.
            var searchNode = vm.Explorer.SearchResults.Single(n => n.Name == "App.cs");
            var searchRow = window.GetVisualDescendants().OfType<Button>()
                .Single(b => ReferenceEquals(b.CommandParameter, searchNode) && b.IsEffectivelyVisible);
            var rowPoint = searchRow.TranslatePoint(new Point(12, searchRow.Bounds.Height / 2), window)!.Value;
            window.MouseDown(rowPoint, MouseButton.Right);
            window.MouseUp(rowPoint, MouseButton.Right);
            await Settle();
            Assert.Same(searchNode, vm.Explorer.SelectedNode);
            var searchMenu = window.GetVisualDescendants().OfType<TKCode.Arc.MenuSurface>().Single(m => m.IsEffectivelyVisible);
            Assert.False(searchMenu.IsOpen); // The scaled host, not MenuSurface, owns the popup.
            Assert.Contains(searchMenu.Items.OfType<TKCode.Arc.MenuEntry>(), e => Equals(e.Header,
                TKCode.Desktop.Localization.Strings.Get(TKCode.Desktop.Localization.TKCodeLangResourceKind.ExplorerReveal)));
            HeadlessApp.Save(window, "explorer-search-context");
            window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            window.KeyRelease(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            await Settle();
            var searchScroll = window.FindControl<ScrollViewer>("ExplorerSearchScroll")!;
            var blankPoint = searchScroll.TranslatePoint(new Point(12, searchScroll.Bounds.Height - 16), window)!.Value;
            window.MouseDown(blankPoint, MouseButton.Right);
            window.MouseUp(blankPoint, MouseButton.Right);
            await Settle();
            var blankMenu = window.GetVisualDescendants().OfType<TKCode.Arc.MenuSurface>().Single(m => m.IsEffectivelyVisible);
            Assert.DoesNotContain(blankMenu.Items.OfType<TKCode.Arc.MenuEntry>(), e => e.IsDestructive);
            Assert.Contains(blankMenu.Items.OfType<TKCode.Arc.MenuEntry>(), e => Equals(e.Header,
                TKCode.Desktop.Localization.Strings.Get(TKCode.Desktop.Localization.TKCodeLangResourceKind.ExplorerOpenRoot)));
            window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            window.KeyRelease(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            await Settle();
            vm.Explorer.Search = "";
            await Settle();
            Assert.False(vm.Explorer.IsSearching);
            Assert.Empty(vm.Explorer.SearchResults);
            Assert.Same(app, vm.Explorer.SelectedNode);

            // “+”添加工具面板；最大化编辑区隐藏对话栏
            vm.Workbench.AddToolCommand.Execute(vm.Workbench.Tools.Single(t => t.Kind == WorkbenchTabKind.Terminal));
            vm.Workbench.ActivateCommand.Execute(vm.Workbench.Tabs.OfType<FileTabViewModel>().Last());
            vm.Workbench.ToggleMaximizeCommand.Execute(null);
            await Settle();
            Assert.False(window.GetVisualDescendants().OfType<Avalonia.Controls.DockPanel>().Single(d => d.Name == "ConversationColumn").IsVisible);
            HeadlessApp.Save(window, "editor-maximized");
            vm.Workbench.ToggleMaximizeCommand.Execute(null);
            vm.ToggleExplorerCommand.Execute(null);
            vm.ToggleWorkbenchCommand.Execute(null);
            await Settle();

            // 审批卡片：外部运行时的命令审批（可选“本会话内允许”）
            var approval = new ApprovalRequestViewModel(new TKCode.Rpc.ToolApprovalRequest(vm.Conversation.Active!.SessionId, "t1", "Bash", "{}",
                "command", "dotnet test tests/TKCode.Desktop.Tests", "Run the UI tests to verify the change", CanAllowForSession: true));
            vm.Conversation.Active.AddApproval(approval);
            vm.Conversation.Active.AddApproval(new ApprovalRequestViewModel(new TKCode.Rpc.ToolApprovalRequest(vm.Conversation.Active.SessionId, "t2", "Edit", "{}",
                "fileChange", "src/App.cs")));
            await Settle();
            Assert.True(vm.Sidebar.VisibleSessions[0].IsWaitingApproval);
            HeadlessApp.Save(window, "approval-card");
            approval.AllowForSessionCommand.Execute(null);
            Assert.True((await approval.Result).ForSession);
            Assert.Equal("fileChange", vm.Conversation.Active.CurrentApproval!.Request.Kind);
            vm.Conversation.Active.CurrentApproval.DenyCommand.Execute(null);
            Assert.False(vm.Conversation.Active.HasPendingApproval);
            await Settle();
            Assert.False(vm.Sidebar.VisibleSessions[0].IsWaitingApproval);

            // 命令面板：根页、搜索会话；新建项目（⇧⌘N）关闭面板并打开创建项目弹窗（docs/design/projects.md §8.4）
            vm.Palette.Open();
            await Settle();
            HeadlessApp.Save(window, "palette-root");
            vm.Palette.Search = "目录";
            await Settle();
            Assert.Contains(vm.Palette.Items, i => i.Title == "看看目录里有哪些文件");
            vm.Palette.OpenNewProject();
            await Settle();
            Assert.False(vm.Palette.IsOpen);
            var projectModal = Assert.Single(TKCode.Arc.Primitives.ModalPresence.GetModals(window));
            var projectDialog = Assert.IsType<ProjectDialog>(Assert.IsType<TKCode.Desktop.Controls.ZoomHost>(projectModal.Panel).Child);
            Assert.False(projectDialog.ViewModel.IsEdit);
            HeadlessApp.Save(window, "palette-new-project");
            projectDialog.FindControl<TKCode.Arc.Button>("CancelButton")!
                .RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
            // 关闭有退出动效：等它从覆盖层移除
            for (var i = 0; i < 100 && TKCode.Arc.Primitives.ModalPresence.GetModals(window).Count > 0; i++)
            {
                await Settle();
            }
            Assert.Empty(TKCode.Arc.Primitives.ModalPresence.GetModals(window));

            // 前进 / 后退
            vm.Conversation.NewChat();
            Assert.True(vm.Conversation.IsEmpty);
            await vm.Conversation.GoBackCommand.ExecuteAsync(null);
            Assert.False(vm.Conversation.IsEmpty);
            Assert.True(vm.Conversation.GoForwardCommand.CanExecute(null));

            // 切换分支：先确认，再执行 git switch
            var branches = vm.Conversation.BranchPicker;
            Assert.Equal("main", branches.Current);
            branches.SelectCommand.Execute(branches.Items.Single(b => b.Name == "feature"));
            Assert.True(branches.IsConfirming);
            await branches.ConfirmCommand.ExecuteAsync(null);
            Assert.Null(branches.Error);
            Assert.Equal("feature", branches.Current);

            // 亮色主题下的同一组界面（检查控件在两套主题中的可读性）
            var themes = provider.GetRequiredService<ThemeService>();
            themes.Attach(Avalonia.Application.Current!);
            await themes.SetModeAsync(ThemeMode.Light);
            await Settle();
            HeadlessApp.Save(window, "main-conversation-light");
            vm.Palette.Open();
            await Settle();
            HeadlessApp.Save(window, "palette-root-light");
            vm.Palette.Close();
            await vm.OpenSettingsCommand.ExecuteAsync(null);
            await Settle();
            HeadlessApp.Save(window, "settings-general-light");
            vm.CloseSettingsCommand.Execute(null);
            vm.Conversation.NewChat();
            await Settle();
            HeadlessApp.Save(window, "main-empty-light");
            // 分支切换确认（弹层改用覆盖层渲染）
            foreach (var popup in window.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.Popup>())
            {
                popup.ShouldUseOverlayLayer = true;
            }
            var branchPicker = window.GetVisualDescendants().OfType<BranchPickerView>().First(v => v.IsEffectivelyVisible);
            branchPicker.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.Popup>().Single().IsOpen = true;
            branches.SelectCommand.Execute(branches.Items.Single(b => b.Name == "main"));
            await Settle();
            HeadlessApp.Save(window, "branch-confirm-light");
            branches.CancelCommand.Execute(null);
            branchPicker.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.Popup>().Single().IsOpen = false;
            await themes.SetModeAsync(ThemeMode.Dark);
            await Settle();

            // 切换语言后文案刷新
            vm.ToggleLanguageCommand.Execute(null);
            await Settle();
            HeadlessApp.Save(window, "main-conversation-en");
            vm.ToggleLanguageCommand.Execute(null);
            window.Close();
            return true;
        }, TestContext.Current.CancellationToken);

        provider.GetRequiredService<AgentHostConnection>().Dispose();
    }

    private static void Git(string cwd, params string[] args)
    {
        var start = new System.Diagnostics.ProcessStartInfo("git") { WorkingDirectory = cwd, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }
        using var process = System.Diagnostics.Process.Start(start)!;
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, process.StandardError.ReadToEnd());
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
        for (var i = 0; i < 5; i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(30);
        }
    }
}
