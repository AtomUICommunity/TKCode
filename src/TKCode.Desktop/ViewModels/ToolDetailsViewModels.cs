using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TKCode.Desktop.Icons;
using TKCode.Desktop.Localization;

namespace TKCode.Desktop.ViewModels;

/// <summary>diff 的一行：+ 新增、- 删除、空格为上下文、@@ 为块头。</summary>
public sealed class DiffLineViewModel(string text, DiffLineKind kind)
{
    public string Text { get; } = text;
    public DiffLineKind Kind { get; } = kind;
    public bool IsAdded => Kind == DiffLineKind.Added;
    public bool IsRemoved => Kind == DiffLineKind.Removed;
    public bool IsHunk => Kind == DiffLineKind.Hunk;

    /// <summary>行号（旧文件 / 新文件）；块头与新增、删除行缺少的一侧为 null。</summary>
    public int? OldNumber { get; init; }
    public int? NewNumber { get; init; }

    /// <summary>行号的显示文字（缺少的一侧为空）。</summary>
    public string OldText => OldNumber?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "";
    public string NewText => NewNumber?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "";

    /// <summary>行首的符号列：+ / - / 空格；块头为空。</summary>
    public string Sign => Kind switch
    {
        DiffLineKind.Added => "+",
        DiffLineKind.Removed => "-",
        _ => "",
    };
}

public enum DiffLineKind
{
    Context,
    Added,
    Removed,
    Hunk,
}

/// <summary>
/// 工具 Details（JSON，kind 区分类型，格式见 TKCode.Agent.Coding.Tools.ToolDetails）的界面解析。
/// 桌面端不引用 Agent 程序集，只读取需要的字段；不认识的 kind 忽略。
/// </summary>
public sealed record ParsedToolDetails
{
    public string Kind { get; init; } = "";

    public IReadOnlyList<DiffLineViewModel> DiffLines { get; init; } = [];
    public int Added { get; init; }
    public int Removed { get; init; }

    /// <summary>diff 对应的文件（相对于 cwd 之外的绝对路径）。</summary>
    public string? Path { get; init; }

    /// <summary>本次改动新建了文件；撤销即删除。</summary>
    public bool Created { get; init; }

    /// <summary>改动前的完整文本；为 null 且非新建时不可撤销。</summary>
    public string? Before { get; init; }

    /// <summary>改动后文件字节的 SHA-256，撤销前用于检测文件是否又被改过。</summary>
    public string? AfterSha256 { get; init; }

    public string? SubagentName { get; init; }
    public string? SubagentModel { get; init; }
    public bool SubagentBackground { get; init; }
    public string? SubagentStatus { get; init; }
    public string? SubagentActivity { get; init; }
    public int ToolUses { get; init; }
    public long Tokens { get; init; }

    public string? OutputPath { get; init; }
    public int? ExitCode { get; init; }

    public IReadOnlyList<TaskItemViewModel>? Tasks { get; init; }

    /// <summary>diff 太长时只显示开头（完整内容仍在文件里）。</summary>
    private const int MaxDiffLines = 400;

