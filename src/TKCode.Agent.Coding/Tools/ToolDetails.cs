using System.Text.Json;
using System.Text.Json.Serialization;

namespace TKCode.Agent.Coding.Tools;

/// <summary>
/// 内置工具给 UI 的结构化数据（序列化后放进 <see cref="AgentToolResult.Details"/>）。
/// JSON 的 kind 字段区分类型；UI 按 kind 选择渲染方式，不认识的 kind 忽略即可。
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(DiffDetails), "diff")]
[JsonDerivedType(typeof(CommandDetails), "command")]
[JsonDerivedType(typeof(TaskListDetails), "tasks")]
[JsonDerivedType(typeof(BackgroundTaskDetails), "backgroundTask")]
[JsonDerivedType(typeof(SubagentDetails), "subagent")]
[JsonDerivedType(typeof(WebFetchDetails), "webFetch")]
[JsonDerivedType(typeof(WebSearchDetails), "webSearch")]
public abstract record ToolDetails
{
    public string ToJson() => JsonSerializer.Serialize(this, ToolDetailsJsonContext.Default.ToolDetails);

    public static ToolDetails? FromJson(string? json)
    {
        if (string.IsNullOrEmpty(json))
        {
            return null;
        }
        try
        {
            return JsonSerializer.Deserialize(json, ToolDetailsJsonContext.Default.ToolDetails);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }
}

/// <param name="Diff">unified diff。</param>
/// <param name="Created">新建的文件。</param>
/// <param name="Before">改动前的完整文本（LF，不含 BOM）；新建文件或超过快照上限时为 null。用于撤销。</param>
/// <param name="AfterSha256">改动后文件字节的 SHA-256（小写十六进制）。撤销前用它检测文件是否又被改过。</param>
public sealed record DiffDetails(string Path, string Diff, int Added, int Removed, int? FirstChangedLine, bool Created = false,
    string? Before = null, string? AfterSha256 = null) : ToolDetails;

/// <param name="OutputPath">完整输出所在的文件（输出被截断或后台运行时）。</param>
public sealed record CommandDetails(string Command, int? ExitCode, bool TimedOut = false, string? OutputPath = null, string? TaskId = null)
    : ToolDetails;

public sealed record TaskItem(string Id, string Subject, string Description, string Status)
{
    public string? ActiveForm { get; init; }
    public string? Owner { get; init; }
    public IReadOnlyList<string> Blocks { get; init; } = [];
    public IReadOnlyList<string> BlockedBy { get; init; } = [];
}

public sealed record TaskListDetails(IReadOnlyList<TaskItem> Tasks) : ToolDetails;

/// <param name="Type">bash / agent。</param>
/// <param name="Status">running / completed / failed / stopped。</param>
public sealed record BackgroundTaskDetails(string TaskId, string Type, string Description, string Status, string? OutputPath = null)
    : ToolDetails;

/// <param name="Status">running / completed / failed / stopped。</param>
/// <param name="Model">子 Agent 使用的模型（显示名）；外部运行时不知道时为 null。</param>
public sealed record SubagentDetails(string AgentId, string Name, string AgentType, string Description, string Status,
    bool Background, int ToolUses, long Tokens, string? CurrentActivity = null, string? Model = null) : ToolDetails;

public sealed record WebFetchDetails(string Url, int StatusCode, string? Title, string ContentType, int TotalChars) : ToolDetails;

public sealed record WebSearchResult(string Title, string Url, string? Snippet);

public sealed record WebSearchDetails(string Query, string Provider, IReadOnlyList<WebSearchResult> Results) : ToolDetails;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ToolDetails))]
public sealed partial class ToolDetailsJsonContext : JsonSerializerContext;
