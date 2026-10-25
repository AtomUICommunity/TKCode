using TKCode.Agent.Diff;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using TKCode.Agent.Coding.Processes;
using TKCode.Agent.Coding.Tools.Files;
using TKCode.Agent.Coding.Workspace;

namespace TKCode.Agent.Coding.Tools.Search;

/// <summary>
/// 搜索文件内容。后端依次为 rg（--json）、git grep（仅 git 仓库）、内置实现；三者输出统一格式：
/// 匹配行 <c>path:line: text</c>，上下文行 <c>path-line- text</c>。
/// </summary>
public sealed class GrepTool(ToolContext context) : IAgentTool
{
    private const int DefaultLimit = 100;
    private const int MaxLineLength = 500;
    private const int MaxOutputBytes = 50 * 1024;

    public string Name => "grep";
    public string Label => "Tool.Grep";

    public string Description =>
        "Search file contents with a regular expression (or a literal string with literal=true). Respects .gitignore and includes dotfiles. " +
        "outputMode: 'content' (default) returns matching lines as `path:line: text` (context lines as `path-line- text`); " +
        "'files_with_matches' returns only file paths; 'count' returns match counts per file. " +
        $"Results are capped at `limit` (default {DefaultLimit}) matches or files and {MaxOutputBytes / 1024}KB; long lines are truncated to {MaxLineLength} characters.";

    public string ParametersSchemaJson => """
        {"type":"object","properties":{
          "pattern":{"type":"string","description":"Regular expression to search for (Rust/PCRE-like syntax), or a literal string when literal is true."},
          "path":{"type":"string","description":"File or directory to search (defaults to the primary working directory; pass an absolute path to search an additional working directory)."},
          "glob":{"type":"string","description":"Only search files matching this glob, e.g. '*.cs' or 'src/**/*.ts'."},
          "ignoreCase":{"type":"boolean","description":"Case-insensitive search."},
          "literal":{"type":"boolean","description":"Treat pattern as a literal string instead of a regular expression."},
          "context":{"type":"integer","minimum":0,"description":"Number of context lines to show before and after each match (content mode only)."},
          "outputMode":{"type":"string","enum":["content","files_with_matches","count"],"description":"Output format (default content)."},
          "limit":{"type":"integer","minimum":1,"description":"Maximum number of matches (content) or files (other modes). Default 100."}},
         "required":["pattern"],"additionalProperties":false}
        """;

    public string PromptSnippet => "Search file contents by regex (ripgrep-backed)";

    public IReadOnlyList<string> PromptGuidelines => ["Use grep instead of running grep/rg through bash to search file contents."];

    public ToolPermission DescribePermission(JsonElement arguments) => new(ToolAccess.Read, [context.Workspace.Resolve(arguments.String("path"))]);

    private sealed record Query(string Pattern, string Target, string? Glob, bool IgnoreCase, bool Literal, int Context, string Mode, int Limit);

    private readonly record struct Hit(string File, int Line, string Text, bool IsMatch);

    public async Task<AgentToolResult> ExecuteAsync(ToolInvocation invocation, CancellationToken cancellationToken)
    {
        var args = invocation.Arguments;
        var target = context.Workspace.Resolve(args.String("path"));
        if (!File.Exists(target) && !Directory.Exists(target))
        {
            return AgentToolResult.Error($"Path not found: {target}");
        }
        var mode = args.String("outputMode") ?? "content";
        if (mode is not ("content" or "files_with_matches" or "count"))
        {
            return AgentToolResult.Error("outputMode must be one of: content, files_with_matches, count.");
        }
        var query = new Query(args.Required("pattern"), target, args.String("glob"), args.Bool("ignoreCase") ?? false,
            args.Bool("literal") ?? false, Math.Clamp(args.Int("context") ?? 0, 0, 20), mode, Math.Max(1, args.Int("limit") ?? DefaultLimit));

        IReadOnlyList<Hit> hits;
        try
        {
            hits = await SearchAsync(query, cancellationToken);
        }
        catch (RegexParseException ex)
        {
            return AgentToolResult.Error($"Invalid regular expression: {ex.Message}. Use literal=true to search for the text as-is.");
        }
        catch (GrepBackendException ex)
        {
            return AgentToolResult.Error(ex.Message);
        }
        return AgentToolResult.Text(Format(query, hits));
    }

    private sealed class GrepBackendException(string message) : Exception(message);