    public static ParsedToolDetails? Parse(string? json)
    {
        if (string.IsNullOrEmpty(json))
        {
            return null;
        }
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            // 外部运行时原样转发结构化结果（如 Claude Code 的 tool_use_result 出错时是字符串），不是对象就不认识
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }
            var kind = Str(root, "kind") ?? "";
            return kind switch
            {
                "diff" => new ParsedToolDetails
                {
                    Kind = kind,
                    DiffLines = DiffLinesOf(Str(root, "diff") ?? ""),
                    Added = Int(root, "added") ?? 0,
                    Removed = Int(root, "removed") ?? 0,
                    Path = Str(root, "path"),
                    Created = root.TryGetProperty("created", out var created) && created.ValueKind == JsonValueKind.True,
                    Before = Str(root, "before"),
                    AfterSha256 = Str(root, "afterSha256"),
                },
                "subagent" => new ParsedToolDetails
                {
                    Kind = kind,
                    SubagentName = Str(root, "name"),
                    SubagentModel = Str(root, "model"),
                    SubagentBackground = root.TryGetProperty("background", out var background) && background.ValueKind == JsonValueKind.True,
                    SubagentStatus = Str(root, "status"),
                    SubagentActivity = Str(root, "currentActivity"),
                    ToolUses = Int(root, "toolUses") ?? 0,
                    Tokens = root.TryGetProperty("tokens", out var t) && t.ValueKind == JsonValueKind.Number && t.TryGetInt64(out var tokens) ? tokens : 0,
                },
                "command" => new ParsedToolDetails { Kind = kind, OutputPath = Str(root, "outputPath"), ExitCode = Int(root, "exitCode") },
                "backgroundTask" => new ParsedToolDetails { Kind = kind, OutputPath = Str(root, "outputPath") },
                "tasks" => new ParsedToolDetails
                {
                    Kind = kind,
                    Tasks = root.TryGetProperty("tasks", out var tasks) && tasks.ValueKind == JsonValueKind.Array
                        ? tasks.EnumerateArray().Select(e => new TaskItemViewModel(Str(e, "id") ?? "", Str(e, "subject") ?? "",
                            Str(e, "status") ?? "pending", Str(e, "activeForm"))).ToArray()
                        : [],
                },
                _ => new ParsedToolDetails { Kind = kind },
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>解析 unified diff，并按块头 @@ -a,b +c,d @@ 推算每行的新旧行号。</summary>
    public static IReadOnlyList<DiffLineViewModel> DiffLinesOf(string diff)
    {
        var lines = new List<DiffLineViewModel>();
        int oldNumber = 0, newNumber = 0;
        foreach (var line in diff.Split('\n'))
        {
            if (line.StartsWith("--- ", StringComparison.Ordinal) || line.StartsWith("+++ ", StringComparison.Ordinal) || line.Length == 0)
            {
                continue;
            }
            if (lines.Count >= MaxDiffLines)
            {
                lines.Add(new DiffLineViewModel("…", DiffLineKind.Hunk));
                break;
            }
            if (line[0] == '@')
            {
                (oldNumber, newNumber) = HunkStart(line);
                lines.Add(new DiffLineViewModel(line, DiffLineKind.Hunk));
                continue;
            }
            // 首字符（+ / - / 空格）单独放在符号列，文字只保留行内容
            var text = line[1..];
            lines.Add(line[0] switch
            {
                '+' => new DiffLineViewModel(text, DiffLineKind.Added) { NewNumber = newNumber++ },
                '-' => new DiffLineViewModel(text, DiffLineKind.Removed) { OldNumber = oldNumber++ },
                _ => new DiffLineViewModel(text, DiffLineKind.Context) { OldNumber = oldNumber++, NewNumber = newNumber++ },
            });
        }
        return lines;
    }

    /// <summary>“@@ -12,4 +12,5 @@”的起始行号；无法解析时返回 (0, 0)。</summary>
    private static (int Old, int New) HunkStart(string header)
    {
        var parts = header.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3)
        {
            return (0, 0);
        }
        return (Start(parts[1]), Start(parts[2]));

        static int Start(string range) =>
            int.TryParse(range.AsSpan(1).ToString().Split(',')[0], out var n) ? n : 0;
    }

    // 只对对象取属性：JsonElement.TryGetProperty 对非对象抛 InvalidOperationException
    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int? Int(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;
}

/// <summary>
/// 任务球里的一项：后台 shell（bash）、后台子 Agent，或前台执行中的子 Agent（来自工具调用，执行结束即移除）。
/// </summary>
public sealed partial class BackgroundTaskItemViewModel(string id, string title, bool isAgent, bool isBackground) : ObservableObject
{
    public string Id { get; } = id;
    public bool IsAgent { get; } = isAgent;
    public bool IsBackground { get; } = isBackground;

    [ObservableProperty]
    public partial string Title { get; set; } = title;

    [ObservableProperty]
    public partial bool IsRunning { get; set; } = true;

    [ObservableProperty]
    public partial bool IsFailed { get; set; }

    /// <summary>后台任务的状态（running / completed / failed / stopped）。</summary>
    public void Update(string title, string status)
    {
        Title = title;
        IsRunning = status == "running";
        IsFailed = status == "failed";
    }
}

/// <summary>任务清单中的一项。</summary>
public sealed class TaskItemViewModel(string id, string subject, string status, string? activeForm)
{
    public string Id { get; } = id;
    public string Subject { get; } = subject;
    public string Status { get; } = status;
    public bool IsCompleted => Status == "completed";
    public bool IsInProgress => Status == "in_progress";
    public bool IsPending => Status == "pending";

