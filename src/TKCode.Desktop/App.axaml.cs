using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using TKCode.Arc.Localization;
using TKCode.Desktop.Controls;
using TKCode.Desktop.Localization;
using TKCode.Desktop.Services;
using TKCode.Desktop.ViewModels;
using TKCode.Desktop.Views;

namespace TKCode.Desktop;

public class App : Application
{
    /// <summary>由 Program 在 Avalonia 启动前设置；XAML 预览器下为 null。</summary>
    public static IServiceProvider? Services { get; set; }

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    private static void ApplyArcMotionLevel() =>
        TKCode.Arc.ArcTheme.Apply(t => t.MotionLevel(TKCode.Desktop.Motion.MotionSettings.IsEnabled ? TKCode.Arc.Motion.ArcMotionLevel.Full : TKCode.Arc.Motion.ArcMotionLevel.Off));

    public override void OnFrameworkInitializationCompleted()
    {
        Strings.Attach(this);
        // 减少动态效果 / 系统设置 / 测试强制关闭：同步给 Arc 的全局动效等级（弹层等从主题继承）
        ApplyArcMotionLevel();
        TKCode.Desktop.Motion.MotionSettings.Changed += ApplyArcMotionLevel;
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
                if (ArcLanguageManager.Current is { } manager && LanguageTag.TryParse(userSettings.Current.Language, out var wanted) &&
                    (wanted == LanguageTags.EnUS || wanted == LanguageTags.ZhCN) && manager.Language != wanted)
                {
                    manager.Set(wanted);
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
