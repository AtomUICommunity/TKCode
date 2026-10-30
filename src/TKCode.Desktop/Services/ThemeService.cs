using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;
using TKCode.Arc;

namespace TKCode.Desktop.Services;

/// <summary>
/// 在 Arc 的明 / 暗外观之间切换，支持跟随系统。
/// 应用专属颜色在 Styling/Palette.axaml 的 ThemeDictionaries 中随之切换。
/// </summary>
public sealed partial class ThemeService : ObservableObject
{
    private readonly UserSettingsService _settings;
    private ThemeMode _applied;

    public ThemeService(UserSettingsService settings)
    {
        _settings = settings;
        _applied = settings.Theme;
        // settings.json 被外部修改主题时立即应用
        settings.Changed += async () =>
        {
            if (_settings.Theme != _applied)
            {
                _applied = _settings.Theme;
                OnPropertyChanged(nameof(Mode));
                await ApplyAsync();
            }
        };
    }

    private Application? _app;

    public ThemeMode Mode => _settings.Theme;

    [ObservableProperty]
    public partial bool IsDark { get; private set; } = true;

    /// <summary>启动时传给 <c>UseArc</c> 的外观；System 由 Arc 自己跟随系统。</summary>
    public ArcAppearance InitialAppearance
    {
        get
        {
            IsDark = Resolve(Mode, Application.Current);
            return ToAppearance(Mode);
        }
    }

    public void Attach(Application app)
    {
        _app = app;
        if (app.PlatformSettings is { } platform)
        {
            platform.ColorValuesChanged += async (_, _) =>
            {
                if (Mode == ThemeMode.System)
                {
                    await ApplyAsync();
                }
            };
        }
    }

    public async Task SetModeAsync(ThemeMode mode)
    {
        _applied = mode;
        _settings.SetTheme(mode);
        OnPropertyChanged(nameof(Mode));
        await ApplyAsync();
    }

    public Task ToggleAsync() => SetModeAsync(IsDark ? ThemeMode.Light : ThemeMode.Dark);

    private async Task ApplyAsync()
    {
        if (_app is null)
        {
            return;
        }
        await ArcTheme.Manager.ApplyAsync(t => t.Appearance(ToAppearance(Mode)));
        IsDark = Resolve(Mode, _app);
    }

    private static ArcAppearance ToAppearance(ThemeMode mode) => mode switch
    {
        ThemeMode.Dark => ArcAppearance.Dark,
        ThemeMode.Light => ArcAppearance.Light,
        _ => ArcAppearance.System,
    };

    private static bool Resolve(ThemeMode mode, Application? app) => mode switch
    {
        ThemeMode.Dark => true,
        ThemeMode.Light => false,
        _ => app?.PlatformSettings?.GetColorValues().ThemeVariant != Avalonia.Platform.PlatformThemeVariant.Light,
    };
}
