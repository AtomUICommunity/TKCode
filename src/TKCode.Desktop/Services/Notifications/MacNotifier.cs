using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;

namespace TKCode.Desktop.Services.Notifications;

/// <summary>
/// macOS 的系统通知与 Dock 角标（docs/design/notifications.md §5.1），全部经 libobjc 调用（<c>[LibraryImport]</c> 的 objc_msgSend），NativeAOT 安全。
/// <list type="bullet">
/// <item>有 bundle id（打包的 TKCode.app）：UNUserNotificationCenter。第一次投递时请求授权，授权回调里再投递这第一条。</item>
/// <item>没有 bundle id（<c>dotnet run</c>、直接运行发布目录里的二进制）：currentNotificationCenter 会抛 ObjC 异常导致进程崩溃，
/// 所以不碰它，退化为 Dock 图标跳一次 + NSSound。</item>
/// </list>
/// 点击由运行时注册的 <c>TKCodeNotificationDelegate</c> 接收（UNUserNotificationCenterDelegate），请求 id 里带会话 id。
/// </summary>
[SupportedOSPlatform("macos")]
internal sealed unsafe partial class MacNotifier : ISystemNotifier
{
    private const string ObjCLibrary = "/usr/lib/libobjc.A.dylib";
    private const string UserNotificationsFramework = "/System/Library/Frameworks/UserNotifications.framework/UserNotifications";
    private const string IdentifierPrefix = "tkcode.session.";
    private const string DelegateClassName = "TKCodeNotificationDelegate";

    // UNAuthorizationOptions: badge | sound | alert
    private const nuint AuthorizationOptions = 1 | 2 | 4;
    // UNNotificationPresentationOptions: sound | list | banner（应用在前台时也显示：只有「总是」档位会在前台发通知）
    private const nuint PresentationOptions = (1 << 1) | (1 << 3) | (1 << 4);
    // NSRequestUserAttentionType.NSInformationalRequest：Dock 图标跳一次
    private const nint InformationalRequest = 10;

    private const int BlockIsGlobal = 1 << 28;
    private const int BlockHasSignature = 1 << 30;

    /// <summary>ObjC 回调是静态函数，经它找到实例（进程里只有一个通知器）。</summary>
    private static MacNotifier? s_current;
    private static nint s_authorizationBlock;
    private static nint s_addRequestBlock;
    private static nint s_delegate;

    private readonly ILogger _logger;
    private readonly Action<Action> _post;
    private readonly nint _center;
    private AuthorizationState _authorization;
    /// <summary>等待授权结果时到达的通知（每个会话保留最新一条）。</summary>
    private readonly Dictionary<string, SystemNotification> _waiting = new(StringComparer.Ordinal);
    private bool _loggedFallback;

    public MacNotifier(ILogger logger, Action<Action> post)
    {
        _logger = logger;
        _post = post;
        s_current = this;
        try
        {
            _center = CreateCenter();
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or InvalidOperationException)
        {
            _logger.LogWarning(ex, "UNUserNotificationCenter is unavailable");
            _center = 0;
        }
    }

    public event Action<string>? Activated;

    /// <summary>能否用 UNUserNotificationCenter（进程有 bundle id 且框架可用）。</summary>
    public bool UsesNotificationCenter => _center != 0;

