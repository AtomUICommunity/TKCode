using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;

namespace TKCode.Desktop.Motion;

/// <summary>
/// 展开 / 收起容器：替代 IsVisible="{Binding IsExpanded}"。
/// 占用的尺寸随进度从 0 变到内容尺寸（周围布局跟着平滑重排），内容同时淡入并从 Offset 处滑到原位；收起播完才隐藏内容。
/// Orientation=Horizontal 时按宽度展开（侧栏）。
/// 设置 ContentTemplate 时内容按需创建：展开时才创建，收起播完后释放。
/// </summary>
public class Reveal : Decorator, IMotionGated
{
    public static readonly StyledProperty<bool> IsOpenProperty =
        AvaloniaProperty.Register<Reveal, bool>(nameof(IsOpen), defaultValue: true);

    /// <summary>按需创建的内容（DataContext 继承自 Reveal）：长列表里大量收起的行不占视图内存，收起动画期间内容仍在。</summary>
    public static readonly StyledProperty<IDataTemplate?> ContentTemplateProperty =
        AvaloniaProperty.Register<Reveal, IDataTemplate?>(nameof(ContentTemplate));

    public static readonly StyledProperty<Orientation> OrientationProperty =
        AvaloniaProperty.Register<Reveal, Orientation>(nameof(Orientation), Orientation.Vertical);

    /// <summary>展开起点处内容沿展开方向的位移（负值：从上方 / 左侧滑入）。</summary>
    public static readonly StyledProperty<double> OffsetProperty =
        AvaloniaProperty.Register<Reveal, double>(nameof(Offset), -6);

    public static readonly StyledProperty<bool> FadeProperty =
        AvaloniaProperty.Register<Reveal, bool>(nameof(Fade), true);

    private readonly MotionValue _progress;
    private readonly MotionGate _gate;

    /// <summary>展开 / 收起动画开始与结束（MainWindow 用来在侧栏动画期间冻结会话区宽度）。</summary>
    public event Action<Reveal, bool>? AnimatingChanged;

    public Reveal()
    {
        ClipToBounds = true;
        _gate = MotionGate.For(this);
        _progress = new MotionValue(this, 1, _ => OnProgress());
    }

    public bool IsOpen
    {
        get => GetValue(IsOpenProperty);
        set => SetValue(IsOpenProperty, value);
    }

    public Orientation Orientation
    {
        get => GetValue(OrientationProperty);
        set => SetValue(OrientationProperty, value);
    }

    public double Offset
    {
        get => GetValue(OffsetProperty);
        set => SetValue(OffsetProperty, value);
    }

    public bool Fade
    {
        get => GetValue(FadeProperty);
        set => SetValue(FadeProperty, value);
    }

    public IDataTemplate? ContentTemplate
    {
        get => GetValue(ContentTemplateProperty);
        set => SetValue(ContentTemplateProperty, value);
    }

    /// <summary>当前进度（0 收起 … 1 展开，弹簧可能略微越界）。</summary>
    public double Progress => _progress.Value;

    public new bool IsAnimating => _progress.IsAnimating;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsOpenProperty)
        {
            var open = change.GetNewValue<bool>();
            if (!_gate.ShouldAnimate())
            {
                _progress.Snap(open ? 1 : 0);
                ReleaseIfClosed();
                return;
            }
            if (open && Child is { IsVisible: false } child)
            {
                child.IsVisible = true;
            }
            AnimatingChanged?.Invoke(this, true);
            _progress.AnimateTo(open ? 1 : 0, open ? MotionTokens.Standard : MotionTokens.Exit, () =>
            {
                AnimatingChanged?.Invoke(this, false);
                ReleaseIfClosed();
            });
        }
        else if (change.Property == ChildProperty)
        {
            OnProgress();
        }
        else if (change.Property == ContentTemplateProperty && change.OldValue is not null)
        {
            // 换模板：旧内容丢弃，需要时按新模板重新创建
            Child = null;
            InvalidateMeasure();
        }
    }

    // 按需创建：展开（或收起动画还在播）时才有内容
    private void Materialize()
    {
        if (Child is null && ContentTemplate is { } template && (IsOpen || _progress.IsAnimating))
        {
            Child = template.Build(DataContext);
        }
    }

    private void ReleaseIfClosed()
    {
        if (ContentTemplate is not null && !IsOpen && !_progress.IsAnimating)
        {
            Child = null;
        }
    }

    private void OnProgress()
    {
        var p = Math.Clamp(_progress.Value, 0, 1);
        if (Child is { } child)
        {
            // 完全收起后隐藏内容：不参与命中测试、不再测量
            child.IsVisible = p > 0 || _progress.IsAnimating;
            child.Opacity = Fade ? Math.Clamp(p * 1.4 - 0.2, 0, 1) : 1;
        }
        InvalidateMeasure();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        Materialize();
        if (Child is not { IsVisible: true } child)
        {
            return default;
        }
        child.Measure(availableSize);
        var desired = child.DesiredSize;
        var p = Math.Clamp(_progress.Value, 0, 1);
        return Orientation == Orientation.Vertical
            ? new Size(desired.Width, desired.Height * p)
            : new Size(desired.Width * p, desired.Height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (Child is not { IsVisible: true } child)
        {
            return finalSize;
        }
        var desired = child.DesiredSize;
        var p = Math.Clamp(_progress.Value, 0, 1);
        var shift = Offset * (1 - p);
        // 内容按完整尺寸排版（展开过程中不重排），越界部分被裁剪
        var rect = Orientation == Orientation.Vertical
            ? new Rect(0, shift, finalSize.Width, Math.Max(desired.Height, p >= 1 ? finalSize.Height : 0))
            : new Rect(shift, 0, Math.Max(desired.Width, p >= 1 ? finalSize.Width : 0), finalSize.Height);
        child.Arrange(rect);
        return finalSize;
    }
}
