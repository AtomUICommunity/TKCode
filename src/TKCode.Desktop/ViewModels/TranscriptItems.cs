using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StreamMark.Avalonia;
using TKCode.Desktop.Localization;
using TKCode.Rpc;

namespace TKCode.Desktop.ViewModels;

public abstract class TranscriptItemViewModel : ObservableObject;

/// <param name="segments">输入框里的样子（含 tag）；没有 tag 时为 null。</param>
public sealed partial class UserMessageItemViewModel(string text, IReadOnlyList<string>? images = null, IReadOnlyList<PromptSegmentDto>? segments = null)
    : TranscriptItemViewModel
{
    /// <summary>发送的原文（含「[Image #N: 路径]」引用；command 已展开）。</summary>
    public string Text { get; } = text;

    /// <summary>有 tag 时按输入框的样子显示（只读的 <see cref="Controls.Composer.ComposerEditor"/>）。</summary>
    public Controls.Composer.ComposerDocument? Segments { get; } =
        segments is { Count: > 0 } ? new Controls.Composer.ComposerDocument(Controls.Composer.ComposerSegments.FromDto(segments)) : null;

    public bool HasSegments => Segments is not null;

    /// <summary>显示用：图片引用折叠为「[图片 #N]」。</summary>
    public string DisplayText { get; } = McpMentions.Collapse(ImagePrompt.Collapse(text));

    /// <summary>超过这么多行就默认收起，点击展开。</summary>
    public const int CollapsedLineLimit = 8;

    /// <summary>默认收起；是否显示按钮由气泡按实际排版行数决定。放在视图模型上，行被回收再实例化时保持展开状态。</summary>
    [ObservableProperty]
    public partial bool IsExpanded { get; set; }

    /// <summary>附图（显示在气泡上方，点击全屏查看）。</summary>
    public IReadOnlyList<ImageAttachmentViewModel> Images { get; } =
        images is null ? [] : images.Select((path, i) => new ImageAttachmentViewModel(path, "", i + 1, thumbnailWidth: 480)).ToArray();

    public bool HasImages => Images.Count > 0;
}

/// <summary>一条助手消息，由按 ContentIndex 排列的块组成（思考 / 文本 / 工具调用）。</summary>
public sealed partial class AssistantMessageItemViewModel(string messageId, DateTimeOffset timestamp) : TranscriptItemViewModel
{
    private readonly Dictionary<int, AssistantBlockViewModel> _byIndex = [];

    public string MessageId { get; } = messageId;
    public DateTimeOffset Timestamp { get; } = timestamp;
    public ObservableCollection<AssistantBlockViewModel> Blocks { get; } = [];

    /// <summary>消息底部的时间，例如 “Oct 16, 10:22 AM”，格式随界面语言。</summary>
    public string TimestampText => Timestamp.ToLocalTime().ToString("g", CultureInfo.CurrentUICulture);

    /// <summary>只在回复结束且有正文时显示时间与复制按钮。</summary>
    public bool ShowFooter => !IsStreaming && Blocks.OfType<TextBlockViewModel>().Any(t => t.Text.Length > 0);

    [ObservableProperty]
    public partial bool IsStreaming { get; set; } = true;

    [ObservableProperty]
    public partial string? ErrorMessage { get; set; }

    public bool HasError => ErrorMessage is not null;

    partial void OnErrorMessageChanged(string? value) => OnPropertyChanged(nameof(HasError));

    partial void OnIsStreamingChanged(bool value) => OnPropertyChanged(nameof(ShowFooter));

    public T GetOrAdd<T>(int index, Func<T> factory) where T : AssistantBlockViewModel
    {
        if (_byIndex.TryGetValue(index, out var block) && block is T typed)
        {
            return typed;
        }
        var created = factory();
        _byIndex[index] = created;
        Blocks.Add(created);
        return created;
    }

    /// <summary>结束单个内容块；没有增量的空块不创建界面条目。</summary>
    public void CompleteBlock(int index)
    {
        if (_byIndex.TryGetValue(index, out var block))
        {
            block.IsStreaming = false;
        }
    }

    /// <summary>结束时间（耗时用）：历史消息用记录的时间戳，实时回复用结束的那一刻。</summary>
    public DateTimeOffset? EndedAt { get; private set; }

    public void Complete(DateTimeOffset? endedAt = null)
    {
        EndedAt = endedAt ?? DateTimeOffset.Now;
        IsStreaming = false;
        foreach (var block in Blocks)
        {
            block.IsStreaming = false;
        }
        OnPropertyChanged(nameof(ShowFooter));
    }

