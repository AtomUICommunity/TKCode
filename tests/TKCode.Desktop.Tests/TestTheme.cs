using TKCode.Arc;

namespace TKCode.Desktop.Tests;

/// <summary>测试里切换 Arc 明 / 暗外观；id 只是测试内部的标签。</summary>
internal static class TestTheme
{
    public const string DarkId = "dark";
    public const string LightId = "light";

    public static string CurrentId => ArcTheme.Manager.IsDark ? DarkId : LightId;

    public static Task ApplyAsync(string id) =>
        ArcTheme.Manager.ApplyAsync(t => t.Appearance(id == LightId ? ArcAppearance.Light : ArcAppearance.Dark));
}