    /// <summary>进行中显示进行时文案（“正在修复…”），否则显示标题。</summary>
    public string DisplayText => IsInProgress && activeForm is { Length: > 0 } active ? active : Subject;
}

/// <summary>
/// 会话的任务清单（来自 task_* 工具结果的全量快照），显示在输入框上方；全部完成后收起。
/// </summary>
public sealed partial class TaskListViewModel : ObservableObject
{
    public ObservableCollection<TaskItemViewModel> Items { get; } = [];

    [ObservableProperty]
    public partial bool IsExpanded { get; set; } = true;

    public bool IsVisible => Items.Any(i => !i.IsCompleted);

    public string Header => Strings.Format(TKCodeLangResourceKind.TasksHeaderFormat, Items.Count(i => i.IsCompleted), Items.Count);

    public void Update(IReadOnlyList<TaskItemViewModel> items)
    {
        Items.Clear();
        foreach (var item in items)
        {
            Items.Add(item);
        }
        OnPropertyChanged(nameof(IsVisible));
        OnPropertyChanged(nameof(Header));
    }

    public void RefreshLanguage() => OnPropertyChanged(nameof(Header));

    [RelayCommand]
    private void Toggle() => IsExpanded = !IsExpanded;
}

/// <summary>
/// 不是用户输入的消息：后台任务完成通知（task-notification）或其他 Agent 会话的消息（session-message）。
/// 显示为一行可展开的提示，而不是用户气泡。
/// </summary>
public sealed partial class NotificationItemViewModel : TranscriptItemViewModel
{
    public NotificationItemViewModel(string source, string text)
    {
        Source = source;
        Text = text;
        IsSessionMessage = source == "session-message";
        if (IsSessionMessage)
        {
            _from = Attribute(text, "from");
            Body = Between(text, ">", "</session-message>")?.Trim() ?? text;
        }
        else
        {
            _status = Element(text, "status");
            _subject = Element(text, "description") ?? Element(text, "agent") ?? Element(text, "task-id");
            Body = (Element(text, "result") ?? text).Trim();
        }
    }

    private readonly string? _from;
    private readonly string? _status;
    private readonly string? _subject;

    public string Source { get; }
    public string Text { get; }
    public string Body { get; }
    public bool IsSessionMessage { get; }

    public LucideKind Icon => IsSessionMessage ? LucideKind.MessageSquare : _status is "failed" ? LucideKind.CircleAlert : LucideKind.ListChecks;

    public string Header => IsSessionMessage
        ? Strings.Format(TKCodeLangResourceKind.SessionMessageFromFormat, _from ?? "?")
        : Strings.Format(TKCodeLangResourceKind.TaskNotificationFormat, _subject ?? "", StatusText);

    private string StatusText => Strings.Get(_status switch
    {
        "completed" => TKCodeLangResourceKind.ToolDone,
        "stopped" => TKCodeLangResourceKind.Aborted,
        _ => TKCodeLangResourceKind.ToolFailed,
    });

    [ObservableProperty]
    public partial bool IsExpanded { get; set; }

    [RelayCommand]
    private void Toggle() => IsExpanded = !IsExpanded;

    public void RefreshLanguage() => OnPropertyChanged(nameof(Header));

    private static string? Element(string text, string name) => Between(text, $"<{name}>", $"</{name}>")?.Trim();

    private static string? Attribute(string text, string name)
    {
        var key = name + "=\"";
        var start = text.IndexOf(key, StringComparison.Ordinal);
        if (start < 0)
        {
            return null;
        }
        start += key.Length;
        var end = text.IndexOf('"', start);
        return end < 0 ? null : text[start..end].Replace("&quot;", "\"", StringComparison.Ordinal);
    }

    private static string? Between(string text, string open, string close)
    {
        var start = text.IndexOf(open, StringComparison.Ordinal);
        if (start < 0)
        {
            return null;
        }
        start += open.Length;
        var end = text.IndexOf(close, start, StringComparison.Ordinal);
        return end < 0 ? null : text[start..end];
    }
}

/// <summary>已完成的任务加删除线。</summary>
public static class TaskDecorations
{
    public static Avalonia.Data.Converters.IValueConverter Strike { get; } =
        new Avalonia.Data.Converters.FuncValueConverter<bool, Avalonia.Media.TextDecorationCollection?>(done =>
            done ? Avalonia.Media.TextDecorations.Strikethrough : null);
}
