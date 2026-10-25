using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TKCode.Arc.Motion;
using TKCode.Config;
using TKCode.Desktop.Controls.Composer;
using TKCode.Desktop.Services;
using TKCode.Desktop.ViewModels;
using TKCode.Desktop.Views;
using Xunit;

namespace TKCode.Desktop.Tests;

/// <summary>
/// 多文件夹项目的界面（docs/design/projects.md §10）：文件树每个文件夹一个根节点，@ 跨文件夹搜索，
/// 分支选择器按仓库切换，/add-dir 只给本会话追加文件夹。
/// </summary>
public sealed class MultiFolderTests
{
    [Fact]
    public Task Explorer_ShowsOneRootPerFolder_RevealsSearchesAndScopesActions() => HeadlessApp.Session.Dispatch(async () =>
    {
        var primary = Directory.CreateTempSubdirectory("tkcode-mf-app-").FullName;
        var lib = Directory.CreateTempSubdirectory("tkcode-mf-lib-").FullName;
        var notes = Directory.CreateTempSubdirectory("tkcode-mf-notes-").FullName;
        Write(primary, "src/App.cs");
        var helper = Write(lib, "util/Helper.cs");
        Write(notes, "todo.md");
        using var hosts = new PtyHostService(NullLogger<PtyHostService>.Instance);
        var workbench = new WorkbenchViewModel(hosts);
        var explorer = new FileExplorerViewModel(workbench);
        string? removed = null;
        explorer.RemoveSessionRootAsync = path =>
        {
            removed = path;
            return Task.CompletedTask;
        };
        string? copied = null;
        explorer.CopyTextAsync = text =>
        {
            copied = text;
            return Task.CompletedTask;
        };

        // 只有一个文件夹：和以前一样直接列出内容，没有根节点
        explorer.SetRoots(primary, []);
        await WaitUntil(() => explorer.RootNodes.Count == 1);
        Assert.Equal("src", explorer.RootNodes[0].Name);
        Assert.False(explorer.IsMultiRoot);

        // 多个文件夹：每个一个根节点，主文件夹在前并展开，会话文件夹注明「本会话」
        explorer.SetRoots(primary, [lib, notes, primary, Path.Combine(primary, "missing")], [notes]);
        await WaitUntil(() => explorer.RootNodes.Count == 3 && explorer.RootNodes[0].IsLoaded);
        Assert.True(explorer.IsMultiRoot);
        Assert.Equal([$"{Project.FolderName(primary)} · 主要", Project.FolderName(lib), $"{Project.FolderName(notes)} · 本会话"],
            explorer.RootNodes.Select(n => n.Name));
        Assert.All(explorer.RootNodes, n => Assert.True(n.IsWorkspaceRoot));
        Assert.True(explorer.RootNodes[0].IsExpanded);
        Assert.Equal("src", Assert.Single(explorer.RootNodes[0].Children).Name);
        Assert.False(explorer.RootNodes[1].IsExpanded);

        // 定位其他文件夹里的文件：展开它的根节点与上级目录
        await explorer.RevealAsync(helper);
        Assert.Equal(helper, explorer.SelectedNode?.FullPath);
        Assert.True(explorer.RootNodes[1].IsExpanded);

        // 搜索覆盖所有文件夹，提示文字带文件夹名
        explorer.Search = "Helper";
        await WaitUntil(() => explorer.SearchResults.Count == 1);
        Assert.Equal(Path.Combine(Project.FolderName(lib), "util"), explorer.SearchResults[0].Hint);
        explorer.Search = "";

        // 右键：作用于所在的根目录；会话文件夹的根节点可以从本会话移除
        var helperNode = explorer.SelectedNode!;
        var target = explorer.CaptureContext(helperNode)!;
        Assert.Equal(lib, target.Root);
        Assert.False(target.IsRoot);
        await explorer.ExecuteActionAsync(target, ExplorerAction.CopyRelativePath);
        Assert.Equal(Path.Combine("util", "Helper.cs"), copied);
        var libRoot = explorer.CaptureContext(explorer.RootNodes[1])!;
        Assert.True(libRoot.IsRoot);
        Assert.DoesNotContain(ExplorerAction.RemoveSessionRoot, explorer.GetContextActions(libRoot));
        var notesRoot = explorer.CaptureContext(explorer.RootNodes[2])!;
        Assert.Contains(ExplorerAction.RemoveSessionRoot, explorer.GetContextActions(notesRoot));
        await explorer.ExecuteActionAsync(notesRoot, ExplorerAction.RemoveSessionRoot);
        Assert.Equal(notes, removed);
        // 空白处作用于主文件夹
        Assert.Equal(primary, explorer.CaptureContext(null)!.Root);

        explorer.SetRoot(null);
        return true;
    }, TestContext.Current.CancellationToken);

