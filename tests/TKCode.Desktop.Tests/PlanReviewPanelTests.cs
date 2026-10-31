using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using StreamMark.Avalonia.Extensions;
using StreamMark.Avalonia.Transcript;
using TKCode.Arc;
using TKCode.Arc.Motion;
using TKCode.Desktop.Controls;
using TKCode.Desktop.Services;
using TKCode.Desktop.ViewModels;
using TKCode.Desktop.Views;
using ToolCallStatus = TKCode.Desktop.ViewModels.ToolCallStatus;
using TKCode.Rpc;
using Xunit;

namespace TKCode.Desktop.Tests;

/// <summary>
/// Plan 模式的界面（docs/design/plan-mode.md §14）：真实 Host + Fake 模型（“mcp:&lt;工具&gt; {…}” 触发调用）。
/// enter_plan_mode 的确认用本地化的问答面板；exit_plan_mode 的审批面板替换输入框，键盘编辑、退回、暂不批准、停止、批准；
/// 批准后选择器随 optionsChanged 更新；“清空上下文”留下分隔线；重新打开会话后计划卡片仍在。
/// </summary>
public sealed class PlanReviewPanelTests
{
    private const string PlanText = """
        ## Context
        Opening settings is slow because every provider is queried on open.

        ## Approach
        Cache provider status for 30 seconds and refresh it in the background.

        ## Changes
        - `SettingsViewModel.cs`: read from the cache first
        - `ProviderStatusCache.cs`: new, keeps the last result per provider

        ## Verification
        - `dotnet test tests/TKCode.Desktop.Tests`
        - Open settings twice; the second open is instant
        """;

