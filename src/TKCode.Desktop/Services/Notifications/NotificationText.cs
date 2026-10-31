using System.Text.RegularExpressions;
using TKCode.Desktop.Localization;

namespace TKCode.Desktop.Services.Notifications;

/// <summary>通知的标题与正文（docs/design/notifications.md §4.6）。</summary>
public static partial class NotificationText
{
    /// <summary>正文的最大长度（字符）。</summary>
    public const int MaxBodyLength = 120;

    private const int MaxTitleLength = 80;

    /// <summary>太短的最后一句（"Done."）连同前一句一起显示。</summary>
    private const int ShortSentence = 12;

    public static string Title(SessionAttention attention) =>
        OneLine(attention.Title) is { Length: > 0 } title ? Truncate(title, MaxTitleLength) : "TKCode";

    public static string Body(SessionAttention attention) => attention.Kind switch
    {
        AttentionKind.TurnCompleted => LastSentence(attention.Detail) ?? Strings.Get(TKCodeLangResourceKind.NotifyTurnFinished),
        AttentionKind.TurnFailed => Truncate(Strings.Format(TKCodeLangResourceKind.NotifyTurnFailedFormat,
            FirstLine(attention.Detail) ?? Strings.Get(TKCodeLangResourceKind.RequestFailed)), MaxBodyLength),
        AttentionKind.Approval => Truncate(Strings.Format(TKCodeLangResourceKind.NotifyApprovalFormat, FirstLine(attention.Detail) ?? ""), MaxBodyLength),
        AttentionKind.Question => Strings.Get(TKCodeLangResourceKind.NotifyQuestion),
        AttentionKind.PlanReview => Strings.Get(TKCodeLangResourceKind.NotifyPlanReview),
        AttentionKind.Elicitation => Truncate(Strings.Format(TKCodeLangResourceKind.NotifyElicitationFormat,
            FirstLine(attention.Detail) ?? "MCP"), MaxBodyLength),
        _ => "",
    };

    /// <summary>
    /// 回复的最后一句：跳过代码块，取最后一个非空行，去掉 Markdown 记号，再取其中最后一句（中英文句号都算），截断到 <paramref name="max"/>。
    /// 没有正文时为 null。
    /// </summary>
    public static string? LastSentence(string? markdown, int max = MaxBodyLength)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return null;
        }
        string? last = null;
        var inFence = false;
        foreach (var raw in markdown.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("```", StringComparison.Ordinal) || line.StartsWith("~~~", StringComparison.Ordinal))
            {
                inFence = !inFence;
                continue;
            }
            if (inFence)
            {
                continue;
            }
            var plain = StripMarkdown(line);
            if (plain.Length > 0)
            {
                last = plain;
            }
        }
        if (last is null)
        {
            return null;
        }
        var sentences = SentenceBreak().Split(last).Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
        if (sentences.Count == 0)
        {
            return Truncate(last, max);
        }
        var sentence = sentences[^1];
        if (sentence.Length < ShortSentence && sentences.Count > 1)
        {
            var previous = sentences[^2];
            sentence = IsWide(previous[^1]) ? previous + sentence : previous + " " + sentence;
        }
        return Truncate(sentence, max);
    }

    /// <summary>去掉行首的标题 / 引用 / 列表 / 任务记号、表格竖线，以及行内的链接、图片、强调与代码记号。</summary>
    internal static string StripMarkdown(string line)
    {
        var text = line;
        // 表格：分隔行整行丢弃，其余行把竖线换成空格
        if (text.StartsWith('|'))
        {
            if (TableSeparator().IsMatch(text))
            {
                return "";
            }
            text = text.Replace('|', ' ');
        }
        text = LinePrefix().Replace(text, "");
        text = Image().Replace(text, "$1");
        text = Link().Replace(text, "$1");
        text = text.Replace("**", "").Replace("__", "").Replace("~~", "").Replace("`", "");
        // 水平线
        if (text.Trim() is "---" or "***" or "___")
        {
            return "";
        }
        return Whitespace().Replace(text, " ").Trim();
    }

    /// <summary>截断到 <paramref name="max"/> 个字符（含省略号），不拆开代理对。</summary>
    public static string Truncate(string text, int max)
    {
        if (text.Length <= max)
        {
            return text;
        }
        var cut = max - 1;
        if (cut > 0 && char.IsHighSurrogate(text[cut - 1]))
        {
            cut--;
        }
        return text[..cut].TrimEnd() + "…";
    }

    private static string? FirstLine(string? text) =>
        text?.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0);

    private static string OneLine(string? text) => text is null ? "" : Whitespace().Replace(text, " ").Trim();

    /// <summary>全角标点后不加空格。</summary>
    private static bool IsWide(char c) => c is '。' or '！' or '？';

    [GeneratedRegex(@"(?<=[.!?])\s+|(?<=[。！？])")]
    private static partial Regex SentenceBreak();

    [GeneratedRegex(@"^\s*(?:#{1,6}\s+|(?:>\s*)+|[-*+]\s+(?:\[[ xX]\]\s+)?|\d+[.)]\s+(?:\[[ xX]\]\s+)?)")]
    private static partial Regex LinePrefix();

    [GeneratedRegex(@"!\[([^\]]*)\]\([^)]*\)")]
    private static partial Regex Image();

    [GeneratedRegex(@"\[([^\]]+)\]\([^)]*\)")]
    private static partial Regex Link();

    [GeneratedRegex(@"^\|?\s*:?-{2,}:?\s*(\|\s*:?-{2,}:?\s*)*\|?\s*$")]
    private static partial Regex TableSeparator();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