    [Fact]
    public async Task Project_SearchesAllFolders_SwitchesBranchesPerRepository_AndAddsSessionFolders()
    {
        var home = Directory.CreateTempSubdirectory("tkcode-ui-home-").FullName;
        var app = NewRepository("app");
        var lib = NewRepository("lib");
        Git(lib, "branch", "feature");
        var notes = Directory.CreateTempSubdirectory("tkcode-mf-notes-").FullName;
        Write(app, "src/Program.cs");
        var helper = Write(lib, "src/Helper.cs");
        Write(notes, "todo.md");
        Environment.SetEnvironmentVariable("TKCODE_HOME", home);
        Environment.SetEnvironmentVariable("TKCODE_AGENTHOST_DIR", Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "../../../../../src/TKCode.AgentHost/bin/Debug/net11.0")));
        Environment.SetEnvironmentVariable("TKCODE_FAKE_PROVIDER", "1");
        Environment.SetEnvironmentVariable("TKCODE_EXTERNAL_RUNTIMES", "0");
        var project = Project.Create("多仓库", [app, lib]);
        ProjectsFile.Save([project]);
        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        Program.ConfigureServices(services);
        await using var provider = services.BuildServiceProvider();

        await HeadlessApp.Session.Dispatch(async () =>
        {
            var vm = provider.GetRequiredService<MainWindowViewModel>();
            var conversation = vm.Conversation;
            var window = new MainWindow { DataContext = vm, Width = 1280, Height = 760 };
            ArcMotion.SetLevel(window, ArcMotionLevel.Off);
            window.Show();
            await WaitUntil(() => conversation.Picker.Selected is not null && conversation.Workspace?.Id == project.Id);
            Assert.Equal([lib], conversation.CurrentAdditionalRoots);

            // @：其他文件夹里的文件显示为「文件夹名/目录」，tag 用绝对路径
            var items = await conversation.Files.QueryAsync("Helper", CancellationToken.None);
            for (var i = 0; i < 100 && items.Count == 0; i++)
            {
                await Task.Delay(50);
                items = await conversation.Files.QueryAsync("Helper", CancellationToken.None);
            }
            var hit = Assert.Single(items, i => i.Title == "Helper.cs");
            Assert.Equal($"{Project.FolderName(lib)}/src", hit.Subtitle);
            Assert.Equal(helper, hit.Action.Tag!.Value);

            // 分支：两个仓库，按钮显示主文件夹的分支和「+1」；选中 lib 后列出它的分支
            var branches = conversation.BranchPicker;
            Assert.Equal([app, lib], branches.Repositories.Select(r => r.Path));
            Assert.Equal("main", branches.DisplayName);
            Assert.Equal("+1", branches.MoreText);
            branches.SelectRepositoryCommand.Execute(branches.Repositories[1]);
            Assert.Equal(lib, branches.SelectedRepository);
            Assert.Equal(["main", "feature"], branches.Items.Select(b => b.Name));
            branches.SelectCommand.Execute(branches.Items.Single(b => b.Name == "feature"));
            await branches.ConfirmCommand.ExecuteAsync(null);
            Assert.Null(branches.Error);
            Assert.Equal("feature", GitRepository.ReadBranch(lib));
            Assert.Equal("main", GitRepository.ReadBranch(app));
            Assert.Equal("main", branches.DisplayName);

            // /add-dir：空状态里先记下，创建会话时一起发送；文件树多出「本会话」的根节点
            conversation.ComposerText = "/add-dir " + notes;
            await conversation.SendCommand.ExecuteAsync(null);
            Assert.Equal([notes], conversation.PendingDirectories);
            Assert.Null(conversation.Active);
            await WaitUntil(() => vm.Explorer.RootNodes.Count == 3);
            conversation.ComposerText = "你好";
            await conversation.SendCommand.ExecuteAsync(null);
            await WaitUntil(() => conversation.Active is { IsRunning: false });
            Assert.Empty(conversation.PendingDirectories);
            Assert.Equal([notes], conversation.Active!.SessionDirectories);
            Assert.Equal(project.Id, conversation.Active.ProjectId);
            Assert.Contains(notes, conversation.CurrentAdditionalRoots);

            vm.IsExplorerOpen = true;
            await WaitUntil(() => vm.Explorer.RootNodes.Count == 3 && vm.Explorer.RootNodes[0].IsLoaded);
            HeadlessApp.Save(window, "multi-folder-explorer");

            // 从本会话移除：文件树回到两个根节点
            var notesRoot = vm.Explorer.CaptureContext(vm.Explorer.RootNodes.Single(n => n.FullPath == notes))!;
            await vm.Explorer.ExecuteActionAsync(notesRoot, ExplorerAction.RemoveSessionRoot);
            await WaitUntil(() => conversation.Active.SessionDirectories.Count == 0 && vm.Explorer.RootNodes.Count == 2);
            window.Close();
            return true;
        }, TestContext.Current.CancellationToken);
    }

    private static string Write(string root, string relative)
    {
        var path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "// multi-folder test\n");
        return path;
    }

    private static string NewRepository(string name)
    {
        var path = Directory.CreateDirectory(Path.Combine(Directory.CreateTempSubdirectory("tkcode-mf-").FullName, name)).FullName;
        Git(path, "init", "-q", "-b", "main");
        Git(path, "-c", "user.email=t@example.com", "-c", "user.name=t", "commit", "-q", "--allow-empty", "-m", "init");
        return path;
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

    private static async Task WaitUntil(Func<bool> condition, int timeoutMs = 20_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("Condition was not reached.");
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