    /// <summary>复制按钮的内容：全部正文块。</summary>
    public string CopyText => string.Join("\n\n", Blocks.OfType<TextBlockViewModel>().Select(t => t.Text));

    public void RefreshLanguage()
    {
        OnPropertyChanged(nameof(TimestampText));
        foreach (var block in Blocks)
        {
            block.RefreshLanguage();
        }
    }
}

public abstract partial class AssistantBlockViewModel : ObservableObject
{
    /// <summary>把高频属性通知合并到下一次界面空闲时发出（每帧至多一次）。</summary>
    protected void NotifyLater(ref bool pending, string propertyName)
    {
        if (pending)
        {
            return;
        }
        pending = true;
        _pendingNotifications.Add(propertyName);
        if (_pendingNotifications.Count == 1)
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(FlushNotifications, Avalonia.Threading.DispatcherPriority.Background);
        }
    }

    private readonly List<string> _pendingNotifications = [];

    private void FlushNotifications()
    {
        var names = _pendingNotifications.ToArray();
        _pendingNotifications.Clear();
        ResetPending();
        foreach (var name in names)
        {
            OnPropertyChanged(name);
        }
    }

    /// <summary>派生类在通知发出前清除各自的“待通知”标记。</summary>
    protected virtual void ResetPending()
    {
    }

    [ObservableProperty]
    public partial bool IsStreaming { get; set; } = true;

    partial void OnIsStreamingChanged(bool value) => OnStreamingChanged();

    protected virtual void OnStreamingChanged()
    {
    }

    /// <summary>正在进行（流式输出中的思考、执行中的工具）：进行中的活动组只显示这些步骤，完成后收起。</summary>
    public virtual bool IsLive => false;

    public virtual void RefreshLanguage()
    {
    }
}

/// <summary>
/// 正文块。实时回复使用 StreamMark 的流式文档 <see cref="Stream"/>（经节奏控制器平滑输出，带渐显与光标），
/// <see cref="Text"/> 的追加会自动送入流中；历史消息（<see cref="FromHistory"/>）不创建流，
/// 由会话视图按需解析（行第一次进入视口时）。
/// </summary>
public sealed partial class TextBlockViewModel : AssistantBlockViewModel
{
    public TextBlockViewModel(bool live = false)
    {
        Stream = new MarkdownStream(smooth: live, profile: Controls.MarkdownSetup.Services.Profile);
    }

    private TextBlockViewModel(string historyText)
    {
        _buffer.Append(historyText);
        IsStreaming = false;
    }

    /// <summary>历史消息的正文：只保存文本，滚动到可见时才解析 Markdown。</summary>
    public static TextBlockViewModel FromHistory(string text) => new(text);

    /// <summary>流式文档；历史正文为 null（按需解析）。</summary>
    public MarkdownStream? Stream { get; }

    private readonly System.Text.StringBuilder _buffer = new();
    private string? _text;
    private bool _textNotifyPending;

    /// <summary>
    /// 全文。流式增量用 <see cref="Append"/>（直接送入流式文档、追加到缓冲区），全文在读取时才生成，
    /// 属性通知合并到每帧一次，避免长回复时每个增量都复制整段文本。
    /// </summary>
    public string Text
    {
        get => _text ??= _buffer.ToString();
        set
        {
            var old = Text;
            if (value == old)
            {
                return;
            }
            if (Stream is not null)
            {
                if (value.Length > old.Length && value.StartsWith(old, StringComparison.Ordinal))
                {
                    Stream.Push(value[old.Length..]);
                }
                else
                {
                    // 非追加的改写（极少见）：整段重置
                    Stream.Document.Reset(value);
                }
            }
            _buffer.Clear().Append(value);
            _text = value;
            OnPropertyChanged();
        }
    }

    protected override void ResetPending() => _textNotifyPending = false;

    public void Append(string delta)
    {
        if (delta.Length == 0)
        {
            return;
        }
        _buffer.Append(delta);
        _text = null;
        Stream?.Push(delta);
        NotifyLater(ref _textNotifyPending, nameof(Text));
    }

    protected override void OnStreamingChanged()
    {
        if (!IsStreaming)
        {
            Stream?.Complete();
        }
    }
}

/// <summary>
/// 思考过程：默认折叠，标题在流式阶段显示“正在思考…”，展开后在左侧引导线旁显示思考内容。
/// 右侧显示耗时与估算的 token 数（<see cref="Stats"/>）：实时回复从第一段到最后一段增量计时，历史用记录的耗时（旧记录没有耗时）。
/// </summary>
public sealed partial class ThinkingBlockViewModel(long? durationMs = null) : AssistantBlockViewModel
{
    private readonly System.Text.StringBuilder _buffer = new();
    private string? _text;
    private bool _textNotifyPending;
    private bool _statsNotifyPending;
    private long _firstDeltaAt;
    private long _lastDeltaAt;
    private long _lastStatsNotifyAt;
    private long _wideChars;
    private long _totalChars;

