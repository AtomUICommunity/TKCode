using Avalonia;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using StreamMark.Avalonia.Rendering;
using StreamMark.Avalonia.Transcript;
using TKCode.Desktop.Controls;
using TKCode.Desktop.Controls.Composer;
using TKCode.Desktop.Previews;
using TKCode.Desktop.Services;
using TKCode.Desktop.Updates;
using TKCode.Desktop.ViewModels;
using TKCode.Desktop.Views;
using TKCode.Terminal.Core;

namespace TKCode.Desktop;

/// <summary>
/// 冒烟模式（<c>TKCode --smoke [--out=frame.png]</c>）：在发布后的二进制（含 NativeAOT）里以无头平台运行真实的应用代码——
/// 启动 Agent Host、用 Fake Provider 发送一条消息、等待 Agent 循环与流式 Markdown 渲染完成、展开思考过程，
/// 检查会话视图的结果并截图。不产生任何系统级输入，用于验证只在发布构建中才会出现的问题。
/// </summary>
internal static class SmokeMode
{
    public static int Run(string[] args)
    {
        var failures = 0;
        void Check(string name, bool ok, string? detail = null)
        {
            Console.WriteLine($"{(ok ? "PASS" : "FAIL")} {name}{(detail is null ? "" : " — " + detail)}");
            if (!ok)
            {
                failures++;
            }
        }

        var home = Directory.CreateTempSubdirectory("tkcode-smoke-home-").FullName;
        var project = Directory.CreateTempSubdirectory("tkcode-smoke-project-").FullName;
        File.WriteAllText(Path.Combine(project, "README.md"), "# smoke\n\nA **sample** project with `code`.\n");
        Directory.CreateDirectory(Path.Combine(project, ".agents", "commands"));
        File.WriteAllText(Path.Combine(project, ".agents", "commands", "hello.md"), "---\ndescription: Say hello\n---\nSay hello.\n");
        Directory.CreateDirectory(Path.Combine(project, ".agents", "skills", "greet"));
        File.WriteAllText(Path.Combine(project, ".agents", "skills", "greet", "SKILL.md"), "---\nname: greet\ndescription: Greeting rules\n---\nBe brief.\n");
        Environment.SetEnvironmentVariable("TKCODE_HOME", home);
        Environment.SetEnvironmentVariable("TKCODE_FAKE_PROVIDER", "1");
        Environment.SetEnvironmentVariable("TKCODE_EXTERNAL_RUNTIMES", "0");
        var state = new StateService(Path.Combine(home, "state.json"));
        state.Current.Workspaces.Add(project);
        state.Save();

        // 更新源：进程内的假 GitHub（临时签名密钥），不访问网络（必须在创建 UpdateService 之前设置环境变量）
        using var updateFeed = SmokeUpdateFeed.Start(home);

        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        Program.ConfigureServices(services);
        using var provider = services.BuildServiceProvider();
        App.Services = provider;

        try
        {
            AppBuilder.Configure<App>()
                .UseSkia()
                .UseHarfBuzz()
                .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
                .UseTKCodeArc()
                .SetupWithoutStarting();

            var vm = provider.GetRequiredService<MainWindowViewModel>();
            var window = new MainWindow { DataContext = vm, Width = 1100, Height = 720 };
            window.Show();

            Check("models.loaded", WaitUntil(() => vm.Conversation.Picker.Selected is not null), vm.Conversation.Picker.Selected?.ToString());

            vm.Conversation.ComposerText = "看看目录里有哪些文件";
            vm.Conversation.SendCommand.Execute(null);
            var finished = WaitUntil(() => vm.Conversation.Active is { IsRunning: false, Items.Count: >= 2 });
            Check("conversation.finished", finished, $"{vm.Conversation.Active?.Items.Count ?? 0} items");

            var assistant = vm.Conversation.Active?.Items.OfType<AssistantMessageItemViewModel>().FirstOrDefault();
            var texts = assistant?.Blocks.OfType<TextBlockViewModel>().ToList() ?? [];
            Check("reply.text", texts.Any(t => t.Text.Length > 0), $"{texts.Count} text blocks");

            // 平滑输出放完积压后，流式文档应进入最终状态
            Check("markdown.complete", WaitUntil(() => texts.Count > 0 && texts.All(t => t.Stream?.Document.Latest.IsComplete ?? true)), null);

            // 展开思考过程：之后的行必须被推下去，不能重叠
            assistant?.Blocks.OfType<ThinkingBlockViewModel>().FirstOrDefault()?.ToggleCommand.Execute(null);
            Pump(20);
            var transcript = window.GetVisualDescendants().OfType<TranscriptView>().FirstOrDefault();
            Check("transcript.view", transcript is not null, null);
            if (transcript is not null)
            {
                var panel = transcript.Panel;
                Check("transcript.rows", panel.RowCount >= 3 && panel.RealizedCount > 0, $"{panel.RealizedCount}/{panel.RowCount} rows realized");
                var rows = panel.Children.Where(c => c.IsVisible).OrderBy(c => c.Bounds.Top).ToList();
                var overlaps = rows.Zip(rows.Skip(1)).Count(p => p.Second.Bounds.Top < p.First.Bounds.Bottom - 0.5);
                Check("transcript.no-overlap", overlaps == 0, $"{overlaps} overlapping rows");
                Check("markdown.rendered", window.GetVisualDescendants().OfType<TextSegmentView>().Any(s => s.Layout is not null), null);
            }

            CheckFilePreviews(Check, vm, window, project);
            CheckExplorerUi(Check, vm, window, project);
            CheckComposer(Check, window);
            CheckDrafts(Check);

            // @ 文件：Host 的文件索引（rg 或内置遍历）与 workspace/searchFiles 在 AOT 下可用
            var search = Task.Run(async () =>
            {
                var server = await provider.GetRequiredService<AgentHostConnection>().GetServerAsync();
                return await server.SearchFilesAsync(new TKCode.Rpc.FileSearchRequest(project, "readme", 10));
            });
            // command / skill / role：frontmatter 解析与 assets/list 在 AOT 下可用
            var assets = Task.Run(async () =>
            {
                var server = await provider.GetRequiredService<AgentHostConnection>().GetServerAsync();
                return await server.ListAssetsAsync(project);
            });
            Check("assets.list", WaitUntil(() => assets.IsCompleted) && assets.Result.Commands.Any(c => c.Name == "hello" && c.Description == "Say hello"),
                assets.IsCompletedSuccessfully ? string.Join(", ", assets.Result.Commands.Select(c => c.Name)) : assets.Exception?.GetBaseException().Message);
            // command：发送时 Host 展开 .md 正文；用户消息的显示片段经会话记录（AiJsonContext）保存
            vm.Conversation.Composer.SetContent([new TagSegment(TagKind.Command, "hello", "hello"), new TextSegment(" world "),
                new TagSegment(TagKind.Skill, "greet", "greet")]);
            vm.Conversation.SendCommand.Execute(null);
            var commandTurn = WaitUntil(() => vm.Conversation.Active is { IsRunning: false } a && a.Items.OfType<UserMessageItemViewModel>().Count() == 2);
            var commandMessage = vm.Conversation.Active?.Items.OfType<UserMessageItemViewModel>().LastOrDefault();
            Check("command.expanded", commandTurn && commandMessage is { HasSegments: true } &&
                commandMessage.Text.Contains("<skill name=\"greet\"", StringComparison.Ordinal) && commandMessage.Text.EndsWith("Say hello.\n\nworld /greet", StringComparison.Ordinal),
                commandMessage?.Text);
            // 压缩上下文：/compact 经 RPC 到 Host 的压缩器（会话记录的压缩条目、事件与分隔卡片在 AOT 下可用）
            vm.Conversation.ComposerText = "/compact keep file names";
            vm.Conversation.SendCommand.Execute(null);
            var compacted = WaitUntil(() => vm.Conversation.Active?.Items.OfType<CompactionItemViewModel>().Any(c => c is { IsRunning: false, HasSummary: true }) == true);
            var card = vm.Conversation.Active?.Items.OfType<CompactionItemViewModel>().LastOrDefault();
            Check("compaction.card", compacted && card is { IsFailed: false }, card?.Header);
            card?.ToggleCommand.Execute(null);
            Pump(10);
            Check("compaction.rendered", window.GetVisualDescendants().Any(v => v.DataContext is CompactionItemViewModel), null);
            // 模式：Host 解析 role、过滤工具并写入会话记录
            vm.Conversation.RolePicker.Choose("plan");
            var planApplied = WaitUntil(() => vm.Conversation.Active?.Options.Role == "plan");
            Check("role.plan", planApplied, vm.Conversation.Active?.Options.Role);
            // 提问：plan 模式也提供 ask_user；问答面板经 input/request 出现，回答后转录里留下问答卡片（docs/design/ask-user.md）
            vm.Conversation.ComposerText = """mcp:ask_user {"questions":[{"question":"Which DB?","header":"Database","options":[{"label":"Postgres","description":"Full"},{"label":"SQLite","description":"Small"}]}]}""";
            vm.Conversation.SendCommand.Execute(null);
            var asked = WaitUntil(() => vm.Conversation.IsAnsweringQuestion);
            var question = vm.Conversation.Active?.CurrentQuestion;
            Pump(5);
            Check("question.panel", asked && window.GetVisualDescendants().OfType<Views.QuestionPanelView>().Any(p => p.IsEffectivelyVisible), question?.Title);
            if (question is not null)
            {
                question.ChooseCommand.Execute(question.Pages[0].Options[1]);
            }
            var answered = WaitUntil(() => vm.Conversation.Active is { IsRunning: false } && !vm.Conversation.IsAnsweringQuestion
                && window.GetVisualDescendants().Any(v => v.DataContext is QuestionCardViewModel { Status: "answered" }));
            Check("question.card", answered, window.GetVisualDescendants().Select(v => v.DataContext).OfType<QuestionCardViewModel>().FirstOrDefault()?.Lines
                .FirstOrDefault()?.Answer);
            // 计划审批（docs/design/plan-mode.md）：Plan 模式下写计划文件 → exit_plan_mode → plan/review 审批面板 →
            // 批准“自动执行”后 Host 推送 optionsChanged 切回 Agent，转录里留下计划卡片
            var planPath = vm.Conversation.Active is { } planSession
                ? Path.Combine(home, "plans", Path.GetFileNameWithoutExtension(planSession.FilePath) + ".md")
                : "";
            vm.Conversation.ComposerText = $$"""mcp:write {"path":"{{System.Text.Json.JsonEncodedText.Encode(planPath)}}","content":"## Plan\n\n- Step one\n- Step two\n"}""";
            vm.Conversation.SendCommand.Execute(null);
            var planWritten = WaitUntil(() => vm.Conversation.Active is { IsRunning: false } && File.Exists(planPath));
            vm.Conversation.ComposerText = "mcp:exit_plan_mode {}";
            vm.Conversation.SendCommand.Execute(null);
            var reviewing = WaitUntil(() => vm.Conversation.IsReviewingPlan);
            Pump(5);
            var review = vm.Conversation.Active?.CurrentPlanReview;
            Check("plan.panel", planWritten && reviewing && window.GetVisualDescendants().OfType<Views.PlanReviewPanelView>().Any(p => p.IsEffectivelyVisible),
                review?.Title);
            if (review?.Options.FirstOrDefault(o => o.Choice == "auto") is { } auto)
            {
                review.Select(auto);
                review.Confirm();
            }
            var planApproved = WaitUntil(() => vm.Conversation.Active is { IsRunning: false, Options.Role: "agent" } && vm.Conversation.RolePicker.Role == "agent"
                && window.GetVisualDescendants().Any(v => v.DataContext is PlanCardViewModel { Status: "approved" }));
            Check("plan.approved", planApproved, $"{vm.Conversation.Active?.Options.Role} / {vm.Conversation.Active?.Options.PermissionMode}");
            Check("files.search", WaitUntil(() => search.IsCompleted) && search.Result.Items.FirstOrDefault()?.Path == "README.md",
                search.IsCompletedSuccessfully ? string.Join(", ", search.Result.Items.Select(i => i.Path)) : search.Exception?.GetBaseException().Message);
            CheckTerminal(Check, vm, project);
            CheckUpdates(Check, vm, window, provider, updateFeed, home);

            using var frame = window.CaptureRenderedFrame();
            Check("ui.frame", frame is not null, null);
            var output = args.FirstOrDefault(a => a.StartsWith("--out=", StringComparison.Ordinal))?["--out=".Length..];
            if (frame is not null && output is not null)
            {
                using var file = File.Create(output);
                frame.Save(file, new PngBitmapEncoderOptions());
                Console.WriteLine($"saved {output}");
            }
            window.Close();
        }
        catch (Exception ex)
        {
            Check("smoke", false, ex.ToString());
        }

        Console.WriteLine(failures == 0 ? "SMOKE OK" : $"SMOKE FAILED ({failures})");
        return failures == 0 ? 0 : 1;
    }

