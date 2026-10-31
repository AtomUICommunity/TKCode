# 系统通知、提示音与未读角标

对应 [message-features-report.md](message-features-report.md) §6.1 R2。托盘、Dock 菜单与跳转列表见 [system-menus.md](system-menus.md)。

## 1. 目标

会话需要用户时，即使用户在别的窗口、别的会话，或者 TKCode 隐藏在托盘里，也能知道：

1. 一轮运行结束（成功且耗时较长，或者出错）时发系统通知。
2. 会话等待用户处理（工具审批、提问、计划审批、MCP 表单）时发系统通知。
3. 通知可以带提示音；点通知回到 TKCode 并切到对应会话。
4. Dock / 任务栏 / 托盘显示"有几个会话等你查看"，打开会话后清除。

## 2. 已确认的决策（用户，2026-10-30）

| 问题 | 决定 |
|---|---|
| 平台 | macOS、Windows、Linux 一起做 |
| 回合结束 | 只在**成功且本轮耗时 ≥ 30 秒**时通知（Cherry Studio 的做法，阈值可配）；**出错**的回合总是通知；用户自己停止的不通知 |
| 需要处理 | 工具审批、`ask_user` 提问（含外部运行时的提问）、计划审批、MCP elicitation |
| 设置 | 「回合结束」「需要你处理」分开设置，各自 从不 / 仅在后台（默认）/ 总是；「播放提示音」开关，默认开 |
| 什么算"后台" | 主窗口不是活动窗口（失焦、最小化、隐藏到托盘），**或**该会话不是当前显示的会话 |
| 内容 | 标题 = 会话标题（没有时用项目名）；正文见 §4.6 |
| 合并 | 同一会话 2 秒内连续到达的只显示最新一条，不刷屏 |
| 点击 | 显示主窗口（包括从托盘恢复）并切到该会话；平台做不到可靠回调的写进文档 |
| 声音 | 系统通知自己会响时不再另外播放 |
| 未读 | macOS Dock 角标；Windows 任务栏覆盖图标 + 托盘；Linux 至少托盘提示 |
| 平台代码 | NativeAOT 安全，不加 NuGet 包；放在接口后面，决策逻辑在任何系统上都能测试 |

## 3. 现状

- **事件都已经到达 Desktop**，只是没人往系统层面转发：
    - 回合开始 / 结束：`SessionViewModel.Apply` 的 `AgentStarted` / `AgentEnded`，结束时已经有 `RunEnded(bool completed)`（排队消息用它续发）。助手消息的停止原因在 `MessageEnded.StopReason`（`Error` / `Aborted`），错误文字记在 `AssistantMessageItemViewModel.ErrorMessage`。
    - 需要处理：`ConversationViewModel` 的四个 RPC 回调 `HandleApprovalAsync`（`tool/approve`）、`HandleUserInputAsync`（`input/request`）、`HandlePlanReviewAsync`（`plan/review`）、`HandleElicitationAsync`（MCP elicitation），各自把请求挂到对应的 `SessionViewModel` 上。侧栏的橙点（`IsWaitingApproval`）就来自这里。
- **托盘与 Dock**：`AppTray`（`Services/AppTray.cs`）持有 Avalonia `TrayIcon`、`ShowWindow()`（含从托盘恢复），关闭窗口默认隐藏到托盘。macOS Dock 图标用 `MacDockIcon` 经 libobjc 设置；Windows 跳转列表 `WindowsJumpList` 用源生成 COM（`GeneratedComInterface`）。
- 没有任何系统通知、提示音、角标代码；`ask-user.md` §7.3、`plan-mode.md` §17 都写着"不在本期"。

## 4. 设计

### 4.1 结构

