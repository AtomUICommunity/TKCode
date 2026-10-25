using System.Text;
using System.Text.Json;

namespace TKCode.Agent.Coding.Tools.Search;

/// <summary>列出目录内容（只读）：按字母序，目录以 / 结尾，包含点文件，超出上限时截断并提示。</summary>
public sealed class LsTool(ToolContext context) : IAgentTool
{
    private const int DefaultLimit = 500;

    public string Name => "ls";
    public string Label => "Tool.ListDirectory";

    public string Description =>
        "List the entries of a directory, sorted alphabetically, with '/' after directory names. Includes dotfiles. " +
        $"Returns at most `limit` entries (default {DefaultLimit}). Path defaults to the primary working directory.";

    public string ParametersSchemaJson => """
        {"type":"object","properties":{
          "path":{"type":"string","description":"Directory to list (absolute or relative to the primary working directory). Defaults to the primary working directory; pass an absolute path to list an additional working directory."},
          "limit":{"type":"integer","minimum":1,"description":"Maximum number of entries to return (default 500)."}},
         "additionalProperties":false}
        """;

    public string PromptSnippet => "List directory contents";

    public ToolPermission DescribePermission(JsonElement arguments) => new(ToolAccess.Read, [context.Workspace.Resolve(arguments.String("path"))]);

    public Task<AgentToolResult> ExecuteAsync(ToolInvocation invocation, CancellationToken cancellationToken)
    {
        var dir = context.Workspace.Resolve(invocation.Arguments.String("path"));
        if (File.Exists(dir))
        {
            return Task.FromResult(AgentToolResult.Error($"{dir} is a file, not a directory. Use the read tool to view it."));
        }
        if (!Directory.Exists(dir))
        {
            return Task.FromResult(AgentToolResult.Error($"Directory not found: {dir}"));
        }
        var limit = Math.Max(1, invocation.Arguments.Int("limit") ?? DefaultLimit);
        var entries = new DirectoryInfo(dir).EnumerateFileSystemInfos()
            .Select(e => e is DirectoryInfo ? e.Name + "/" : e.Name)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var sb = new StringBuilder();
        foreach (var entry in entries.Take(limit))
        {
            sb.Append(entry).Append('\n');
        }
        if (entries.Count > limit)
        {
            sb.Append($"\n[Showing {limit} of {entries.Count} entries. Increase limit or list a subdirectory.]\n");
        }
        if (entries.Count == 0)
        {
            sb.Append("(empty directory)\n");
        }
        return Task.FromResult(AgentToolResult.Text(sb.ToString()));
    }
}