    /// <summary>统计（耗时 / token 数）的刷新间隔：不必逐帧更新。</summary>
    private static readonly TimeSpan StatsInterval = TimeSpan.FromMilliseconds(500);

    /// <summary>思考全文（增量用 <see cref="Append"/>；展开时的文本刷新合并到每帧一次）。</summary>
    public string Text
    {
        get => _text ??= _buffer.ToString();
        set
        {
            _buffer.Clear().Append(value);
            _text = value;
            _wideChars = _totalChars = 0;
            CountChars(value);
            OnPropertyChanged();
            OnPropertyChanged(nameof(Stats));
        }
    }

    protected override void ResetPending() => _textNotifyPending = _statsNotifyPending = false;

    private void CountChars(string text)
    {
        foreach (var c in text)
        {
            if (c >= '\u2E80')
            {
                _wideChars++;
            }
        }
        _totalChars += text.Length;
    }

    public void Append(string delta)
    {
        _buffer.Append(delta);
        _text = null;
        CountChars(delta);
        _lastDeltaAt = System.Diagnostics.Stopwatch.GetTimestamp();
        if (_firstDeltaAt == 0)
        {
            _firstDeltaAt = _lastDeltaAt;
        }
        NotifyLater(ref _textNotifyPending, nameof(Text));
        if (System.Diagnostics.Stopwatch.GetElapsedTime(_lastStatsNotifyAt, _lastDeltaAt) >= StatsInterval)
        {
            _lastStatsNotifyAt = _lastDeltaAt;
            NotifyLater(ref _statsNotifyPending, nameof(Stats));
        }
    }

    private TimeSpan? Duration => durationMs is { } ms
        ? TimeSpan.FromMilliseconds(ms)
        : _firstDeltaAt != 0 ? System.Diagnostics.Stopwatch.GetElapsedTime(_firstDeltaAt, _lastDeltaAt) : null;

    /// <summary>“12 秒 · 约 1.2k tokens”；没有记录耗时时只显示 token 数。</summary>
    public string Stats
    {
        get
        {
            var tokens = Strings.Format(TKCodeLangResourceKind.ThinkingTokensFormat, ToolCallBlockViewModel.FormatTokens(EstimateTokens(_wideChars, _totalChars)));
            return Duration is { } duration
                ? $"{ActivityGroupViewModel.ElapsedText(duration < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : duration)} · {tokens}"
                : tokens;
        }
    }

    /// <summary>粗略估算：中日韩等宽字符约 1 token / 字，其余约 4 字符 / token。</summary>
    private static long EstimateTokens(long wide, long total) => wide + (total - wide + 3) / 4;

    private bool _userToggled;

    /// <summary>流式输出时自动展开，结束后收起；用户手动展开 / 收起过则不再自动改变。</summary>
    [ObservableProperty]
    public partial bool IsExpanded { get; set; } = true;

    public string Header => Strings.Get(IsStreaming ? TKCodeLangResourceKind.Thinking : TKCodeLangResourceKind.ThoughtProcess);

    public override bool IsLive => IsStreaming;

    protected override void OnStreamingChanged()
    {
        if (!_userToggled)
        {
            IsExpanded = IsStreaming;
        }
        OnPropertyChanged(nameof(Header));
        OnPropertyChanged(nameof(Stats));
        OnPropertyChanged(nameof(IsLive));
    }

    public override void RefreshLanguage()
    {
        OnPropertyChanged(nameof(Header));
        OnPropertyChanged(nameof(Stats));
    }

    [RelayCommand]
    private void Toggle()
    {
        _userToggled = true;
        IsExpanded = !IsExpanded;
    }
}

public enum ToolCallStatus
{
    Streaming,
    Running,
    Succeeded,
    Failed,
}

public sealed partial class ToolCallBlockViewModel(string toolCallId, string toolName) : AssistantBlockViewModel
{
    public string ToolCallId { get; } = toolCallId;
    public string ToolName { get; } = toolName;

    /// <summary>类别：决定图标、动词与活动组摘要中的计数项。</summary>
    public ToolCategory Category { get; } = ToolPresentation.Classify(toolName);

    public Icons.LucideKind Icon => ToolPresentation.IconFor(Category);

    public string Verb => ToolPresentation.VerbFor(Category, ToolName);