```
ConversationViewModel ──SessionAttention──▶ NotificationCenter ──▶ ISystemNotifier（平台实现）
   ▲ TurnEnded / 四个 RPC 回调              │  决策、合并、未读      ├ MacNotifier      UNUserNotificationCenter + Dock 角标
   │                                        │                        ├ WindowsNotifier  Toast + 任务栏覆盖图标
IAppPresence（窗口是否在前台、显示哪个会话）─┘                        ├ LinuxNotifier    org.freedesktop.Notifications（gdbus）
                                            └─UnreadChanged──▶ AppTray（托盘提示文字 + 图标红点）
```

- `NotificationCenter`（`Services/Notifications/`）不引用 Avalonia：时间来自 `TimeProvider`，回到 UI 线程用注入的 `post`，窗口状态来自 `IAppPresence`，设置来自 `Func<NotificationPreferences>`。单元测试用假的 notifier 与 presence。
- 平台实现只在真正的桌面启动路径（`App.OnFrameworkInitializationCompleted`）里创建；无头测试、冒烟模式不会弹出真实通知。
- `ConversationViewModel` 新增事件 `AttentionRaised(SessionAttention)` 与 `SessionDeleted(string)`；`SessionViewModel` 新增 `TurnEnded(TurnSummary)`（停止原因、错误、耗时、最后一段正文）。子 Agent 的转录不经过 `Track`，不会触发。

### 4.2 事件与规则

| 事件 | 来源 | 设置项 | 额外条件 |
|---|---|---|---|
| 回合完成 | `AgentEnded`，停止原因不是 `Error` / `Aborted` | 回合结束 | 本轮耗时 ≥ `minTurnSeconds`（默认 30） |
| 回合出错 | `AgentEnded`，最后一条助手消息 `Error` | 回合结束 | 无（总是） |
| 用户停止 | `Aborted` | — | 不通知、不算未读 |
| 工具审批 | `HandleApprovalAsync` | 需要你处理 | — |
| 提问 | `HandleUserInputAsync` | 需要你处理 | — |
| 计划审批 | `HandlePlanReviewAsync` | 需要你处理 | — |
| MCP 表单 | `HandleElicitationAsync` | 需要你处理 | — |

档位：`never` 不发；`background` 只在 §4.3 的"后台"时发；`always` 总是发（包括正在看这个会话时）。

耗时从 `AgentStarted` 开始计；打开一个已经在运行的会话时从打开的那一刻算（只会偏短）。正常结束、但排队的下一条消息会自动发出时，不算「回合结束」（Agent 还在继续工作）。

### 4.3 什么算"后台"

`IAppPresence`：

- `IsForeground`：主窗口可见、未最小化、是活动窗口（`Window.IsActive`）。隐藏到托盘、⌘H、切到别的应用都算不在前台。
- `VisibleSessionId`：设置页打开时为 null，否则是 `Conversation.Active` 的会话。

某个会话"在后台" = `!IsForeground || VisibleSessionId != 会话`。窗口激活 / 失活、显示 / 隐藏、最小化、切换会话、开关设置页都会调用 `NotificationCenter.PresenceChanged()`。

### 4.4 合并与提示音

- 每个会话在系统通知中心里**只占一条**：macOS 用固定的请求 id，Windows 用固定的 Tag/Group，Linux 用 `replaces_id`。新的一条替换旧的。
- 节流（先发后合并）：会话距上一次投递不到 2 秒时，新事件先存为"待发"（只保留最新一条），到 2 秒时再投递；期间用户打开了该会话（不再是后台）就丢弃。
- 提示音也按 2 秒节流（跨会话），连续几个会话同时结束只响一次；合并后补发的那条不再响。
- 声音由平台实现负责：系统通知自己能响（macOS `UNNotificationSound.default`、Windows Toast 默认音、Linux 服务器支持 `sound` 能力时传 `sound-name`）就只交给系统；否则自己放一个短的系统声音（§5）。"播放提示音"关闭时显式静音（Toast `<audio silent="true"/>`、Linux `suppress-sound`）。

### 4.5 未读