    private async Task<IReadOnlyList<Hit>> SearchAsync(Query query, CancellationToken ct)
    {
        var isFile = File.Exists(query.Target);
        var dir = isFile ? Path.GetDirectoryName(query.Target)! : query.Target;
        var scope = isFile ? Path.GetFileName(query.Target) : ".";

        if (SearchBackend.RipgrepPath is { } rg)
        {
            var result = await ProcessRunner.RunAsync(rg, RgArguments(query, scope), dir, ct);
            if (result.ExitCode is 0 or 1)
            {
                return ParseRgJson(result.Stdout, dir);
            }
            if (result.ExitCode == 2 && result.Stderr.Contains("regex", StringComparison.OrdinalIgnoreCase))
            {
                throw new GrepBackendException("Invalid regular expression: " + result.Stderr.Trim());
            }
        }
        if (SearchBackend.GitPath is { } git && GitInfo.Find(dir) is not null)
        {
            var result = await ProcessRunner.RunAsync(git, GitArguments(query, scope), dir, ct);
            if (result.ExitCode is 0 or 1)
            {
                return ParseGitGrep(result.Stdout, dir);
            }
        }
        return ManagedSearch(query, dir, isFile ? query.Target : null, ct);
    }

    private static List<string> RgArguments(Query q, string scope)
    {
        List<string> args = ["--json", "--hidden", "--no-require-git", "--no-messages", "-g", "!.git"];
        if (q.IgnoreCase)
        {
            args.Add("-i");
        }
        if (q.Literal)
        {
            args.Add("-F");
        }
        if (q.Context > 0 && q.Mode == "content")
        {
            args.AddRange(["-C", q.Context.ToString()]);
        }
        if (q.Glob is { } glob)
        {
            args.AddRange(["-g", glob]);
        }
        args.AddRange(["-e", q.Pattern, "--", scope]);
        return args;
    }

    private static List<string> GitArguments(Query q, string scope)
    {
        List<string> args = ["grep", "-n", "-I", "--no-color", "--untracked", "--full-name"];
        args.Add(q.Literal ? "-F" : "-E");
        if (q.IgnoreCase)
        {
            args.Add("-i");
        }
        if (q.Context > 0 && q.Mode == "content")
        {
            args.AddRange(["-C", q.Context.ToString()]);
        }
        args.AddRange(["-e", q.Pattern, "--"]);
        if (q.Glob is { } glob)
        {
            var pathspec = glob.Contains('/') ? glob.TrimStart('/') : "**/" + glob;
            args.Add($":(glob){(scope == "." ? "" : scope + "/")}{pathspec}");
            if (!glob.Contains('/'))
            {
                args.Add($":(glob){(scope == "." ? "" : scope + "/")}{glob}");
            }
        }
        else
        {
            args.Add(scope);
        }
        return args;
    }

