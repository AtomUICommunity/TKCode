using Avalonia;
using Avalonia.Data.Converters;
using TKCode.Desktop.Icons;

namespace TKCode.Desktop.Views;

public static class Converters
{
    /// <summary>暗色时显示太阳（切到亮色），亮色时显示月亮。</summary>
    public static readonly IValueConverter ThemeIcon =
        new FuncValueConverter<bool, LucideKind>(isDark => isDark ? LucideKind.Sun : LucideKind.Moon);

    /// <summary>被忽略的文件（bin、obj、.git 等）在文件树中变暗。</summary>
    public static readonly IValueConverter DimOpacity =
        new FuncValueConverter<bool, double>(dimmed => dimmed ? 0.45 : 1.0);

    public static readonly IValueConverter LeftIndent =
        new FuncValueConverter<double, Thickness>(indent => new Thickness(indent - 8, 0, 0, 0));
}
