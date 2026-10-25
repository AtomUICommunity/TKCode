using TKCode.Agent.Diff;
using System.Text.Json;

namespace TKCode.Agent.Coding.Tools.Files;

/// <summary>写入整个文件：新建或完整覆盖，自动创建父目录。覆盖已有文件前必须先读过（并且之后没被改过）。</summary>
public sealed class WriteTool(ToolContext context) : IAgentTool
{
    public string Name => "write";
    public string Label => "Tool.Write";

    public string Description =>
        "Write content to a file, creating it (and parent directories) if needed or completely overwriting it. " +
        "Overwriting an existing file requires reading it first with the read tool. " +
        "Prefer the edit tool for modifying existing files; use write for new files or complete rewrites.";

    public string ParametersSchemaJson => """
        {"type":"object","properties":{
          "path":{"type":"string","description":"Path to the file to write (absolute or relative to the primary working directory)."},
          "content":{"type":"string","description":"The complete content to write to the file."}},
         "required":["path","content"],"additionalProperties":false}
        """;

    public string PromptSnippet => "Create or overwrite files";

    public IReadOnlyList<string> PromptGuidelines =>
    [
        "Use write only for new files or complete rewrites; use edit for targeted changes.",
        "Do not create documentation or README files unless the user asks for them.",
    ];

    public ToolPermission DescribePermission(JsonElement arguments)
    {
        if (context.InPlanMode)
        {
            // Plan 模式：计划文件免审批，其他路径在执行时直接拦截（docs/design/plan-mode.md §6）
            return ToolPermission.NoAccess;
        }
        var path = context.Workspace.Resolve(arguments.Required("path"));
        return new(ToolAccess.Write, [path], Title: path);
    }

    public Task<AgentToolResult> ExecuteAsync(ToolInvocation invocation, CancellationToken cancellationToken)
    {
        var path = context.Workspace.Resolve(invocation.Arguments.Required("path"));
        if (context.InPlanMode && !context.WritableInPlanMode(path))
        {
            return Task.FromResult(AgentToolResult.Error(context.PlanModeWriteMessage));
        }
        var content = context.PrepareWrite(path, TextFile.NormalizeNewLines(invocation.Arguments.Required("content")));
        return context.Mutations.RunAsync(path, () => WriteAsync(path, content, cancellationToken), cancellationToken);
    }

    private async Task<AgentToolResult> WriteAsync(string path, string content, CancellationToken ct)
    {
        if (Directory.Exists(path))
        {
            return AgentToolResult.Error($"{path} is a directory.");
        }
        var exists = File.Exists(path);
        var previous = "";
        string? snapshot = null;
        TextFile format;
        if (exists)
        {
            var current = await File.ReadAllBytesAsync(path, ct);
            if (context.Files.CheckFresh(path, current) is { } stale)
            {
                return AgentToolResult.Error(stale);
            }
            if (TextFile.LooksBinary(current))
            {
                return AgentToolResult.Error($"{path} is a binary file; refusing to overwrite it.");
            }
            format = TextFile.Decode(current);
            previous = format.Text;
            snapshot = current.Length <= FileRevert.MaxSnapshotBytes ? previous : null;
        }
        else
        {
            format = new TextFile("", false, "\n");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        }

        var bytes = format.Encode(content);
        await File.WriteAllBytesAsync(path, bytes, ct);
        context.Files.Record(path, bytes);

        var diff = LineDiff.Compute(previous, content, path);
        var details = new DiffDetails(path, diff.UnifiedDiff, diff.Added, diff.Removed, diff.FirstChangedLine, Created: !exists,
            Before: snapshot, AfterSha256: FileRevert.Sha256(bytes)).ToJson();
        var lineCount = LineDiff.SplitLines(content).Length;
        var message = exists ? $"Overwrote {path} ({lineCount} lines)." : $"Created {path} ({lineCount} lines).";
        if (context.WriteReminder(path, content) is { } reminder)
        {
            message += "\n" + reminder;
        }
        if (context.NestedInstructionsFor(path) is { } nested)
        {
            message += "\n" + nested;
        }
        return AgentToolResult.Text(message) with { Details = details };
    }
}