    private static List<Hit> ParseRgJson(string output, string dir)
    {
        var hits = new List<Hit>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                var type = root.GetProperty("type").GetString();
                if (type is not ("match" or "context"))
                {
                    continue;
                }
                var data = root.GetProperty("data");
                if (!data.GetProperty("path").TryGetProperty("text", out var pathText) || !data.GetProperty("lines").TryGetProperty("text", out var text))
                {
                    continue;
                }
                var lineNumber = data.TryGetProperty("line_number", out var n) && n.ValueKind == JsonValueKind.Number ? n.GetInt32() : 0;
                hits.Add(new Hit(Path.GetFullPath(pathText.GetString()!, dir), lineNumber, text.GetString()!.TrimEnd('\n', '\r'), type == "match"));
            }
            catch (JsonException)
            {
            }
            catch (KeyNotFoundException)
            {
            }
        }
        return hits;
    }

    private static readonly Regex GitLine = new(@"^(?<path>.+?)(?<sep>[:\-])(?<line>\d+)\k<sep>(?<text>.*)$", RegexOptions.CultureInvariant);

    private static List<Hit> ParseGitGrep(string output, string dir)
    {
        // --full-name 输出相对仓库根的路径。
        var repoRoot = GitInfo.Find(dir)?.Root ?? dir;
        var hits = new List<Hit>();
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (line == "--")
            {
                continue;
            }
            var m = GitLine.Match(line);
            if (m.Success)
            {
                hits.Add(new Hit(Path.GetFullPath(m.Groups["path"].Value, repoRoot), int.Parse(m.Groups["line"].Value),
                    m.Groups["text"].Value.TrimEnd('\r'), m.Groups["sep"].Value == ":"));
            }
        }
        return hits;
    }

    private static List<Hit> ManagedSearch(Query q, string dir, string? singleFile, CancellationToken ct)
    {
        var options = RegexOptions.CultureInvariant | RegexOptions.Multiline | (q.IgnoreCase ? RegexOptions.IgnoreCase : RegexOptions.None);
        var regex = new Regex(q.Literal ? Regex.Escape(q.Pattern) : q.Pattern, options, TimeSpan.FromSeconds(2));
        var glob = q.Glob is { } g ? new GlobPattern(g, ignoreCase: false) : null;
        var files = singleFile is not null
            ? [new FileWalker.Entry(singleFile, Path.GetFileName(singleFile))]
            : FileWalker.EnumerateFiles(dir, ct);

        var hits = new List<Hit>();
        var matches = 0;
        foreach (var file in files)
        {
            if (glob is not null && !glob.IsMatch(file.RelativePath))
            {
                continue;
            }
            byte[] bytes;
            try
            {
                if (new FileInfo(file.FullPath).Length > 10 * 1024 * 1024)
                {
                    continue;
                }
                bytes = File.ReadAllBytes(file.FullPath);
            }
            catch (IOException)
            {
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }
            if (TextFile.LooksBinary(bytes))
            {
                continue;
            }
            var lines = LineDiff.SplitLines(TextFile.Decode(bytes).Text);
            var emitted = -1;
            for (var i = 0; i < lines.Length; i++)
            {
                bool isMatch;
                try
                {
                    isMatch = regex.IsMatch(lines[i]);
                }
                catch (RegexMatchTimeoutException)
                {
                    break;
                }
                if (!isMatch)
                {
                    continue;
                }
                for (var c = Math.Max(emitted + 1, i - q.Context); c < i; c++)
                {
                    hits.Add(new Hit(file.FullPath, c + 1, lines[c], false));
                }
                hits.Add(new Hit(file.FullPath, i + 1, lines[i], true));
                emitted = i;
                var after = Math.Min(lines.Length - 1, i + q.Context);
                for (var c = i + 1; c <= after; c++)
                {
                    if (regex.IsMatch(lines[c]))
                    {
                        break;
                    }
                    hits.Add(new Hit(file.FullPath, c + 1, lines[c], false));
                    emitted = c;
                }
                if (++matches > q.Limit * 4)
                {
                    return hits;
                }
            }
        }
        return hits;
    }

    private string Format(Query q, IReadOnlyList<Hit> hits)
    {
        var matchHits = hits.Where(h => h.IsMatch).ToList();
        if (matchHits.Count == 0)
        {
            return $"No matches for '{q.Pattern}'.";
        }
        var sb = new StringBuilder();
        switch (q.Mode)
        {
            case "files_with_matches":
            {
                var files = matchHits.Select(h => h.File).Distinct(PathUtil.Comparer).ToList();
                foreach (var file in files.Take(q.Limit))
                {
                    sb.Append(Display(file)).Append('\n');
                }
                if (files.Count > q.Limit)
                {
                    sb.Append($"\n[Showing {q.Limit} of {files.Count} files.]\n");
                }
                break;
            }
            case "count":
            {
                var counts = matchHits.GroupBy(h => h.File, PathUtil.Comparer).Select(g => (File: g.Key, Count: g.Count())).ToList();
                foreach (var (file, count) in counts.Take(q.Limit))
                {
                    sb.Append(Display(file)).Append(':').Append(count).Append('\n');
                }
                if (counts.Count > q.Limit)
                {
                    sb.Append($"\n[Showing {q.Limit} of {counts.Count} files.]\n");
                }
                break;
            }
            default:
            {
                var shown = 0;
                var bytes = 0;
                string? lastFile = null;
                var lastLine = 0;
                var truncated = false;
                foreach (var hit in hits)
                {
                    if (hit.IsMatch && shown >= q.Limit)
                    {
                        truncated = true;
                        break;
                    }
                    if (lastFile is not null && q.Context > 0 && (hit.File != lastFile || hit.Line > lastLine + 1))
                    {
                        sb.Append("--\n");
                    }
                    var text = hit.Text.Length > MaxLineLength ? hit.Text[..MaxLineLength] + "…" : hit.Text;
                    var formatted = $"{Display(hit.File)}{(hit.IsMatch ? ':' : '-')}{hit.Line}{(hit.IsMatch ? ':' : '-')} {text}\n";
                    bytes += Encoding.UTF8.GetByteCount(formatted);
                    if (bytes > MaxOutputBytes)
                    {
                        truncated = true;
                        break;
                    }
                    sb.Append(formatted);
                    if (hit.IsMatch)
                    {
                        shown++;
                    }
                    lastFile = hit.File;
                    lastLine = hit.Line;
                }
                if (truncated)
                {
                    sb.Append($"\n[Output truncated after {shown} of {matchHits.Count}{(matchHits.Count >= q.Limit * 4 ? "+" : "")} matches. Narrow the pattern, path or glob, or use outputMode=files_with_matches.]\n");
                }
                break;
            }
        }
        return sb.ToString();
    }

    /// <summary>工作区内的文件显示为相对 cwd 的路径，其余显示绝对路径。</summary>
    private string Display(string file)
    {
        var relative = Path.GetRelativePath(context.Cwd, file);
        return relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative) ? file : relative.Replace('\\', '/');
    }
}