    /// <summary>
    /// 步骤行的单行文本：主参数（命令、路径…）。参数流式到达时只扫描开头（主参数通常在最前），
    /// 参数完整后才做一次完整解析——写大文件等长参数不会因每个片段都重新解析而变慢。
    /// </summary>
    public string Summary
    {
        get
        {
            if (IsStreaming)
            {
                return _partialSummary ?? ToolPresentation.SingleLine(Head(Arguments, 160));
            }
            return _summary ??= ToolPresentation.SingleLine(PrimaryArgument ?? Arguments);
        }
    }

    /// <summary>展开后的完整参数：主参数原文，取不到时显示完整 JSON。参数流式输出时显示末尾一段（随参数刷新，长参数不整段重排）。</summary>
    public string Detail => IsStreaming ? StreamingDetail : _detail ??= PrimaryArgument ?? Arguments;

    private const int StreamingDetailLength = 2000;

    private string StreamingDetail => _args.Length <= StreamingDetailLength
        ? Arguments
        : "…" + _args.ToString(_args.Length - StreamingDetailLength, StreamingDetailLength);

    private string? PrimaryArgument => ToolPresentation.PrimaryArgument(Category, Arguments);

    private readonly System.Text.StringBuilder _args = new();
    private string? _argsText;
    private string? _summary;
    private string? _detail;
    private string? _partialSummary;
    private bool _argsNotifyPending;
    private bool _detailNotifyPending;

    /// <summary>完整（或截至目前）的参数 JSON。</summary>
    public string Arguments
    {
        get => _argsText ??= _args.ToString();
        set
        {
            _args.Clear().Append(value);
            _argsText = value;
            _summary = _detail = null;
            OnPropertyChanged();
            OnPropertyChanged(nameof(Summary));
            OnPropertyChanged(nameof(Detail));
        }
    }

    /// <summary>追加流式参数片段：只在主参数仍未确定时扫描开头，通知合并到每帧一次。</summary>
    public void AppendArguments(string fragment)
    {
        _args.Append(fragment);
        _argsText = null;
        // 主参数一旦提取到就固定；开头扫描范围内没有时也不再扫描
        if (_partialSummary is null && _args.Length - fragment.Length <= ToolPresentation.PartialScanLength)
        {
            var partial = ToolPresentation.PartialPrimaryArgument(Category, _args);
            _partialSummary = partial is null ? null : ToolPresentation.SingleLine(partial);
        }
        NotifyLater(ref _argsNotifyPending, nameof(Summary));
        NotifyLater(ref _detailNotifyPending, nameof(Detail));
    }

    protected override void ResetPending() => _argsNotifyPending = _detailNotifyPending = false;

