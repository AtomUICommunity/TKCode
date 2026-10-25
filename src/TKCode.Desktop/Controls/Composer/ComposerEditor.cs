using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.Threading;
using TKCode.Arc;

namespace TKCode.Desktop.Controls.Composer;

/// <summary>
/// 自绘的输入框（docs/design/composer.md §6）：正文与内联 tag 混排，tag 像一个字符一样参与光标移动、选择与删除。
/// 排版见 <see cref="ComposerLayout"/>，文档模型见 <see cref="ComposerDocument"/>。
/// 高度随内容在 <see cref="MinLines"/> 与 <see cref="MaxLines"/> 之间自动增长，超出后在内部滚动。
/// Enter 不在这里处理（由宿主决定发送）；Shift+Enter 插入换行。
/// </summary>
public partial class ComposerEditor : Control, IExpandableLineSource
{
    public static readonly StyledProperty<FontFamily> FontFamilyProperty = TextElement.FontFamilyProperty.AddOwner<ComposerEditor>();
    public static readonly StyledProperty<double> FontSizeProperty = TextElement.FontSizeProperty.AddOwner<ComposerEditor>();
    public static readonly StyledProperty<FontWeight> FontWeightProperty = TextElement.FontWeightProperty.AddOwner<ComposerEditor>();
    public static readonly StyledProperty<FontStyle> FontStyleProperty = TextElement.FontStyleProperty.AddOwner<ComposerEditor>();
    public static readonly StyledProperty<IBrush?> ForegroundProperty = TextElement.ForegroundProperty.AddOwner<ComposerEditor>();
    public static readonly StyledProperty<Thickness> PaddingProperty = TemplatedControl.PaddingProperty.AddOwner<ComposerEditor>();

    /// <summary>行高；NaN 表示按字体的自然行高。</summary>
    public static readonly StyledProperty<double> LineHeightProperty =
        AvaloniaProperty.Register<ComposerEditor, double>(nameof(LineHeight), double.NaN);

    public static readonly StyledProperty<int> MinLinesProperty = AvaloniaProperty.Register<ComposerEditor, int>(nameof(MinLines), 1);

    /// <summary>最多显示的行数，超出后内部滚动；0 表示不限。</summary>
    public static readonly StyledProperty<int> MaxLinesProperty = AvaloniaProperty.Register<ComposerEditor, int>(nameof(MaxLines));

    public static readonly StyledProperty<string?> PlaceholderTextProperty = AvaloniaProperty.Register<ComposerEditor, string?>(nameof(PlaceholderText));
    public static readonly StyledProperty<IBrush?> PlaceholderForegroundProperty = AvaloniaProperty.Register<ComposerEditor, IBrush?>(nameof(PlaceholderForeground));
    public static readonly StyledProperty<IBrush?> SelectionBrushProperty = AvaloniaProperty.Register<ComposerEditor, IBrush?>(nameof(SelectionBrush));
    public static readonly StyledProperty<IBrush?> CaretBrushProperty = AvaloniaProperty.Register<ComposerEditor, IBrush?>(nameof(CaretBrush));
    public static readonly StyledProperty<IBrush?> TagBackgroundProperty = AvaloniaProperty.Register<ComposerEditor, IBrush?>(nameof(TagBackground));
    public static readonly StyledProperty<IBrush?> TagForegroundProperty = AvaloniaProperty.Register<ComposerEditor, IBrush?>(nameof(TagForeground));
    public static readonly StyledProperty<double> TagMaxWidthProperty = AvaloniaProperty.Register<ComposerEditor, double>(nameof(TagMaxWidth), 240);

    /// <summary>只读（转录里的用户消息）：可以选择和复制，不能编辑，不显示光标，宽度按内容。</summary>
    public static readonly StyledProperty<bool> IsReadOnlyProperty = AvaloniaProperty.Register<ComposerEditor, bool>(nameof(IsReadOnly));

    public static readonly DirectProperty<ComposerEditor, ComposerDocument> DocumentProperty =
        AvaloniaProperty.RegisterDirect<ComposerEditor, ComposerDocument>(nameof(Document), e => e.Document, (e, v) => e.Document = v);

