using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml.MarkupExtensions;

namespace TKCode.Desktop.Icons;

/// <summary>文件类型图标：按 <see cref="ColorKey"/> 动态引用调色板颜色，主题切换时随之变化。</summary>
public sealed class FileTypeIcon : Decorator
{
    public static readonly StyledProperty<LucideKind> KindProperty =
        AvaloniaProperty.Register<FileTypeIcon, LucideKind>(nameof(Kind), LucideKind.File);

    public static readonly StyledProperty<string?> ColorKeyProperty =
        AvaloniaProperty.Register<FileTypeIcon, string?>(nameof(ColorKey));

    static FileTypeIcon()
    {
        KindProperty.Changed.AddClassHandler<FileTypeIcon>((icon, _) => icon.Rebuild());
        ColorKeyProperty.Changed.AddClassHandler<FileTypeIcon>((icon, _) => icon.Rebuild());
    }

    public FileTypeIcon()
    {
        Width = 14;
        Height = 14;
        Rebuild();
    }

    public LucideKind Kind
    {
        get => GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    public string? ColorKey
    {
        get => GetValue(ColorKeyProperty);
        set => SetValue(ColorKeyProperty, value);
    }

    private void Rebuild()
    {
        var icon = new LucideIcon(Kind);
        if (ColorKey is { } key)
        {
            icon[!GlyphIcon.StrokeBrushProperty] = new DynamicResourceExtension(key);
        }
        Child = icon;
    }
}
