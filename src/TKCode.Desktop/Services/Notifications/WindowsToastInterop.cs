using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace TKCode.Desktop.Services.Notifications;

// Windows.UI.Notifications / Windows.Data.Xml.Dom 的 ABI 接口（只声明用到的方法；之后的槽位用不到可以省略）。
// WinRT 接口派生自 IInspectable：前三个方法是 GetIids / GetRuntimeClassName / GetTrustLevel。
// 方法顺序与 Windows SDK 头文件（及 Wine 的 windows.ui.notifications.idl / windows.data.xml.dom.idl）的虚函数表一致；
// HSTRING 一律按 nint 传递（由 WindowsNotifier.HString 创建与释放）。

[GeneratedComInterface]
[Guid("6cd0e74e-ee65-4489-9ebf-ca43e87ba637")]
internal partial interface IXmlDocumentIO
{
    void GetIids(out uint count, out nint iids);
    void GetRuntimeClassName(out nint className);
    void GetTrustLevel(out int trustLevel);
    void LoadXml(nint xml);
}

[GeneratedComInterface]
[Guid("04124b20-82c6-4229-b109-fd9ed4662b53")]
internal partial interface IToastNotificationFactory
{
    void GetIids(out uint count, out nint iids);
    void GetRuntimeClassName(out nint className);
    void GetTrustLevel(out int trustLevel);

    /// <param name="content">Windows.Data.Xml.Dom.XmlDocument 的默认接口 IXmlDocument*。</param>
    void CreateToastNotification(nint content, out IToastNotification value);
}

[GeneratedComInterface]
[Guid("50ac103f-d235-4598-bbef-98fe4d1a3ad4")]
internal partial interface IToastNotificationManagerStatics
{
    void GetIids(out uint count, out nint iids);
    void GetRuntimeClassName(out nint className);
    void GetTrustLevel(out int trustLevel);
    void CreateToastNotifier(out IToastNotifier result);
    void CreateToastNotifierWithId(nint applicationId, out IToastNotifier result);
}

[GeneratedComInterface]
[Guid("7ab93c52-0e48-4750-ba9d-1a4113981847")]
internal partial interface IToastNotificationManagerStatics2
{
    void GetIids(out uint count, out nint iids);
    void GetRuntimeClassName(out nint className);
    void GetTrustLevel(out int trustLevel);
    void get_History(out IToastNotificationHistory value);
}

[GeneratedComInterface]
[Guid("5caddc63-01d3-4c97-986f-0533483fee14")]
internal partial interface IToastNotificationHistory
{
    void GetIids(out uint count, out nint iids);
    void GetRuntimeClassName(out nint className);
    void GetTrustLevel(out int trustLevel);
    void RemoveGroup(nint group);
    void RemoveGroupWithId(nint group, nint applicationId);
    void RemoveGroupedTagWithId(nint tag, nint group, nint applicationId);
}

[GeneratedComInterface]
[Guid("75927b93-03f3-41ec-91d3-6e5bac1b38e7")]
internal partial interface IToastNotifier
{
    void GetIids(out uint count, out nint iids);
    void GetRuntimeClassName(out nint className);
    void GetTrustLevel(out int trustLevel);
    void Show(IToastNotification notification);
    void Hide(IToastNotification notification);

    /// <summary>NotificationSetting：0 = Enabled，其余为被应用 / 用户 / 组策略 / 清单关闭。</summary>
    void get_Setting(out int value);
}

[GeneratedComInterface]
[Guid("997e2675-059e-4e60-8b06-1760917c8b80")]
internal partial interface IToastNotification
{
    void GetIids(out uint count, out nint iids);
    void GetRuntimeClassName(out nint className);
    void GetTrustLevel(out int trustLevel);
    void get_Content(out nint value);
    void put_ExpirationTime(nint value);
    void get_ExpirationTime(out nint value);
    void add_Dismissed(nint handler, out long token);
    void remove_Dismissed(long token);
    void add_Activated(IToastActivatedHandler handler, out long token);
    void remove_Activated(long token);
}

[GeneratedComInterface]
[Guid("9dfb9fd1-143a-490e-90bf-b9fba7132de7")]
internal partial interface IToastNotification2
{
    void GetIids(out uint count, out nint iids);
    void GetRuntimeClassName(out nint className);
    void GetTrustLevel(out int trustLevel);
    void put_Tag(nint value);
    void get_Tag(out nint value);
    void put_Group(nint value);
}

/// <summary>
/// Windows.Foundation.TypedEventHandler&lt;ToastNotification, Object&gt;：委托派生自 IUnknown，只有 Invoke。
/// IID 按 WinRT 参数化接口的算法（pinterface 签名的 SHA-1 名字空间 UUID）算出，与 SDK 头文件一致。
/// </summary>
[GeneratedComInterface]
[Guid("ab54de2d-97d9-5528-b6ad-105afe156530")]
internal partial interface IToastActivatedHandler
{
    void Invoke(nint sender, nint args);
}

/// <summary>标记接口：事件源可以在任意线程直接调用处理器，不必封送回注册它的线程。</summary>
[GeneratedComInterface]
[Guid("94ea2b94-e9cc-49e0-c0ff-ee64ca8f5b90")]
internal partial interface IAgileObject
{
}

[GeneratedComClass]
internal sealed partial class ToastActivatedHandler(Action activated) : IToastActivatedHandler, IAgileObject
{
    public void Invoke(nint sender, nint args) => activated();
}

[GeneratedComInterface]
[Guid("ea1afb91-9e28-4b86-90e9-9e9f8a5eefaf")]
internal partial interface ITaskbarList3
{
    // ITaskbarList
    void HrInit();
    void AddTab(nint hwnd);
    void DeleteTab(nint hwnd);
    void ActivateTab(nint hwnd);
    void SetActiveAlt(nint hwnd);

    // ITaskbarList2
    void MarkFullscreenWindow(nint hwnd, int fullscreen);

    // ITaskbarList3
    void SetProgressValue(nint hwnd, ulong completed, ulong total);
    void SetProgressState(nint hwnd, int flags);
    void RegisterTab(nint tab, nint mdi);
    void UnregisterTab(nint tab);
    void SetTabOrder(nint tab, nint insertBefore);
    void SetTabActive(nint tab, nint mdi, uint reserved);
    void ThumbBarAddButtons(nint hwnd, uint count, nint buttons);
    void ThumbBarUpdateButtons(nint hwnd, uint count, nint buttons);
    void ThumbBarSetImageList(nint hwnd, nint imageList);
    void SetOverlayIcon(nint hwnd, nint icon, [MarshalAs(UnmanagedType.LPWStr)] string? description);
}