    public static readonly DirectProperty<ComposerEditor, int> CaretIndexProperty =
        AvaloniaProperty.RegisterDirect<ComposerEditor, int>(nameof(CaretIndex), e => e.CaretIndex, (e, v) => e.CaretIndex = v);

    /// <summary>粘贴之前触发：宿主可以先处理（例如剪贴板里是图片），设置 Handled 后不再粘贴文本。</summary>
    public static readonly RoutedEvent<RoutedEventArgs> PastingEvent =
        RoutedEvent.Register<ComposerEditor, RoutedEventArgs>(nameof(Pasting), RoutingStrategies.Bubble);

    private static readonly TimeSpan BlinkInterval = TimeSpan.FromMilliseconds(530);

    private ComposerDocument _document = new();
    private ComposerLayout? _layout;
    private double _layoutWidth = double.NaN;
    private int _layoutVersion = -1;
    private bool _layoutDirty = true;
    private int _caret;
    private int _anchor;
    private double? _desiredX;
    private double _scrollY;
    private bool _caretVisible = true;
    private readonly DispatcherTimer _blink;
    // 鼠标拖选：按下时的点击次数（2 = 按单词扩展，3 = 按段落扩展）与初始范围
    private int _clickCount;
    private (int Start, int End) _clickRange;
    private bool _selecting;

    static ComposerEditor()
    {
        FocusableProperty.OverrideDefaultValue<ComposerEditor>(true);
        CursorProperty.OverrideDefaultValue<ComposerEditor>(new Cursor(StandardCursorType.Ibeam));
        AffectsMeasure<ComposerEditor>(FontFamilyProperty, FontSizeProperty, FontWeightProperty, FontStyleProperty, PaddingProperty,
            LineHeightProperty, MinLinesProperty, MaxLinesProperty, TagMaxWidthProperty, IsReadOnlyProperty);
        AffectsRender<ComposerEditor>(ForegroundProperty, PlaceholderTextProperty, PlaceholderForegroundProperty, SelectionBrushProperty,
            CaretBrushProperty, TagBackgroundProperty, TagForegroundProperty);
    }

    public ComposerEditor()
    {
        _document.Changed += OnDocumentChanged;
        _ime = new ImeClient(this);
        AddHandler(TextInputMethodClientRequestedEvent, (_, e) => e.Client = IsReadOnly ? null : _ime);
        _blink = new DispatcherTimer { Interval = BlinkInterval };
        _blink.Tick += (_, _) =>
        {
            _caretVisible = !_caretVisible;
            InvalidateVisual();
        };
    }

    public FontFamily FontFamily { get => GetValue(FontFamilyProperty); set => SetValue(FontFamilyProperty, value); }
    public double FontSize { get => GetValue(FontSizeProperty); set => SetValue(FontSizeProperty, value); }
    public FontWeight FontWeight { get => GetValue(FontWeightProperty); set => SetValue(FontWeightProperty, value); }
    public FontStyle FontStyle { get => GetValue(FontStyleProperty); set => SetValue(FontStyleProperty, value); }
    public IBrush? Foreground { get => GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }
    public Thickness Padding { get => GetValue(PaddingProperty); set => SetValue(PaddingProperty, value); }
    public double LineHeight { get => GetValue(LineHeightProperty); set => SetValue(LineHeightProperty, value); }
    public int MinLines { get => GetValue(MinLinesProperty); set => SetValue(MinLinesProperty, value); }
    public int MaxLines { get => GetValue(MaxLinesProperty); set => SetValue(MaxLinesProperty, value); }
    public string? PlaceholderText { get => GetValue(PlaceholderTextProperty); set => SetValue(PlaceholderTextProperty, value); }
    public IBrush? PlaceholderForeground { get => GetValue(PlaceholderForegroundProperty); set => SetValue(PlaceholderForegroundProperty, value); }
    public IBrush? SelectionBrush { get => GetValue(SelectionBrushProperty); set => SetValue(SelectionBrushProperty, value); }
    public IBrush? CaretBrush { get => GetValue(CaretBrushProperty); set => SetValue(CaretBrushProperty, value); }
    public IBrush? TagBackground { get => GetValue(TagBackgroundProperty); set => SetValue(TagBackgroundProperty, value); }
    public IBrush? TagForeground { get => GetValue(TagForegroundProperty); set => SetValue(TagForegroundProperty, value); }
    public double TagMaxWidth { get => GetValue(TagMaxWidthProperty); set => SetValue(TagMaxWidthProperty, value); }
    public bool IsReadOnly { get => GetValue(IsReadOnlyProperty); set => SetValue(IsReadOnlyProperty, value); }