    [Fact]
    public async Task PlanMode_EndToEnd_EnterReviseDismissStopApproveAndClear()
    {
        var home = Directory.CreateTempSubdirectory("tkcode-plan-home-").FullName;
        var project = Directory.CreateTempSubdirectory("tkcode-plan-project-").FullName;
        Environment.SetEnvironmentVariable("TKCODE_HOME", home);
        Environment.SetEnvironmentVariable("TKCODE_FAKE_PROVIDER", "1");
        Environment.SetEnvironmentVariable("TKCODE_EXTERNAL_RUNTIMES", "0");
        Environment.SetEnvironmentVariable("TKCODE_AGENTHOST_DIR", Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "../../../../../src/TKCode.AgentHost/bin/Debug/net11.0")));
        var state = new StateService(Path.Combine(home, "state.json"));
        state.Current.Workspaces.Add(project);
        state.Current.LastWorkspace = project;
        state.Save();
        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        Program.ConfigureServices(services);
        await using var provider = services.BuildServiceProvider();

        await HeadlessApp.Session.Dispatch(async () =>
        {
            var vm = provider.GetRequiredService<MainWindowViewModel>();
            var window = new MainWindow { DataContext = vm, Width = 1100, Height = 860 };
            window.Show();
            await WaitUntil(() => vm.Conversation.Picker.Selected is not null, "models loaded");
            var conversation = vm.Conversation;
            var opened = new List<string>();
            conversation.OpenPlanFile = opened.Add;
            var panel = window.FindControl<PlanReviewPanelView>("PlanReviewPanel")!;
            var questionPanel = window.FindControl<QuestionPanelView>("QuestionPanel")!;
            var composer = window.FindControl<ComposerView>("DockedComposer")!;
            Assert.Equal(RolePickerViewModel.Agent, conversation.RolePicker.Role);

            // 1. Agent 模式下模型申请进入 Plan：问答面板显示本地化的标题与选项，回答仍是 Host 的选项原文
            await SendAsync(conversation, """mcp:enter_plan_mode {"reason":"The change touches the settings page and a new provider cache."}""");
            await WaitUntil(() => conversation.IsAnsweringQuestion, "enter plan question", 30_000);
            var session = conversation.Active!;
            var enter = session.CurrentQuestion!;
            Assert.True(enter.IsEnterPlanMode);
            Assert.Equal("进入 Plan 模式？", enter.Title);
            Assert.Equal(["先做计划（推荐）", "直接执行"], enter.Pages[0].Options.Select(o => o.Label));
            Assert.Equal("调研后写计划，你批准后再动手", enter.Pages[0].Options[0].Description);
            Assert.False(enter.Pages[0].AllowOther);
            await WaitUntil(() => questionPanel.IsKeyboardFocusWithin, "question panel focused");
            await SaveBothThemesAsync(window, "enter-plan-mode-question");
            Assert.Equal(["Plan first", "Proceed"], enter.Pages[0].Options.Select(o => o.Value));
            window.KeyPress(Key.D1, RawInputModifiers.None, PhysicalKey.Digit1, "1");
            // optionsChanged：模式芯片切到 Plan，不经 SetOptionsAsync
            await WaitUntil(() => !session.IsRunning && session.Options.Role == RolePickerViewModel.Plan
                && conversation.RolePicker.Role == RolePickerViewModel.Plan, "switched to Plan mode", 30_000);
            Assert.Contains("\"status\":\"accepted\"", Tool(session, "enter_plan_mode")!.DetailsJson);
            var enterCard = Cards<QuestionCardViewModel>(window).Single(c => c.IsEnterPlan);
            Assert.Equal("已进入 Plan 模式", enterCard.StatusText);
            Assert.Equal("The change touches the settings page and a new provider cache.", enterCard.Lines.Single().Question);

            // 2. 写计划文件（Plan 模式下只能写这个文件）
            var planPath = Path.Combine(home, "plans", Path.GetFileNameWithoutExtension(session.FilePath) + ".md");
            await SendAsync(conversation, "mcp:write " + new JsonObject { ["path"] = planPath, ["content"] = PlanText }.ToJsonString());
            await WaitUntil(() => !session.IsRunning && File.Exists(planPath) && Tool(session, "write")?.Status == ToolCallStatus.Succeeded, "plan written", 60_000);

            // 3. exit_plan_mode：面板替换输入框，发送不可用，草稿保留，侧边栏橙点亮起
            var review = await RequestReviewAsync(conversation, session);
            Assert.Equal("计划待批准", review.Title);
            Assert.Equal(Path.GetFileName(planPath), review.FileName);
            Assert.Equal(["clear", "auto", "ask", "acp"], review.Request.Choices);
            Assert.Equal(["清空上下文，批准并自动执行", "批准，自动执行", "批准，逐项审批", "批准，交给 ACP 执行（询问）", "继续完善"],
                review.Options.Select(o => o.Label));
            Assert.StartsWith("规划用掉 ", review.Options[0].Suffix);
            Assert.Equal(0, review.Highlight);
            Assert.True(panel.IsVisible);
            Assert.False(composer.IsVisible);
            Assert.True(conversation.Sessions.Single(s => s.SessionId == session.SessionId).IsWaitingApproval);
            conversation.ComposerText = "draft kept";
            Assert.False(conversation.SendCommand.CanExecute(null));
            await WaitUntil(() => panel.IsKeyboardFocusWithin, "panel focused");
            Assert.Equal("等待批准…", Cards<PlanCardViewModel>(window).Single().StatusText);
            await SaveBothThemesAsync(window, "plan-review-panel");

            // 4. 编辑：E 进入源码编辑框，输入；Esc 回到渲染视图并标为“已编辑”
            Press(window, Key.E, PhysicalKey.E);
            await WaitUntil(() => review.IsEditing && panel.FindControl<Control>("PlanEditor")!.IsKeyboardFocusWithin, "editor focused");
            window.KeyTextInput("Edited in the panel. ");
            await Settle();
            Assert.Contains("Edited in the panel. ", review.EditText);
            await SaveBothThemesAsync(window, "plan-review-panel-editing");
            Press(window, Key.Escape, PhysicalKey.Escape);
            await Settle();
            Assert.False(review.IsEditing);
            Assert.True(review.IsEdited);
            Assert.True(review.ShowEditedTag);
            Assert.False(review.IsDecided);

            // 5. 继续完善：数字键选到反馈框，输入；有编辑时第一次确认先提示会丢弃修改，再确认才退回
            PressDigit(window, review.Options.Count);
            await WaitUntil(() => review.IsReviseHighlighted && panel.FindControl<Control>("FeedbackInput")!.IsKeyboardFocusWithin, "feedback focused");
            window.KeyTextInput("Add a rollback step");
            await Settle();
            Assert.Equal("Add a rollback step", review.Feedback);
            Press(window, Key.Enter, PhysicalKey.Enter);
            await Settle();
            Assert.True(review.ShowDiscardWarning);
            Assert.False(review.IsDecided);
            Press(window, Key.Enter, PhysicalKey.Enter);
            await WaitUntil(() => !session.IsRunning && ExitCalls(session).Count == 1 && ExitCalls(session)[0].Status == ToolCallStatus.Succeeded, "revised", 30_000);
            var revised = ExitCalls(session)[0];
            Assert.Contains("Their feedback:", revised.Result);
            Assert.Contains("Add a rollback step", revised.Result);
            Assert.Contains("\"status\":\"revise\"", revised.DetailsJson);
            Assert.Equal(PlanText.Trim(), File.ReadAllText(planPath).Trim()); // 退回时不保存编辑
            Assert.True(composer.IsVisible);
            Assert.Equal("draft kept", conversation.ComposerText);
            Assert.False(conversation.Sessions.Single(s => s.SessionId == session.SessionId).IsWaitingApproval);
            var revisedCard = Cards<PlanCardViewModel>(window).Single(c => ReferenceEquals(c.Call, revised));
            Assert.Equal("已退回", revisedCard.StatusText);
            Assert.Equal("Add a rollback step", revisedCard.Feedback);

            // 6. Esc：暂不批准，模型收到“未批准，结束本轮”
            review = await RequestReviewAsync(conversation, session);
            await WaitUntil(() => panel.IsKeyboardFocusWithin, "panel focused again");
            Press(window, Key.Escape, PhysicalKey.Escape);
            await WaitUntil(() => !session.IsRunning && ExitCalls(session).Count == 2 && !ExitCalls(session)[1].IsBusy, "dismissed", 30_000);
            Assert.StartsWith("The user did not approve the plan yet and wants to keep planning.", ExitCalls(session)[1].Result);
            Assert.Contains("\"status\":\"dismissed\"", ExitCalls(session)[1].DetailsJson);

            // 7. 停止：Host 撤回请求，面板收起，输入框与草稿回来
            review = await RequestReviewAsync(conversation, session);
            conversation.ComposerText = "draft kept";
            await conversation.StopCommand.ExecuteAsync(null);
            await WaitUntil(() => !conversation.IsReviewingPlan && !session.IsRunning && ExitCalls(session).Count == 3 && !ExitCalls(session)[2].IsBusy,
                "withdrawn after stop", 30_000);
            Assert.True(review.IsDecided);
            Assert.True(composer.IsVisible);
            Assert.False(panel.IsVisible);
            Assert.Equal("draft kept", conversation.ComposerText);
            Assert.Empty(session.PendingPlanReviews);
            Assert.Equal("已中止", Cards<PlanCardViewModel>(window).Single(c => ReferenceEquals(c.Call, ExitCalls(session)[2])).StatusText);

            // 8. 编辑后批准“自动执行”：Host 写回计划、切到 Agent + 自动放行（optionsChanged），权限按项目记住
            review = await RequestReviewAsync(conversation, session);
            await WaitUntil(() => panel.IsKeyboardFocusWithin, "panel focused for approval");
            review.OpenInEditorCommand.Execute(null);
            Assert.Equal([planPath], opened);
            Press(window, Key.E, PhysicalKey.E);
            await WaitUntil(() => review.IsEditing && panel.FindControl<Control>("PlanEditor")!.IsKeyboardFocusWithin, "editor focused again");
            window.KeyTextInput("Edited in the panel. ");
            Press(window, Key.Escape, PhysicalKey.Escape);
            await Settle();
            PressDigit(window, review.Options.Single(o => o.Choice == "auto").Index + 1);
            await Settle();
            Assert.Equal("auto", review.Selected.Choice);
            Press(window, Key.Enter, PhysicalKey.Enter);
            await WaitUntil(() => !session.IsRunning && ExitCalls(session).Count == 4 && ExitCalls(session)[3].Status == ToolCallStatus.Succeeded
                && conversation.RolePicker.Role == RolePickerViewModel.Agent, "approved", 30_000);
            var approved = ExitCalls(session)[3];
            Assert.Contains("The user approved the plan. The user edited the plan before approving it", approved.Result);
            Assert.Contains("Edited in the panel.", approved.Result);
            Assert.Contains("\"choice\":\"auto\"", approved.DetailsJson);
            Assert.Contains("\"edited\":true", approved.DetailsJson);
            Assert.Contains("Edited in the panel.", File.ReadAllText(planPath));
            Assert.Equal(RolePickerViewModel.Agent, session.Options.Role);
            Assert.Equal(PermissionPickerViewModel.AutoApprove, session.Options.PermissionMode);
            Assert.Equal(PermissionPickerViewModel.AutoApprove, conversation.Permission.Mode);
            Assert.Equal(PermissionPickerViewModel.AutoApprove, provider.GetRequiredService<StateService>().Current.PermissionModes[
                provider.GetRequiredService<ProjectService>().FindByPath(project)!.Id]);
            var approvedCard = Cards<PlanCardViewModel>(window).Single(c => ReferenceEquals(c.Call, approved));
            Assert.Equal("已批准 · 自动执行", approvedCard.StatusText);
            Assert.True(approvedCard.IsEdited);
            await Settle();
            Assert.True(PlanClamp(window, approvedCard).IsOverflowing);
            HeadlessApp.Save(window, "plan-card-transcript");

            // 9. 再次进入 Plan，选“清空上下文”（默认选中）：历史替换，转录里留下“已清空上下文，按计划执行”的分隔线
            conversation.RolePicker.Choose(RolePickerViewModel.Plan);
            await WaitUntil(() => session.Options.Role == RolePickerViewModel.Plan, "back to Plan mode");
            review = await RequestReviewAsync(conversation, session);
            Assert.Equal("clear", review.Selected.Choice);
            await WaitUntil(() => panel.IsKeyboardFocusWithin, "panel focused for clear");
            Press(window, Key.Enter, PhysicalKey.Enter);
            await WaitUntil(() => !session.IsRunning && session.Items.OfType<CompactionItemViewModel>().Any(c => c is { IsHandoff: true, IsRunning: false, HasSummary: true }),
                "handoff divider", 30_000);
            var divider = session.Items.OfType<CompactionItemViewModel>().Single(c => c.IsHandoff);
            Assert.Equal("已清空上下文，按计划执行", divider.Header);
            Assert.False(divider.IsFailed);
            Assert.Contains("The user approved the plan below in Plan mode and cleared the earlier conversation.", divider.Summary);
            Assert.Equal(RolePickerViewModel.Agent, conversation.RolePicker.Role);
            Assert.Contains("\"choice\":\"clear\"", ExitCalls(session)[4].DetailsJson);
            Assert.Equal("已批准 · 清空上下文后执行", Cards<PlanCardViewModel>(window).Single(c => ReferenceEquals(c.Call, ExitCalls(session)[4])).StatusText);
            await SaveBothThemesAsync(window, "plan-handoff-divider");

            // 10. 重新打开会话（Host 从会话文件载入）：计划卡片与分隔线仍在
            var server = await provider.GetRequiredService<AgentHostConnection>().GetServerAsync();
            await server.CloseSessionAsync(session.SessionId);
            var item = conversation.Sessions.Single(s => s.SessionId == session.SessionId);
            item.Live = null;
            conversation.NewChat();
            await conversation.OpenSessionCommand.ExecuteAsync(item);
            var reopened = conversation.Active!;
            Assert.NotSame(session, reopened);
            await Settle();
            Assert.Contains(reopened.Items.OfType<CompactionItemViewModel>(), c => c is { IsHandoff: true, HasSummary: true });
            var transcript = window.FindControl<TranscriptView>("Transcript")!;
            var cards = transcript.Entries.OfType<ContentEntry>().Select(e => e.Content).OfType<PlanCardViewModel>().ToList();
            Assert.Contains(cards, c => c.StatusText == "已批准 · 清空上下文后执行" && c.HasPlan);
            Assert.Contains(cards, c => c.StatusText == "已批准 · 自动执行" && c.IsEdited);
            Assert.Contains(cards, c => c.StatusText == "已退回" && c.Feedback == "Add a rollback step");
            Assert.Contains(cards, c => c.StatusText == "未批准");
            Assert.Contains(cards, c => c.StatusText == "已中止");
            Assert.Contains(transcript.Entries.OfType<ContentEntry>().Select(e => e.Content).OfType<QuestionCardViewModel>(),
                c => c is { IsEnterPlan: true, Status: "accepted" });
            window.Close();
            return true;
        }, TestContext.Current.CancellationToken);
    }

    /// <summary>不经 Host：Claude 来源的面板（没有“清空上下文”）、完全授权的文字、外部编辑器改动、键盘映射，以及计划卡片的各种结局。</summary>
    [Fact]
    public async Task PlanReviewPanel_ClaudeSource_Keyboard_ExternalEdits_AndPlanCards()
    {
        var dir = Directory.CreateTempSubdirectory("tkcode-plan-card-").FullName;
        var planPath = Path.Combine(dir, "20261019T101530_ab12cd.md");
        await File.WriteAllTextAsync(planPath, PlanText + "\n", TestContext.Current.CancellationToken);

        await HeadlessApp.Session.Dispatch(async () =>
        {
            // Claude 来源：标题带运行时名，不提供“清空上下文”；ACP 选项显示当前档位
            var request = new PlanReviewRequest("s1", "r1", "claude-code", "toolu_1", planPath, PlanText, ["auto", "ask", "acp"], "autoApprove");
            var opened = new List<string>();
            var review = new PlanReviewViewModel(request, "Claude Agent", contextPercent: 38, opened.Add);
            Assert.Equal("Claude Agent 计划待批准", review.Title);
            Assert.Equal(["批准，自动执行", "批准，逐项审批", "批准，交给 ACP 执行（自动放行）", "继续完善"], review.Options.Select(o => o.Label));
            Assert.All(review.Options, o => Assert.Null(o.Suffix));
            Assert.Equal("↑↓ 选择 · 1-4 直接选 · E 编辑", review.HintsText);

            var panel = new PlanReviewPanelView { DataContext = review, VerticalAlignment = VerticalAlignment.Bottom };
            var window = Host(new Border { Padding = new Thickness(24), Child = panel }, 800, 760);
            await Settle();
            await SaveBothThemesAsync(window, "plan-review-panel-claude");

            // 键盘：↑↓ 循环移动；数字键选中；E 编辑；编辑框里 Enter / 方向键不归面板；Esc 先退出编辑
            Assert.True(PlanReviewPanelView.HandleKey(review, Key.Up, KeyModifiers.None, false, false));
            Assert.True(review.IsReviseHighlighted);
            Assert.True(PlanReviewPanelView.HandleKey(review, Key.Down, KeyModifiers.None, false, false));
            Assert.Equal(0, review.Highlight);
            Assert.True(PlanReviewPanelView.HandleKey(review, Key.D2, KeyModifiers.None, false, false));
            Assert.Equal("ask", review.Selected.Choice);
            Assert.True(PlanReviewPanelView.HandleKey(review, Key.E, KeyModifiers.None, false, false));
            Assert.True(review.IsEditing);
            Assert.False(PlanReviewPanelView.HandleKey(review, Key.Enter, KeyModifiers.None, true, false));
            Assert.False(PlanReviewPanelView.HandleKey(review, Key.Down, KeyModifiers.None, true, false));
            Assert.False(PlanReviewPanelView.HandleKey(review, Key.E, KeyModifiers.None, true, false));
            Assert.True(PlanReviewPanelView.HandleKey(review, Key.Escape, KeyModifiers.None, true, false));
            Assert.False(review.IsEditing);
            Assert.False(review.IsEdited);
            // 反馈框里打字即选中“继续完善”；Esc 退出反馈框而不是暂不批准
            review.Feedback = "x";
            Assert.True(review.IsReviseHighlighted);
            Assert.True(PlanReviewPanelView.HandleKey(review, Key.Escape, KeyModifiers.None, false, true));
            Assert.False(review.IsDecided);
            Assert.Equal(0, review.Highlight);
            review.Feedback = "";

            // 外部编辑器：打开计划文件；窗口重新激活时重读，有变化就刷新并按“已编辑”处理
            review.OpenInEditorCommand.Execute(null);
            Assert.Equal([planPath], opened);
            Assert.False(review.RefreshFromFile());
            await File.WriteAllTextAsync(planPath, PlanText + "\n- Measure the open time before and after\n", TestContext.Current.CancellationToken);
            Assert.True(review.RefreshFromFile());
            Assert.True(review.IsEdited);
            Assert.EndsWith("- Measure the open time before and after", review.Plan);
            // Cmd/Ctrl+Enter 在任何位置确认（这里是编辑框里）
            review.Select(review.Options[1]);
            Assert.True(PlanReviewPanelView.HandleKey(review, Key.Enter, KeyModifiers.Meta, true, false));
            Assert.True(review.Result.IsCompleted);
            var response = await review.Result;
            Assert.Equal(PlanReviewResponse.Approved, response.Outcome);
            Assert.Equal("ask", response.Choice);
            Assert.EndsWith("- Measure the open time before and after", response.EditedPlan);
            window.Close();

            // 完全授权：批准方式的文字随之变化；规划用掉的上下文显示在“清空上下文”后面
            var full = new PlanReviewViewModel(request with { Source = "builtin", Choices = ["clear", "auto", "ask"], PermissionMode = "fullAccess" }, null, 38);
            Assert.Equal("清空上下文，批准并完全授权执行", full.Options[0].Label);
            Assert.Equal("规划用掉 38% 上下文", full.Options[0].Suffix);
            Assert.Equal("批准，完全授权执行", full.Options[1].Label);
            full.Dismiss();
            Assert.Equal(PlanReviewResponse.Dismissed, (await full.Result).Outcome);

            // 工具分类：内置与 Claude 的工具名（大小写不敏感）都归到计划卡片 / 进入 Plan 的卡片
            Assert.Equal(ToolCategory.PlanReview, ToolPresentation.Classify("exit_plan_mode"));
            Assert.Equal(ToolCategory.PlanReview, ToolPresentation.Classify("ExitPlanMode"));
            Assert.Equal(ToolCategory.EnterPlan, ToolPresentation.Classify("enter_plan_mode"));
            Assert.Equal(ToolCategory.EnterPlan, ToolPresentation.Classify("EnterPlanMode"));
            Assert.Equal("请求批准计划", ToolPresentation.VerbFor(ToolCategory.PlanReview, "exit_plan_mode"));
            Assert.Equal("申请进入 Plan 模式", ToolPresentation.VerbFor(ToolCategory.EnterPlan, "enter_plan_mode"));

            // 计划卡片：批准（编辑过）、退回（附反馈）、等待批准，以及进入 Plan 模式的卡片
            var approvedCall = Call("exit_plan_mode", ToolCallStatus.Succeeded,
                Details("approved", planPath, PlanText + "\n- Measure the open time before and after", edited: true, choice: "auto"));
            var approved = new PlanCardViewModel(approvedCall);
            Assert.Equal("已批准 · 自动执行", approved.StatusText);
            Assert.Equal("20261019T101530_ab12cd.md", approved.FileName);
            Assert.Equal($"展开全部 {approved.Plan.Split('\n').Length} 行", approved.ExpandText);
            var revised = new PlanCardViewModel(Call("ExitPlanMode", ToolCallStatus.Succeeded,
                Details("revise", planPath, PlanText, feedback: "Keep the old cache as a fallback")));
            Assert.Equal("已退回", revised.StatusText);
            var pending = new PlanCardViewModel(Call("exit_plan_mode", ToolCallStatus.Running, Details("pending", planPath, "## Context\nShort plan.")));
            Assert.True(pending.IsPending);
            Assert.Equal("等待批准…", pending.StatusText);
            // Claude 没有 Details 时按参数显示计划；被停止时是“已中止”
            var claude = new PlanCardViewModel(Call("ExitPlanMode", ToolCallStatus.Failed, null,
                """{"plan":"## Plan\nDo it.","planFilePath":"/tmp/plan.md"}""", "Operation aborted."));
            Assert.Equal("已中止", claude.StatusText);
            Assert.Equal("## Plan\nDo it.", claude.Plan);
            Assert.Equal("plan.md", claude.FileName);
            var enterCall = Call("enter_plan_mode", ToolCallStatus.Succeeded, """{"kind":"enterPlanMode","status":"declined","reason":"Several designs are possible."}""",
                """{"reason":"Several designs are possible."}""");
            var enter = new QuestionCardViewModel(enterCall);
            Assert.True(enter.IsEnterPlan);
            Assert.Equal("进入 Plan 模式？", enter.Title);
            Assert.Equal("继续直接执行", enter.StatusText);

            var stack = new StackPanel { Spacing = 8, Width = 720 };
            stack.Children.Add(new PlanCardView { DataContext = approved });
            stack.Children.Add(new PlanCardView { DataContext = revised });
            stack.Children.Add(new PlanCardView { DataContext = pending });
            stack.Children.Add(new QuestionCardView { DataContext = enter });
            var cards = Host(new ScrollViewer { Content = new Border { Padding = new Thickness(24), Child = stack } }, 780, 900);
            await Settle();
            // 计划正文由 Arc ExpandableContent 收起：长计划显示「展开全部 N 行」，短计划没有按钮
            Assert.True(PlanClamp(cards, approved).IsOverflowing);
            Assert.Equal(approved.ExpandText, PlanClamp(cards, approved).MoreLabel);
            Assert.False(PlanClamp(cards, pending).IsOverflowing);
            await SaveBothThemesAsync(cards, "plan-card");
            // 卡片的“打开”走工具块的 OpenFileRequested（会话把它接到右侧工作区），不再交给系统默认程序
            approvedCall.OpenFileRequested = opened.Add;
            approved.OpenCommand.Execute(null);
            Assert.Equal([planPath, planPath], opened);
            var collapsed = PlanClamp(cards, approved).Bounds.Height;
            approved.IsExpanded = true;
            await Settle();
            Assert.True(PlanClamp(cards, approved).IsExpanded);
            Assert.Equal("收起", PlanClamp(cards, approved).LessLabel);
            Assert.True(PlanClamp(cards, approved).Bounds.Height > collapsed + 20);
            stack.Children.RemoveRange(1, 3);
            await Settle();
            await SaveBothThemesAsync(cards, "plan-card-expanded");
            cards.Close();
            return true;
        }, TestContext.Current.CancellationToken);
    }

    // ---------------- helpers ----------------

    private static async Task SendAsync(ConversationViewModel conversation, string text)
    {
        conversation.ComposerText = text;
        Assert.True(conversation.SendCommand.CanExecute(null));
        await conversation.SendCommand.ExecuteAsync(null);
    }

    /// <summary>发送 exit_plan_mode，等审批面板出现。</summary>
    private static async Task<PlanReviewViewModel> RequestReviewAsync(ConversationViewModel conversation, SessionViewModel session)
    {
        var draft = conversation.ComposerText;
        await SendAsync(conversation, "mcp:exit_plan_mode {}");
        await WaitUntil(() => conversation.IsReviewingPlan, "plan review", 30_000);
        conversation.ComposerText = draft;
        return session.CurrentPlanReview!;
    }

    private static ToolCallBlockViewModel? Tool(SessionViewModel session, string name) =>
        session.Items.OfType<AssistantMessageItemViewModel>().SelectMany(m => m.Blocks)
            .OfType<ToolCallBlockViewModel>().LastOrDefault(t => t.ToolName == name);

    private static List<ToolCallBlockViewModel> ExitCalls(SessionViewModel session) =>
        session.Items.OfType<AssistantMessageItemViewModel>().SelectMany(m => m.Blocks)
            .OfType<ToolCallBlockViewModel>().Where(t => t.ToolName == "exit_plan_mode").ToList();

    /// <summary>转录里的独立卡片（由 TranscriptAdapter 创建）。</summary>
    private static List<T> Cards<T>(Window window) =>
        window.FindControl<TranscriptView>("Transcript")!.Entries.OfType<ContentEntry>().Select(e => e.Content).OfType<T>().ToList();

    private static ToolCallBlockViewModel Call(string name, ToolCallStatus status, string? details, string arguments = "{}", string? result = null) =>
        new($"call_{Guid.NewGuid():N}", name) { Arguments = arguments, Status = status, IsStreaming = false, DetailsJson = details, Result = result };

    private static string Details(string status, string path, string plan, bool edited = false, string? choice = null, string? feedback = null)
    {
        var node = new JsonObject { ["kind"] = "planReview", ["status"] = status, ["path"] = path, ["plan"] = plan };
        if (edited)
        {
            node["edited"] = true;
        }
        if (choice is not null)
        {
            node["choice"] = choice;
        }
        if (feedback is not null)
        {
            node["feedback"] = feedback;
        }
        return node.ToJsonString();
    }

    private static ExpandableContent PlanClamp(Visual root, PlanCardViewModel card) =>
        root.GetVisualDescendants().OfType<PlanCardView>().Single(v => ReferenceEquals(v.DataContext, card)).FindDescendantOfType<ExpandableContent>()!;

    private static Window Host(Control content, double width, double height)
    {
        var window = new Window { Width = width, Height = height, Content = content };
        // 单独的测试窗口没有 ZoomHost：和主窗口一样关掉 Arc 动效
        ArcMotion.SetLevel(window, ArcMotionLevel.Off);
        window[!TemplatedControl.BackgroundProperty] = window.GetResourceObservable("TKCode.WindowBg").ToBinding();
        // 与主窗口一致：正文颜色由窗口的 Foreground 继承
        window[!TemplatedControl.ForegroundProperty] = window.GetResourceObservable("TKCode.TextPrimary").ToBinding();
        MarkdownServices.SetServices(window, MarkdownSetup.Services);
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    private static void Press(Window window, Key key, PhysicalKey physical)
    {
        window.KeyPress(key, RawInputModifiers.None, physical, null);
        window.KeyRelease(key, RawInputModifiers.None, physical, null);
    }

    private static void PressDigit(Window window, int digit) =>
        Press(window, Key.D1 + (digit - 1), PhysicalKey.Digit1 + (digit - 1));

    /// <summary>暗色与亮色各存一张（暗色是测试默认主题，结束时切回暗色）。</summary>
    private static async Task SaveBothThemesAsync(Window window, string name)
    {
        await Settle();
        HeadlessApp.Save(window, name);
        try
        {
            await TestTheme.ApplyAsync(TestTheme.LightId);
            await Settle();
            HeadlessApp.Save(window, name + "-light");
        }
        finally
        {
            await TestTheme.ApplyAsync(TestTheme.DarkId);
            await Settle();
        }
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
