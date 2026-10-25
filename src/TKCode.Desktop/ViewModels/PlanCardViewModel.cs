using System.ComponentModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TKCode.Desktop.Localization;

namespace TKCode.Desktop.ViewModels;

/// <summary>
/// 计划审批（内置 exit_plan_mode、Claude ExitPlanMode）在转录里的独立卡片（docs/design/plan-mode.md §14.2）：
/// 头部是“计划”、文件名和结局；正文默认只显示前 8 行，展开后是完整的渲染计划，附“在编辑器中打开”。
/// 内容取自工具结果的 Details（<c>{"kind":"planReview","status":…,"path":…,"plan":…,"edited":…,"choice":…,"feedback":…}</c>）——
/// 计划是审批当时的快照；还没有 Details 时按参数（Claude 的 plan / planFilePath）显示，状态按工具状态推断。
/// </summary>
public sealed partial class PlanCardViewModel : TranscriptItemViewModel
{
    /// <summary>收起时显示的行数。</summary>
    public const int CollapsedLines = 8;

    public PlanCardViewModel(ToolCallBlockViewModel call)
    {
        Call = call;
        call.PropertyChanged += OnCallChanged;
        Refresh();
    }

    public ToolCallBlockViewModel Call { get; }

    /// <summary>pending / approved / revise / dismissed / aborted / failed。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPending), nameof(StatusText), nameof(IsApproved))]
    public partial string Status { get; private set; } = "pending";

    /// <summary>批准方式：clear / auto / ask / acp。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    public partial string? Choice { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FileName), nameof(HasPath))]
    public partial string? Path { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPlan), nameof(ExpandText))]
    public partial string Plan { get; private set; } = "";

    [ObservableProperty]
    public partial bool IsEdited { get; private set; }

    /// <summary>退回时的反馈；出错时是错误文本。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFeedback))]
    public partial string? Feedback { get; private set; }

    /// <summary>正文是否展开（视图里 Arc 的 ExpandableContent 双向绑定；行被回收再实例化时保持状态）。</summary>
    [ObservableProperty]
    public partial bool IsExpanded { get; set; }

    public bool IsPending => Status == "pending";
    public bool IsApproved => Status == "approved";
    public bool HasFeedback => !string.IsNullOrWhiteSpace(Feedback);
    public bool HasPlan => Plan.Length > 0;
    public bool HasPath => !string.IsNullOrEmpty(Path);
    public string Title => Strings.Get(TKCodeLangResourceKind.PlanCardTitle);
    public string FileName => Path is { Length: > 0 } path ? System.IO.Path.GetFileName(path) : "";

    public string StatusText => Status switch
    {
        "approved" => Choice is { } choice
            ? Strings.Format(TKCodeLangResourceKind.PlanCardApprovedFormat, Strings.Get(choice switch
            {
                "ask" => TKCodeLangResourceKind.PlanCardChoiceAsk,
                "acp" => TKCodeLangResourceKind.PlanCardChoiceAcp,
                "clear" => TKCodeLangResourceKind.PlanCardChoiceClear,
                _ => TKCodeLangResourceKind.PlanCardChoiceAuto,
            }))
            : Strings.Get(TKCodeLangResourceKind.PlanCardApproved),
        "revise" => Strings.Get(TKCodeLangResourceKind.PlanCardRevised),
        "dismissed" => Strings.Get(TKCodeLangResourceKind.PlanCardDismissed),
        "aborted" => Strings.Get(TKCodeLangResourceKind.PlanCardAborted),
        "failed" => Strings.Get(TKCodeLangResourceKind.ToolFailed),
        _ => Strings.Get(TKCodeLangResourceKind.PlanCardWaiting),
    };

    /// <summary>收起时的「展开全部 N 行」（N 是计划原文的行数）。</summary>
    public string ExpandText => Strings.Format(TKCodeLangResourceKind.PlanCardExpandFormat, Plan.Split('\n').Length);

    /// <summary>在右侧工作区打开计划文件：与 read 工具同一条通路，由会话把请求接到工作区。</summary>
    [RelayCommand]
    private void Open()
    {
        if (Path is { Length: > 0 } path)
        {
            Call.OpenFileRequested?.Invoke(path);
        }
    }

    private void OnCallChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ToolCallBlockViewModel.DetailsJson) or nameof(ToolCallBlockViewModel.Status)
            or nameof(ToolCallBlockViewModel.Arguments) or nameof(ToolCallBlockViewModel.IsStreaming) or nameof(ToolCallBlockViewModel.Result))
        {
            Refresh();
        }
    }

    private void Refresh()
    {
        var details = Parse(Call.DetailsJson, details: true);
        // 等待批准时的 Details 是 pending；被停止时工具抛出取消、结果里没有新的 Details，这时按工具状态判断
        if (details is { Status: not (null or "pending") } || (details is not null && Call.IsBusy))
        {
            Status = details.Value.Status ?? "pending";
            Choice = details.Value.Choice;
            Path = details.Value.Path ?? Path;
            Plan = Clean(details.Value.Plan) ?? Plan;
            IsEdited = details.Value.Edited;
            Feedback = details.Value.Feedback;
            return;
        }
        if (details is { } pending)
        {
            Path = pending.Path ?? Path;
            Plan = Clean(pending.Plan) ?? Plan;
        }
        // 还没有 Details：Claude 的 ExitPlanMode 参数里带着计划；出错（例如还没写计划文件）时显示错误
        else if (Parse(Call.Arguments, details: false) is { } args)
        {
            Path = args.Path ?? Path;
            Plan = Clean(args.Plan) ?? Plan;
        }
        Choice = null;
        IsEdited = false;
        (Status, Feedback) = Call.Status switch
        {
            ToolCallStatus.Failed when IsAborted(Call.Result) => ("aborted", null),
            ToolCallStatus.Failed => ("failed", Call.Result),
            ToolCallStatus.Succeeded => ("approved", null),
            _ => ("pending", null),
        };
    }

    /// <summary>停止本轮时工具结果是 Agent 循环统一的 “Operation aborted.”。</summary>
    private static bool IsAborted(string? result) => result?.StartsWith("Operation aborted", StringComparison.Ordinal) == true;

    private static string? Clean(string? plan) => plan?.Replace("\r\n", "\n").Trim();

    internal readonly record struct ParsedPlan(string? Status, string? Path, string? Plan, bool Edited, string? Choice, string? Feedback);

    /// <summary>解析 Details（kind = planReview）或工具参数（Claude ExitPlanMode：plan / planFilePath）。</summary>
    internal static ParsedPlan? Parse(string? json, bool details)
    {
        if (string.IsNullOrEmpty(json))
        {
            return null;
        }
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }
            if (details)
            {
                if (!(root.TryGetProperty("kind", out var kind) && kind.ValueEquals("planReview")))
                {
                    return null;
                }
                return new ParsedPlan(Str(root, "status"), Str(root, "path"), Str(root, "plan"),
                    root.TryGetProperty("edited", out var edited) && edited.ValueKind == JsonValueKind.True,
                    Str(root, "choice"), Str(root, "feedback"));
            }
            return new ParsedPlan(null, Str(root, "planFilePath"), Str(root, "plan"), false, null, null);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    public void RefreshLanguage()
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(ExpandText));
    }
}