    public ComposerDocument Document
    {
        get => _document;
        set
        {
            // 绑定源为 null（例如模板里没有 tag 的消息）时当作空文档
            value ??= new ComposerDocument();
            var old = _document;
            if (SetAndRaise(DocumentProperty, ref _document, value))
            {
                old.Changed -= OnDocumentChanged;
                value.Changed += OnDocumentChanged;
                _caret = _anchor = Math.Min(_caret, value.Length);
                _layoutDirty = true;
                InvalidateMeasure();
                ContentChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    /// <summary>光标位置；设置时清除选区。</summary>
    public int CaretIndex
    {
        get => _caret;
        set => Select(value, value);
    }

    public int SelectionStart => Math.Min(_anchor, _caret);

    public int SelectionEnd => Math.Max(_anchor, _caret);

    public bool HasSelection => _anchor != _caret;

    /// <summary>光标在第一行（折行后的显示行）。↑ 翻历史输入时用。</summary>
    public bool IsCaretOnFirstLine => EnsureLayout().LineIndexOf(ToLayout(_caret)) == 0;

    /// <summary>光标在最后一行（折行后的显示行）。↓ 翻历史输入时用。</summary>
    public bool IsCaretOnLastLine
    {
        get
        {
            var layout = EnsureLayout();
            return layout.LineIndexOf(ToLayout(_caret)) >= layout.LineCount - 1;
        }
    }

    /// <summary>光标或选区变化。</summary>
    public event EventHandler? SelectionChanged;

    /// <summary>文档内容变化（包括替换整个文档）。</summary>
    public event EventHandler? ContentChanged;

    public event EventHandler<RoutedEventArgs>? Pasting
    {
        add => AddHandler(PastingEvent, value);
        remove => RemoveHandler(PastingEvent, value);
    }

    /// <summary>单行（不折行）时内容的宽度，宿主据此判断紧凑形态能否放下。</summary>
    public double SingleLineWidth
    {
        get
        {
            using var layout = CreateLayout(double.PositiveInfinity);
            return layout.Width;
        }
    }

    /// <summary>设置选区：<paramref name="anchor"/> 是固定端，<paramref name="caret"/> 是光标所在的活动端。</summary>
    public void Select(int anchor, int caret)
    {
        anchor = Math.Clamp(anchor, 0, _document.Length);
        caret = Math.Clamp(caret, 0, _document.Length);
        if (anchor == _anchor && caret == _caret)
        {
            return;
        }
        var oldCaret = _caret;
        _anchor = anchor;
        _caret = caret;
        if (caret != oldCaret)
        {
            _document.BreakUndoGroup();
            RaisePropertyChanged(CaretIndexProperty, oldCaret, caret);
        }
        AfterSelectionChanged();
    }

    private void AfterSelectionChanged()
    {
        ResetBlink();
        EnsureCaretVisible();
        InvalidateVisual();
        _ime?.NotifySelection();
        _ime?.NotifyCursorRectangle();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SelectAll() => Select(0, _document.Length);

    // ---- 编辑：都以选区为对象，完成后光标落在插入内容之后 ----

    /// <summary>用文本替换选区（键入、粘贴）。</summary>
    public void InsertText(string text, EditKind kind = EditKind.Other)
    {
        if (IsReadOnly)
        {
            return;
        }
        var start = SelectionStart;
        var caret = Own(() => _document.Replace(start, SelectionEnd - start, text.Length == 0 ? [] : [new TextSegment(text)],
            HasSelection ? EditKind.Other : kind));
        Select(caret, caret, keepUndoGroup: true);
    }

    /// <summary>用片段替换选区（插入 tag、粘贴内部格式）。</summary>
    public void InsertSegments(IReadOnlyList<Segment> segments)
    {
        var start = SelectionStart;
        var caret = Own(() => _document.Replace(start, SelectionEnd - start, segments));
        Select(caret, caret);
    }

    /// <summary>替换文档中的一段（补全确认时把 @查询 换成 tag）。</summary>
    public void ReplaceRange(int start, int length, IReadOnlyList<Segment> segments)
    {
        var caret = Own(() => _document.Replace(start, length, segments));
        Select(caret, caret);
    }

    /// <summary>退格：有选区时删除选区；否则删除前一个字符（代理对整体删除）或前一个单词。</summary>
    public void Backspace(bool word = false)
    {
        if (IsReadOnly)
        {
            return;
        }
        if (HasSelection)
        {
            DeleteSelection();
            return;
        }
        if (_caret == 0)
        {
            return;
        }
        var start = word ? _document.PreviousWordBoundary(_caret) : PreviousCaretStop(_caret);
        Own(() => _document.Delete(start, _caret - start, EditKind.DeleteBackward));
        Select(start, start, keepUndoGroup: true);
    }

    /// <summary>向前删除：有选区时删除选区；否则删除后一个字符或后一个单词。</summary>
    public void DeleteForward(bool word = false)
    {
        if (IsReadOnly)
        {
            return;
        }
        if (HasSelection)
        {
            DeleteSelection();
            return;
        }
        if (_caret == _document.Length)
        {
            return;
        }
        var end = word ? _document.NextWordBoundary(_caret) : NextCaretStop(_caret);
        Own(() => _document.Delete(_caret, end - _caret, EditKind.DeleteForward));
        Select(_caret, _caret, keepUndoGroup: true);
    }

    public void DeleteSelection()
    {
        if (!HasSelection || IsReadOnly)
        {
            return;
        }
        var start = SelectionStart;
        Own(() => _document.Delete(start, SelectionEnd - start));
        Select(start, start);
    }

    public void Undo()
    {
        if (IsReadOnly)
        {
            return;
        }
        if (Own(() => _document.Undo()) is { } caret)
        {
            Select(caret, caret);
        }
    }

    public void Redo()
    {
        if (IsReadOnly)
        {
            return;
        }
        if (Own(() => _document.Redo()) is { } caret)
        {
            Select(caret, caret);
        }
    }

    /// <summary>上一个光标停靠点：代理对不拆开。</summary>
    private int PreviousCaretStop(int position)
    {
        if (position >= 2 && _document.CharAt(position - 1) is { } low && char.IsLowSurrogate(low) &&
            _document.CharAt(position - 2) is { } high && char.IsHighSurrogate(high))
        {
            return position - 2;
        }
        return Math.Max(0, position - 1);
    }

    private int NextCaretStop(int position)
    {
        if (_document.CharAt(position) is { } high && char.IsHighSurrogate(high) &&
            _document.CharAt(position + 1) is { } low && char.IsLowSurrogate(low))
        {
            return position + 2;
        }
        return Math.Min(_document.Length, position + 1);
    }

    private void Select(int anchor, int caret, bool keepUndoGroup)
    {
        if (!keepUndoGroup)
        {
            Select(anchor, caret);
            return;
        }
        // 键入、退格之后的光标移动不结束撤销分组（否则每个字符都成为单独一步）
        var oldCaret = _caret;
        _anchor = Math.Clamp(anchor, 0, _document.Length);
        _caret = Math.Clamp(caret, 0, _document.Length);
        _desiredX = null;
        if (_caret != oldCaret)
        {
            RaisePropertyChanged(CaretIndexProperty, oldCaret, _caret);
        }
        AfterSelectionChanged();
    }

    // ---- 剪贴板 ----

    private IClipboard? Clipboard => TopLevel.GetTopLevel(this)?.Clipboard;

    public async Task CopyAsync()
    {
        if (HasSelection && Clipboard is { } clipboard)
        {
            await ComposerClipboard.CopyAsync(clipboard, _document.Slice(SelectionStart, SelectionEnd - SelectionStart));
        }
    }

    public async Task CutAsync()
    {
        if (HasSelection && !IsReadOnly)
        {
            await CopyAsync();
            DeleteSelection();
        }
    }

    /// <summary>粘贴：先触发 <see cref="PastingEvent"/>，宿主没有处理时插入剪贴板内容（内部格式还原 tag）。</summary>
    public async Task PasteAsync(bool raiseEvent = true)
    {
        if (IsReadOnly)
        {
            return;
        }
        if (raiseEvent)
        {
            var args = new RoutedEventArgs(PastingEvent);
            RaiseEvent(args);
            if (args.Handled)
            {
                return;
            }
        }
        if (Clipboard is { } clipboard && await ComposerClipboard.ReadAsync(clipboard) is { } segments)
        {
            InsertSegments(segments.Select(s => s is TextSegment t ? new TextSegment(t.Text.ReplaceLineEndings("\n")) : s).ToList());
        }
    }

    // ---- 键盘 ----

    protected override void OnTextInput(TextInputEventArgs e)
    {
        base.OnTextInput(e);
        if (e.Handled || string.IsNullOrEmpty(e.Text))
        {
            return;
        }
        // 控制字符（Tab、退格等）由按键处理
        var text = new string(e.Text.Where(c => !char.IsControl(c) || c == '\n').ToArray());
        if (text.Length == 0)
        {
            return;
        }
        // 输入法提交：预编辑文字由提交的文字取代
        _preedit = null;
        _preeditCursor = null;
        _layoutDirty = true;
        InsertText(text, EditKind.Typing);
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled)
        {
            return;
        }
        var mac = OperatingSystem.IsMacOS();
        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        var command = e.KeyModifiers.HasFlag(mac ? KeyModifiers.Meta : KeyModifiers.Control);
        // 按单词移动：macOS 为 Option，其他平台为 Ctrl
        var word = e.KeyModifiers.HasFlag(mac ? KeyModifiers.Alt : KeyModifiers.Control);
        // 到行首行尾：macOS 为 Cmd+←/→
        var line = mac && e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        e.Handled = true;
        switch (e.Key)
        {
            case Key.Left:
                Move(!shift && HasSelection && !word && !line ? SelectionStart
                    : line ? LineStart(_caret) : word ? _document.PreviousWordBoundary(_caret) : PreviousCaretStop(_caret), shift);
                break;
            case Key.Right:
                Move(!shift && HasSelection && !word && !line ? SelectionEnd
                    : line ? LineEnd(_caret) : word ? _document.NextWordBoundary(_caret) : NextCaretStop(_caret), shift);
                break;
            case Key.Up when command && mac:
                Move(0, shift);
                break;
            case Key.Down when command && mac:
                Move(_document.Length, shift);
                break;
            case Key.Up:
                MoveVertical(-1, shift);
                break;
            case Key.Down:
                MoveVertical(1, shift);
                break;
            case Key.Home:
                Move(command ? 0 : LineStart(_caret), shift);
                break;
            case Key.End:
                Move(command ? _document.Length : LineEnd(_caret), shift);
                break;
            case Key.Back:
                Backspace(word);
                break;
            case Key.Delete:
                DeleteForward(word);
                break;
            case Key.Enter when shift:
                InsertText("\n", EditKind.Typing);
                break;
            case Key.A when command:
                SelectAll();
                break;
            case Key.C when command:
                _ = CopyAsync();
                break;
            case Key.X when command:
                _ = CutAsync();
                break;
            case Key.V when command:
                _ = PasteAsync();
                break;
            case Key.Z when command && shift:
            case Key.Y when command && !mac:
                Redo();
                break;
            case Key.Z when command:
                Undo();
                break;
            default:
                e.Handled = false;
                break;
        }
    }

    private void Move(int position, bool extend)
    {
        _desiredX = null;
        Select(extend ? _anchor : position, position);
    }

    private void MoveVertical(int delta, bool extend)
    {
        var layout = EnsureLayout();
        var index = layout.LineIndexOf(ToLayout(_caret)) + delta;
        int target;
        var desired = _desiredX ?? layout.CaretRect(ToLayout(_caret)).X;
        if (index < 0)
        {
            target = 0;
        }
        else if (index >= layout.LineCount)
        {
            target = _document.Length;
        }
        else
        {
            target = FromLayout(layout.HitTestLine(index, desired));
        }
        Select(extend ? _anchor : target, target);
        _desiredX = desired;
    }

    private int LineStart(int position)
    {
        var layout = EnsureLayout();
        return FromLayout(layout.LineRange(layout.LineIndexOf(ToLayout(position))).Start);
    }

    private int LineEnd(int position)
    {
        var layout = EnsureLayout();
        var index = layout.LineIndexOf(ToLayout(position));
        var (_, end) = layout.LineRange(index);
        // 折行处的行尾停在最后一个字符之前（否则光标显示在下一行开头）
        return FromLayout(index + 1 < layout.LineCount && layout.LineRange(index + 1).Start == end && end > 0 ? end - 1 : end);
    }

    // ---- 鼠标 ----

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var point = e.GetCurrentPoint(this);
        if (!point.Properties.IsLeftButtonPressed)
        {
            return;
        }
        Focus();
        var position = HitTest(point.Position);
        _clickCount = e.ClickCount;
        _desiredX = null;
        switch (e.ClickCount)
        {
            case 2:
                _clickRange = _document.WordAt(position);
                Select(_clickRange.Start, _clickRange.End);
                break;
            case >= 3:
                _clickRange = ParagraphAt(position);
                Select(_clickRange.Start, _clickRange.End);
                break;
            default:
                _clickRange = (position, position);
                Select(e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? _anchor : position, position);
                break;
        }
        _selecting = true;
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!_selecting)
        {
            UpdateTagToolTip(e.GetPosition(this));
            return;
        }
        var position = HitTest(e.GetPosition(this));
        if (_clickCount < 2)
        {
            Select(_anchor, position);
            return;
        }
        // 双击 / 三击后拖动：按单词 / 段落扩展，始终包含最初的范围
        var range = _clickCount == 2 ? _document.WordAt(position) : ParagraphAt(position);
        if (position < _clickRange.Start)
        {
            Select(_clickRange.End, range.Start);
        }
        else
        {
            Select(_clickRange.Start, Math.Max(range.End, _clickRange.End));
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_selecting)
        {
            _selecting = false;
            e.Pointer.Capture(null);
        }
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        var max = MaxScroll();
        if (max <= 0)
        {
            return;
        }
        var scroll = Math.Clamp(_scrollY - e.Delta.Y * 50, 0, max);
        if (scroll != _scrollY)
        {
            _scrollY = scroll;
            InvalidateVisual();
            e.Handled = true;
        }
    }

    /// <summary>控件坐标到文档位置。</summary>
    public int HitTest(Point point) =>
        FromLayout(EnsureLayout().HitTest(new Point(point.X - Padding.Left, point.Y - Padding.Top + _scrollY)));

    /// <summary>位置处光标的矩形（控件坐标，宽度为 0）：输入法候选框、补全列表定位使用。</summary>
    public Rect CaretBounds(int position) =>
        EnsureLayout().CaretRect(ToLayout(Math.Clamp(position, 0, _document.Length))).Translate(new Vector(Padding.Left, Padding.Top - _scrollY));

    private (int Start, int End) ParagraphAt(int position)
    {
        var start = position;
        while (start > 0 && _document.CharAt(start - 1) != '\n')
        {
            start--;
        }
        var end = position;
        while (end < _document.Length && _document.CharAt(end) != '\n')
        {
            end++;
        }
        return (start, end);
    }

    private TagSegment? _toolTipTag;

    private void UpdateTagToolTip(Point point)
    {
        var tag = EnsureLayout().TagAt(new Point(point.X - Padding.Left, point.Y - Padding.Top + _scrollY))?.Run.Tag;
        if (ReferenceEquals(tag, _toolTipTag))
        {
            return;
        }
        _toolTipTag = tag;
        ToolTip.SetTip(this, tag is null ? null : TagTip(tag));
        ToolTip.SetIsOpen(this, tag is not null);
    }

    /// <summary>tag 的悬停提示：附件文件为绝对路径，图片为占位名称，其余为纯文本。</summary>
    protected virtual string TagTip(TagSegment tag) => tag.Kind switch
    {
        TagKind.Image => tag.Label,
        TagKind.AttachedFile => tag.Value,
        _ => tag.ToPlainText(),
    };

    // ---- 焦点与光标闪烁 ----

    protected override void OnGotFocus(FocusChangedEventArgs e)
    {
        base.OnGotFocus(e);
        ResetBlink();
        InvalidateVisual();
    }

    protected override void OnLostFocus(FocusChangedEventArgs e)
    {
        base.OnLostFocus(e);
        _blink.Stop();
        _document.BreakUndoGroup();
        InvalidateVisual();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _blink.Stop();
    }

    private void ResetBlink()
    {
        _caretVisible = true;
        _blink.Stop();
        if (IsFocused)
        {
            _blink.Start();
        }
    }

    // ---- 排版与绘制 ----

    // 正在由本控件修改文档（键入、删除、撤销等）：光标由调用处设置
    private int _ownEdits;

    private T Own<T>(Func<T> edit)
    {
        _ownEdits++;
        try
        {
            return edit();
        }
        finally
        {
            _ownEdits--;
        }
    }

    private void Own(Action edit) => Own(() =>
    {
        edit();
        return 0;
    });

    private void OnDocumentChanged(DocumentChange change)
    {
        _layoutDirty = true;
        // 外部修改（设置文本、恢复草稿、补全插入、删除图片 tag）：光标移到变化范围的末尾
        if (_ownEdits == 0)
        {
            _preedit = null;
            var end = Math.Min(change.Start + change.InsertedLength, _document.Length);
            Select(end, end);
        }
        InvalidateMeasure();
        InvalidateVisual();
        _ime?.NotifySurroundingText();
        ContentChanged?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == FontFamilyProperty || change.Property == FontSizeProperty || change.Property == FontWeightProperty ||
            change.Property == FontStyleProperty || change.Property == LineHeightProperty || change.Property == ForegroundProperty ||
            change.Property == TagBackgroundProperty || change.Property == TagForegroundProperty || change.Property == TagMaxWidthProperty)
        {
            _layoutDirty = true;
        }
    }

