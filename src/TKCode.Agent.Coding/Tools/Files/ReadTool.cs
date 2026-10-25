using TKCode.Agent.Diff;
using System.Text;
using System.Text.Json;
using TKCode.Ai.Messages;

namespace TKCode.Agent.Coding.Tools.Files;

/// <summary>
/// 读取文本文件（cat -n 格式带行号）或图片。文本从头截断到 2000 行 / 50KB，单行截断到 2000 字符，
/// 并提示如何用 offset 继续读取；二进制文件与目录拒绝读取。
/// </summary>
public sealed class ReadTool(ToolContext context) : IAgentTool
{
    public const int MaxLines = 2000;
    public const int MaxBytes = 50 * 1024;
    public const int MaxLineLength = 2000;
    private const long MaxImageBytes = 5 * 1024 * 1024;

    public string Name => "read";
    public string Label => "Tool.Read";

    public string Description =>
        "Read a file from the local filesystem. Returns text with line numbers in `cat -n` format (line numbers start at 1). " +
        $"Output is limited to {MaxLines} lines or {MaxBytes / 1024}KB, and lines longer than {MaxLineLength} characters are truncated; " +
        "use offset (1-based line number) and limit to page through large files. " +
        "Images (png, jpg, gif, webp, bmp) are returned as attachments. Binary files and directories cannot be read (use ls for directories). " +
        "Paths may be absolute or relative to the primary working directory.";

    public string ParametersSchemaJson => """
        {"type":"object","properties":{
          "path":{"type":"string","description":"Path to the file to read (absolute or relative to the primary working directory)."},
          "offset":{"type":"integer","minimum":1,"description":"Line number to start reading from (1-based). Only provide for large files."},
          "limit":{"type":"integer","minimum":1,"description":"Maximum number of lines to read. Only provide for large files."}},
         "required":["path"],"additionalProperties":false}
        """;

    public string PromptSnippet => "Read file contents (text with line numbers, or images)";

    public IReadOnlyList<string> PromptGuidelines =>
    [
        "Use read instead of cat/head/tail/sed to view files, and read a file before editing it.",
        "Line numbers in read output are for reference only; never include them in edit oldText or written content.",
    ];

    public ToolPermission DescribePermission(JsonElement arguments) =>
        new(ToolAccess.Read, [context.Workspace.Resolve(arguments.Required("path"))]);

    public async Task<AgentToolResult> ExecuteAsync(ToolInvocation invocation, CancellationToken cancellationToken)
    {
        var args = invocation.Arguments;
        var path = context.Workspace.Resolve(args.Required("path"));
        if (Directory.Exists(path))
        {
            return AgentToolResult.Error($"{path} is a directory. Use the ls tool to list its contents.");
        }
        if (!File.Exists(path))
        {
            return AgentToolResult.Error($"File not found: {path}");
        }

        var info = new FileInfo(path);
        if (ImageMime(path) is { } mime)
        {
            if (info.Length > MaxImageBytes)
            {
                return AgentToolResult.Error($"Image is too large to attach ({info.Length / 1024 / 1024}MB, limit {MaxImageBytes / 1024 / 1024}MB).");
            }
            var data = await File.ReadAllBytesAsync(path, cancellationToken);
            if (!context.ModelSupportsImages())
            {
                return AgentToolResult.Text($"[{path} is an image ({mime}, {data.Length} bytes); the current model cannot view images.]");
            }
            return new AgentToolResult([new TextContent($"Image {path} ({mime}, {data.Length} bytes):"), new ImageContent(Convert.ToBase64String(data), mime)]);
        }

        var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
        if (TextFile.LooksBinary(bytes))
        {
            return AgentToolResult.Error($"{path} appears to be a binary file and cannot be read as text.");
        }
        context.Files.Record(path, bytes);
        var text = TextFile.Decode(bytes).Text;
        var lines = LineDiff.SplitLines(text);
        if (lines.Length == 0)
        {
            return WithNested(path, $"[{path} is empty.]");
        }

        var offset = Math.Max(1, args.Int("offset") ?? 1);
        var limit = Math.Clamp(args.Int("limit") ?? MaxLines, 1, MaxLines);
        if (offset > lines.Length)
        {
            return AgentToolResult.Error($"offset {offset} is beyond the end of the file ({lines.Length} lines).");
        }

        var sb = new StringBuilder();
        var bytesUsed = 0;
        var last = offset - 1;
        var truncatedLines = false;
        for (var i = offset - 1; i < lines.Length && i < offset - 1 + limit; i++)
        {
            var line = lines[i];
            if (line.Length > MaxLineLength)
            {
                line = line[..MaxLineLength] + "… [line truncated]";
                truncatedLines = true;
            }
            var formatted = $"{i + 1,6}\t{line}\n";
            var size = Encoding.UTF8.GetByteCount(formatted);
            if (bytesUsed + size > MaxBytes && i > offset - 1)
            {
                break;
            }
            sb.Append(formatted);
            bytesUsed += size;
            last = i + 1;
        }
        if (last < lines.Length)
        {
            sb.Append($"\n[Showing lines {offset}-{last} of {lines.Length}. Use offset={last + 1} to continue.]\n");
        }
        else if (truncatedLines)
        {
            sb.Append("\n[Some long lines were truncated.]\n");
        }
        return WithNested(path, sb.ToString());
    }

    private AgentToolResult WithNested(string path, string text) =>
        context.NestedInstructionsFor(path) is { } nested ? AgentToolResult.Text(text + "\n" + nested) : AgentToolResult.Text(text);

    /// <summary>按文件头识别图片类型（不看扩展名）。</summary>
    public static string? ImageMime(string path)
    {
        Span<byte> head = stackalloc byte[12];
        int read;
        try
        {
            using var stream = File.OpenRead(path);
            read = stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
        head = head[..read];
        if (head.StartsWith((ReadOnlySpan<byte>)[0x89, 0x50, 0x4E, 0x47]))
        {
            return "image/png";
        }
        if (head.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xD8, 0xFF]))
        {
            return "image/jpeg";
        }
        if (head.StartsWith("GIF8"u8))
        {
            return "image/gif";
        }
        if (head.Length >= 12 && head.StartsWith("RIFF"u8) && head[8..12].SequenceEqual("WEBP"u8))
        {
            return "image/webp";
        }
        if (head.StartsWith("BM"u8) && path.EndsWith(".bmp", StringComparison.OrdinalIgnoreCase))
        {
            return "image/bmp";
        }
        return null;
    }
}
