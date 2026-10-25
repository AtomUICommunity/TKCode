using System.ComponentModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TKCode.Desktop.Localization;

namespace TKCode.Desktop.ViewModels;

/// <summary>
/// 子 Agent 组（<see cref="SubagentGroupViewModel"/>）里的一行：名称、模型、后台标记与运行状态，点击在右侧打开它的转录。
/// 信息取自工具调用的 Details（<c>kind:"subagent"</c>），没有时回退到参数（name / subagent_type / model / run_in_background）。
/// </summary>
public sealed partial class SubagentCardViewModel : TranscriptItemViewModel
{
    public SubagentCardViewModel(ToolCallBlockViewModel call)
    {
        Call = call;
        call.PropertyChanged += OnCallChanged;
        Refresh();
    }

    public ToolCallBlockViewModel Call { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayName))]
    public partial string? Name { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasModel))]
    public partial string? Model { get; private set; }

    [ObservableProperty]
    public partial bool IsBackground { get; private set; }

    public string DisplayName => Name ?? Strings.Get(TKCodeLangResourceKind.SubagentDefaultName);

    public bool HasModel => !string.IsNullOrEmpty(Model);

    /// <summary>前台子 Agent 运行中（后台的工具调用启动后立即返回，没有运行态）。</summary>
    public bool IsRunning => Call.IsBusy && !IsBackground;

    public bool IsFailed => Call.IsFailed;

    partial void OnIsBackgroundChanged(bool value) => OnPropertyChanged(nameof(IsRunning));

    [RelayCommand]
    private void Open() => Call.OpenSubagentRequested?.Invoke(Call);

    public void RefreshLanguage() => OnPropertyChanged(nameof(DisplayName));

    private void OnCallChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ToolCallBlockViewModel.DetailsJson) or nameof(ToolCallBlockViewModel.Arguments)
            or nameof(ToolCallBlockViewModel.IsStreaming))
        {
            Refresh();
        }
        if (e.PropertyName is nameof(ToolCallBlockViewModel.IsBusy))
        {
            OnPropertyChanged(nameof(IsRunning));
            OnPropertyChanged(nameof(IsFailed));
        }
    }

    private void Refresh() => (Name, Model, IsBackground) = Describe(Call);

    /// <summary>名称、模型、是否后台：Details 优先，其次参数。</summary>
    public static (string? Name, string? Model, bool Background) Describe(ToolCallBlockViewModel call)
    {
        var (name, model, background) = call.IsStreaming ? default : FromArguments(call.Arguments);
        if (call.Parsed is { Kind: "subagent" } details)
        {
            name = details.SubagentName ?? name;
            model = details.SubagentModel ?? model;
            background |= details.SubagentBackground;
        }
        return (string.IsNullOrWhiteSpace(name) ? null : name, model, background);
    }

    private static (string? Name, string? Model, bool Background) FromArguments(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return default;
            }
            return (Str(root, "name") ?? Str(root, "subagent_type"), Str(root, "model"),
                root.TryGetProperty("run_in_background", out var b) && b.ValueKind == JsonValueKind.True);
        }
        catch (JsonException)
        {
            return default;
        }
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s ? s : null;
}