- 会话在后台时**回合结束（成功或出错）**或**需要处理**，记为未读；与通知档位无关（档位只管弹不弹通知），用户停止的不算。
- 会话变成"前台可见"（窗口在前台且显示它）时清除未读，并撤回它在通知中心里的那条通知；删除会话时也清除。
- 未读数（会话个数）同步到：
    - macOS：`NSApp.dockTile.badgeLabel`（0 时清空）。
    - Windows：任务栏按钮的覆盖图标（`ITaskbarList3::SetOverlayIcon`，红底白字，超过 9 显示 9+）。
    - 三个平台的托盘：提示文字「TKCode · 待查看：N」，图标右上角加红点。
    - Linux 启动器计数（Unity `LauncherEntry`）不做：发行包是 tar.gz，没有固定的 `.desktop` id。

### 4.6 文案

- 标题：会话的显示标题（自定义标题 → 自动标题）→ 项目名 → "TKCode"。
- 正文：

| 事件 | en-US | zh-CN |
|---|---|---|
| 回合完成 | 最后一段正文的最后一句（去掉 Markdown 记号、跳过代码块，约 120 字）；没有正文时 "Finished" | 同左；"已完成" |
| 回合出错 | Turn failed: {错误首行} | 运行失败：{错误首行} |
| 工具审批 | Waiting for approval: {命令 / 路径 / 工具名} | 等待审批：{…} |
| 提问 | Has a question for you | 有问题需要你回答 |
| 计划审批 | Plan ready for review | 计划已就绪，等待审批 |
| MCP 表单 | {服务器} is asking for input | {服务器} 需要你填写信息 |

正文统一截断到 120 个字符（不拆开代理对）。

### 4.7 点击

平台回调 `ISystemNotifier.Activated(sessionId)` → 回到 UI 线程 → `AppTray.ShowWindow()`（含从托盘恢复、取消最小化）→ 关闭设置页 → `ConversationViewModel.OpenSessionByIdAsync`（与命令面板相同，走 `OpenSessionCommand`，不改侧栏的项目筛选）。会话已被删除时只显示窗口；点「测试通知」也只显示窗口，不离开设置页。

### 4.8 设置

`settings.json`：

```jsonc
"notifications": {
  "turnCompleted": "background",   // never | background（默认）| always
  "needsInput": "background",      // never | background（默认）| always
  "sound": true,                   // 默认 true
  "minTurnSeconds": 30             // 回合完成通知的最短耗时（秒），只能手改
}
```

默认值不写入文件（与其他字段一致），空的 `notifications` 节一并删除。未知字段、非法取值给出警告并按默认处理。

设置页「通用」新增一张「通知」卡片：「回合结束」「需要你处理」两行分段选择（从不 / 仅在后台 / 总是），「播放提示音」开关，以及卡片标题右侧的「发送测试通知」按钮（不受档位与后台规则限制，用来确认系统权限）。

## 5. 平台实现

全部在 `Services/Notifications/`，任何失败只写日志、不抛出，不影响主流程。

### 5.1 macOS（`MacNotifier`）

- 通过 libobjc（`[LibraryImport]` 的 `objc_msgSend` 重载）调用，不依赖绑定库。
- **通知**：`UNUserNotificationCenter`。
    - 前提是有 bundle id：`[[NSBundle mainBundle] bundleIdentifier]` 为 nil（`dotnet run`、直接运行发布目录里的二进制）时，`currentNotificationCenter` 会抛 ObjC 异常让进程崩溃，所以先判断；没有 bundle 时退化为 `[NSApp requestUserAttention:NSInformationalRequest]`（Dock 图标跳一次）+ `NSSound "Glass"`，并记一条日志。发布的 `TKCode.app`（`tools/package_macos_app.sh`，`com.tkcode.desktop`）走正常路径。
    - 第一次投递时 `requestAuthorizationWithOptions:(alert|sound|badge)`，回调里再投递这第一条；用户拒绝后系统静默丢弃，不再重复请求。
    - 内容：`UNMutableNotificationContent` 的 title / body，声音开时 `UNNotificationSound.defaultSound`；请求 id = `tkcode.session.<会话 id>`（同一会话替换）。
    - completion handler 是 ObjC block：用常驻内存的 global block（`_NSConcreteGlobalBlock`，`[UnmanagedCallersOnly]` 的 invoke），`Block_copy` 对 global block 返回自身，所以不需要拷贝辅助函数。
    - 点击：运行时用 `objc_allocateClassPair` 注册 `TKCodeNotificationDelegate`，实现 `didReceiveNotificationResponse`（取请求 id 解析会话，调用 completion handler）和 `willPresentNotification`（应用在前台时也显示横幅：只有「总是」档位和测试通知会在前台发）。
    - 撤回：`removeDeliveredNotificationsWithIdentifiers:`。
