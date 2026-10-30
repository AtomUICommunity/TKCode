using System.Collections.Frozen;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace TKCode.Desktop.Icons;

/// <summary>
/// LobeHub 单色品牌图标（24×24 填充路径），基于 <see cref="GlyphIcon"/>，跟随前景色。
/// 路径数据由 tools/gen_provider_icons.py 生成。
/// </summary>
public sealed class ProviderGlyph : GlyphIcon
{
    public static readonly StyledProperty<string?> KeyProperty =
        AvaloniaProperty.Register<ProviderGlyph, string?>(nameof(Key));

    private static readonly Dictionary<string, GlyphPath[]> Cache = new();

    static ProviderGlyph()
    {
        AffectsRender<ProviderGlyph>(KeyProperty);
    }

    public string? Key
    {
        get => GetValue(KeyProperty);
        set => SetValue(KeyProperty, value);
    }

    internal override IReadOnlyList<GlyphPath> Paths => Get(Key);

    private static GlyphPath[] Get(string? key)
    {
        if (key is null || !ProviderIconPaths.Mono.TryGetValue(key, out var paths))
        {
            return [];
        }
        lock (Cache)
        {
            if (!Cache.TryGetValue(key, out var instructions))
            {
                instructions = paths.Select(p => new GlyphPath(StreamGeometry.Parse(p.Data), true, p.Opacity)).ToArray();
                Cache[key] = instructions;
            }
            return instructions;
        }
    }
}

/// <summary>
/// 官方只提供位图的品牌图标（Assets/ProviderIcons/png/&lt;key&gt;.png，已裁掉透明边距并缩小）。
/// LobeHub 没有收录的厂商（如 NextCoWork）用它；位图在所有 <see cref="ProviderLogo"/> 之间共享。
/// </summary>
internal static class ProviderRasterIcons
{
    public static readonly FrozenSet<string> Keys = new[] { "nextcowork" }.ToFrozenSet(StringComparer.Ordinal);

    private static readonly Dictionary<string, Bitmap> Cache = new(StringComparer.Ordinal);

    public static Bitmap Get(string key)
    {
        lock (Cache)
        {
            if (!Cache.TryGetValue(key, out var bitmap))
            {
                using var stream = AssetLoader.Open(new Uri($"avares://TKCode/Assets/ProviderIcons/png/{key}.png"));
                bitmap = new Bitmap(stream);
                Cache[key] = bitmap;
            }
            return bitmap;
        }
    }
}

/// <summary>
/// 品牌图标：有彩色版本且 <see cref="PreferColor"/> 时用 Svg 渲染彩色 SVG（含渐变），否则用 <see cref="ProviderGlyph"/>；
/// 只有位图的品牌（<see cref="ProviderRasterIcons"/>）用 Image。
/// <see cref="Key"/> 为空或未知时显示通用的立方体图标。
/// </summary>
public sealed class ProviderLogo : Decorator
{
    public static readonly StyledProperty<string?> KeyProperty =
        AvaloniaProperty.Register<ProviderLogo, string?>(nameof(Key));

    public static readonly StyledProperty<bool> PreferColorProperty =
        AvaloniaProperty.Register<ProviderLogo, bool>(nameof(PreferColor), true);

    private static readonly Uri BaseUri = new("avares://TKCode/");

    static ProviderLogo()
    {
        KeyProperty.Changed.AddClassHandler<ProviderLogo>((logo, _) => logo.Rebuild());
        PreferColorProperty.Changed.AddClassHandler<ProviderLogo>((logo, _) => logo.Rebuild());
    }

    public ProviderLogo()
    {
        Width = 14;
        Height = 14;
        Rebuild();
    }

    public string? Key
    {
        get => GetValue(KeyProperty);
        set => SetValue(KeyProperty, value);
    }

    public bool PreferColor
    {
        get => GetValue(PreferColorProperty);
        set => SetValue(PreferColorProperty, value);
    }

    private void Rebuild()
    {
        var key = Key;
        if (key is not null && ProviderRasterIcons.Keys.Contains(key))
        {
            var image = new Image { Source = ProviderRasterIcons.Get(key), Stretch = Stretch.Uniform };
            RenderOptions.SetBitmapInterpolationMode(image, Avalonia.Media.Imaging.BitmapInterpolationMode.HighQuality);
            Child = image;
        }
        else if (key is not null && PreferColor && ProviderIconPaths.Colored.Contains(key))
        {
            Child = new Avalonia.Svg.Svg(BaseUri) { Path = $"avares://TKCode/Assets/ProviderIcons/svg/{key}-color.svg", Stretch = Stretch.Uniform };
        }
        else if (key is not null && ProviderIconPaths.Mono.ContainsKey(key))
        {
            Child = new ProviderGlyph { Key = key };
        }
        else
        {
            Child = new LucideIcon(LucideKind.Box);
        }
    }
}
