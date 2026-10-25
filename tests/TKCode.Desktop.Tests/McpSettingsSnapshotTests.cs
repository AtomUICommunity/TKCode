using System.Text.Json;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TKCode.Desktop.ViewModels;
using TKCode.Desktop.Views;
using TKCode.Rpc;
using Xunit;

namespace TKCode.Desktop.Tests;

/// <summary>
/// 设置 › MCP 服务器端到端：settings.json 中的 stdio 测试服务器连接成功并列出工具；表单添加服务器写回 settings.json；
/// “始终允许”写回 alwaysAllow；配置错误的服务器单独显示错误；待信任的项目服务器给出信任 / 忽略；
/// 导入、导出是 Arc 模态框，打开、取消与完成后的关闭都跟视图模型一致。
/// </summary>
public sealed class McpSettingsSnapshotTests
{
    [Fact]
    public async Task McpSection_ListsServers_ConnectsAndWritesBack()
    {
        var home = Directory.CreateTempSubdirectory("tkcode-mcp-settings-home-").FullName;
        Environment.SetEnvironmentVariable("TKCODE_HOME", home);
        Environment.SetEnvironmentVariable("TKCODE_FAKE_PROVIDER", "1");
        Environment.SetEnvironmentVariable("TKCODE_EXTERNAL_RUNTIMES", "0");
        Environment.SetEnvironmentVariable("TKCODE_AGENTHOST_DIR", Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "../../../../../src/TKCode.AgentHost/bin/Debug/net11.0")));
        var serverDll = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "../../../../TKCode.Mcp.TestServer/bin/Debug/net11.0/TKCode.Mcp.TestServer.dll"));
        Assert.True(File.Exists(serverDll), serverDll);
        var settingsPath = Path.Combine(home, "settings.json");
        File.WriteAllText(settingsPath, $$"""
        {
          "mcpServers": {
            "tester": { "command": "dotnet", "args": [{{JsonSerializer.Serialize(serverDll)}}], "name": "Test Server" },
            "broken": { "type": "ws", "url": "wss://example.com" },
            "remote": { "url": "https://mcp.example.com/mcp", "enabled": false }
          }
        }
        """);

        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        Program.ConfigureServices(services);
        await using var provider = services.BuildServiceProvider();

        await HeadlessApp.Session.Dispatch(async () =>
        {
            var vm = provider.GetRequiredService<MainWindowViewModel>();
            var window = new MainWindow { DataContext = vm, Width = 1180, Height = 820 };
            window.Show();
            await WaitUntil(() => vm.Conversation.Picker.Selected is not null, "models loaded");

            await vm.OpenSettingsCommand.ExecuteAsync(null);
            vm.Settings.NavigateCommand.Execute(vm.Settings.Nav.Single(n => n.Section == SettingsSection.Mcp));
            var mcp = vm.Settings.Mcp;
            await WaitUntil(() => mcp.GlobalServers.Count == 3, "servers listed");
            Assert.True(mcp.GlobalServers.Single(s => s.Key == "broken").IsError);
            Assert.True(mcp.GlobalServers.Single(s => s.Key == "remote").IsOff);

            // 选中测试服务器：临时连接并列出工具
            await mcp.SelectCommand.ExecuteAsync(mcp.GlobalServers.Single(s => s.Key == "tester"));
            var detail = mcp.Selected!;
            await WaitUntil(() => detail.Tools.Count > 0, "tools listed", 30_000);
            Assert.Contains(detail.Tools, t => t.Name == "echo" && t.Dto.ReadOnlyHint);
            await WaitUntil(() => mcp.GlobalServers.Single(s => s.Key == "tester").IsOk, "connected status");
            await Settle();
            HeadlessApp.Save(window, "settings-mcp");

            // 始终允许 echo → 写回 alwaysAllow
            detail.Tools.Single(t => t.Name == "echo").AlwaysAllow = true;
            await WaitUntil(() => File.ReadAllText(settingsPath).Contains("\"alwaysAllow\""), "alwaysAllow written");
            Assert.Contains("\"echo\"", File.ReadAllText(settingsPath));

            // 配置错误的服务器
            await mcp.SelectCommand.ExecuteAsync(mcp.GlobalServers.Single(s => s.Key == "broken"));
            await Settle();
            Assert.True(mcp.Selected!.HasStatusError);
            HeadlessApp.Save(window, "settings-mcp-error");

            // 项目里待信任的服务器：给出信任 / 忽略
            var untrusted = new McpServerDto("proj", "proj", "project", "stdio", "npx -y proj", true, "untrusted", "idle", null, 0, 0, 0,
                null, null, null, null, false, [], """{ "command": "npx", "args": ["-y", "proj"] }""", false);
            await mcp.SelectCommand.ExecuteAsync(new McpServerItemViewModel(untrusted));
            await Settle();
            Assert.True(mcp.Selected!.IsUntrusted);
            HeadlessApp.Save(window, "settings-mcp-untrusted");

            // 添加远程服务器：连接方式是分段选择，选中项跟随视图模型
            mcp.AddCommand.Execute(null);
            var added = mcp.Selected!;
            Assert.Equal("local", added.TransportChoices.Single(c => c.IsSelected).Value);
            added.ChooseTransportCommand.Execute(added.TransportChoices.Single(c => c.Value == "remote"));
            Assert.True(added.IsRemote);
            Assert.Equal("remote", added.TransportChoices.Single(c => c.IsSelected).Value);
            Assert.Equal("global", added.ScopeChoices.Single(c => c.IsSelected).Value);
            added.NameInput = "docs";
            added.UrlInput = "https://docs.example.com/mcp";
            added.HeadersInput = "Authorization: Bearer {env:DOCS_TOKEN}";
            await Settle();
            HeadlessApp.Save(window, "settings-mcp-add");
            await added.SaveCommand.ExecuteAsync(null);
            await WaitUntil(() => mcp.GlobalServers.Any(s => s.Key == "docs"), "added server listed");
            var text = File.ReadAllText(settingsPath);
            Assert.Contains("\"docs\"", text);
            Assert.Contains("Bearer {env:DOCS_TOKEN}", text);
            Assert.Contains("\"tester\"", text);

            // 导入：粘贴 VS Code 格式（servers + inputs），重名的 docs 默认改名前提示覆盖
            mcp.OpenImportCommand.Execute(null);
            await Settle();
            var import = mcp.Import!;
            // 取消（Esc、遮罩、关闭按钮走同一条路径）：对话框关闭后视图模型的状态随之还原
            Assert.Single(window.GetVisualDescendants().OfType<McpImportDialog>()).Close();
            await WaitUntil(() => !mcp.IsImporting, "import dismissed");
            await WaitUntil(() => !window.GetVisualDescendants().OfType<McpImportDialog>().Any(), "import dialog removed");
            mcp.OpenImportCommand.Execute(null);
            await Settle();
            import = mcp.Import!;
            Assert.Single(window.GetVisualDescendants().OfType<McpImportDialog>());
            import.ChooseModeCommand.Execute(import.ModeChoices.Single(c => c.Value == "scan"));
            Assert.True(import.IsScanMode);
            Assert.Equal("scan", import.ModeChoices.Single(c => c.IsSelected).Value);
            import.ChooseModeCommand.Execute(import.ModeChoices.Single(c => c.Value == "paste"));
            Assert.False(import.IsScanMode);
            import.Text = """
                {
                  "inputs": [ { "type": "promptString", "id": "key", "description": "API Key", "password": true } ],
                  "servers": {
                    "docs": { "type": "http", "url": "https://other.example.com/mcp" },
                    "search": { "command": "npx", "args": ["-y", "search-mcp"], "env": { "KEY": "${input:key}" }, "dev": {} }
                  }
                }
                """;
            await Settle();
            Assert.Equal(2, import.Items.Count);
            Assert.True(import.Items.Single(i => i.Key == "docs").Overwrites);
            import.Items.Single(i => i.Key == "docs").Key = "docs2";
            import.Items.Single(i => i.Key == "search").Inputs.Single().Value = "{env:SEARCH_KEY}";
            await Settle();
            HeadlessApp.Save(window, "settings-mcp-import");
            await import.ImportCommand.ExecuteAsync(null);
            await WaitUntil(() => mcp.GlobalServers.Any(s => s.Key == "search") && mcp.GlobalServers.Any(s => s.Key == "docs2"), "imported servers listed");
            Assert.False(mcp.IsImporting);
            await WaitUntil(() => !window.GetVisualDescendants().OfType<McpImportDialog>().Any(), "import dialog closed after importing");
            text = File.ReadAllText(settingsPath);
            Assert.Contains("{env:SEARCH_KEY}", text);
            Assert.Contains("https://docs.example.com/mcp", text);

            // 导出
            mcp.OpenExportCommand.Execute(null);
            await Settle();
            Assert.Contains("\"mcpServers\"", mcp.Export!.Json);
            Assert.Contains("${SEARCH_KEY}", mcp.Export.Json);
            Assert.Single(window.GetVisualDescendants().OfType<McpExportDialog>());
            HeadlessApp.Save(window, "settings-mcp-export");
            mcp.Export.CloseCommand.Execute(null);
            await WaitUntil(() => !window.GetVisualDescendants().OfType<McpExportDialog>().Any(), "export dialog closed");
            mcp.OpenExportCommand.Execute(null);
            await Settle();
            Assert.Single(window.GetVisualDescendants().OfType<McpExportDialog>()).Close();
            await WaitUntil(() => !mcp.IsExporting, "export dismissed");
            window.Close();
            return true;
        }, TestContext.Current.CancellationToken);

        provider.GetRequiredService<TKCode.Desktop.Services.AgentHostConnection>().Dispose();
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