- **角标**：`[[NSApp dockTile] setBadgeLabel:]`，有没有 bundle 都有效。

### 5.2 Windows（`WindowsNotifier`）

- **Toast**：手写 WinRT 互操作，不用 CsWinRT 投影（Desktop 的目标框架是跨平台的 `net11.0`）。
    - `combase.dll` 的 `RoGetActivationFactory` / `RoActivateInstance` / `WindowsCreateString`，接口用 `GeneratedComInterface` 声明（前三个方法是 IInspectable 的 `GetIids` / `GetRuntimeClassName` / `GetTrustLevel`），虚函数表顺序与 Windows SDK / Wine 的 IDL 一致。
    - 用到：`XmlDocument`（`IXmlDocumentIO.LoadXml`）、`IToastNotificationFactory.CreateToastNotification`、`IToastNotificationManagerStatics.CreateToastNotifierWithId`、`IToastNotifier.Show`、`IToastNotification2.Tag/Group`、`IToastNotificationManagerStatics2.History` → `IToastNotificationHistory.RemoveGroupedTagWithId`。
    - **AppUserModelID**：非打包应用必须用已登记的 AUMID。不给进程设置显式 AUMID（跳转列表的决定：否则已固定的快捷方式与运行中的按钮分成两个），而是在 `HKCU\Software\Classes\AppUserModelId\TKCode.Desktop` 写 `DisplayName=TKCode`、`IconUri=<数据目录>/notification-icon.png`，再用 `CreateToastNotifierWithId("TKCode.Desktop")`。不需要开始菜单快捷方式（与 BurntToast、Windows App SDK 非打包应用相同的做法）；系统「设置 → 通知」里会出现 "TKCode"。
    - XML：`ToastGeneric` 模板两行文字；声音关时 `<audio silent="true"/>`，开时用系统默认通知音（不再自己播放）。Tag = 会话 id 的短哈希（旧系统 Tag 上限 16 字符），Group = `tkcode`。
    - **点击**：在 `ToastNotification.Activated` 上注册事件处理器——一个 `GeneratedComClass`，实现 `TypedEventHandler<ToastNotification, Object>`（IID `ab54de2d-97d9-5528-b6ad-105afe156530`，按 WinRT 参数化接口算法核对过）与 `IAgileObject`。只在 TKCode 进程还在运行时有效（关闭窗口默认只隐藏到托盘，所以通常成立）；退出后在操作中心里点旧通知不会启动 TKCode（没有注册 COM 激活器）。从后台进程抢前台可能被系统拦下，此时任务栏按钮闪烁而不是直接置前。
    - Toast 失败（例如系统关闭了通知、AUMID 登记失败）时退化为 `MessageBeep` + `FlashWindowEx`（任务栏按钮闪烁）。
- **角标**：`ITaskbarList3::SetOverlayIcon`。图标由 Avalonia 画成 32×32 PNG（红底白字），`CreateIconFromResourceEx` 转成 HICON。
- 这部分代码在 macOS 上只能编译（`dotnet build -r win-x64`）与通过 AOT / 裁剪分析器，**没有在真机上运行过**（与跳转列表相同）。

### 5.3 Linux（`LinuxNotifier`）

