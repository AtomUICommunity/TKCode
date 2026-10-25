using AtomUI;
using AtomUI.Localization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using TKCode.Desktop.Controls;
using TKCode.Desktop.Localization;
using TKCode.Desktop.Services;
using TKCode.Desktop.ViewModels;
using TKCode.Desktop.Views;

namespace TKCode.Desktop;

public partial class App : Application
{
    /// <summary>由 Program 在 Avalonia 启动前设置；XAML 预览器下为 null。</summary>
    public static IServiceProvider? Services { get; set; }

    public override void RegisterServices()
    {
        base.RegisterServices();
        // 6.2.6 起内核在 Initialize 之前提交；配置在 Program.UseTKCodeAtomUI。
        ApplicationStartup.Attach(this);
    }

    public override void Initialize()
    {
        // 6.2.6 在 Load 之前把 LanguageResourceProvider 放进 Resources。
        // App.axaml 的 Application.Resources 会换成一张新表，{loc:} 因此全部落空。
        // 运行时 new ResourceInclude 加载调色板不经过 XAML 编译，AOT 不安全，所以只挪已编译的合并项。
        var committed = Resources;
        AvaloniaXamlLoader.Load(this);
        if (ReferenceEquals(Resources, committed) ||
            Resources is not ResourceDictionary loaded ||
            committed is not ResourceDictionary target)
        {
            return;
        }

        var merged = new IResourceProvider[loaded.MergedDictionaries.Count];
        for (var i = 0; i < merged.Length; i++)
        {
            merged[i] = loaded.MergedDictionaries[i];
        }

        // 先摘掉所有者，否则 AddOwner 会抛 “already has a parent”。
        for (var i = merged.Length - 1; i >= 0; i--)
        {
            loaded.MergedDictionaries.RemoveAt(i);
        }

        Resources = target;
        foreach (var provider in merged)
        {
            target.MergedDictionaries.Add(provider);
        }
    }

    public override void OnFrameworkInitializationCompleted()
    {
        Strings.Attach(this);
        // UI 线程上未处理的异常：先写崩溃日志，不标记 Handled，行为与之前一致（随后进程终止）
        Dispatcher.UIThread.UnhandledException += (_, e) => TKCode.Config.CrashLog.Write(e.Exception, "ui thread");
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop && Services is not null)
        {
            Services.GetRequiredService<ThemeService>().Attach(this);
            // settings.json 被外部修改语言时立即切换
            var userSettings = Services.GetRequiredService<UserSettingsService>();
            TKCode.Desktop.Motion.MotionSettings.UserReduceMotion = userSettings.Current.ReduceMotion == true;
            MarkdownSetup.OriginalImages = userSettings.Current.OriginalImages == true;
            UiZoom.Follow(userSettings);
            userSettings.Changed += () =>
            {
                TKCode.Desktop.Motion.MotionSettings.UserReduceMotion = userSettings.Current.ReduceMotion == true;
                MarkdownSetup.OriginalImages = userSettings.Current.OriginalImages == true;
                if (this.GetLanguageManager() is { } manager && LanguageTag.TryParse(userSettings.Current.Language, out var wanted) &&
                    (wanted == LanguageTags.EnUS || wanted == LanguageTags.ZhCN) && manager.Current.CurrentLanguage != wanted)
                {
                    manager.ChangeLanguage(wanted);
                }
            };
            var mainViewModel = Services.GetRequiredService<MainWindowViewModel>();
            var window = new MainWindow { DataContext = mainViewModel };
            desktop.MainWindow = window;
            // 系统托盘；默认关闭窗口只隐藏，进程与会话继续运行
            // 托盘图标、窗口与更新的事件持有它，不需要另存引用
            var tray = new AppTray(this, desktop, window, userSettings, Services.GetRequiredService<UpdatesViewModel>(), mainViewModel,
                Services.GetRequiredService<ProjectService>());
            // 命令行指令（Windows 跳转列表的条目）：首次启动时带的在这里执行；之后再启动的进程把它转发给本进程
            var startup = AppCommand.Parse(desktop.Args ?? []);
            if (startup.Kind != AppCommandKind.Show)
            {
                tray.Execute(startup);
            }
            if (Program.Instance is { } instance)
            {
                instance.CommandReceived += command => Dispatcher.UIThread.Post(() => tray.Execute(command));
            }
            desktop.ShutdownRequested += (_, _) =>
            {
                // 输入框里没发送的内容落盘，重启后恢复（docs/design/composer-drafts.md §4.4）
                try
                {
                    Services.GetRequiredService<ConversationViewModel>().FlushDraft();
                }
                catch (Exception)
                {
                    // 草稿是尽力而为的，不能拖住退出
                }
                Services.GetRequiredService<AgentHostConnection>().Dispose();
            };
            // 应用内更新：安装程序启动后退出；启动 60 秒后开始检查（docs/design/auto-update.md）
            Services.GetRequiredService<UpdatesViewModel>().Shutdown = () => desktop.Shutdown();
            Services.GetRequiredService<TKCode.Desktop.Updates.UpdateService>().Start();
        }
        base.OnFrameworkInitializationCompleted();
        TKCode.Desktop.Services.MacDockIcon.Apply();
    }
}