    public void Show(SystemNotification notification)
    {
        try
        {
            if (_center == 0)
            {
                Fallback(notification);
                return;
            }
            switch (_authorization)
            {
                case AuthorizationState.Granted:
                    Post(notification);
                    break;
                case AuthorizationState.Denied:
                    break;
                case AuthorizationState.Requesting:
                    _waiting[notification.SessionId] = notification;
                    break;
                default:
                    _waiting[notification.SessionId] = notification;
                    _authorization = AuthorizationState.Requesting;
                    SendVoid(_center, Sel("requestAuthorizationWithOptions:completionHandler:"), AuthorizationOptions, AuthorizationBlock());
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Showing a notification failed");
        }
    }

    public void Withdraw(string sessionId)
    {
        if (_center == 0)
        {
            return;
        }
        try
        {
            _waiting.Remove(sessionId);
            var identifiers = Send(Class("NSArray"), Sel("arrayWithObject:"), NSString(IdentifierPrefix + sessionId));
            Send(_center, Sel("removeDeliveredNotificationsWithIdentifiers:"), identifiers);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Withdrawing a notification failed");
        }
    }

    /// <summary>Dock 图标的角标；有没有 bundle id 都有效。</summary>
    public void SetBadge(int count)
    {
        try
        {
            var tile = Send(SharedApplication(), Sel("dockTile"));
            Send(tile, Sel("setBadgeLabel:"), count > 0 ? NSString(count > 99 ? "99+" : count.ToString(System.Globalization.CultureInfo.InvariantCulture)) : 0);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Setting the Dock badge failed");
        }
    }

    public void Dispose()
    {
        if (ReferenceEquals(s_current, this))
        {
            s_current = null;
        }
    }

    /// <summary>
    /// 冒烟模式：注册委托类、创建 block，在 .app 里还取得 currentNotificationCenter；不请求授权、不弹出任何东西。
    /// 返回是否会走 UNUserNotificationCenter。
    /// </summary>
    internal static bool Probe()
    {
        _ = DelegateClass();
        _ = AuthorizationBlock();
        _ = AddRequestBlock();
        return HasBundleIdentifier() && NativeLibrary.TryLoad(UserNotificationsFramework, out _) &&
            Class("UNUserNotificationCenter") is var type && type != 0 && Send(type, Sel("currentNotificationCenter")) != 0;
    }

    private static nint CreateCenter()
    {
        if (!HasBundleIdentifier())
        {
            return 0;
        }
        if (!NativeLibrary.TryLoad(UserNotificationsFramework, out _) || Class("UNUserNotificationCenter") is var type && type == 0)
        {
            return 0;
        }
        var center = Send(type, Sel("currentNotificationCenter"));
        if (center == 0)
        {
            return 0;
        }
        if (s_delegate == 0)
        {
            // delegate 属性是 weak：alloc/init 的 +1 引用永不释放，对象常驻
            s_delegate = Send(Send(DelegateClass(), Sel("alloc")), Sel("init"));
        }
        Send(center, Sel("setDelegate:"), s_delegate);
        return center;
    }

    private static bool HasBundleIdentifier()
    {
        var bundle = Send(Class("NSBundle"), Sel("mainBundle"));
        return bundle != 0 && Send(bundle, Sel("bundleIdentifier")) != 0;
    }

    private void Post(SystemNotification notification)
    {
        var content = Send(Send(Class("UNMutableNotificationContent"), Sel("alloc")), Sel("init"));
        try
        {
            Send(content, Sel("setTitle:"), NSString(notification.Title));
            Send(content, Sel("setBody:"), NSString(notification.Body));
            if (notification.Sound)
            {
                Send(content, Sel("setSound:"), Send(Class("UNNotificationSound"), Sel("defaultSound")));
            }
            // 同一会话用同一个 id：新通知替换通知中心里的旧通知
            var request = Send(Class("UNNotificationRequest"), Sel("requestWithIdentifier:content:trigger:"),
                NSString(IdentifierPrefix + notification.SessionId), content, 0);
            Send(_center, Sel("addNotificationRequest:withCompletionHandler:"), request, AddRequestBlock());
        }
        finally
        {
            Send(content, Sel("release"));
        }
    }

    /// <summary>没有 bundle id：Dock 图标跳一次，需要时放一声系统提示音。</summary>
    private void Fallback(SystemNotification notification)
    {
        if (!_loggedFallback)
        {
            _loggedFallback = true;
            _logger.LogInformation("TKCode is not running from an app bundle; system notifications fall back to a Dock bounce");
        }
        Send(SharedApplication(), Sel("requestUserAttention:"), InformationalRequest);
        if (notification.Sound && Send(Class("NSSound"), Sel("soundNamed:"), NSString("Glass")) is var sound && sound != 0)
        {
            Send(sound, Sel("play"));
        }
    }

    private void AuthorizationFinished(bool granted, string? error)
    {
        _authorization = granted ? AuthorizationState.Granted : AuthorizationState.Denied;
        if (!granted)
        {
            _logger.LogInformation("Notifications are not authorized{Reason}", error is null ? "" : ": " + error);
            _waiting.Clear();
            return;
        }
        foreach (var notification in _waiting.Values.ToList())
        {
            try
            {
                Post(notification);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Showing a notification failed");
            }
        }
        _waiting.Clear();
    }

    // ---- ObjC 回调（静态，不得抛出异常） ----

    [UnmanagedCallersOnly]
    private static void OnAuthorization(nint block, byte granted, nint error)
    {
        try
        {
            var reason = error == 0 ? null : ToManaged(Send(error, Sel("localizedDescription")));
            if (s_current is { } current)
            {
                current._post(() => current.AuthorizationFinished(granted != 0, reason));
            }
        }
        catch
        {
            // 回调里抛出会终止进程
        }
    }

    [UnmanagedCallersOnly]
    private static void OnRequestAdded(nint block, nint error)
    {
        try
        {
            if (error != 0 && s_current is { } current)
            {
                var reason = ToManaged(Send(error, Sel("localizedDescription")));
                current._post(() => current._logger.LogWarning("Adding a notification failed: {Reason}", reason));
            }
        }
        catch
        {
        }
    }

    /// <summary>userNotificationCenter:didReceiveNotificationResponse:withCompletionHandler:</summary>
    [UnmanagedCallersOnly]
    private static void DidReceiveResponse(nint self, nint cmd, nint center, nint response, nint completion)
    {
        try
        {
            var action = ToManaged(Send(response, Sel("actionIdentifier")));
            var identifier = ToManaged(Send(Send(Send(response, Sel("notification")), Sel("request")), Sel("identifier")));
            if (action != "com.apple.UNNotificationDismissActionIdentifier" && identifier is not null &&
                identifier.StartsWith(IdentifierPrefix, StringComparison.Ordinal))
            {
                s_current?.Activated?.Invoke(identifier[IdentifierPrefix.Length..]);
            }
        }
        catch
        {
        }
        finally
        {
            InvokeBlock(completion);
        }
    }

    /// <summary>userNotificationCenter:willPresentNotification:withCompletionHandler:（应用在前台时也显示横幅）</summary>
    [UnmanagedCallersOnly]
    private static void WillPresent(nint self, nint cmd, nint center, nint notification, nint completion)
    {
        if (completion != 0)
        {
            ((delegate* unmanaged<nint, nuint, void>)((BlockLiteral*)completion)->Invoke)(completion, PresentationOptions);
        }
    }

    private static void InvokeBlock(nint block)
    {
        if (block != 0)
        {
            ((delegate* unmanaged<nint, void>)((BlockLiteral*)block)->Invoke)(block);
        }
    }

    // ---- 委托类与 block ----

    private static nint DelegateClass()
    {
        var existing = Class(DelegateClassName);
        if (existing != 0)
        {
            return existing;
        }
        var type = objc_allocateClassPair(Class("NSObject"), DelegateClassName, 0);
        if (type == 0)
        {
            throw new InvalidOperationException("objc_allocateClassPair failed.");
        }
        class_addMethod(type, Sel("userNotificationCenter:didReceiveNotificationResponse:withCompletionHandler:"),
            (nint)(delegate* unmanaged<nint, nint, nint, nint, nint, void>)&DidReceiveResponse, "v@:@@@?");
        class_addMethod(type, Sel("userNotificationCenter:willPresentNotification:withCompletionHandler:"),
            (nint)(delegate* unmanaged<nint, nint, nint, nint, nint, void>)&WillPresent, "v@:@@@?");
        if (objc_getProtocol("UNUserNotificationCenterDelegate") is var protocol && protocol != 0)
        {
            class_addProtocol(type, protocol);
        }
        objc_registerClassPair(type);
        return type;
    }

    /// <summary>void (^)(BOOL granted, NSError *error)</summary>
    private static nint AuthorizationBlock() => s_authorizationBlock != 0
        ? s_authorizationBlock
        : s_authorizationBlock = GlobalBlock((nint)(delegate* unmanaged<nint, byte, nint, void>)&OnAuthorization,
            RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "v@?B@" : "v@?c@");

    /// <summary>void (^)(NSError *error)</summary>
    private static nint AddRequestBlock() => s_addRequestBlock != 0
        ? s_addRequestBlock
        : s_addRequestBlock = GlobalBlock((nint)(delegate* unmanaged<nint, nint, void>)&OnRequestAdded, "v@?@");

    /// <summary>
    /// 常驻内存的 global block（isa = _NSConcreteGlobalBlock）：<c>Block_copy</c> 对 global block 返回自身，
    /// 所以没有拷贝 / 释放辅助函数，也不会被释放。
    /// </summary>
    private static nint GlobalBlock(nint invoke, string signature)
    {
        var isa = GlobalBlockClass();
        var descriptor = (BlockDescriptor*)NativeMemory.AllocZeroed((nuint)sizeof(BlockDescriptor));
        descriptor->Size = (nuint)sizeof(BlockLiteral);
        descriptor->Signature = Marshal.StringToCoTaskMemUTF8(signature);
        var block = (BlockLiteral*)NativeMemory.AllocZeroed((nuint)sizeof(BlockLiteral));
        block->Isa = isa;
        block->Flags = BlockIsGlobal | BlockHasSignature;
        block->Invoke = invoke;
        block->Descriptor = (nint)descriptor;
        return (nint)block;
    }

    private static nint GlobalBlockClass()
    {
        foreach (var library in (string[])["/usr/lib/libSystem.B.dylib", "/usr/lib/system/libsystem_blocks.dylib"])
        {
            if (NativeLibrary.TryLoad(library, out var handle) && NativeLibrary.TryGetExport(handle, "_NSConcreteGlobalBlock", out var symbol))
            {
                return symbol;
            }
        }
        throw new EntryPointNotFoundException("_NSConcreteGlobalBlock");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BlockLiteral
    {
        public nint Isa;
        public int Flags;
        public int Reserved;
        public nint Invoke;
        public nint Descriptor;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BlockDescriptor
    {
        public nuint Reserved;
        public nuint Size;
        public nint Signature;
    }

    // ---- libobjc ----

    private static nint SharedApplication() => Send(Class("NSApplication"), Sel("sharedApplication"));

    private static nint Class(string name) => objc_getClass(name);

    private static nint Sel(string name) => sel_registerName(name);

    /// <summary>autorelease 的 NSString（UI 线程在 Avalonia 的运行循环里，有 autorelease pool）。</summary>
    private static nint NSString(string text)
    {
        var utf8 = Marshal.StringToCoTaskMemUTF8(text);
        try
        {
            return Send(Class("NSString"), Sel("stringWithUTF8String:"), utf8);
        }
        finally
        {
            Marshal.FreeCoTaskMem(utf8);
        }
    }

    private static string? ToManaged(nint nsString) =>
        nsString == 0 ? null : Marshal.PtrToStringUTF8(Send(nsString, Sel("UTF8String")));

    [LibraryImport(ObjCLibrary, StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint objc_getClass(string name);

    [LibraryImport(ObjCLibrary, StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint sel_registerName(string name);

    [LibraryImport(ObjCLibrary, StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint objc_getProtocol(string name);

    [LibraryImport(ObjCLibrary, StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint objc_allocateClassPair(nint superclass, string name, nuint extraBytes);

    [LibraryImport(ObjCLibrary)]
    private static partial void objc_registerClassPair(nint type);

    [LibraryImport(ObjCLibrary, StringMarshalling = StringMarshalling.Utf8)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static partial bool class_addMethod(nint type, nint selector, nint implementation, string types);

    [LibraryImport(ObjCLibrary)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static partial bool class_addProtocol(nint type, nint protocol);

    [LibraryImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
    private static partial nint Send(nint receiver, nint selector);

    [LibraryImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
    private static partial nint Send(nint receiver, nint selector, nint arg1);

    [LibraryImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
    private static partial nint Send(nint receiver, nint selector, nint arg1, nint arg2);

    [LibraryImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
    private static partial nint Send(nint receiver, nint selector, nint arg1, nint arg2, nint arg3);

    [LibraryImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
    private static partial void SendVoid(nint receiver, nint selector, nuint arg1, nint arg2);

    private enum AuthorizationState
    {
        NotRequested,
        Requesting,
        Granted,
        Denied,
    }
}