- 通过 `gdbus call --session --dest org.freedesktop.Notifications …`（glib 自带，GNOME / KDE 桌面基本都有）调用 `Notify`，不写 D-Bus 客户端；没有 `gdbus` 时用 `notify-send`（不能点击、不能替换）；两者都没有就静默跳过。
    - 第一次使用时 `GetCapabilities`：有 `actions` 才带 `default` 动作；有 `body-markup` 才转义 `& < >`；有 `sound` 时声音交给服务器（`sound-name: message-new-instant`），否则自己用 `canberra-gtk-play`，或 `pw-play` / `paplay` 播放 freedesktop 声音主题里的文件，都没有就不响。声音关闭时传 `suppress-sound`。
    - 同一会话用上一条的 id 作为 `replaces_id`。
    - **点击**：常驻一个 `gdbus monitor --session --dest org.freedesktop.Notifications`，解析 `ActionInvoked (uint32 id, 'default')`。GNOME Shell、KDE Plasma、mako 广播这个信号，可以点击；dunst 只把信号发给发出通知的那条连接（`gdbus call` 已退出），点击无效。Wayland 下窗口能否被置前取决于合成器的焦点策略。
    - 撤回：`CloseNotification`。
- **角标**：只有托盘（提示文字 + 图标红点）。

## 6. 测试

- `NotificationTests`（假 notifier / presence、`FakeTimeProvider`）：
    - 档位 × 前后台：`never` / `background` / `always`，失焦、隐藏、看着别的会话、设置页打开。
    - 回合完成的 30 秒阈值、出错总是通知、用户停止不通知也不算未读、`minTurnSeconds` 生效。
    - 合并：同一会话 2 秒内只投递首条 + 最新一条（补发的不出声），打开会话后待发的丢弃；跨会话提示音节流。
    - 未读：计数、打开清除并撤回通知、删除会话清除、与档位无关。
    - `SessionViewModel.TurnEnded`：停止原因、错误、最后一段正文，队列会自动续发时标记 `ContinuesWithQueue`。
    - 文案：最后一句的提取（跳过代码块、去掉 Markdown、中英文句号、截断不拆代理对）。
    - 点击回调转成 `OpenRequested`；测试通知不看档位。
    - 设置：`UserSettingsService` 写回 `notifications.*`（默认值删除字段与空节）、`SettingsFile` 对非法取值的警告与类型错误。
    - 角标图片（`notification-badge-*.png`、`notification-tray-dot.png`）与托盘提示文字。
- `NotificationUiTests`（真实 Host + Fake 模型）：
    - 端到端：会话事件经 `DesktopNotifications.Attach` 进入通知中心。后台时一轮结束收到回复最后一句的通知、记为未读；`mcp:ask_user` 收到「有问题需要你回答」；回到前台并显示该会话后未读清零、通知撤回；`ShowSessionAsync` 打开会话。
    - 无头快照：设置 › 通用的「通知」卡片（`settings-notifications`、`settings-notifications-changed`），切换档位、关提示音写回 settings.json。
- 冒烟：`--smoke` 在发布的 AOT 二进制里跑一遍真实会话 → 通知决策 → 角标渲染（记录用的通知器，不弹真实通知）；macOS 上注册委托类、创建 block，在 `.app` 里还取得 `currentNotificationCenter`（不请求授权）。
- 平台代码：Windows / Linux 只能在 macOS 上编译（`-r win-x64` / `-r linux-x64`）并保持 AOT 分析器零警告。

## 7. 风险与不做的事

- **未在真机验证**：Windows Toast 与任务栏覆盖图标、Linux 点击都没有在对应系统上跑过；macOS 的 UNUserNotificationCenter 只有打包后的 `.app` 才能走到。冒烟与单测覆盖不到系统权限弹窗。
- 不做：通知里直接批准 / 拒绝（Cursor）、自定义提示音、应用内铃铛 / 活动面板（Codex）、侧栏未读样式与"标记为未读"（R13 单独做）、Linux 启动器计数、退出后点通知重新启动 TKCode（需要 COM 激活器 / D-Bus 激活）。