    private TextParagraphProperties ParagraphProperties(out TextRunProperties run)
    {
        run = new GenericTextRunProperties(new Typeface(FontFamily, FontStyle, FontWeight), FontSize, foregroundBrush: Foreground ?? Brushes.Black);
        return new GenericTextParagraphProperties(run, TextAlignment.Left, TextWrapping.Wrap, double.IsNaN(LineHeight) ? 0 : LineHeight, 0);
    }

    private ComposerLayout CreateLayout(double maxWidth)
    {
        var paragraph = ParagraphProperties(out var run);
        var tags = new TagStyle(TagBackground ?? Brushes.LightGray, TagForeground ?? run.ForegroundBrush ?? Brushes.Black, TagMaxWidth);
        var segments = LayoutSegments();
        var source = new ComposerTextSource(segments, run, tags);
        return new ComposerLayout(source, segments.Sum(s => s.Length), paragraph, maxWidth);
    }

    private ComposerLayout EnsureLayout(double? width = null)
    {
        var maxWidth = Math.Max(1, (width ?? Bounds.Width) - Padding.Left - Padding.Right);
        if (_layout is null || _layoutDirty || _layoutVersion != _document.Version || Math.Abs(maxWidth - _layoutWidth) > 0.01)
        {
            _layout?.Dispose();
            _layout = CreateLayout(maxWidth);
            _layoutWidth = maxWidth;
            _layoutVersion = _document.Version;
            _layoutDirty = false;
        }
        return _layout;
    }