    protected override void OnStreamingChanged()
    {
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(Detail));
    }

    private static string Head(string text, int length) => text.Length <= length ? text : text[..length];

    public bool HasResult => !string.IsNullOrEmpty(Result);

    /// <summary>工具给 UI 的结构化数据（diff、子 Agent 进度、任务清单…），见 <see cref="ParsedToolDetails"/>。</summary>
    [ObservableProperty]
    public partial string? DetailsJson { get; set; }

    private ParsedToolDetails? _parsed;

    public ParsedToolDetails? Parsed => _parsed;

    partial void OnDetailsJsonChanged(string? value)
    {
        _parsed = ParsedToolDetails.Parse(value);
        OnPropertyChanged(nameof(Parsed));
        OnPropertyChanged(nameof(HasDiff));
        OnPropertyChanged(nameof(DiffLines));
        OnPropertyChanged(nameof(DiffStats));
        OnPropertyChanged(nameof(HasSubagent));
        OnPropertyChanged(nameof(SubagentStats));
        OnPropertyChanged(nameof(ShowResult));
    }

    public bool HasDiff => _parsed is { Kind: "diff", DiffLines.Count: > 0 };

    public IReadOnlyList<DiffLineViewModel> DiffLines => _parsed?.DiffLines ?? [];

    /// <summary>“+3 −1”。</summary>
    public string DiffStats => _parsed is { Kind: "diff" } d ? $"+{d.Added} −{d.Removed}" : "";

    public bool HasSubagent => _parsed is { Kind: "subagent" };

    /// <summary>子 Agent 的进度：“reviewer · 12 次工具调用 · 34.5k tokens”。</summary>
    public string SubagentStats => _parsed is { Kind: "subagent" } a
        ? Strings.Format(TKCodeLangResourceKind.SubagentStatsFormat, a.SubagentName ?? "", a.ToolUses, FormatTokens(a.Tokens))
        : "";

    /// <summary>有 diff 时结果文本只是一句“已替换…”，不再单独显示。</summary>
    public bool ShowResult => HasResult && !HasDiff && !IsRead;

    internal static string FormatTokens(long tokens) => tokens >= 1000 ? $"{tokens / 1000.0:0.#}k" : tokens.ToString(CultureInfo.InvariantCulture);



    [ObservableProperty]
    public partial ToolCallStatus Status { get; set; } = ToolCallStatus.Streaming;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasResult))]
    [NotifyPropertyChangedFor(nameof(ShowResult))]
    public partial string? Result { get; set; }

    [ObservableProperty]
    public partial string? Duration { get; set; }

    private bool _userToggled;

    /// <summary>
    /// 执行中（参数流式输出 / 运行）自动展开，完成后收起；用户手动展开 / 收起过则不再自动改变。
    /// read 没有详情，不展开。详情视图按需创建，收起后释放（见 ToolStepView）。
    /// </summary>
    [ObservableProperty]
    public partial bool IsExpanded { get; set; } = ToolPresentation.Classify(toolName) != ToolCategory.Read;

    private void UpdateExpanded()
    {
        if (!_userToggled && !IsRead)
        {
            IsExpanded = IsBusy;
        }
    }

    public bool IsBusy => Status is ToolCallStatus.Streaming or ToolCallStatus.Running;

    public override bool IsLive => IsBusy;
    public bool IsFailed => Status == ToolCallStatus.Failed;
    public bool IsSucceeded => Status == ToolCallStatus.Succeeded;

    public string StatusText => Strings.Get(Status switch
    {
        ToolCallStatus.Streaming => TKCodeLangResourceKind.ToolPreparing,
        ToolCallStatus.Running => TKCodeLangResourceKind.ToolRunning,
        ToolCallStatus.Succeeded => TKCodeLangResourceKind.ToolDone,
        _ => TKCodeLangResourceKind.ToolFailed,
    });

    partial void OnStatusChanged(ToolCallStatus value)
    {
        OnPropertyChanged(nameof(IsBusy));
        OnPropertyChanged(nameof(IsFailed));
        OnPropertyChanged(nameof(IsSucceeded));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(IsLive));
        UpdateExpanded();
    }

    public override void RefreshLanguage()
    {
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(Verb));
        OnPropertyChanged(nameof(SubagentStats));
    }

    /// <summary>read 工具：只显示一行卡片，不展开内容；点击在右侧工作区打开文件预览。</summary>
    public bool IsRead => Category == ToolCategory.Read;

    /// <summary>read 的文件路径（参数里的原文，可能相对工作目录）；只在参数完整后取一次。</summary>
    public string? ReadPath => IsRead && !IsStreaming ? PrimaryArgument : null;

    /// <summary>由会话设置：请求在工作区打开文件（路径已解析为绝对路径）。</summary>
    public Action<string>? OpenFileRequested { get; set; }

    /// <summary>由会话设置：请求在右侧打开这个子 Agent 的转录（docs/design/subagent-view.md §6.2）。</summary>
    public Action<ToolCallBlockViewModel>? OpenSubagentRequested { get; set; }

    [RelayCommand]
    private void OpenFile()
    {
        if (ReadPath is { Length: > 0 } path)
        {
            OpenFileRequested?.Invoke(path);
        }
    }

    /// <summary>行点击：read 打开文件，其他工具展开 / 收起。</summary>
    [RelayCommand]
    private void Activate()
    {
        if (IsRead)
        {
            OpenFile();
        }
        else
        {
            Toggle();
        }
    }

    [RelayCommand]
    private void Toggle()
    {
        _userToggled = true;
        IsExpanded = !IsExpanded;
    }
}

/// <summary>
/// 活动组：两段正文之间连续的思考与工具调用（可跨多次模型请求），显示为一行可折叠的摘要。
/// 进行中只显示正在进行的步骤（流式输出中的思考、执行中的工具），步骤完成后带动画收起、计入摘要；点击摘要展开全部步骤。
/// 一轮结束后，除最后一段正文外的全部内容收进“已工作 N 分 N 秒”的工作空间组（<see cref="SetWorked"/>），默认收起。
/// </summary>
public sealed partial class ActivityGroupViewModel : TranscriptItemViewModel
{
    private IReadOnlyList<AssistantBlockViewModel> _source = [];
    private IReadOnlyList<AssistantBlockViewModel> _display = [];
    private Dictionary<AssistantBlockViewModel, ExploreStepViewModel> _explore = new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// 显示的步骤。只在组内容显示（<see cref="ShowSteps"/>）时与内容同步：收起期间（包括收起动画）保持原样，再次显示时才同步——
    /// 进行中的组就地变成工作空间组时，收起动画不会因内容变多而跳动；历史里收起的组也不创建步骤视图。
    /// </summary>
    public ObservableCollection<AssistantBlockViewModel> Steps { get; } = [];