    // 输入框：tag 排版与绘制、剪贴板内部格式（源生成 JSON）在 AOT 下都要可用
    private static void CheckComposer(Action<string, bool, string?> check, MainWindow window)
    {
        var editor = window.GetVisualDescendants().OfType<ComposerEditor>().FirstOrDefault(e => e.IsEffectivelyVisible);
        check("composer.editor", editor is not null, null);
        if (editor is null)
        {
            return;
        }
        var tag = new TagSegment(TagKind.File, "README.md", "README.md");
        editor.Document.SetContent([new TextSegment("看下 "), tag, new TextSegment(" 的内容")]);
        Pump(5);
        check("composer.tag-width", editor.CaretBounds(4).X - editor.CaretBounds(3).X > 20, null);
        var json = ComposerClipboard.Serialize(editor.Document.Segments);
        var restored = ComposerClipboard.Deserialize(json);
        check("composer.clipboard-json", restored is not null && restored.SequenceEqual(editor.Document.Segments), json);
        editor.Document.SetContent([]);
    }

    // 输入框草稿：落盘（源生成 JSON）与读回、↑ 历史消息还原成草稿，在 AOT 下都要可用（docs/design/composer-drafts.md）
    private static void CheckDrafts(Action<string, bool, string?> check)
    {
        var dir = Path.Combine(Path.GetTempPath(), "tkcode-smoke-drafts-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var now = DateTimeOffset.UtcNow;
            var draft = new ComposerDraft(
                [new TextSegment("看下 "), new TagSegment(TagKind.File, "README.md", "README.md")], [], 1,
                new McpPromptSnapshot("srv", "review", null, new Dictionary<string, string> { ["file"] = "a.cs" }), now);
            var store = new DraftStore(dir);
            store.Set("smoke", draft);
            store.FlushAsync().GetAwaiter().GetResult();
            var restarted = new DraftStore(dir);
            // 主线程带着 UI 同步上下文：直接 GetResult() 会让 LoadAsync 的延续投回这个被阻塞的线程而死锁，所以放到线程池上跑
            var loaded = Task.Run(() => restarted.LoadAsync()).GetAwaiter().GetResult();
            var restored = restarted.Get("smoke");
            check("drafts.roundtrip", loaded == 1 && restored is { McpPrompt: { Name: "review" } } &&
                restored.Segments.SequenceEqual(draft.Segments) && restored.McpPrompt.Arguments["file"] == "a.cs", File.Exists(Path.Combine(dir, "smoke.json")) ? null : "no file");

            var message = new UserMessageItemViewModel("展开后的正文", null, ComposerSegments.ToDto(draft.Segments));
            var recalled = InputHistory.ToDraft(message, now);
            check("drafts.history", recalled.Segments.SequenceEqual(draft.Segments), null);
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    // Exercise compiled explorer popup/dialog resources in NativeAOT, without executing file actions or native trash.
    private static void CheckExplorerUi(Action<string, bool, string?> check, MainWindowViewModel vm, MainWindow window, string project)
    {
        var wasOpen = vm.IsExplorerOpen;
        var motionDisabled = Motion.MotionSettings.ForceDisabled;
        Motion.MotionSettings.ForceDisabled = true;
        try
        {
            vm.IsExplorerOpen = true;
            Pump(4);
            var tree = window.GetVisualDescendants().OfType<FileExplorerTree>().Single();
            var target = vm.Explorer.CaptureContext(null);
            check("explorer.root", target is not null, vm.Explorer.Root);
            if (target is not null)
            {
                using var menu = new ExplorerContextMenu(vm.Explorer, window, error =>
                {
                    check("explorer.menu-action", false, error.Message);
                    return Task.CompletedTask;
                });
                menu.Show(target, tree, new Point(12, 12), tree);
                Pump(3);
                var surface = window.GetVisualDescendants().OfType<TKCode.Arc.MenuSurface>().FirstOrDefault();
                check("explorer.scaled-menu", surface is { IsOpen: false } && surface.GetVisualAncestors().OfType<ZoomHost>().Any(), null);
                menu.Close();
            }
            foreach (var action in new[] { ExplorerAction.Rename, ExplorerAction.Trash })
            {
                var completion = ExplorerFileDialog.ShowAsync(window, new ExplorerFileDialogRequest(
                    action, Path.Combine(project, "README.md"), false, "README.md",
                    static _ => Task.FromException(new InvalidOperationException("Smoke must not submit file actions."))));
                Pump(3);
                var dialog = window.GetVisualDescendants().OfType<ExplorerFileDialog>().Single();
                var cancel = dialog.GetVisualDescendants().OfType<TKCode.Arc.Button>().Single(button => button.Name == "CancelButton");
                var input = dialog.GetVisualDescendants().OfType<TKCode.Arc.Input>().FirstOrDefault();
                check(action == ExplorerAction.Rename ? "explorer.rename-input" : "explorer.trash-cancel-focus",
                    action == ExplorerAction.Rename ? input is { IsKeyboardFocusWithin: true, Text: "README.md" } : cancel.IsKeyboardFocusWithin, null);
                cancel.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
                check("explorer.dialog-closed", WaitUntil(() => completion.IsCompleted), null);
            }
        }
        finally
        {
            vm.IsExplorerOpen = wasOpen;
            Motion.MotionSettings.ForceDisabled = motionDisabled;
        }
    }

    // 文件预览：图片（含动图解码与 SVG 栅格化）与源码着色在 AOT 下都要可用
    private static void CheckFilePreviews(Action<string, bool, string?> check, MainWindowViewModel vm, MainWindow window, string project)
    {
        using (var bitmap = new SkiaSharp.SKBitmap(32, 16))
        {
            bitmap.Erase(new SkiaSharp.SKColor(0x4F, 0x8C, 0xFF, 0x80));
            using var encoded = bitmap.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
            File.WriteAllBytes(Path.Combine(project, "logo.png"), encoded.ToArray());
        }
        // 两帧 2×2 动图
        File.WriteAllBytes(Path.Combine(project, "anim.gif"), Convert.FromBase64String(
            "R0lGODlhAgACAIEAAAAAAP8AAAD/AAAA/yH/C05FVFNDQVBFMi4wAwEAAAAh+QQACgAAACwAAAAAAgACAAACBAzFMQUAIfkEAAoAAAAsAAAAAAIAAgAAAgQcQ3EFADs="));
        File.WriteAllText(Path.Combine(project, "icon.svg"), """<svg xmlns="http://www.w3.org/2000/svg" width="24" height="24"><rect width="24" height="12" fill="#4F8CFF"/></svg>""");
        File.WriteAllText(Path.Combine(project, "data.json"), "{\n  \"name\": \"smoke\",\n  \"ok\": true\n}\n");

        FilePreviewViewModel? Open(string name)
        {
            vm.Workbench.OpenFile(Path.Combine(project, name));
            WaitUntil(() => vm.Workbench.ActiveFile is { } file && (file.Preview is not null || file.HasNotice));
            return vm.Workbench.ActiveFile?.Preview;
        }

        var png = Open("logo.png") as ImagePreviewViewModel;
        check("preview.png", png is { PixelSize.Width: 32, PixelSize.Height: 16 }, vm.Workbench.ActiveFile?.Notice ?? png?.Info);
        var gif = Open("anim.gif") as ImagePreviewViewModel;
        check("preview.gif", gif is { IsAnimated: true, Frames.Count: 2 }, vm.Workbench.ActiveFile?.Notice ?? gif?.Info);
        var svg = Open("icon.svg") as ImagePreviewViewModel;
        check("preview.svg", svg is { IsVector: true, PixelSize.Width: 24 } && vm.Workbench.ActiveFile!.HasViewSwitch, vm.Workbench.ActiveFile?.Notice ?? svg?.Info);
        var json = Open("data.json") as CodePreviewViewModel;
        var highlighted = WaitUntil(() => window.GetVisualDescendants().OfType<CodeView>().FirstOrDefault() is { IsHighlighted: true });
        check("preview.code", json is { Language: "json" } && highlighted, vm.Workbench.ActiveFile?.Notice);
    }

    // 终端：发布的 pty host（ptyhost/）以 AOT 运行，经 READY 握手、建会话，shell 的输出进入 UI 侧核心并发布为帧。
    // 不发送任何输入：只等 shell 自己输出（提示符）。
    private static void CheckTerminal(Action<string, bool, string?> check, MainWindowViewModel vm, string project)
    {
        vm.Workbench.Root = project;
        var tool = vm.Workbench.Tools.First(t => t.Kind == WorkbenchTabKind.Terminal);
        vm.Workbench.AddToolCommand.Execute(tool);
        var tab = vm.Workbench.ActiveTab as TerminalTabViewModel;
        var produced = WaitUntil(() => tab?.Frame is { } frame && frame.Rows.Any(r => r.Cells.Any(c => TCell.Codepoint(c) is not (0 or ' '))), 30_000);
        check("terminal.output", tab is not null && produced, tab?.Status);
        if (tab is not null)
        {
            // 关闭会话：冒烟结束后不留下孤儿进程。必须在 UI 线程上发起（关闭会移除标签，改动 UI 绑定的属性）
            var closed = false;
            var done = false;
            Dispatcher.UIThread.Post(async () =>
            {
                closed = await tab.CloseAsync(force: true);
                done = true;
            });
            WaitUntil(() => done);
            check("terminal.close", closed, tab.Status);
        }
    }

    // 应用内更新：Release 列表与清单（源生成 JSON）、ECDSA 验签、下载与 SHA-256 校验、StreamMark 渲染更新说明，在 AOT 下都要可用。
    // 只下载不安装：下载用单独的服务实例并指定 Windows 安装方式（没有平台校验，也不会启动安装程序）
    private static void CheckUpdates(Action<string, bool, string?> check, MainWindowViewModel vm, MainWindow window, IServiceProvider provider,
        SmokeUpdateFeed feed, string home)
    {
        var updates = vm.Updates;
        updates.CheckNowCommand.Execute(null);
        check("update.check", WaitUntil(() => updates.Service.Status == UpdateStatus.Available) && updates.VersionText == SmokeUpdateFeed.Version,
            updates.StatusText);
        updates.ShowNotesCommand.Execute(null);
        var notes = WaitUntil(() => vm.IsSettingsOpen && vm.Settings.IsAbout &&
                                    window.GetVisualDescendants().OfType<StreamMark.Avalonia.MarkdownView>().Any(m => m.IsEffectivelyVisible && m.Bounds.Height > 20));
        check("update.notes", notes, updates.Notes);
        vm.CloseSettingsCommand.Execute(null);

        using var service = new UpdateService(provider.GetRequiredService<StateService>(), provider.GetRequiredService<UserSettingsService>(),
            provider.GetRequiredService<ILogger<UpdateService>>(), new UpdateServiceOptions
            {
                Target = new InstallTarget.WindowsInstaller(home),
                UpdatesDirectory = Path.Combine(home, "updates-download"),
                FeedOverride = feed.Url,
                PublicKeys = [feed.PublicKey],
            });
        var checking = service.CheckAsync(manual: true);
        WaitUntil(() => checking.IsCompleted);
        var downloading = service.DownloadAsync();
        var ready = WaitUntil(() => downloading.IsCompleted) && service.Status == UpdateStatus.Ready;
        var installer = Path.Combine(home, "updates-download", SmokeUpdateFeed.Version, feed.InstallerName);
        check("update.download", ready && File.Exists(installer) && File.ReadAllBytes(installer).SequenceEqual(feed.Installer), service.Error ?? service.Status.ToString());
    }

    private static bool WaitUntil(Func<bool> condition, int timeoutMs = 30_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                return false;
            }
            Pump(1);
        }
        Pump(5);
        return true;
    }