    private double LineUnit(ComposerLayout layout) => double.IsNaN(LineHeight) ? layout.FirstLineHeight : LineHeight;

    /// <summary>
    /// 转录里的用户消息在 <see cref="ExpandableContent"/> 里收起：前 <paramref name="lines"/> 行的底边（用上次测量的排版），
    /// 收起高度停在行边界上，不会露出下一行的一条。
    /// </summary>
    double IExpandableLineSource.GetLinesBottom(int lines)
    {
        var layout = _layout ?? EnsureLayout();
        return lines < layout.LineCount ? Padding.Top + layout.LineTop(lines) : Padding.Top + layout.Height + Padding.Bottom;
    }

    /// <summary>整段排版（带 tag 的消息都不长），不需要按行数截断。</summary>
    void IExpandableLineSource.LimitLayout(int lines)
    {
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsFinite(availableSize.Width) ? availableSize.Width : 10_000;
        var layout = EnsureLayout(width);
        var unit = LineUnit(layout);
        var height = Math.Max(layout.Height, unit * Math.Max(1, MinLines));
        if (MaxLines > 0)
        {
            height = Math.Min(height, unit * MaxLines);
        }
        var contentWidth = layout.Width + Padding.Left + Padding.Right + 2;
        // 只读时宽度按内容（气泡贴着文字），否则占满可用宽度
        var desiredWidth = double.IsFinite(availableSize.Width) && !IsReadOnly ? availableSize.Width
            : double.IsFinite(availableSize.Width) ? Math.Min(availableSize.Width, contentWidth) : contentWidth;
        return new Size(desiredWidth, height + Padding.Top + Padding.Bottom);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        EnsureLayout(finalSize.Width);
        _scrollY = Math.Clamp(_scrollY, 0, MaxScroll(finalSize.Height));
        return finalSize;
    }

