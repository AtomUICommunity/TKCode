using StreamMark.Avalonia.Transcript;
using TKCode.Desktop.ViewModels;

namespace TKCode.Desktop.Controls;

/// <summary>
/// 会话内查找（⌘F，docs/design/message-features-report.md R4）：助手正文用 StreamMark 的 <see cref="TranscriptView.Search"/>
/// （覆盖虚拟化移出屏幕的内容，结果以选区高亮），用户消息按文字匹配，命中时高亮气泡并滚动到它。
/// 两类结果按在会话中的先后合成一个列表，↵ / ⇧↵ 依次跳转。只搜索已加载的消息（更早的分页由宿主先加载）。
/// </summary>
public sealed class TranscriptFinder(TranscriptView view)
{
    /// <param name="Entry">结果所在条目的序号（排序用）。</param>
    /// <param name="Markdown">StreamMark 结果的序号；用户消息为 -1。</param>
    private readonly record struct Hit(int Entry, int Markdown, UserMessageItemViewModel? User);

    private readonly List<Hit> _hits = [];
    private UserMessageItemViewModel? _highlighted;

    public string Query { get; private set; } = "";

    public int Count => _hits.Count;

    /// <summary>当前结果序号（0 起），没有结果时为 -1。</summary>
    public int Current { get; private set; } = -1;

    /// <summary>结果或当前结果变化。</summary>
    public event Action? Changed;

    /// <summary>查找全部结果，并跳到当前视口之后（或之中）的第一个结果。</summary>
    public void Find(string query)
    {
        Query = query;
        _hits.Clear();
        SetHighlight(null);
        if (string.IsNullOrEmpty(query))
        {
            view.Search.Clear();
            Current = -1;
            Changed?.Invoke();
            return;
        }
        view.Search.Find(query);
        var markdown = view.Search.Matches;
        for (var i = 0; i < markdown.Count; i++)
        {
            _hits.Add(new Hit(markdown[i].Start.Entry, i, null));
        }
        var entries = view.Entries;
        for (var i = 0; i < entries.Count; i++)
        {
            if (entries[i] is ContentEntry { Content: UserMessageItemViewModel user } &&
                user.DisplayText.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                _hits.Add(new Hit(i, -1, user));
            }
        }
        // 稳定排序：同一条目内保持 StreamMark 的先后
        var ordered = _hits.Select((hit, order) => (hit, order)).OrderBy(x => x.hit.Entry).ThenBy(x => x.order).Select(x => x.hit).ToList();
        _hits.Clear();
        _hits.AddRange(ordered);
        if (_hits.Count == 0)
        {
            Current = -1;
            Changed?.Invoke();
            return;
        }
        var first = FirstVisibleEntry();
        var start = _hits.FindIndex(h => h.Entry >= first);
        MoveTo(start < 0 ? _hits.Count - 1 : start);
    }

    public void Next()
    {
        if (_hits.Count > 0)
        {
            MoveTo((Current + 1) % _hits.Count);
        }
    }

    public void Previous()
    {
        if (_hits.Count > 0)
        {
            MoveTo((Current - 1 + _hits.Count) % _hits.Count);
        }
    }

    /// <summary>清除结果与高亮（关闭查找条时）。</summary>
    public void Clear()
    {
        Query = "";
        _hits.Clear();
        Current = -1;
        SetHighlight(null);
        view.Search.Clear();
        Changed?.Invoke();
    }

    private void MoveTo(int index)
    {
        Current = index;
        var hit = _hits[index];
        if (hit.User is { } user)
        {
            SetHighlight(user);
            TranscriptNavigation.ScrollToItem(view, user);
        }
        else
        {
            SetHighlight(null);
            view.Search.MoveTo(hit.Markdown);
        }
        Changed?.Invoke();
    }

    private void SetHighlight(UserMessageItemViewModel? user)
    {
        if (_highlighted is not null)
        {
            _highlighted.IsSearchHit = false;
        }
        _highlighted = user;
        if (user is not null)
        {
            user.IsSearchHit = true;
        }
    }

    /// <summary>视口里第一个已实例化的模板条目的序号；没有时为 0。</summary>
    private int FirstVisibleEntry()
    {
        var entries = view.Entries;
        var first = int.MaxValue;
        foreach (var child in view.Panel.Children)
        {
            if (child.DataContext is not { } context || child.Bounds.Bottom < 0)
            {
                continue;
            }
            for (var i = 0; i < entries.Count && i < first; i++)
            {
                if (entries[i] is ContentEntry content && ReferenceEquals(content.Content, context))
                {
                    first = i;
                    break;
                }
            }
        }
        return first == int.MaxValue ? 0 : first;
    }
}
