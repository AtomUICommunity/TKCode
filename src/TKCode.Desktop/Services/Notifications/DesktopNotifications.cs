using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using TKCode.Config;
using TKCode.Desktop.Localization;
using TKCode.Desktop.ViewModels;

namespace TKCode.Desktop.Services.Notifications;

/// <summary>
/// 把通知接到桌面应用上（docs/design/notifications.md §4.1）：平台通知器、窗口的前后台状态、会话事件、托盘、点击后打开会话。
/// 只在真正的桌面启动路径（<see cref="App"/>）里创建；无头测试与冒烟模式不会弹出真实通知。
/// </summary>
internal sealed class DesktopNotifications : IAppPresence, IDisposable
{
    private readonly Window _window;
    private readonly MainWindowViewModel _main;

    public DesktopNotifications(Window window, MainWindowViewModel main, UserSettingsService settings, AppTray tray, ILogger logger)
    {
        _window = window;
        _main = main;
        var notifier = CreateNotifier(logger, () => window.TryGetPlatformHandle()?.Handle ?? 0);
        Center = new NotificationCenter(notifier, () => NotificationPreferences.From(settings.Current.Notifications), this,
            post: action => Dispatcher.UIThread.Post(action));
        Attach(Center, main.Conversation);
        Center.UnreadChanged += tray.SetUnread;
        Center.OpenRequested += sessionId =>
        {
            tray.ShowWindow();
            // 测试通知是在设置页里发的：只把窗口带回来，不离开设置页
            if (sessionId != NotificationCenter.TestSessionId)
            {
                _ = main.ShowSessionAsync(sessionId);
            }
        };
        main.TestNotification = () => Center.SendTest("TKCode", Strings.Get(TKCodeLangResourceKind.NotifyTestBody));

        // 前后台变化：窗口激活 / 失活、显示 / 隐藏、最小化，切换会话，开关设置页
        window.Activated += (_, _) => Center.PresenceChanged();
        window.Deactivated += (_, _) => Center.PresenceChanged();
        window.PropertyChanged += (_, e) =>
        {
            if (e.Property == Visual.IsVisibleProperty || e.Property == Window.WindowStateProperty)
            {
                Center.PresenceChanged();
            }
        };
        main.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainWindowViewModel.IsSettingsOpen))
            {
                Center.PresenceChanged();
            }
        };
        main.Conversation.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ConversationViewModel.Active))
            {
                Center.PresenceChanged();
            }
        };
    }

    public NotificationCenter Center { get; }

    public bool IsForeground => _window.IsVisible && _window.IsActive && _window.WindowState != WindowState.Minimized;

    public string? VisibleSessionId => _main.IsSettingsOpen ? null : _main.Conversation.Active?.SessionId;

    /// <summary>会话事件 → 通知中心（端到端测试用同一份接线）。</summary>
    internal static void Attach(NotificationCenter center, ConversationViewModel conversation)
    {
        conversation.AttentionRaised += center.Report;
        conversation.SessionDeleted += center.Forget;
    }

    private static ISystemNotifier CreateNotifier(ILogger logger, Func<nint> windowHandle)
    {
        try
        {
            if (OperatingSystem.IsMacOS())
            {
                return new MacNotifier(logger, action => Dispatcher.UIThread.Post(action));
            }
            if (OperatingSystem.IsWindows())
            {
                return new WindowsNotifier(logger, windowHandle, IconFile(logger));
            }
            if (OperatingSystem.IsLinux())
            {
                return new LinuxNotifier(logger, IconFile(logger));
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "System notifications are unavailable");
        }
        return new NullNotifier();
    }

    /// <summary>
    /// Toast（Windows 的 AUMID 登记）与 freedesktop 通知需要图标文件的路径：把应用图标写到数据目录。内容相同时不重写。
    /// </summary>
    private static string? IconFile(ILogger logger)
    {
        try
        {
            using var source = AssetLoader.Open(new Uri("avares://TKCode/Assets/app-logo.png"));
            using var buffer = new MemoryStream();
            source.CopyTo(buffer);
            var bytes = buffer.ToArray();
            var path = Path.Combine(ConfigPaths.Home, "notification-icon.png");
            if (!File.Exists(path) || new FileInfo(path).Length != bytes.Length)
            {
                Directory.CreateDirectory(ConfigPaths.Home);
                File.WriteAllBytes(path, bytes);
            }
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogDebug(ex, "Writing the notification icon failed");
            return null;
        }
    }

    public void Dispose() => Center.Dispose();
}