    /// <summary>展开：显示全部步骤（用户点击摘要）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowSteps))]
    public partial bool IsExpanded { get; set; }

    /// <summary>组内有正在进行的步骤（一轮结束后不算）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowSteps))]
    public partial bool HasLiveSteps { get; private set; }

    /// <summary>组内容是否显示：展开时显示全部步骤；否则只显示正在进行的步骤，没有时整组收起。</summary>
    public bool ShowSteps => IsExpanded || HasLiveSteps;

    partial void OnIsExpandedChanged(bool value) => SyncIfShown();

    partial void OnHasLiveStepsChanged(bool value) => SyncIfShown();

    private void SyncIfShown()
    {
        if (ShowSteps)
        {
            SyncSteps(_display);
        }
    }

    /// <summary>组仍在进行（其后还没有正文，且会话在运行）。</summary>
    [ObservableProperty]
    public partial bool IsActive { get; set; }

    [ObservableProperty]
    public partial string Summary { get; private set; } = "";

    /// <summary>一轮结束后的工作空间组（“已工作 N 分 N 秒”）。</summary>
    public bool IsTurnWork { get; private set; }

    /// <summary>工作空间组的耗时。</summary>
    public TimeSpan Elapsed { get; private set; }

    private readonly HashSet<AssistantBlockViewModel> _watched = new(ReferenceEqualityComparer.Instance);

    /// <summary>只监听组内思考与工具是否仍在进行（IsLive）：进行中只显示这些步骤，完成后收起。</summary>
    private void WatchLive(IReadOnlyList<AssistantBlockViewModel> steps)
    {
        var blocks = new HashSet<AssistantBlockViewModel>(steps.Where(b => b is ThinkingBlockViewModel or ToolCallBlockViewModel), ReferenceEqualityComparer.Instance);
        foreach (var old in _watched.Where(b => !blocks.Contains(b)).ToList())
        {
            old.PropertyChanged -= OnBlockLiveChanged;
            _watched.Remove(old);
        }
        foreach (var block in blocks)
        {
            if (_watched.Add(block))
            {
                block.PropertyChanged += OnBlockLiveChanged;
            }
        }
    }

    private void OnBlockLiveChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AssistantBlockViewModel.IsLive))
        {
            RefreshLive();
        }
    }

    private void RefreshLive() => HasLiveSteps = !IsTurnWork && _watched.Any(b => b.IsLive);

    /// <summary>按顺序同步步骤（通常只是追加），连续的探索类工具合并为一个“查阅”组，并刷新摘要。</summary>
    public void SetSteps(IReadOnlyList<AssistantBlockViewModel> steps)
    {
        _source = steps;
        WatchLive(steps);
        var next = new Dictionary<AssistantBlockViewModel, ExploreStepViewModel>(ReferenceEqualityComparer.Instance);
        var display = new List<AssistantBlockViewModel>(steps.Count);
        for (var i = 0; i < steps.Count;)
        {
            if (!IsExplore(steps[i]))
            {
                display.Add(steps[i++]);
                continue;
            }
            var j = i;
            while (j < steps.Count && IsExplore(steps[j]))
            {
                j++;
            }
            if (j - i >= 2)
            {
                if (!_explore.TryGetValue(steps[i], out var explore))
                {
                    explore = new ExploreStepViewModel();
                }
                explore.SetCalls(steps.Skip(i).Take(j - i).Cast<ToolCallBlockViewModel>().ToArray());
                next[steps[i]] = explore;
                display.Add(explore);
            }
            else
            {
                display.AddRange(steps.Skip(i).Take(j - i));
            }
            i = j;
        }
        _explore = next;
        _display = display;
        RefreshLive();
        SyncIfShown();
        RefreshSummary();
    }

    /// <summary>read / 搜索：可以合并为“查阅”组的探索类工具。</summary>
    private static bool IsExplore(AssistantBlockViewModel block) =>
        block is ToolCallBlockViewModel { Category: ToolCategory.Read or ToolCategory.Search };

    // 增量同步：共同前缀保持不动，只替换后面的部分（流式追加时几乎只是 Add）
    private void SyncSteps(IReadOnlyList<AssistantBlockViewModel> desired)
    {
        var common = 0;
        while (common < desired.Count && common < Steps.Count && ReferenceEquals(desired[common], Steps[common]))
        {
            common++;
        }
        while (Steps.Count > common)
        {
            Steps.RemoveAt(Steps.Count - 1);
        }
        for (var i = common; i < desired.Count; i++)
        {
            Steps.Add(desired[i]);
        }
    }

    /// <summary>一轮结束：收进工作空间，显示耗时，默认收起（用户展开过则保持）。先于 <see cref="SetSteps"/> 调用，组先收起再换内容。</summary>
    public void SetWorked(TimeSpan elapsed)
    {
        IsTurnWork = true;
        Elapsed = elapsed;
        IsActive = false;
        RefreshLive();
        RefreshSummary();
    }

    public void RefreshSummary()
    {
        foreach (var explore in _explore.Values)
        {
            explore.RefreshLanguage();
        }
        Summary = IsTurnWork
            ? Strings.Format(TKCodeLangResourceKind.WorkedFor, ElapsedText(Elapsed))
            : ToolPresentation.Summary(
                _source.OfType<ThinkingBlockViewModel>().Count(),
                _source.OfType<ToolCallBlockViewModel>().Select(t => t.Category));
    }

    /// <summary>“4 分 30 秒”。</summary>
    public static string ElapsedText(TimeSpan elapsed)
    {
        if (elapsed.TotalHours >= 1)
        {
            return Strings.Format(TKCodeLangResourceKind.DurationHM, (int)elapsed.TotalHours, elapsed.Minutes);
        }
        if (elapsed.TotalMinutes >= 1)
        {
            return Strings.Format(TKCodeLangResourceKind.DurationMS, (int)elapsed.TotalMinutes, elapsed.Seconds);
        }
        return Strings.Format(TKCodeLangResourceKind.DurationS, (int)elapsed.TotalSeconds);
    }

    [RelayCommand]
    private void Toggle() => IsExpanded = !IsExpanded;
}

