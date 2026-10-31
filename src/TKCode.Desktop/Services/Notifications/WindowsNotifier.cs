using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Runtime.Versioning;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace TKCode.Desktop.Services.Notifications;

/// <summary>
/// Windows 的 Toast 通知与任务栏覆盖图标（docs/design/notifications.md §5.2）。
/// <list type="bullet">
/// <item>Toast：手写 WinRT 互操作（combase 的 RoGetActivationFactory / RoActivateInstance + <c>GeneratedComInterface</c>），
/// 不用 CsWinRT 投影，NativeAOT 安全。非打包应用需要已登记的 AppUserModelID：写在
/// <c>HKCU\Software\Classes\AppUserModelId\TKCode.Desktop</c>，不给进程设置显式 AUMID（见 system-menus.md：否则固定的快捷方式与运行中的按钮会分开）。</item>
/// <item>点击：ToastNotification.Activated 事件，只在 TKCode 进程还在时有效（关闭窗口默认隐藏到托盘）。</item>
/// <item>Toast 不可用时退化为 MessageBeep + 任务栏按钮闪烁。</item>
/// <item>未读数：ITaskbarList3::SetOverlayIcon，图标由 <see cref="BadgeRenderer"/> 画。</item>
/// </list>
/// 这部分代码只在 macOS 上编译过、通过了 AOT 分析器，没有在 Windows 真机上运行过。
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed partial class WindowsNotifier : ISystemNotifier
{
    internal const string AppUserModelId = "TKCode.Desktop";
    private const string Group = "tkcode";

    private static readonly Guid IidXmlDocument = new("f7f3a506-1e87-42d6-bcfb-b8c809fa5494");
    private static readonly Guid IidToastNotificationFactory = new("04124b20-82c6-4229-b109-fd9ed4662b53");
    private static readonly Guid IidToastNotificationManagerStatics = new("50ac103f-d235-4598-bbef-98fe4d1a3ad4");
    private static readonly Guid IidToastNotificationManagerStatics2 = new("7ab93c52-0e48-4750-ba9d-1a4113981847");
    private static readonly Guid ClsidTaskbarList = new("56FDF344-FD6D-11d0-958A-006097C9A090");
    private static readonly Guid IidUnknown = new("00000000-0000-0000-C000-000000000046");

    private static readonly StrategyBasedComWrappers Wrappers = new();

    private readonly ILogger _logger;
    private readonly Func<nint> _windowHandle;
    private readonly string? _iconPath;
    private IToastNotifier? _notifier;
    private IToastNotificationFactory? _factory;
    private bool _toastUnavailable;
    /// <summary>每个会话最近一条 Toast：保持引用，Activated 事件才会一直有效；替换时注销旧的处理器。</summary>
    private readonly Dictionary<string, ShownToast> _shown = new(StringComparer.Ordinal);
    private ITaskbarList3? _taskbar;
    private nint _overlayIcon;

    /// <param name="windowHandle">主窗口的 HWND（任务栏覆盖图标、闪烁）；窗口尚未创建时为 0。</param>
    /// <param name="iconPath">Toast 与「设置 → 通知」里显示的应用图标（PNG 文件）。</param>
    public WindowsNotifier(ILogger logger, Func<nint> windowHandle, string? iconPath)
    {
        _logger = logger;
        _windowHandle = windowHandle;
        _iconPath = iconPath;
    }

    public event Action<string>? Activated;

    public void Show(SystemNotification notification)
    {
        if (!_toastUnavailable)
        {
            try
            {
                ShowToast(notification);
                return;
            }
            catch (Exception ex)
            {
                // 系统关闭了通知、AUMID 登记失败、旧系统缺少接口……之后都走退化路径
                _toastUnavailable = true;
                _logger.LogWarning(ex, "Toast notifications are unavailable; falling back to flashing the taskbar button");
            }
        }
        Fallback(notification);
    }

    public void Withdraw(string sessionId)
    {
        if (!_shown.Remove(sessionId, out var shown))
        {
            return;
        }
        try
        {
            shown.Toast.remove_Activated(shown.Token);
            var statics = Factory<IToastNotificationManagerStatics2>("Windows.UI.Notifications.ToastNotificationManager", IidToastNotificationManagerStatics2);
            statics.get_History(out var history);
            using var tag = new HString(Tag(sessionId));
            using var group = new HString(Group);
            using var appId = new HString(AppUserModelId);
            history.RemoveGroupedTagWithId(tag.Handle, group.Handle, appId.Handle);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Withdrawing a toast failed");
        }
    }

    public void SetBadge(int count)
    {
        var hwnd = _windowHandle();
        if (hwnd == 0)
        {
            return;
        }
        try
        {
            _taskbar ??= CreateTaskbar();
            var icon = count > 0 ? CreateOverlayIcon(count) : 0;
            _taskbar.SetOverlayIcon(hwnd, icon, count > 0 ? BadgeRenderer.CountText(count) : null);
            if (_overlayIcon != 0)
            {
                DestroyIcon(_overlayIcon);
            }
            _overlayIcon = icon;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Setting the taskbar overlay icon failed");
        }
    }

    public void Dispose()
    {
        foreach (var shown in _shown.Values)
        {
            try
            {
                shown.Toast.remove_Activated(shown.Token);
            }
            catch (Exception)
            {
                // 退出时尽力而为
            }
        }
        _shown.Clear();
        if (_overlayIcon != 0)
        {
            DestroyIcon(_overlayIcon);
            _overlayIcon = 0;
        }
    }

    private void ShowToast(SystemNotification notification)
    {
        EnsureNotifier();
        var inspectable = ActivateInstance("Windows.Data.Xml.Dom.XmlDocument");
        IToastNotification toast;
        try
        {
            var io = (IXmlDocumentIO)Wrappers.GetOrCreateObjectForComInstance(inspectable, CreateObjectFlags.UniqueInstance);
            using (var xml = new HString(ToastXml(notification)))
            {
                io.LoadXml(xml.Handle);
            }
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(inspectable, in IidXmlDocument, out var document));
            try
            {
                _factory!.CreateToastNotification(document, out toast);
            }
            finally
            {
                Marshal.Release(document);
            }
        }
        finally
        {
            Marshal.Release(inspectable);
        }
        // 同一会话固定的 Tag / Group：操作中心里新通知替换旧通知
        var tagged = (IToastNotification2)toast;
        using (var tag = new HString(Tag(notification.SessionId)))
        {
            tagged.put_Tag(tag.Handle);
        }
        using (var group = new HString(Group))
        {
            tagged.put_Group(group.Handle);
        }
        var sessionId = notification.SessionId;
        var handler = new ToastActivatedHandler(() => Activated?.Invoke(sessionId));
        toast.add_Activated(handler, out var token);
        if (_shown.Remove(sessionId, out var previous))
        {
            try
            {
                previous.Toast.remove_Activated(previous.Token);
            }
            catch (COMException)
            {
            }
        }
        _shown[sessionId] = new ShownToast(toast, handler, token);
        _notifier!.Show(toast);
    }

    private void EnsureNotifier()
    {
        if (_notifier is not null)
        {
            return;
        }
        Register();
        _factory = Factory<IToastNotificationFactory>("Windows.UI.Notifications.ToastNotification", IidToastNotificationFactory);
        var statics = Factory<IToastNotificationManagerStatics>("Windows.UI.Notifications.ToastNotificationManager", IidToastNotificationManagerStatics);
        using var appId = new HString(AppUserModelId);
        statics.CreateToastNotifierWithId(appId.Handle, out var notifier);
        // 用户在「设置 → 通知」里关掉了 TKCode：不弹 Toast，改为闪烁任务栏按钮
        notifier.get_Setting(out var setting);
        if (setting != 0)
        {
            throw new InvalidOperationException($"Toast notifications are disabled (NotificationSetting {setting}).");
        }
        _notifier = notifier;
    }

    /// <summary>非打包应用的 AUMID 登记（与 BurntToast、Windows App SDK 的非打包应用相同）；系统「设置 → 通知」里显示为 TKCode。</summary>
    private void Register()
    {
        using var key = Registry.CurrentUser.CreateSubKey(@"Software\Classes\AppUserModelId\" + AppUserModelId);
        key.SetValue("DisplayName", "TKCode");
        if (_iconPath is not null)
        {
            key.SetValue("IconUri", _iconPath);
        }
    }

    private static string ToastXml(SystemNotification notification)
    {
        var xml = new StringBuilder();
        xml.Append("<toast launch=\"").Append(SecurityElement.Escape(notification.SessionId)).Append("\">");
        xml.Append("<visual><binding template=\"ToastGeneric\">");
        xml.Append("<text>").Append(SecurityElement.Escape(notification.Title)).Append("</text>");
        xml.Append("<text>").Append(SecurityElement.Escape(notification.Body)).Append("</text>");
        xml.Append("</binding></visual>");
        // 出声时用系统默认的通知音（不再另外播放）；静音要显式写出来
        if (!notification.Sound)
        {
            xml.Append("<audio silent=\"true\"/>");
        }
        xml.Append("</toast>");
        return xml.ToString();
    }

    /// <summary>Tag 在旧版 Windows 10 上最多 16 个字符：用会话 id 的短哈希。</summary>
    private static string Tag(string sessionId) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sessionId)))[..16];

    private void Fallback(SystemNotification notification)
    {
        if (notification.Sound)
        {
            MessageBeep(MbIconInformation);
        }
        var hwnd = _windowHandle();
        if (hwnd == 0)
        {
            return;
        }
        var info = new FlashWindowInfo
        {
            Size = (uint)Marshal.SizeOf<FlashWindowInfo>(),
            Window = hwnd,
            // 闪烁任务栏按钮，直到窗口回到前台
            Flags = FlashTray | FlashTimerNoForeground,
        };
        FlashWindowEx(ref info);
    }

    private static nint CreateOverlayIcon(int count)
    {
        var png = BadgeRenderer.OverlayPng(count);
        var icon = CreateIconFromResourceEx(png, (uint)png.Length, true, 0x00030000, 32, 32, 0);
        if (icon == 0)
        {
            throw new InvalidOperationException($"CreateIconFromResourceEx failed ({Marshal.GetLastPInvokeError()}).");
        }
        return icon;
    }

    private static ITaskbarList3 CreateTaskbar()
    {
        Marshal.ThrowExceptionForHR(CoCreateInstance(in ClsidTaskbarList, 0, 1, in IidUnknown, out var unknown));
        try
        {
            var taskbar = (ITaskbarList3)Wrappers.GetOrCreateObjectForComInstance(unknown, CreateObjectFlags.UniqueInstance);
            taskbar.HrInit();
            return taskbar;
        }
        finally
        {
            Marshal.Release(unknown);
        }
    }

    private static T Factory<T>(string runtimeClass, Guid iid) where T : class
    {
        using var name = new HString(runtimeClass);
        Marshal.ThrowExceptionForHR(RoGetActivationFactory(name.Handle, in iid, out var factory));
        try
        {
            return (T)Wrappers.GetOrCreateObjectForComInstance(factory, CreateObjectFlags.UniqueInstance);
        }
        finally
        {
            Marshal.Release(factory);
        }
    }

    /// <summary>激活一个 WinRT 类的实例，返回 IInspectable*（调用方负责 Release）。</summary>
    private static nint ActivateInstance(string runtimeClass)
    {
        using var name = new HString(runtimeClass);
        Marshal.ThrowExceptionForHR(RoActivateInstance(name.Handle, out var instance));
        return instance;
    }

    private sealed record ShownToast(IToastNotification Toast, ToastActivatedHandler Handler, long Token);

    /// <summary>HSTRING：用完删除。</summary>
    private sealed class HString : IDisposable
    {
        public HString(string value)
        {
            Marshal.ThrowExceptionForHR(WindowsCreateString(value, (uint)value.Length, out var handle));
            Handle = handle;
        }

        public nint Handle { get; private set; }

        public void Dispose()
        {
            if (Handle != 0)
            {
                WindowsDeleteString(Handle);
                Handle = 0;
            }
        }
    }

    private const uint MbIconInformation = 0x40;
    private const uint FlashTray = 0x2;
    private const uint FlashTimerNoForeground = 0xC;

    [StructLayout(LayoutKind.Sequential)]
    private struct FlashWindowInfo
    {
        public uint Size;
        public nint Window;
        public uint Flags;
        public uint Count;
        public uint Timeout;
    }

    [LibraryImport("combase.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int WindowsCreateString(string sourceString, uint length, out nint hstring);

    [LibraryImport("combase.dll")]
    private static partial int WindowsDeleteString(nint hstring);

    [LibraryImport("combase.dll")]
    private static partial int RoGetActivationFactory(nint activatableClassId, in Guid iid, out nint factory);

    [LibraryImport("combase.dll")]
    private static partial int RoActivateInstance(nint activatableClassId, out nint instance);

    [LibraryImport("ole32.dll")]
    private static partial int CoCreateInstance(in Guid rclsid, nint pUnkOuter, uint dwClsContext, in Guid riid, out nint ppv);

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial nint CreateIconFromResourceEx(byte[] presbits, uint dwResSize, [MarshalAs(UnmanagedType.Bool)] bool fIcon, uint dwVer,
        int cxDesired, int cyDesired, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyIcon(nint icon);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool MessageBeep(uint type);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool FlashWindowEx(ref FlashWindowInfo info);
}
