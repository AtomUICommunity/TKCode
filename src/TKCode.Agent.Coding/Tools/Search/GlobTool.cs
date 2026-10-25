using System.Text;
using System.Text.Json;
using TKCode.Agent.Coding.Processes;

namespace TKCode.Agent.Coding.Tools.Search;

/// <summary>按 glob 查找文件：遵守 .gitignore，包含点文件，结果按修改时间倒序。有 rg 时用 rg --files，否则用内置遍历。</summary>
public sealed class GlobTool(ToolContext context) : IAgentTool
{
    private const int DefaultLimit = 100;

    public string Name => "glob";
    public string Label => "Tool.Glob";

    public string Description =>
        "Find files by glob pattern, e.g. '*.cs', '**/*.json' or 'src/**/*Tests.cs'. Respects .gitignore and includes dotfiles. " +
        "Patterns without '/' match file names at any depth. " +
        $"Results are sorted by modification time (newest first) and capped at `limit` (default {DefaultLimit}).";

    public string ParametersSchemaJson => """
        {"type":"object","properties":{
          "pattern":{"type":"string","description":"Glob pattern to match files, e.g. '*.ts', '**/*.json' or 'src/**/*.spec.ts'."},
          "path":{"type":"string","description":"Directory to search in (defaults to the primary working directory; pass an absolute path to search an additional working directory)."},
          "limit":{"type":"integer","minimum":1,"description":"Maximum number of results (default 100)."}},
         "required":["pattern"],"additionalProperties":false}
        """;

    public string PromptSnippet => "Find files by glob pattern";

    public IReadOnlyList<string> PromptGuidelines => ["Use glob instead of find/ls -R to locate files by name."];

    public ToolPermission DescribePermission(JsonElement arguments) => new(ToolAccess.Read, [context.Workspace.Resolve(arguments.String("path"))]);

    public async Task<AgentToolResult> ExecuteAsync(ToolInvocation invocation, CancellationToken cancellationToken)
    {
        var args = invocation.Arguments;
        var pattern = args.Required("pattern");
        var root = context.Workspace.Resolve(args.String("path"));
        if (!Directory.Exists(root))
        {
            return AgentToolResult.Error($"Directory not found: {root}");
        }
        var limit = Math.Max(1, args.Int("limit") ?? DefaultLimit);

        var files = await FindAsync(root, pattern, cancellationToken);
        var sorted = files
            .Select(rel => (Rel: rel, Time: SafeMtime(Path.Combine(root, rel))))
            .OrderByDescending(f => f.Time)
            .ThenBy(f => f.Rel, StringComparer.Ordinal)
            .ToList();
        if (sorted.Count == 0)
        {
            return AgentToolResult.Text($"No files matched '{pattern}' in {root}.");
        }
        var sb = new StringBuilder();
        foreach (var file in sorted.Take(limit))
        {
            sb.Append(file.Rel).Append('\n');
        }
        if (sorted.Count > limit)
        {
            sb.Append($"\n[Showing {limit} of {sorted.Count} files. Use a more specific pattern or increase limit.]\n");
        }
        return AgentToolResult.Text(sb.ToString());
    }

    private static async Task<IReadOnlyList<string>> FindAsync(string root, string pattern, CancellationToken ct)
    {
        if (SearchBackend.RipgrepPath is { } rg)
        {
            var result = await ProcessRunner.RunAsync(rg, ["--files", "--hidden", "--no-require-git", "--no-messages", "-g", "!.git", "-g", pattern], root, ct);
            if (result.ExitCode is 0 or 1)
            {
                return result.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(SearchBackend.NormalizeRelative).ToArray();
            }
        }
        var glob = new GlobPattern(pattern, ignoreCase: false);
        return FileWalker.EnumerateFiles(root, ct).Where(f => glob.IsMatch(f.RelativePath)).Select(f => f.RelativePath).ToArray();
    }

    private static DateTime SafeMtime(string path)
    {
        try
        {
            return File.GetLastWriteTimeUtc(path);
        }
        catch (IOException)
        {
            return DateTime.MinValue;
        }
    }
}