/// <summary>
/// 查阅组：连续的读取 / 搜索工具合并为一行“查阅 · 1 搜索, 4 文件”。
/// 默认收起；组内有工具仍在执行时保持展开，全部完成后自动收起（用户手动操作过则不再自动改变）。
/// </summary>
public sealed partial class ExploreStepViewModel : AssistantBlockViewModel
{
    private bool _userToggled;

    public IReadOnlyList<ToolCallBlockViewModel> Calls { get; private set; } = [];

    public string Summary { get; private set; } = "";

    [ObservableProperty]
    public partial bool IsExpanded { get; set; }

    public void SetCalls(IReadOnlyList<ToolCallBlockViewModel> calls)
    {
        // 只监听工具的执行状态：运行结束时据此收起（旧的订阅先取消，避免重复）
        foreach (var old in Calls)
        {
            old.PropertyChanged -= OnCallChanged;
        }
        foreach (var call in calls)
        {
            call.PropertyChanged += OnCallChanged;
        }
        Calls = calls;
        // 执行中：展开，不收起
        var busy = calls.Any(c => c.IsBusy);
        if (!_userToggled)
        {
            IsExpanded = busy;
        }
        OnPropertyChanged(nameof(Calls));
        OnPropertyChanged(nameof(IsLive));
        RefreshSummary();
    }

    public override bool IsLive => Calls.Any(c => c.IsBusy);

    private void OnCallChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ToolCallBlockViewModel.IsBusy))
        {
            Refresh();
        }
    }

    private void RefreshSummary()
    {
        var searches = Calls.Count(c => c.Category == ToolCategory.Search);
        var files = Calls.Count(c => c.Category == ToolCategory.Read);
        var parts = new List<string>(2);
        if (searches > 0)
        {
            parts.Add(Strings.Format(TKCodeLangResourceKind.ExploreSearchCount, searches));
        }
        if (files > 0)
        {
            parts.Add(Strings.Format(TKCodeLangResourceKind.ExploreFileCount, files));
        }
        Summary = Strings.Get(TKCodeLangResourceKind.ExploreTitle) + " · " + string.Join(", ", parts);
        OnPropertyChanged(nameof(Summary));
    }

    public override void RefreshLanguage() => RefreshSummary();

    /// <summary>工具状态变化时刷新：执行中展开，全部完成后收起。（或开始）时由活动组调用：全部完成后自动收起。</summary>
    private void Refresh()
    {
        if (!_userToggled)
        {
            IsExpanded = Calls.Any(c => c.IsBusy);
        }
        OnPropertyChanged(nameof(IsLive));
        RefreshSummary();
    }

    [RelayCommand]
    private void Toggle()
    {
        _userToggled = true;
        IsExpanded = !IsExpanded;
    }
}