    private static void Pump(int rounds)
    {
        for (var i = 0; i < rounds; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Thread.Sleep(16);
        }
    }
}

/// <summary>冒烟模式的更新源：本机 HttpListener 扮演 GitHub（releases.json、清单与签名、安装程序），用临时生成的密钥签名。</summary>
internal sealed class SmokeUpdateFeed : IDisposable
{
    public const string Version = "99.0.0";
    private readonly System.Net.HttpListener _listener;

    private SmokeUpdateFeed(System.Net.HttpListener listener, string url, string publicKey, string installerName, byte[] installer)
    {
        _listener = listener;
        Url = url;
        PublicKey = publicKey;
        InstallerName = installerName;
        Installer = installer;
    }

    public string Url { get; }
    public string PublicKey { get; }
    public string InstallerName { get; }
    public byte[] Installer { get; }

    public static SmokeUpdateFeed Start(string home)
    {
        using var key = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
        var publicKey = key.ExportSubjectPublicKeyInfoPem();
        var installer = System.Text.Encoding.UTF8.GetBytes(new string('x', 200_000));
        var installerName = $"TKCode-{Version}-{AppVersionInfo.Rid}.bin";
        var manifest = System.Text.Encoding.UTF8.GetBytes(new System.Text.Json.Nodes.JsonObject
        {
            ["schema"] = 1,
            ["version"] = Version,
            ["channel"] = "stable",
            ["assets"] = new System.Text.Json.Nodes.JsonObject
            {
                [AppVersionInfo.Rid] = new System.Text.Json.Nodes.JsonObject
                {
                    ["name"] = installerName,
                    ["size"] = installer.Length,
                    ["sha256"] = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(installer)),
                },
            },
        }.ToJsonString());
        var signature = key.SignData(manifest, System.Security.Cryptography.HashAlgorithmName.SHA256, System.Security.Cryptography.DSASignatureFormat.Rfc3279DerSequence);

