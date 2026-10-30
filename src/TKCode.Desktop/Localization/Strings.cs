using System.Globalization;
using Avalonia;
using TKCode.Arc.Localization;

namespace TKCode.Desktop.Localization;

/// <summary>
/// C# 侧读取界面文案。返回的是当前语言下的快照；需要随语言切换刷新的文案应在 AXAML 中用
/// {loc:TKCodeLangResource Key} 绑定，或订阅 <see cref="LanguageChanged"/> 后重新读取。
/// </summary>
public static class Strings
{
    public static string Get(TKCodeLangResourceKind key) =>
        TKCodeLangResources.Get(key);

    public static string Format(TKCodeLangResourceKind key, params object?[] args) =>
        TKCodeLangResources.Format(key, args);

    public static event EventHandler? LanguageChanged;

    internal static void Attach(Application app)
    {
        if (ArcLanguageManager.Current is { } manager)
        {
            ApplyCulture(manager.Language);
            manager.Changed += (_, _) =>
            {
                ApplyCulture(manager.Language);
                LanguageChanged?.Invoke(null, EventArgs.Empty);
            };
        }
    }

    /// <summary>Arc 不修改 CultureInfo；日期等格式化跟随界面语言需要自己设置。</summary>
    private static void ApplyCulture(LanguageTag language)
    {
        try
        {
            var culture = CultureInfo.GetCultureInfo(language.Value);
            CultureInfo.CurrentCulture = culture;
            CultureInfo.CurrentUICulture = culture;
            CultureInfo.DefaultThreadCurrentCulture = culture;
            CultureInfo.DefaultThreadCurrentUICulture = culture;
        }
        catch (CultureNotFoundException)
        {
        }
    }
}
