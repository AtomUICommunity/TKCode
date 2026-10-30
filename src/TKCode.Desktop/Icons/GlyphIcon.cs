using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace TKCode.Desktop.Icons;

/// <summary>一条图标路径：<see cref="Filled"/> 为真时按前景填充，否则按前景描边。</summary>
internal readonly record struct GlyphPath(Geometry Data, bool Filled, double Opacity = 1);

/// <summary>
/// 24×24 视口的单色矢量图标基类：按 <see cref="StrokeBrush"/>（未设置时取继承的前景色）描边或填充，
/// 随控件尺寸等比缩放。因此放在按钮里会跟随按钮前景色（悬停 / 禁用）。
/// </summary>
public abstract class GlyphIcon : Control
{
    public static readonly StyledProperty<IBrush?> StrokeBrushProperty =
        AvaloniaProperty.Register<GlyphIcon, IBrush?>(nameof(StrokeBrush));

    /// <summary>继承的前景色；<see cref="StrokeBrush"/> 未设置时使用。</summary>
    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        TextElement.ForegroundProperty.AddOwner<GlyphIcon>();

    public static readonly StyledProperty<double> StrokeWidthProperty =
        AvaloniaProperty.Register<GlyphIcon, double>(nameof(StrokeWidth), 2);

    private const double ViewBox = 24;

    static GlyphIcon()
    {
        AffectsRender<GlyphIcon>(StrokeBrushProperty, ForegroundProperty, StrokeWidthProperty);
    }

    public IBrush? StrokeBrush
    {
        get => GetValue(StrokeBrushProperty);
        set => SetValue(StrokeBrushProperty, value);
    }

    public IBrush? Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    public double StrokeWidth
    {
        get => GetValue(StrokeWidthProperty);
        set => SetValue(StrokeWidthProperty, value);
    }

    internal abstract IReadOnlyList<GlyphPath> Paths { get; }

    public override void Render(DrawingContext context)
    {
        var brush = StrokeBrush ?? Foreground;
        var size = Bounds.Size;
        if (brush is null || size.Width <= 0 || size.Height <= 0)
        {
            return;
        }
        var scale = Math.Min(size.Width, size.Height) / ViewBox;
        var offset = new Vector((size.Width - ViewBox * scale) / 2, (size.Height - ViewBox * scale) / 2);
        using var _ = context.PushTransform(Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(offset.X, offset.Y));
        var pen = new Pen(brush, StrokeWidth, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
        foreach (var path in Paths)
        {
            using var opacity = path.Opacity < 1 ? context.PushOpacity(path.Opacity) : default;
            if (path.Filled)
            {
                context.DrawGeometry(brush, null, path.Data);
            }
            else
            {
                context.DrawGeometry(null, pen, path.Data);
            }
        }
    }
}
