using TKCode.Agent.Diff;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace TKCode.Agent.Coding.Tools.Files;

/// <summary>
/// 精确替换（docs/design/builtin-agent-tools.md §5.3）：edits[] 全部对原始内容匹配，必须唯一且互不重叠；
/// 精确匹配失败时按归一化规则（NFKC、行尾空白、弯引号、破折号、特殊空格）再试一次。
/// 要求先读后改，保留原文件的 BOM 与换行风格。
/// </summary>
public sealed class EditTool(ToolContext context) : IAgentTool
{
    public string Name => "edit";
    public string Label => "Tool.Edit";

    public string Description =>
        "Edit a file by replacing exact text. Each edits[].oldText must match exactly one location in the original file " +
        "(include enough surrounding lines to make it unique) and must not overlap other edits in the same call; " +
        "all edits are matched against the original file, not against the result of earlier edits. " +
        "The file must have been read with the read tool first. Do not include the line-number prefix from read output.";

    public string ParametersSchemaJson => """
        {"type":"object","properties":{
          "path":{"type":"string","description":"Path to the file to edit (absolute or relative to the primary working directory)."},
          "edits":{"type":"array","minItems":1,"description":"One or more targeted replacements.","items":{"type":"object","properties":{
            "oldText":{"type":"string","description":"Exact text for one targeted replacement. It must be unique in the original file and must not overlap with any other edits[].oldText in the same call."},
            "newText":{"type":"string","description":"Replacement text."}},
           "required":["oldText","newText"],"additionalProperties":false}}},
         "required":["path","edits"],"additionalProperties":false}
        """;

    public string PromptSnippet => "Make targeted edits to existing files (exact text replacement)";

    public IReadOnlyList<string> PromptGuidelines =>
    [
        "Prefer edit over write for changing existing files; batch several replacements for one file into a single edit call.",
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
        var edits = ParseEdits(invocation.Arguments);
        return context.Mutations.RunAsync(path, () => EditAsync(path, edits, cancellationToken), cancellationToken);
    }

    public sealed record Replacement(string OldText, string NewText);

    /// <summary>兼容模型常见的参数错误：edits 为 JSON 字符串或单个对象、使用顶层 oldText/newText 或 old_string/new_string。</summary>
    public static IReadOnlyList<Replacement> ParseEdits(JsonElement args)
    {
        if (args.TryGetProperty("edits", out var edits))
        {
            if (edits.ValueKind == JsonValueKind.String)
            {
                try
                {
                    using var doc = JsonDocument.Parse(edits.GetString()!);
                    return ParseEditList(doc.RootElement.Clone());
                }
                catch (JsonException)
                {
                    throw new ToolArgumentException("Parameter 'edits' must be an array of {oldText, newText} objects.");
                }
            }
            return ParseEditList(edits);
        }
        if (ParseOne(args) is { } single)
        {
            return [single];
        }
        throw new ToolArgumentException("Missing required parameter 'edits'.");
    }

    private static IReadOnlyList<Replacement> ParseEditList(JsonElement edits)
    {
        if (edits.ValueKind == JsonValueKind.Object && ParseOne(edits) is { } single)
        {
            return [single];
        }
        if (edits.ValueKind != JsonValueKind.Array || edits.GetArrayLength() == 0)
        {
            throw new ToolArgumentException("Parameter 'edits' must be a non-empty array of {oldText, newText} objects.");
        }
        return edits.EnumerateArray()
            .Select((e, i) => ParseOne(e) ?? throw new ToolArgumentException($"edits[{i}] must have string oldText and newText."))
            .ToArray();
    }

