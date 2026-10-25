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
/// 设置页端到端：外部编辑 settings.json → Host 热重载 → 模型选择器出现新厂商；
/// 在设置页切换模型启用 → 写回 settings.json。
/// </summary>
public sealed class SettingsSnapshotTests
{
    [Fact]
    public async Task ExternalEdit_HotReloads_AndUiWritesBack()
    {
        var home = Directory.CreateTempSubdirectory("tkcode-settings-home-").FullName;
        Environment.SetEnvironmentVariable("TKCODE_HOME", home);
        Environment.SetEnvironmentVariable("TKCODE_FAKE_PROVIDER", "1");
        Environment.SetEnvironmentVariable("TKCODE_EXTERNAL_RUNTIMES", "0");
        Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", null);
        Environment.SetEnvironmentVariable("TKCODE_AGENTHOST_DIR", Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "../../../../../src/TKCode.AgentHost/bin/Debug/net11.0")));
        var settingsPath = Path.Combine(home, "settings.json");
        // 本地 HTTP 服务同时扮演 OpenAI 兼容端点（/v1/models）与 models.dev（/api.json）；端口需在 Host 启动前确定
        var port = FreePort();
        Environment.SetEnvironmentVariable("TKCODE_CATALOG_URL", $"http://127.0.0.1:{port}/api.json");

        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        Program.ConfigureServices(services);
        await using var provider = services.BuildServiceProvider();

        await HeadlessApp.Session.Dispatch(async () =>
        {
            var vm = provider.GetRequiredService<MainWindowViewModel>();
            var window = new MainWindow { DataContext = vm, Width = 1100, Height = 760 };
            window.Show();
            await WaitUntil(() => vm.Conversation.Picker.Selected is not null, "models loaded");
            Assert.DoesNotContain(vm.Conversation.Picker.AllModels, m => m.Provider == "anthropic");

            await vm.OpenSettingsCommand.ExecuteAsync(null);
            await Settle();
            HeadlessApp.Save(window, "settings-general");

            // 左侧导航是 Arc 的静态菜单面板：勾始终跟着选中的分区（用户点击，或别处经视图模型切换，都一样）
            var navMenu = window.GetVisualDescendants().OfType<TKCode.Arc.MenuSurface>().Single(m => m.Name == "NavMenu");
            var navEntries = navMenu.Items.OfType<TKCode.Arc.MenuEntry>().Skip(1).ToList(); // 第一项是「返回」
            Assert.Equal(vm.Settings.Nav.Count, navEntries.Count);
            for (var i = 0; i < navEntries.Count; i++)
            {
                Assert.Same(vm.Settings.NavigateCommand, navEntries[i].Command);
                Assert.Same(vm.Settings.Nav[i], navEntries[i].CommandParameter);
            }
            Assert.Equal([true, false, false, false, false, false], navEntries.Select(e => e.IsChecked).ToArray());
            navEntries[3].PerformClick(); // 用户点「MCP 服务器」
            await Settle();
            Assert.True(vm.Settings.IsMcp);
            Assert.Equal([false, false, false, true, false, false], navEntries.Select(e => e.IsChecked).ToArray());
            vm.Settings.NavigateCommand.Execute(vm.Settings.Nav[5]); // 托盘「检查更新」之类的入口直接切到「关于」
            await Settle();
            Assert.Equal([false, false, false, false, false, true], navEntries.Select(e => e.IsChecked).ToArray());
            navEntries[1].PerformClick();
            await Settle();
            Assert.True(vm.Settings.IsProviders);

            // 还没有任何厂商：左栏与右栏都是空状态
            vm.Settings.NavigateCommand.Execute(vm.Settings.Nav[1]);
            await Settle();
            HeadlessApp.Save(window, "settings-providers-empty");

            // 本地 OpenAI 兼容服务：GET /v1/models 返回两个模型（其中一个在目录中有元数据）
            using var server = new System.Net.HttpListener();
            server.Prefixes.Add($"http://127.0.0.1:{port}/");
            server.Start();
            _ = Task.Run(async () =>
            {
                while (server.IsListening)
                {
                    var context = await server.GetContextAsync();
                    if (context.Request.Url!.AbsolutePath == "/api.json" && context.Request.Headers["If-None-Match"] == "\"fixture\"")
                    {
                        context.Response.StatusCode = 304;
                        context.Response.Close();
                        continue;
                    }
                    if (context.Request.Url!.AbsolutePath == "/api.json")
                    {
                        var catalogBytes = System.Text.Encoding.UTF8.GetBytes(ModelsDevFixture);
                        context.Response.Headers["ETag"] = "\"fixture\"";
                        await context.Response.OutputStream.WriteAsync(catalogBytes);
                        context.Response.Close();
                        continue;
                    }
                    var ok = context.Request.Headers["Authorization"] == "Bearer local-key";
                    context.Response.StatusCode = ok ? 200 : 401;
                    var body = ok ? """{ "data": [ { "id": "deepseek-v4-pro" }, { "id": "local-coder" } ] }""" : """{ "error": { "message": "bad key" } }""";
                    var bytes = System.Text.Encoding.UTF8.GetBytes(body);
                    await context.Response.OutputStream.WriteAsync(bytes);
                    context.Response.Close();
                }
            });

            // 外部编辑：内置厂商只写 Key；自定义厂商不写 models（自动获取）；亮色主题
            File.WriteAllText(settingsPath, $$"""
            {
              "theme": "light",
              "providers": {
                "anthropic": { "apiKey": "sk-ant-test-1234567890" },
                "local": { "name": "本地", "api": "chat", "baseUrl": "http://127.0.0.1:{{port}}/v1", "apiKey": "local-key" }
              }
            }
            """);
            await WaitUntil(() => vm.Conversation.Picker.AllModels.Any(m => m.Ref == "anthropic/claude-opus-5-5"), "hot reload", 15_000);
            await WaitUntil(() => vm.Conversation.Picker.AllModels.Any(m => m.Ref == "local/local-coder"), "model discovery", 15_000);
            Assert.Equal(1_000_000, vm.Conversation.Picker.AllModels.Single(m => m.Ref == "local/deepseek-v4-pro").ContextWindows[^1]);

            vm.Settings.NavigateCommand.Execute(vm.Settings.Nav[1]);
            await WaitUntil(() => vm.Settings.ConfiguredList.Any(p => p.Id == "local" && p.IsOk), "local provider connected");
            vm.Settings.SelectProviderCommand.Execute(vm.Settings.ConfiguredList.Single(p => p.Id == "local"));
            var local = vm.Settings.Selected!;
            Assert.True(local.SupportsDiscovery);
            Assert.Equal(2, local.Models.Count(m => m.IsEnabled));

            // 测试连接：未保存的错误 Key → 401；清空后用已保存的 Key → 成功
            local.KeyInput = "wrong";
            await local.TestCommand.ExecuteAsync(null);
            Assert.False(local.TestSucceeded);
            local.KeyInput = "";
            await local.TestCommand.ExecuteAsync(null);
            Assert.True(local.TestSucceeded, local.TestResult);
            await Settle();
            HeadlessApp.Save(window, "settings-providers-local");
            // 亮色主题（测试中 App.Services 为空，需要手动挂接 ThemeService）
            var themes = provider.GetRequiredService<ThemeService>();
            themes.Attach(Avalonia.Application.Current!);
            await themes.SetModeAsync(ThemeMode.Light);
            await Settle();
            HeadlessApp.Save(window, "settings-providers-local-light");
            await themes.SetModeAsync(ThemeMode.Dark);

            // 内置厂商：停用 Haiku → models 被写入
            vm.Settings.SelectProviderCommand.Execute(vm.Settings.ConfiguredList.Single(p => p.Id == "anthropic"));
            var anthropic = vm.Settings.Selected!;
            await Settle();
            HeadlessApp.Save(window, "settings-providers");
            var haiku = anthropic.Models.Single(m => m.Id == "claude-haiku-5-5");
            var themeChoices = vm.Settings.Themes.ToList();
            anthropic.ToggleModelCommand.Execute(haiku);
            await WaitUntil(() => !vm.Conversation.Picker.AllModels.Any(m => m.Ref == "anthropic/claude-haiku-5-5"), "model disabled", 15_000);
            await Settle();
            // 写设置后的刷新（写入本身、文件监视、Host 推来的新快照）原地更新：行和选项还是原来的对象，开关不会被重建
            Assert.Same(anthropic, vm.Settings.Selected);
            Assert.Same(haiku, anthropic.Models.Single(m => m.Id == "claude-haiku-5-5"));
            Assert.Contains(haiku, anthropic.VisibleModels);
            Assert.False(haiku.IsEnabled);
            Assert.Equal(themeChoices, vm.Settings.Themes);
            var text = File.ReadAllText(settingsPath);
            Assert.Contains("\"claude-opus-5-5\": {}", text);
            Assert.DoesNotContain("claude-haiku-5-5", text);

            // 覆盖单个模型的上下文窗口：只写入改动的字段
            anthropic = vm.Settings.Selected!;
            anthropic.EditModelCommand.Execute(anthropic.Models.Single(m => m.Id == "claude-opus-5-5"));
            anthropic.Editing!.ContextInput = "200000";
            anthropic.Editing.SaveCommand.Execute(null);
            await WaitUntil(() => File.ReadAllText(settingsPath).Contains("\"contextWindow\": 200000"), "override written");
            Assert.DoesNotContain("\"maxTokens\"", File.ReadAllText(settingsPath));

            // 单独指定协议：保留已有的上下文覆盖，只新增 api
            await WaitUntil(() => vm.Settings.Selected?.Models.FirstOrDefault(m => m.Id == "claude-opus-5-5")?.Model.ContextWindow == 200_000, "override applied");
            anthropic = vm.Settings.Selected!;
            anthropic.EditModelCommand.Execute(anthropic.Models.Single(m => m.Id == "claude-opus-5-5"));
            anthropic.Editing!.ChooseApiCommand.Execute(anthropic.Editing.ApiChoices.Single(c => c.Value == "chat"));
            anthropic.Editing.SaveCommand.Execute(null);
            await WaitUntil(() => File.ReadAllText(settingsPath).Contains("\"api\": \"chat\""), "api override written");
            Assert.Contains("\"contextWindow\": 200000", File.ReadAllText(settingsPath));
            await WaitUntil(() => vm.Settings.Selected?.Models.FirstOrDefault(m => m.Id == "claude-opus-5-5")?.ApiOverridden == true, "api override shown");
            await Settle();
            HeadlessApp.Save(window, "settings-model-protocol");

            // 添加厂商：打开模板选择框
            vm.Settings.OpenTemplatePickerCommand.Execute(null);
            await Settle();
            Assert.Contains(vm.Settings.TemplateGroups, g => g.Items.Any(t => t.Id == "custom-messages"));
            Assert.DoesNotContain(vm.Settings.TemplateGroups.SelectMany(g => g.Items), t => t.Id == "anthropic"); // 已添加的不再列出
            HeadlessApp.Save(window, "settings-add-provider");
            // 模板选择是 Arc 模态框；用户自己关闭（Esc、遮罩、关闭按钮走同一条路径）后视图模型的状态随之还原
            var picker = Assert.Single(window.GetVisualDescendants().OfType<ProviderTemplateDialog>());
            picker.Close();
            await WaitUntil(() => !vm.Settings.IsPickingTemplate, "picker dismissed");
            await WaitUntil(() => !window.GetVisualDescendants().OfType<ProviderTemplateDialog>().Any(), "picker removed");
            vm.Settings.OpenTemplatePickerCommand.Execute(null);
            await Settle();
            Assert.Single(window.GetVisualDescendants().OfType<ProviderTemplateDialog>());

            // 选内置厂商：成为草稿，保存前不写文件；保存后写入
            vm.Settings.PickTemplateCommand.Execute(vm.Settings.TemplateGroups.SelectMany(g => g.Items).First(t => t.Id == "deepseek"));
            await WaitUntil(() => !window.GetVisualDescendants().OfType<ProviderTemplateDialog>().Any(), "picker closed after picking");
            var draft = vm.Settings.Selected!;
            Assert.True(draft.IsDraft);
            Assert.Equal("deepseek", vm.Settings.ConfiguredList[0].Id);
            Assert.DoesNotContain("deepseek", File.ReadAllText(settingsPath));
            HeadlessApp.Save(window, "settings-draft");
            draft.KeyInput = "sk-deepseek-test";
            draft.SaveCommand.Execute(null);
            await WaitUntil(() => vm.Conversation.Picker.AllModels.Any(m => m.Provider == "deepseek"), "draft saved", 15_000);
            Assert.Contains("sk-deepseek-test", File.ReadAllText(settingsPath));
            Assert.False(vm.Settings.Selected!.IsDraft);

            // 选自定义模板：进入自定义表单并预选协议
            vm.Settings.OpenTemplatePickerCommand.Execute(null);
            vm.Settings.PickTemplateCommand.Execute(vm.Settings.TemplateGroups.SelectMany(g => g.Items).First(t => t.Id == "custom-messages"));
            Assert.True(vm.Settings.IsCreatingCustom);
            Assert.Equal("messages", vm.Settings.Protocols.Single(p => p.IsSelected).Value);
            await Settle();
            HeadlessApp.Save(window, "settings-custom-provider");
            // 没填厂商 id 就创建：表单里用 Alert 显示原因，不创建
            vm.Settings.CreateCustomProviderCommand.Execute(null);
            await Settle();
            Assert.NotNull(vm.Settings.CustomError);
            Assert.True(vm.Settings.IsCreatingCustom);
            HeadlessApp.Save(window, "settings-custom-provider-error");

            // 从 “models.dev” 同步目录：Anthropic 的模型元数据随之更新
            vm.Settings.NavigateCommand.Execute(vm.Settings.Nav[0]);
            await vm.Settings.RefreshCatalogStatusAsync();
            Assert.Equal("embedded", vm.Settings.Catalog!.Source);
            // 检查更新：只下载并比较，不改变当前目录
            await vm.Settings.CheckCatalogCommand.ExecuteAsync(null);
            Assert.True(vm.Settings.HasPendingUpdate);
            Assert.Equal("embedded", vm.Settings.Catalog!.Source);
            Assert.Contains(vm.Conversation.Picker.AllModels, m => m.Ref == "anthropic/claude-opus-5-5" && m.Name == "Claude Opus 5.5");
            // 默认只看已添加的厂商：Anthropic 的 Opus 改名、其余模型被移除
            var anthropicChange = Assert.Single(vm.Settings.CatalogChanges, c => c.Dto.ProviderId == "anthropic");
            Assert.Contains(anthropicChange.Models, m => m.IsChanged && m.Changes.Any(c => c.NewText == "Claude Opus 5.5 (synced)"));
            Assert.Contains(anthropicChange.Models, m => m.IsRemoved);
            Assert.All(vm.Settings.CatalogChanges, c => Assert.True(c.IsConfigured));
            await Settle();
            HeadlessApp.Save(window, "settings-catalog-diff");
            vm.Settings.ShowOnlyConfiguredChanges = false;
            Assert.Contains(vm.Settings.CatalogChanges, c => !c.IsConfigured);

            // 应用更新
            await vm.Settings.ApplyCatalogUpdateCommand.ExecuteAsync(null);
            Assert.Equal("synced", vm.Settings.Catalog!.Source);
            Assert.False(vm.Settings.HasPendingUpdate);
            await WaitUntil(() => vm.Conversation.Picker.AllModels.FirstOrDefault(m => m.Ref == "anthropic/claude-opus-5-5")?.Name == "Claude Opus 5.5 (synced)",
                "synced metadata", 15_000);
            await Settle();
            HeadlessApp.Save(window, "settings-catalog-synced");
            // 再次检查：ETag 未变 → 已是最新（本地服务对 If-None-Match 返回 304）
            await vm.Settings.CheckCatalogCommand.ExecuteAsync(null);
            Assert.True(vm.Settings.IsUpToDate);
            vm.Settings.NavigateCommand.Execute(vm.Settings.Nav[1]);

            // 错误的文件：保留旧配置并显示错误
            File.WriteAllText(settingsPath, """{ "providers": { "anthropic": """);
            await WaitUntil(() => vm.Settings.HasErrors, "error shown", 15_000);
            Assert.Contains(vm.Conversation.Picker.AllModels, m => m.Ref == "anthropic/claude-opus-5-5");
            HeadlessApp.Save(window, "settings-error");
            server.Stop();

            vm.CloseSettingsCommand.Execute(null);
            window.Close();
            return true;
        }, TestContext.Current.CancellationToken);

        provider.GetRequiredService<AgentHostConnection>().Dispose();
    }

    private static ServiceProvider StartApp(string home, int catalogPort)
    {
        Environment.SetEnvironmentVariable("TKCODE_HOME", home);
        Environment.SetEnvironmentVariable("TKCODE_FAKE_PROVIDER", "1");
        Environment.SetEnvironmentVariable("TKCODE_EXTERNAL_RUNTIMES", "0");
        Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", null);
        Environment.SetEnvironmentVariable("COWORK_API_KEY", null);
        Environment.SetEnvironmentVariable("TKCODE_AGENTHOST_DIR", Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "../../../../../src/TKCode.AgentHost/bin/Debug/net11.0")));
        // 不访问真实的 models.dev
        Environment.SetEnvironmentVariable("TKCODE_CATALOG_URL", $"http://127.0.0.1:{catalogPort}/api.json");
        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        Program.ConfigureServices(services);
        return services.BuildServiceProvider();
    }

    /// <summary>
    /// 厂商删除：草稿只是放弃（不写文件）；已配置的厂商确认后才删除；靠环境变量检测出来的内置厂商移除后写入 hidden，
    /// 重新添加（保存）时清除。
    /// </summary>
    [Fact]
    public async Task Providers_DiscardDraft_ConfirmBeforeRemove_AndHideEnvironmentProvider()
    {
        var home = Directory.CreateTempSubdirectory("tkcode-provider-remove-").FullName;
        var settingsPath = Path.Combine(home, "settings.json");
        File.WriteAllText(settingsPath, """
            { "providers": {
                "gw": { "name": "My Gateway", "api": "chat", "baseUrl": "http://127.0.0.1:9/v1", "apiKey": "x", "models": { "m": {} } },
                "openai": { "apiKey": "sk-openai" } } }
            """);
        var savedDeepSeek = Environment.GetEnvironmentVariable("DEEPSEEK_API_KEY");
        Environment.SetEnvironmentVariable("DEEPSEEK_API_KEY", "sk-env-deepseek");
        await using var provider = StartApp(home, FreePort());
        try
        {
            await HeadlessApp.Session.Dispatch(async () =>
            {
                var vm = provider.GetRequiredService<MainWindowViewModel>();
                var window = new MainWindow { DataContext = vm, Width = 1100, Height = 760 };
                window.Show();
                var confirmations = new List<(string Title, string Text)>();
                var answer = false;
                vm.ConfirmAsync = (title, text, _) =>
                {
                    confirmations.Add((title, text));
                    return Task.FromResult(answer);
                };
                await WaitUntil(() => vm.Conversation.Picker.AllModels.Any(m => m.Provider == "deepseek"), "env provider loaded");
                await vm.OpenSettingsCommand.ExecuteAsync(null);
                vm.Settings.NavigateCommand.Execute(vm.Settings.Nav[1]);
                await vm.Settings.RefreshProvidersAsync();
                await Settle();
                Assert.Contains(vm.Settings.ConfiguredList, i => i.Id == "deepseek");
                Assert.Contains(vm.Settings.ConfiguredList, i => i.Id == "gw");

                // 草稿：放弃按钮不确认、不写文件
                vm.Settings.OpenTemplatePickerCommand.Execute(null);
                vm.Settings.PickTemplateCommand.Execute(vm.Settings.TemplateGroups.SelectMany(g => g.Items).First(t => t.Id == "nextcowork"));
                await Settle();
                Assert.Equal("nextcowork", vm.Settings.Selected!.Id);
                Assert.True(vm.Settings.Selected.IsPureDraft);
                HeadlessApp.Save(window, "settings-draft-nextcowork");
                await vm.Settings.Selected.RemoveCommand.ExecuteAsync(null);
                await Settle();
                Assert.Empty(confirmations);
                Assert.DoesNotContain(vm.Settings.ConfiguredList, i => i.Id == "nextcowork");
                Assert.NotNull(vm.Settings.Selected);
                Assert.DoesNotContain("nextcowork", File.ReadAllText(settingsPath));

                // 已配置的自定义厂商：取消确认 → 保留；确认 → 删除条目（没有 hidden 标记）
                vm.Settings.SelectProviderCommand.Execute(vm.Settings.ConfiguredList.Single(i => i.Id == "gw"));
                await Settle();
                await vm.Settings.Selected!.RemoveCommand.ExecuteAsync(null);
                Assert.Single(confirmations);
                Assert.Contains("gw", File.ReadAllText(settingsPath));
                answer = true;
                await vm.Settings.Selected!.RemoveCommand.ExecuteAsync(null);
                await WaitUntil(() => !File.ReadAllText(settingsPath).Contains("\"gw\""), "custom provider removed");
                await WaitUntil(() => vm.Settings.ConfiguredList.All(i => i.Id != "gw"), "custom provider left the list");
                Assert.DoesNotContain("hidden", File.ReadAllText(settingsPath));

                // 环境变量检测出来的内置厂商：确认文案点明变量名；移除后写入 hidden，模型也不再可选
                vm.Settings.SelectProviderCommand.Execute(vm.Settings.ConfiguredList.Single(i => i.Id == "deepseek"));
                await Settle();
                Assert.False(vm.Settings.Selected!.IsConfigured);
                Assert.False(vm.Settings.Selected.IsPureDraft);
                await vm.Settings.Selected.RemoveCommand.ExecuteAsync(null);
                Assert.Contains("DEEPSEEK_API_KEY", confirmations[^1].Text);
                await WaitUntil(() => File.ReadAllText(settingsPath).Contains("\"hidden\": true"), "hidden marker written");
                await WaitUntil(() => vm.Settings.ConfiguredList.All(i => i.Id != "deepseek"), "env provider left the list");
                await WaitUntil(() => vm.Conversation.Picker.AllModels.All(m => m.Provider != "deepseek"), "env provider models removed", 15_000);
                // 其它厂商不受影响
                Assert.Contains(vm.Settings.ConfiguredList, i => i.Id == "openai");
                HeadlessApp.Save(window, "settings-provider-removed");

                // 重新添加：回到模板列表，保存后清除 hidden
                vm.Settings.OpenTemplatePickerCommand.Execute(null);
                vm.Settings.PickTemplateCommand.Execute(vm.Settings.TemplateGroups.SelectMany(g => g.Items).First(t => t.Id == "deepseek"));
                await Settle();
                var readded = vm.Settings.Selected!;
                Assert.True(readded.IsPureDraft);
                readded.KeyInput = "sk-readded";
                readded.SaveCommand.Execute(null);
                await WaitUntil(() => File.ReadAllText(settingsPath).Contains("sk-readded"), "re-added provider saved");
                Assert.DoesNotContain("hidden", File.ReadAllText(settingsPath));
                await WaitUntil(() => vm.Conversation.Picker.AllModels.Any(m => m.Provider == "deepseek"), "re-added models", 15_000);

                vm.CloseSettingsCommand.Execute(null);
                window.Close();
                return true;
            }, TestContext.Current.CancellationToken);
        }
        finally
        {
            Environment.SetEnvironmentVariable("DEEPSEEK_API_KEY", savedDeepSeek);
            provider.GetRequiredService<AgentHostConnection>().Dispose();
        }
    }

    /// <summary>
    /// NextCoWork：填入 Key 后通过 /v1/models 发现模型（图像模型被排除）；Claude 固定走 Messages，其余跟随厂商的 Responses。
    /// Base URL 指向本地服务，避免访问真实网关。
    /// </summary>
    [Fact]
    public async Task NextCoWork_DiscoversModels_ClaudeOnMessages_OthersOnResponses()
    {
        var home = Directory.CreateTempSubdirectory("tkcode-nextcowork-").FullName;
        var port = FreePort();
        File.WriteAllText(Path.Combine(home, "settings.json"),
            $$"""{ "providers": { "nextcowork": { "apiKey": "sk-cowork", "baseUrl": "http://127.0.0.1:{{port}}/v1" } } }""");
        using var server = new System.Net.HttpListener();
        server.Prefixes.Add($"http://127.0.0.1:{port}/");
        server.Start();
        string? authorization = null;
        _ = Task.Run(async () =>
        {
            while (server.IsListening)
            {
                var context = await server.GetContextAsync();
                var body = "{}";
                if (context.Request.Url!.AbsolutePath == "/v1/models")
                {
                    authorization = context.Request.Headers["Authorization"];
                    body = """
                        { "object": "list", "data": [ { "id": "claude-opus-5-5" }, { "id": "claude-sonnet-5-5" }, { "id": "gpt-6-sol" },
                            { "id": "deepseek-v4-pro" }, { "id": "gpt-image-2" }, { "id": "grok-imagine-image" } ] }
                        """;
                }
                var bytes = System.Text.Encoding.UTF8.GetBytes(body);
                context.Response.ContentType = "application/json";
                await context.Response.OutputStream.WriteAsync(bytes);
                context.Response.Close();
            }
        });

        await using var provider = StartApp(home, FreePort());
        try
        {
            await HeadlessApp.Session.Dispatch(async () =>
            {
                var vm = provider.GetRequiredService<MainWindowViewModel>();
                var window = new MainWindow { DataContext = vm, Width = 1100, Height = 760 };
                window.Show();
                await WaitUntil(() => vm.Conversation.Picker.AllModels.Any(m => m.Provider == "nextcowork"), "nextcowork models discovered");
                Assert.Equal("Bearer sk-cowork", authorization);
                Assert.DoesNotContain(vm.Conversation.Picker.AllModels, m => m.Id.StartsWith("gpt-image", StringComparison.Ordinal));
                Assert.DoesNotContain(vm.Conversation.Picker.AllModels, m => m.Id.StartsWith("grok-imagine", StringComparison.Ordinal));

                await vm.OpenSettingsCommand.ExecuteAsync(null);
                vm.Settings.NavigateCommand.Execute(vm.Settings.Nav[1]);
                await vm.Settings.RefreshProvidersAsync();
                await Settle();
                Assert.Equal("nextcowork", vm.Settings.Selected?.Id);
                var detail = vm.Settings.Selected!;
                Assert.True(detail.IsBuiltIn);
                var models = detail.Models.ToDictionary(m => m.Id);
                Assert.Equal(["claude-opus-5-5", "claude-sonnet-5-5", "deepseek-v4-pro", "gpt-6-sol"], models.Keys.Order());
                Assert.True(models["claude-opus-5-5"].ApiOverridden);
                Assert.Equal("messages", models["claude-opus-5-5"].Model.Api);
                Assert.False(models["gpt-6-sol"].ApiOverridden);
                Assert.Equal("responses", models["gpt-6-sol"].Model.Api);
                HeadlessApp.Save(window, "settings-nextcowork");

                // 编辑 Claude 模型：“跟随厂商”指向它自己的默认协议（Messages），而不是厂商的 Responses
                detail.EditModelCommand.Execute(models["claude-opus-5-5"]);
                Assert.Equal("", detail.Editing!.ApiInput);
                Assert.Contains(ProviderModelItemViewModel.ProtocolLabel("messages"), detail.Editing.ProviderApiText);
                HeadlessApp.Save(window, "settings-nextcowork-edit");

                vm.CloseSettingsCommand.Execute(null);
                window.Close();
                return true;
            }, TestContext.Current.CancellationToken);
        }
        finally
        {
            server.Stop();
            provider.GetRequiredService<AgentHostConnection>().Dispose();
        }
    }

    /// <summary>models.dev 格式的最小目录：Anthropic 的 Opus 5.5 与 DeepSeek。</summary>
    private const string ModelsDevFixture = """
    {
      "anthropic": { "id": "anthropic", "name": "Anthropic", "npm": "@ai-sdk/anthropic", "env": ["ANTHROPIC_API_KEY"],
        "models": { "claude-opus-5-5": { "id": "claude-opus-5-5", "name": "Claude Opus 5.5 (synced)", "tool_call": true, "reasoning": true,
          "limit": { "context": 1000000, "output": 128000 }, "modalities": { "input": ["text", "image"], "output": ["text"] } } } },
      "deepseek": { "id": "deepseek", "name": "DeepSeek", "npm": "@ai-sdk/openai-compatible", "api": "https://api.deepseek.com", "env": ["DEEPSEEK_API_KEY"],
        "models": { "deepseek-v4-pro": { "id": "deepseek-v4-pro", "name": "DeepSeek V4 Pro", "tool_call": true, "limit": { "context": 1000000, "output": 393216 } } } }
    }
    """;

    private static int FreePort()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
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