        var port = FreePort();
        var url = $"http://127.0.0.1:{port}";
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            [$"/download/{installerName}"] = installer,
            ["/download/manifest.json"] = manifest,
            ["/download/manifest.json.sig"] = signature,
        };
        var assets = new System.Text.Json.Nodes.JsonArray();
        foreach (var (path, data) in files)
        {
            // 非泛型的 Add(JsonNode)：泛型重载需要运行时生成代码（IL3050）
            assets.Add((System.Text.Json.Nodes.JsonNode)new System.Text.Json.Nodes.JsonObject { ["name"] = path["/download/".Length..], ["size"] = data.Length, ["browser_download_url"] = url + path });
        }
        files["/releases.json"] = System.Text.Encoding.UTF8.GetBytes(new System.Text.Json.Nodes.JsonArray(new System.Text.Json.Nodes.JsonObject
        {
            ["tag_name"] = "v" + Version,
            ["draft"] = false,
            ["prerelease"] = false,
            ["body"] = "## Smoke\n\n- **signed** manifest\n- `sha256` checked download\n",
            ["html_url"] = url + "/release",
            ["assets"] = assets,
        }).ToJsonString());

        var listener = new System.Net.HttpListener();
        listener.Prefixes.Add(url + "/");
        listener.Start();
        _ = Task.Run(async () =>
        {
            while (listener.IsListening)
            {
                System.Net.HttpListenerContext context;
                try
                {
                    context = await listener.GetContextAsync();
                }
                catch (Exception)
                {
                    return;
                }
                if (files.TryGetValue(context.Request.Url!.AbsolutePath, out var body))
                {
                    await context.Response.OutputStream.WriteAsync(body);
                }
                else
                {
                    context.Response.StatusCode = 404;
                }
                context.Response.Close();
            }
        });

        var keyFile = Path.Combine(home, "smoke-update-key.pem");
        File.WriteAllText(keyFile, publicKey);
        Environment.SetEnvironmentVariable("TKCODE_UPDATE_FEED", url);
        Environment.SetEnvironmentVariable("TKCODE_UPDATE_KEY", keyFile);
        return new SmokeUpdateFeed(listener, url, publicKey, installerName, installer);
    }

    private static int FreePort()
    {
        var socket = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        socket.Start();
        var port = ((System.Net.IPEndPoint)socket.LocalEndpoint).Port;
        socket.Stop();
        return port;
    }

    public void Dispose()
    {
        _listener.Stop();
        _listener.Close();
    }
}
