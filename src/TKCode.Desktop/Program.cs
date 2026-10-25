using AtomUI;
using AtomUI.Desktop.Controls;
using AtomUI.Localization;
using AtomUI.Theme.Definitions;
using Avalonia;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TKCode.Arc;
using TKCode.Config;
using TKCode.Desktop.Services;
using TKCode.Desktop.Updates;
using TKCode.Desktop.ViewModels;

namespace TKCode.Desktop;

internal static class Program
{
    /// <summary>本进程是主实例时的单实例句柄（收其他进程转发来的命令）；<c>--new-instance</c> 或测试中为 null。</summary>
    internal static SingleInstance? Instance { get; private set; }

    [STAThread]
    public static int Main(string[] args)
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        if (args.Contains("--smoke"))
        {
            return SmokeMode.Run(args);
        }
        if (args.Contains("--version"))
        {
            // 发布 CI 的冒烟检查与安装后的版本确认
            Console.WriteLine(AppVersionInfo.Display);
            return 0;
        }
        // 未处理异常写崩溃日志（NativeAOT 只打印到 stderr 后 abort，从 Finder 启动时会丢失）
        var logs = Path.Combine(ConfigPaths.Home, "logs");
        CrashLog.Install(logs, "desktop", AppVersionInfo.Display);
        if (args.Contains("--crash-test"))
        {
            // 验证崩溃日志链路（发布版也可用）：主线程抛出未处理异常
            throw new InvalidOperationException("TKCode --crash-test");
        }
        // 单实例：已有 TKCode 在运行（包括隐藏在托盘里）时，只把命令转发给它（再次启动 = 显示窗口；跳转列表的条目带参数）然后退出。
        // --new-instance 跳过（开发时同时运行本地构建）。
        var command = AppCommand.Parse(args);
        SingleInstance? single = null;
        if (!args.Contains("--new-instance") && !args.Contains("--startup-bench") && !SingleInstance.TryClaim(ConfigPaths.Home, command, out single))
        {
            return 0;
        }
        using var instanceGuard = single;
        if (command.Kind == AppCommandKind.Quit)
        {
            // 跳转列表的「退出 TKCode」在没有运行中的实例时什么也不用做
            return 0;
        }
        Instance = single;
        // Windows 安装程序（Packaging/windows/TKCode.iss 的 AppMutex）据此等待 TKCode 退出后再替换文件
        using var instance = OperatingSystem.IsWindows() ? new Mutex(false, "TKCodeDesktop") : null;
        // Avalonia 必须占用主线程（macOS 要求），因此 Host 只 Start，不 Run。
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            Args = args,
            ContentRootPath = AppContext.BaseDirectory,
        });
        builder.Logging.ClearProviders();
        builder.Logging.AddDebug();
        builder.Logging.AddConsole();
        builder.Logging.AddProvider(new FileLoggerProvider(logs, "desktop"));
        ConfigureServices(builder.Services);

        using var host = builder.Build();
        App.Services = host.Services;
        host.StartAsync().GetAwaiter().GetResult();
        try
        {
            if (args.Contains("--startup-bench"))
            {
                return StartupBenchmark.Run(args, started);
            }
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            host.StopAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
        }
    }

    // XAML 预览器也会调用此方法（不经过 Main）。
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
                  .UseAtomUIPlatformDetect()
                  .WithAtomUIDefaultOptions()
                  .UseTKCodeAtomUI()
                  .LogToTrace();

    /// <summary>
    /// AtomUI 6.2.6 的配置回调在平台初始化之前执行，只能声明包与设置。
    /// 无头测试、冒烟模式和首帧基准各自构建 AppBuilder，必须走同一入口。
    /// </summary>
    internal static AppBuilder UseTKCodeAtomUI(this AppBuilder builder) =>
        builder.UseAtomUI(static atom =>
        {
            atom.WithApplicationId("TKCode");
            var settings = App.Services?.GetRequiredService<UserSettingsService>();
            var themes = App.Services?.GetRequiredService<ThemeService>();
            var language = LanguageTag.TryParse(settings?.Current.Language, out var tag) && (tag == LanguageTags.EnUS || tag == LanguageTags.ZhCN)
                ? tag
                : LanguageTags.ZhCN;
            atom.UseLanguages(language, [LanguageTags.EnUS, LanguageTags.ZhCN]);
            atom.AddThemeDefinitionResolver(new AvaloniaAssetThemeDefinitionResolver(
                "TKCode.Themes",
                [
                    new Uri("avares://TKCode/Assets/Themes/TKCodeDark.theme.xml"),
                    new Uri("avares://TKCode/Assets/Themes/TKCodeLight.theme.xml"),
                ],
                typeof(App).Assembly.GetName().Version?.ToString() ?? "1"));
            atom.WithInitialTheme(themes?.ResolveInitialThemeId() ?? ThemeService.DarkThemeId);
            atom.WithDefaultFontFamily("$Default");
            atom.UseDesktopControls();
            // Arc 组件库（TKCode.Arc）：设置页等处的表单控件；主题令牌随 TKCodeDark / TKCodeLight 的明暗算法生成
            atom.UseArc();
        });

    internal static void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<StateService>();
        services.AddSingleton<UserSettingsService>();
        services.AddSingleton(_ => new DraftStore());
        services.AddSingleton<ThemeService>();
        services.AddSingleton<AgentHostConnection>();
        services.AddSingleton<ProjectService>();
        services.AddSingleton<PtyHostService>();
        services.AddSingleton<ITrashService>(_ => TrashService.Create());
        services.AddSingleton<IExplorerFileOperations, ExplorerFileOperations>();
        services.AddSingleton<IExplorerPlatformActions, ExplorerPlatformActions>();
        services.AddSingleton<UpdateService>();
        services.AddSingleton<UpdatesViewModel>();

        services.AddSingleton<MainWindowViewModel>();
        services.AddSingleton<SidebarViewModel>();
        services.AddSingleton<ConversationViewModel>();
        services.AddSingleton<WorkbenchViewModel>();
    }
}