/// <summary>会话顶部的提示：还有更早的消息，滚动到顶部时自动加载。</summary>
public sealed class EarlierHistoryViewModel(SessionViewModel session) : TranscriptItemViewModel
{
    public SessionViewModel Session { get; } = session;
}

/// <summary>
/// 上下文压缩的分隔卡片（docs/design/compaction.md §12）：压缩中 → 已压缩（可展开查看摘要）/ 失败；
/// 也用于显示“已停用自动压缩”的提示。
/// </summary>
public sealed partial class CompactionItemViewModel : TranscriptItemViewModel
{
    private long? _preTokens;
    private long? _postTokens;
    private string? _error;
    private string? _suspended;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Header), nameof(HasSummary))]
    public partial bool IsRunning { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSummary))]
    public partial string? Summary { get; private set; }

    [ObservableProperty]
    public partial bool IsExpanded { get; set; }

    public bool HasSummary => !IsRunning && !string.IsNullOrEmpty(Summary);
    public bool IsFailed => _error is not null || _suspended is not null;

    /// <summary>
    /// 批准计划时选了“清空上下文”（docs/design/plan-mode.md §11）：同样是历史替换，但没有摘要请求，
    /// 分隔线显示“已清空上下文，按计划执行”，展开看到的是交接消息（计划全文）。
    /// </summary>
    public bool IsHandoff { get; private init; }

    public Icons.LucideKind Icon => IsHandoff ? Icons.LucideKind.ClipboardCheck : Icons.LucideKind.Minimize2;

    public string Header =>
        _suspended is not null ? Strings.Format(TKCodeLangResourceKind.CompactionSuspendedFormat, _suspended)
        : _error is not null ? Strings.Format(TKCodeLangResourceKind.CompactionFailedFormat, _error)
        : IsHandoff ? Strings.Get(TKCodeLangResourceKind.CompactionPlanHandoff)
        : IsRunning ? Strings.Get(TKCodeLangResourceKind.CompactionRunning)
        : _preTokens is { } pre && _postTokens is { } post
            ? Strings.Format(TKCodeLangResourceKind.CompactionDoneFormat, Tokens(pre), Tokens(post))
            : Strings.Get(TKCodeLangResourceKind.CompactionDone);

    /// <param name="handoff">清空上下文后按计划执行（压缩触发类型 planHandoff）。</param>
    public static CompactionItemViewModel Running(long preTokens, bool handoff = false) =>
        new() { IsRunning = true, _preTokens = preTokens, IsHandoff = handoff };

    /// <summary>历史中的摘要消息（不知道压缩前后的 token 数）。</summary>
    public static CompactionItemViewModel FromSummary(string text) => new() { Summary = SummaryBody(text) };

    /// <summary>历史中的交接消息（清空上下文后按计划执行）。</summary>
    public static CompactionItemViewModel FromHandoff(string text) => new() { Summary = text.Trim(), IsHandoff = true };

    public static CompactionItemViewModel SuspendedNotice(string reason) => new() { _suspended = reason };

    public void Complete(long preTokens, long? postTokens, string? error)
    {
        _preTokens = preTokens;
        _postTokens = postTokens;
        _error = error;
        IsRunning = false;
        OnPropertyChanged(nameof(IsFailed));
    }

    public void SetSummary(string text) => Summary = IsHandoff ? text.Trim() : SummaryBody(text);

    [RelayCommand]
    private void Toggle()
    {
        if (HasSummary)
        {
            IsExpanded = !IsExpanded;
        }
    }

    public void RefreshLanguage() => OnPropertyChanged(nameof(Header));

    private static string Tokens(long tokens) => ModelPickerViewModel.FormatTokens((int)Math.Min(int.MaxValue, tokens));

    /// <summary>摘要消息里给模型的说明不显示，只留摘要正文。</summary>
    private static string SummaryBody(string text)
    {
        var start = text.IndexOf("Summary:\n", StringComparison.Ordinal);
        var end = text.IndexOf("\n\nIf you need specific details from before compaction", StringComparison.Ordinal);
        if (start < 0)
        {
            return text.Trim();
        }
        start += "Summary:\n".Length;
        return (end > start ? text[start..end] : text[start..]).Trim();
    }
}

/// <summary>压缩卡片：有摘要可展开时显示手形光标。</summary>
public static class CompactionCursors
{
    private static readonly Avalonia.Input.Cursor Hand = new(Avalonia.Input.StandardCursorType.Hand);

    public static Avalonia.Data.Converters.IValueConverter Of { get; } =
        new Avalonia.Data.Converters.FuncValueConverter<bool, Avalonia.Input.Cursor?>(expandable => expandable ? Hand : null);
}
