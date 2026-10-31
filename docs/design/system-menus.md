# 系统菜单：托盘、Dock、任务栏跳转列表

`closeToTray` 默认开启（[auto-update.md §9.1](auto-update.md)）：关闭窗口只是隐藏到托盘。Windows 任务栏按钮的系统右键菜单只有「固定到任务栏 / 关闭窗口」，
想真正退出只剩托盘图标（常被收进 `^` 折叠区）或任务管理器。所以三个入口都提供同一组基础操作和最近项目。

## 入口与内容

| 入口 | 内容 | 实现 |
|---|---|---|
| 托盘图标右键（Windows 通知区域 / macOS 菜单栏 / Linux StatusNotifier） | 显示 TKCode · 新会话 · 项目 ▸ · —— · 更新项 · 打开设置 · —— · 退出 TKCode | `AppTray.BuildItems` → Avalonia `NativeMenu` |
| macOS Dock 图标右键 | 新会话 · —— · 项目（标题）· 平铺的最近项目；「退出」「隐藏」由系统自带 | `AppTray.BuildDockItems` → `NativeDock.SetMenu` |
| Windows 任务栏按钮右键（跳转列表） | 任务：新会话、退出 TKCode；类别「项目」：最近项目 | `AppTray.BuildJumpList` → `WindowsJumpList` |
| Linux 启动器 | 无（`.desktop` 的动作是静态的，不放项目） | — |

有会话在后台结束或等待处理、还没打开过时，托盘提示文字附上「待查看：N」、图标右上角加红点；macOS Dock 显示角标，Windows 任务栏按钮显示覆盖图标（`AppTray.SetUnread`，见 [notifications.md](notifications.md) §4.5）。

## 项目列表

- 最近使用的在前，最多 8 个（`AppTray.ProjectLimit`）；`state.json` 的 `recentProjects`（项目 id）记录使用顺序，`ConversationViewModel.SetWorkspace` 每次切换项目时更新。
  不足 8 个时按侧栏顺序补齐，从没切换过项目时菜单也不是空的；主文件夹已不存在的项目不显示。不改动侧栏项目列表自己的顺序。
- 点击项目 = 在侧栏的项目列表里选中它（`MainWindowViewModel.OpenProject` → `SidebarViewModel.OpenProject`）：显示并激活窗口、离开设置页、筛选会话、新会话默认在该项目中。
  菜单条目与 `--open-project` 的参数都是项目的主文件夹路径：按「主文件夹等于它 → 某个文件夹等于它」查找项目，都找不到时新建一个单文件夹项目；目录不存在则忽略（docs/design/projects.md §8.4）。
- 同名项目（不同目录下的同名文件夹）显示为「名称 (上级目录名)」。
- 菜单在切换项目、增删项目、切换语言、更新状态变化时重建。`NativeMenu` 实例只创建一次，原地更新（Avalonia.Native 不接受换实例）。

## 命令行指令与单实例

跳转列表的条目是「TKCode.exe + 参数」，所以需要把命令交给已运行的实例：

- `AppCommand`：`--new-session`、`--open-project <路径>`、`--quit`；没有可识别的参数 = 显示窗口。
- `SingleInstance`：数据目录下的 `instance.lock`（独占打开，进程退出或崩溃时由系统释放）决定主实例；主实例在命名管道上收命令
  （`tkcode-<数据目录哈希>`，`CurrentUserOnly`）。后启动的进程拿不到锁就转发命令后退出，转发失败（主实例卡住，超时 5 秒）则照常启动。
  Windows 上转发前调用 `AllowSetForegroundWindow(ASFW_ANY)`，主实例才能把窗口提到前台。
- 没有运行中的实例时：`--quit` 直接退出（不启动界面），其余指令在窗口创建后执行。
- `--new-instance` 和 `--startup-bench` 跳过单实例；`TKCODE_HOME` 不同的数据目录互不干扰。开发时同时运行本地构建用 `--new-instance`。
- Windows 安装程序仍通过 `TKCodeDesktop` 互斥量等 TKCode 退出，与锁文件无关。

## Windows 跳转列表

- `Services/WindowsJumpList.cs` 用 .NET 源生成 COM（`GeneratedComInterface`）调用 `ICustomDestinationList`，没有运行时反射，NativeAOT 安全（项目需要 `AllowUnsafeBlocks`）。
- 未设置显式 AppUserModelID，使用系统按 exe 路径推导的默认值，这样已有的固定快捷方式和运行中的按钮仍然合并为同一个。
- 只有进程名为 `TKCode` 时才更新（`dotnet run` 的宿主不是）；任何失败只写一条 `Trace` 警告，不影响启动。
- 用户在跳转列表里移除过某个项目后，再追加该类别会被系统拒绝：这时只丢掉「项目」类别，任务照常提交。
- 条目数受系统「跳转列表显示项数」设置限制（默认 10，`BeginList` 返回的最小槽位数），超出的项目不写入。

## 测试

- `SystemMenuTests`：命令行解析与引号、单实例转发与交接、最近项目排序、菜单内容、从菜单进入项目。
- 原生菜单与跳转列表无法在无头平台渲染；COM 调用只能在 Windows 上验证（构建时源生成器已校验签名，虚函数表槽位与 Windows SDK 头文件一致）。