    private static Replacement? ParseOne(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object)
        {
            return null;
        }
        var oldText = e.String("oldText") ?? e.String("old_text") ?? e.String("old_string") ?? e.String("old_str");
        var newText = e.String("newText") ?? e.String("new_text") ?? e.String("new_string") ?? e.String("new_str");
        return oldText is null || newText is null ? null : new Replacement(oldText, newText);
    }

    private async Task<AgentToolResult> EditAsync(string path, IReadOnlyList<Replacement> edits, CancellationToken ct)
    {
        if (!File.Exists(path))
        {
            return AgentToolResult.Error($"File not found: {path}. Use the write tool to create new files.");
        }
        var bytes = await File.ReadAllBytesAsync(path, ct);
        if (context.Files.CheckFresh(path, bytes) is { } stale)
        {
            return AgentToolResult.Error(stale);
        }
        if (TextFile.LooksBinary(bytes))
        {
            return AgentToolResult.Error($"{path} is a binary file and cannot be edited.");
        }
        var file = TextFile.Decode(bytes);

        string updated;
        try
        {
            updated = Apply(file.Text, edits);
        }
        catch (EditException ex)
        {
            return AgentToolResult.Error(ex.Message.Replace("{path}", path, StringComparison.Ordinal));
        }

        updated = context.PrepareWrite(path, updated);
        var output = file.Encode(updated);
        await File.WriteAllBytesAsync(path, output, ct);
        context.Files.Record(path, output);

        var diff = LineDiff.Compute(file.Text, updated, path);
        var snapshot = bytes.Length <= FileRevert.MaxSnapshotBytes ? file.Text : null;
        var message = $"Replaced {edits.Count} block(s) in {path}.";
        if (context.WriteReminder(path, updated) is { } reminder)
        {
            message += "\n" + reminder;
        }
        if (context.NestedInstructionsFor(path) is { } nested)
        {
            message += "\n" + nested;
        }
        return AgentToolResult.Text(message) with
        {
            Details = new DiffDetails(path, diff.UnifiedDiff, diff.Added, diff.Removed, diff.FirstChangedLine,
                Before: snapshot, AfterSha256: FileRevert.Sha256(output)).ToJson(),
        };
    }

    public sealed class EditException(string message) : Exception(message);

    /// <summary>对 LF 文本应用全部替换（纯函数，便于测试）。</summary>
    public static string Apply(string text, IReadOnlyList<Replacement> edits)
    {
        Normalized? normalizedFile = null;
        var spans = new List<(int Start, int End, string Replacement, int Index)>();
        for (var i = 0; i < edits.Count; i++)
        {
            var oldText = TextFile.NormalizeNewLines(edits[i].OldText);
            var newText = TextFile.NormalizeNewLines(edits[i].NewText);
            var label = edits.Count == 1 ? "oldText" : $"edits[{i}].oldText";
            if (oldText.Length == 0)
            {
                throw new EditException($"{label} must not be empty. Use the write tool to create or overwrite a file.");
            }
            if (oldText == newText)
            {
                throw new EditException($"{label} and newText are identical; nothing to change.");
            }

            var exact = CountOccurrences(text, oldText, out var exactAt);
            if (exact == 1)
            {
                spans.Add((exactAt, exactAt + oldText.Length, newText, i));
                continue;
            }
            if (exact > 1)
            {
                throw new EditException($"Found {exact} occurrences of {label} in {{path}}. Provide more surrounding context to make it unique.");
            }

            normalizedFile ??= Normalized.From(text);
            var needle = Normalized.From(oldText).Text;
            var fuzzy = CountOccurrences(normalizedFile.Text, needle, out var at);
            if (fuzzy == 0 || needle.Length == 0)
            {
                throw new EditException($"Could not find {label} in {{path}}. It must match the file exactly, including whitespace and indentation; read the file again if unsure.");
            }
            if (fuzzy > 1)
            {
                throw new EditException($"Found {fuzzy} occurrences of {label} in {{path}}. Provide more surrounding context to make it unique.");
            }
            var start = normalizedFile.Starts[at];
            var end = normalizedFile.Ends[at + needle.Length - 1];
            spans.Add((start, end, PreserveUnchangedLines(text[start..end], oldText, newText), i));
        }

        spans.Sort((x, y) => x.Start.CompareTo(y.Start));
        for (var i = 1; i < spans.Count; i++)
        {
            if (spans[i].Start < spans[i - 1].End)
            {
                throw new EditException($"edits[{spans[i - 1].Index}] and edits[{spans[i].Index}] overlap. Merge them into a single edit.");
            }
        }
        var sb = new StringBuilder(text);
        for (var i = spans.Count - 1; i >= 0; i--)
        {
            sb.Remove(spans[i].Start, spans[i].End - spans[i].Start).Insert(spans[i].Start, spans[i].Replacement);
        }
        var result = sb.ToString();
        if (result == text)
        {
            throw new EditException("The edits did not change {path}.");
        }
        return result;
    }

    /// <summary>
    /// 归一化匹配时，oldText 与 newText 之间未改动的行沿用文件里的原始写法（例如弯引号、行尾空白），
    /// 只有真正改动的行使用 newText。匹配区间与 oldText 的行数一致（归一化不改变换行）。
    /// </summary>
    private static string PreserveUnchangedLines(string original, string oldText, string newText)
    {
        var originalLines = original.Split('\n');
        var oldLines = oldText.Split('\n');
        if (originalLines.Length != oldLines.Length)
        {
            return newText;
        }
        var newLines = newText.Split('\n');
        var parts = new List<string>(newLines.Length);
        foreach (var line in LineDiff.Diff(oldLines, newLines))
        {
            switch (line.Op)
            {
                case LineDiff.Op.Equal:
                    parts.Add(originalLines[line.OldNumber - 1]);
                    break;
                case LineDiff.Op.Insert:
                    parts.Add(line.Text);
                    break;
            }
        }
        return string.Join('\n', parts);
    }

    private static int CountOccurrences(string haystack, string needle, out int first)
    {
        first = haystack.IndexOf(needle, StringComparison.Ordinal);
        if (first < 0)
        {
            return 0;
        }
        var count = 1;
        for (var at = haystack.IndexOf(needle, first + 1, StringComparison.Ordinal); at >= 0; at = haystack.IndexOf(needle, at + 1, StringComparison.Ordinal))
        {
            count++;
        }
        return count;
    }

    /// <summary>归一化后的文本，以及每个字符在原文中的起止位置。</summary>
    private sealed class Normalized
    {
        public required string Text { get; init; }
        public required int[] Starts { get; init; }
        public required int[] Ends { get; init; }

        public static Normalized From(string source)
        {
            var sb = new StringBuilder(source.Length);
            var starts = new List<int>(source.Length);
            var ends = new List<int>(source.Length);
            var i = 0;
            while (i < source.Length)
            {
                // 行尾空白：跳过直到换行或结尾的空格 / 制表符
                if (source[i] is ' ' or '\t' || IsSpecialSpace(source[i]))
                {
                    var j = i;
                    while (j < source.Length && (source[j] is ' ' or '\t' || IsSpecialSpace(source[j])))
                    {
                        j++;
                    }
                    if (j == source.Length || source[j] == '\n')
                    {
                        i = j;
                        continue;
                    }
                }
                var length = char.IsHighSurrogate(source[i]) && i + 1 < source.Length && char.IsLowSurrogate(source[i + 1]) ? 2 : 1;
                var mapped = Map(source.AsSpan(i, length));
                foreach (var c in mapped)
                {
                    sb.Append(c);
                    starts.Add(i);
                    ends.Add(i + length);
                }
                i += length;
            }
            return new Normalized { Text = sb.ToString(), Starts = starts.ToArray(), Ends = ends.ToArray() };
        }

        private static string Map(ReadOnlySpan<char> c)
        {
            if (c.Length == 1)
            {
                switch (c[0])
                {
                    case '‘' or '’' or '‚' or '‛' or '′':
                        return "'";
                    case '“' or '”' or '„' or '‟' or '″':
                        return "\"";
                    case '‐' or '‑' or '‒' or '–' or '—' or '―' or '−':
                        return "-";
                    case var s when IsSpecialSpace(s):
                        return " ";
                    case < '\u0080':
                        return c[0].ToString();
                }
            }
            var text = c.ToString();
            try
            {
                return text.Normalize(NormalizationForm.FormKC);
            }
            catch (ArgumentException)
            {
                return text;
            }
        }

        private static bool IsSpecialSpace(char c) =>
            c is ' ' or ' ' or ' ' or '　' || (c >= ' ' && c <= ' ') ||
            (c != ' ' && c != '\t' && c != '\n' && char.GetUnicodeCategory(c) == UnicodeCategory.SpaceSeparator);
    }
}