    private double MaxScroll(double? height = null) =>
        Math.Max(0, EnsureLayout().Height - ((height ?? Bounds.Height) - Padding.Top - Padding.Bottom));

    /// <summary>滚动到光标可见。</summary>
    private void EnsureCaretVisible()
    {
        if (Bounds.Height <= 0)
        {
            return;
        }
        var caret = EnsureLayout().CaretRect(LayoutCaret);
        var viewport = Bounds.Height - Padding.Top - Padding.Bottom;
        if (caret.Top < _scrollY)
        {
            _scrollY = caret.Top;
        }
        else if (caret.Bottom > _scrollY + viewport)
        {
            _scrollY = caret.Bottom - viewport;
        }
        _scrollY = Math.Clamp(_scrollY, 0, MaxScroll());
    }

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        // 透明底色：整个区域都能命中（点击空白处放置光标）
        context.FillRectangle(Brushes.Transparent, bounds);
        var layout = EnsureLayout();
        var origin = new Point(Padding.Left, Padding.Top - _scrollY);
        using (context.PushClip(bounds))
        {
            if (HasSelection && SelectionBrush is { } selection)
            {
                foreach (var rect in layout.RangeRects(ToLayout(SelectionStart), ToLayout(SelectionEnd)))
                {
                    context.FillRectangle(selection, rect.Translate(origin));
                }
            }
            if (_document.IsEmpty && !HasPreedit && !string.IsNullOrEmpty(PlaceholderText))
            {
                var paragraph = ParagraphProperties(out var run);
                using var placeholder = new TextLayout(PlaceholderText, run.Typeface, FontSize, PlaceholderForeground ?? Brushes.Gray,
                    TextAlignment.Left, TextWrapping.Wrap, maxWidth: Math.Max(1, bounds.Width - Padding.Left - Padding.Right),
                    lineHeight: paragraph.LineHeight);
                placeholder.Draw(context, origin);
            }
            layout.Draw(context, origin);
            if (HasSelection && SelectionBrush is { } tagSelection)
            {
                foreach (var rect in layout.TagRects(ToLayout(SelectionStart), ToLayout(SelectionEnd)))
                {
                    context.DrawRectangle(tagSelection, null, rect.Translate(origin), 5, 5);
                }
            }
            if (IsFocused && _caretVisible && !HasSelection && !IsReadOnly)
            {
                var caret = layout.CaretRect(LayoutCaret).Translate(origin);
                var x = Math.Round(caret.X) + 0.5;
                context.DrawLine(new Pen(CaretBrush ?? Foreground ?? Brushes.Black, 1), new Point(x, caret.Top + 1), new Point(x, caret.Bottom - 1));
            }
        }
    }
}
