# 自动更新设计（GitHub Releases）

状态：已实现（2026-10-17）；macOS 的签名、公证与发布等 Apple 证书就绪后再开启（§11）
关联文档：[architecture.md](../architecture.md)、[settings.md](../settings.md)、[terminal/04-pty-host.md](../terminal/04-pty-host.md)（新旧 host 并存）

## 1. 目标

- 用 GitHub Releases（`AtomUICommunity/TKCode`）发布 TKCode。
- 程序启动约 60 秒后在后台检查一次更新，之后每 6 小时检查一次。发现新版本时以非模态方式提醒用户，不打断正在进行的工作。
- 「设置 → 关于」页提供手动「检查更新」，并可切换更新通道、开关自动检查。
- 用户点击更新后在后台下载**安装程序**。下载并校验完成后提示「安装并重启」，点击后 TKCode 启动安装程序，然后自己退出，由安装程序完成替换并重新启动 TKCode。
- **不新增更新器进程或项目**：Windows 的文件替换交给安装程序；macOS 和 Linux 由 TKCode 在退出前自己替换（§7.2、§7.3）。
- 整个链路保持 NativeAOT：不引入运行时代码生成或反射序列化。

## 2. 已确认的决策

| 维度 | 决策 | 说明 |
|---|---|---|
| 更新机制 | **自研：检查 + 下载安装程序 + 启动安装程序后退出** | 不引入 Velopack 或 NetSparkle，也不新增更新器控制台程序。流程为：查询 GitHub、验证签名清单、下载并校验 SHA-256，然后启动安装程序 |
| 交互 | **提醒 → 后台下载 → 安装并重启** | 提醒卡片提供「更新 / 稍后 / 跳过此版本」三个按钮；下载完成后显示「安装并重启」；有会话正在运行时先确认 |
| 平台 | **三个平台 + macOS 签名与公证** | osx-arm64、osx-x64、win-x64、linux-x64；macOS 的 .app 和 dmg 都要签名并公证；Windows 签名留作后续开关 |
| 预发布版本与频率 | **默认接收预发布版本；启动 60 秒后检查，之后每 6 小时** | 预发布版本即 GitHub 的 Pre-release（0.1.0 本身就是预发布版本）；设置里可以关闭「接收预发布版本更新」与自动检查；手动检查不受开关影响 |
| 托盘 | **默认关闭窗口不退出** | 点窗口关闭按钮只隐藏到系统托盘，会话继续运行；从托盘菜单、macOS ⌘Q 或安装更新时才退出；`closeToTray: false` 恢复为关闭即退出（§9.1） |

## 3. 版本号

现状：`Directory.Build.props` 写死 `<Version>0.1.0</Version>`，`tools/package_macos_app.sh` 的 Info.plist 里又写死了一份。

- **唯一来源是 git tag**：`v1.2.0` 是稳定版，`v1.2.0-beta.1` / `-rc.1` 是预览版。发布 CI 用 `-p:Version=<tag 去掉 v>` 构建，Info.plist 和 Inno Setup 脚本的版本都从同一个变量生成。
- `Directory.Build.props` 的默认值改为 `0.0.0-dev`。本地开发构建**不做自动检查**，手动检查时显示“开发版本不支持自动更新”。如需测试，可通过 `TKCODE_UPDATE_FEED` 指定本地源（见 §10）。
- 运行时从 `AssemblyInformationalVersionAttribute` 读取版本（去掉 `+commit` 后缀）。关于页显示 `1.2.0 (abc1234)`。
- 版本比较遵循 SemVer 2.0 优先级规则，自己实现一个约 80 行的 `SemVersion`，不引入依赖。

## 4. Release 产物与清单

| 资产 | 首次安装 | 应用内更新 |
|---|---|---|
| `TKCode-<ver>-osx-arm64.dmg` / `-osx-x64.dmg` | ✓（拖到「应用程序」） | |
| `TKCode-<ver>-osx-arm64.zip` / `-osx-x64.zip` | | ✓ |
| `TKCode-<ver>-win-x64-setup.exe` | ✓ | ✓ |
| `TKCode-<ver>-linux-x64.tar.gz` | ✓ | ✓ |
| `manifest.json` + `manifest.json.sig` | | 更新清单及其签名 |

各平台的安装程序：

- **macOS**：
  - 签名后的 TKCode.app 先公证并 staple，再用 `ditto -c -k --keepParent` 打成 zip（顶层是 `TKCode.app`，用于应用内更新），并用 hdiutil 打成 dmg（签名、公证、staple，用于首次安装）。
  - 不打 pkg：pkg 需要额外的 Developer ID Installer 证书，更新时还要求管理员密码；与 electron-builder / Squirrel.Mac 一样，由应用自己替换 .app（§7.2）。
- **Windows**：
  - 用 Inno Setup 生成，按用户安装到 `%LocalAppData%\Programs\TKCode`，`PrivilegesRequired=lowest`，**不需要 UAC**。
  - 首次安装和更新使用同一个 setup.exe：首次安装走完整向导，更新时用静默参数启动（§7.1）。
- **Linux**：只提供 tar.gz（解压即用的目录），保留可执行权限位。

`manifest.json`：

```jsonc
{
  "schema": 1,
  "version": "1.2.0",
  "channel": "stable",                    // stable | preview，与 prerelease 标志一致
  "publishedAt": "2026-11-01T08:00:00Z",
  "notesUrl": "https://github.com/AtomUICommunity/TKCode/releases/tag/v1.2.0",
  "assets": {
    "osx-arm64": { "name": "TKCode-1.2.0-osx-arm64.zip", "size": 48213456, "sha256": "…", "minOs": "11.0" },
    "osx-x64":   { "name": "TKCode-1.2.0-osx-x64.zip", "size": 0, "sha256": "…", "minOs": "11.0" },
    "win-x64":   { "name": "TKCode-1.2.0-win-x64-setup.exe", "size": 0, "sha256": "…" },
    "linux-x64": { "name": "TKCode-1.2.0-linux-x64.tar.gz", "size": 0, "sha256": "…" }
  }
}
```

- 更新说明不写进清单，直接使用 Release 的 body（Markdown），在 UI 里用 StreamMark 渲染。
- **清单签名**：使用 ECDSA P-256 + SHA-256（.NET 内置，AOT 安全，三个平台都可用）。私钥只存在 CI secret `UPDATE_SIGNING_KEY` 中，公钥编译进客户端。验签失败时整个更新视为不可用，并记录日志。
  - 为什么在 HTTPS 之外还要签名：GitHub token 泄露或者 Release 被篡改时，单靠 HTTPS 和 SHA-256 拦不住，因为攻击者可以连同清单一起改；签名私钥不在 GitHub 上，可以拦住这种情况。
  - 公钥支持数组，方便将来轮换密钥：新版本同时带上新旧两个公钥，过渡一个版本后再停用旧钥。
- **Windows 安装程序暂不签名**：启动前只依靠清单签名和 SHA-256 保证完整性。TKCode 自己下载的文件不会被打上 MOTW 标记，因此静默运行时不会触发 SmartScreen。

## 5. 检查流程

```
UpdateService（Desktop，单例）
  ├─ UpdateScheduler    启动后 60 s 检查一次，之后用 PeriodicTimer 每 6 h 检查；时间来源是 TimeProvider，方便测试
  ├─ GitHubReleaseFeed  查询 Release 列表，选出候选版本，下载清单并验签
  ├─ UpdateDownloader   下载到 staging，校验大小和 SHA-256，支持断点续传
  ├─ UpdateInstaller    按平台启动安装程序（或在 macOS / Linux 上替换安装位置）后让 App 退出
  └─ UpdateState        状态机 → 绑定到 UI
```

**查询方式**

- 调用 `GET https://api.github.com/repos/AtomUICommunity/TKCode/releases?per_page=20`，请求带 `If-None-Match`（ETag 存在 state.json 中）。返回 304 时不计入未认证请求的配额（每 IP 每小时 60 次）。
- 过滤规则：
  - 跳过 draft；
  - stable 通道只看 `prerelease == false` 的版本；preview 通道两者都看，取最高版本；
  - 必须带有 `manifest.json` 资产；
  - 版本号必须大于当前版本。
- API 被限流（403/429）时回退到 `https://github.com/AtomUICommunity/TKCode/releases/latest/download/manifest.json`。这个地址不走 API 配额，但只能获取稳定版。
- 选出候选版本后，下载 `manifest.json` 和 `.sig` 并验签，再检查清单中的 `version` 与 tag 一致、本平台 RID 存在、`minOs` 满足。
- HttpClient 使用 `SocketsHttpHandler`，遵循系统代理，单次请求超时 30 秒，User-Agent 为 `TKCode/<ver> (<rid>)`。

**状态机**（`UpdateState`）

```
Idle ─check─▶ Checking ─┬─▶ UpToDate
                        ├─▶ Available(ver, notes) ─download─▶ Downloading(%) ─▶ Ready(ver) ─install─▶ Installing → App 退出
                        ├─▶ ManualOnly(ver)   // 不支持应用内安装（§7.4）：只提醒，按钮为「打开下载页面」
                        └─▶ Failed(error)     // 自动检查失败只写日志；手动检查失败才显示
```

- 同一时刻只运行一个检查任务。手动检查会取消正在等待的自动检查，并重置 6 小时计时。
- 已经是 `Ready` 状态时不再检查。如果检查到更新的版本，作废已下载的包，重新从 `Available` 开始。
- 「跳过此版本」把 `skippedVersion` 写入 state.json，自动检查不再提醒这个版本，手动检查仍然显示。「稍后」只关闭本次提醒，6 小时后再次检查到时会再提醒。
- `Ready` 状态下用户一直没点安装时，安装包保留在暂存目录，下次启动直接进入 `Ready`，不重新下载（启动时重新校验 SHA-256）。

## 6. 下载与校验

- 暂存目录为 `~/.tkcode/updates/<ver>/`（遵循 `TKCODE_HOME`）。先下载到 `<asset>.partial`，下载过程中计算 SHA-256；完成后校验大小和哈希，再改名为正式文件名。
- 断点续传：`.partial` 已存在时发送 `Range` 请求；服务器返回 200 而不是 206 时从头下载。GitHub 资产会 302 跳转到 objects.githubusercontent.com，这个跳转是支持 Range 的。
- 平台附加校验（通过后才进入 `Ready`）：
  - **macOS**：用 `ditto -x -k` 解压到 .app **旁边**的 `TKCode.app.new/`（顶层必须是一个 `.app`）。当前应用已签名时：`codesign --verify --strict --deep` 必须通过，签名者的 Team ID 必须与当前运行的应用相同，`spctl -a -t exec` 必须认可为 `Notarized Developer ID`（用户关闭了 Gatekeeper 时接受 `security disabled`）。然后与 Linux 一样检查三个可执行文件（在 `Contents/MacOS/` 下）并运行 pty host 自检。
  - **Windows**：只依靠清单签名和 SHA-256（安装程序暂未签名）。
  - **Linux**：解压到安装目录**旁边**的 `<安装目录>.new/`（保证在同一个文件系统上，§7.3 才能用 rename 替换）。解压后检查 `TKCode`、`agenthost/TKCode.AgentHost`、`ptyhost/tkcode-ptyhost` 三个可执行文件都在，并运行 `tkcode-ptyhost --self-test`（返回 0 才算通过）。
- 每次启动时清理除当前候选版本以外的旧暂存目录，以及残留的 `.old`、`.new` 目录。

## 7. 安装：启动安装程序后退出

通用前置步骤（所有平台）：

1. 用户点击「安装并重启」。如果 AgentHost 报告有正在运行的轮次或待审批请求，先弹出确认框，列出这些会话，提供「等待完成后安装 / 立即安装 / 取消」三个选项。「等待完成后安装」会在 AgentHost 空闲时自动继续。
2. Desktop 正常保存状态，关闭 AgentHost。终端会话按 `revive` 方式退出：pty host 保存快照后退出，新版本启动后再从快照恢复。Windows 必须这样做，否则正在运行的 ptyhost 会占住文件；macOS 和 Linux 也统一这样处理，行为一致。
3. 按平台启动安装程序或执行替换（下面三节），然后 Desktop 立即退出。

### 7.1 Windows（Inno Setup）

- 启动命令：`TKCode-<ver>-win-x64-setup.exe /SILENT /SUPPRESSMSGBOXES /NORESTART /UPDATE=1`。
  - `/SILENT` 不显示向导，只显示安装进度窗口，让用户知道正在更新。
- 安装脚本：
  - `[Code] InitializeSetup` 中检测 TKCode 的 mutex（`AppMutex` 同名），最多等 30 秒，等 Desktop 和 AgentHost 完全退出；超时则提示失败并退出。安装程序没有修改任何文件，所以旧版本完好。
  - `CloseApplications=yes` 作为兜底，在仍有进程占用文件时让 Restart Manager 关闭它们。
  - `[Run]` 中启动 TKCode 的条目带 `Check: IsUpdate`，即带 `/UPDATE=1` 参数时安装完成后自动启动 TKCode，并附带 `--updated-from <oldver>`。
- 失败处理：Inno Setup 自带回滚，复制失败时会恢复旧文件。

### 7.2 macOS（zip，由 TKCode 自己替换 .app）

做法与 Linux（§7.3）相同，不需要管理员密码，也没有安装界面：

1. 将 `TKCode.app` 改名为 `TKCode.app.old`，再将 `TKCode.app.new` 改名为 `TKCode.app`。
2. 用 `open -n TKCode.app` 启动新版本（`-n`：旧进程还没退出，不加只会激活旧进程）。失败时反向改名恢复。
3. Desktop 退出。新版本启动后删除 `.old`。

TKCode 自己下载的 zip 不带 quarantine 属性，新的 .app 首次启动不会弹 Gatekeeper 确认；签名与公证已在 §6 校验。

### 7.3 Linux（tar.gz，由 TKCode 自己替换）

Linux 没有安装程序。在 Unix 上，运行中的文件可以被改名（旧的 inode 仍然有效），所以 Desktop 可以在退出前自己完成替换，不需要额外进程：

1. 将 `<安装目录>` 改名为 `<安装目录>.old`，再将 `<安装目录>.new` 改名为 `<安装目录>`。两次都是同一文件系统上的 rename。
2. 用 `Process.Start` 启动新版本的 `TKCode --updated-from <oldver>`。启动失败时把两次改名反向执行，恢复旧目录，并显示错误。
3. Desktop 退出。新版本启动后删除 `.old` 目录。

### 7.4 安装方式检测（是否支持应用内安装）

| 情况 | 行为 |
|---|---|
| macOS：从 `*.app` 包内运行，且 .app 所在目录可写 | 下载 zip，自己替换 .app |
| macOS：.app 所在目录不可写（例如标准用户的 `/Applications`） | `ManualOnly` |
| macOS：从 DMG 挂载卷或 App Translocation 路径（`/private/var/folders/.../AppTranslocation/`）运行 | `ManualOnly`，提示用户先把应用拖到「应用程序」文件夹 |
| Windows：由 Inno Setup 安装（注册表中存在卸载项，且安装目录与当前运行目录一致） | 下载 setup.exe 并静默安装 |
| Windows：便携方式运行（解压 zip 后直接运行） | `ManualOnly` |
| Linux：安装目录及其上级目录都可写（tar.gz 解压到 home 下） | 自己替换目录 |
| Linux：目录不可写（例如 `/opt`、`/usr`，或者由系统包管理器安装） | `ManualOnly` |
| 开发构建（`0.0.0-dev`）或者设置了 `TKCODE_NO_UPDATE=1` | 不做自动检查 |

### 7.5 安装后

- 新版本带 `--updated-from <oldver>` 启动时：
  - 提醒卡片显示「已更新到 1.2.0」，附「查看更新说明」链接；
  - 清理 `~/.tkcode/updates/` 和残留的 `.old` 目录；
  - 清空 state.json 中的 `pendingVersion`。
- 如果用户在安装程序中取消或安装失败，TKCode 不会被重新启动。用户手动打开 TKCode 时，发现 `pendingVersion` 仍然存在，而当前版本仍是旧版本，就提示「上次更新未完成」，并提供「重试」按钮。

## 8. 设置与状态

`settings.json`（用户可编辑，同步到 schema 和设置界面）：

```jsonc
"update": {
  "prerelease": true,       // 默认 true：也接收 GitHub 上标记为 Pre-release 的版本；false：只接收正式版
  "autoCheck": true         // false：只能在关于页手动检查
},
"closeToTray": true         // 默认 true：关闭窗口隐藏到托盘；false：关闭窗口即退出
```

`state.json`（程序自动写入）：`update.lastCheckAt`、`update.etag`、`update.skippedVersion`、`update.pendingVersion`、`update.installingVersion`。Release 列表的响应体缓存在 `updates/releases-cache.json`，更新说明随安装程序暂存为 `updates/<版本>/notes.md`（「已就绪」恢复后与「已更新到 …」时显示）。

## 9. UI

- **提醒卡片**（`Views/UpdateCardView.axaml`，自绘，便于放按钮与进度条）：位于窗口右下角，不自动消失。卡片内容：
  - 标题：「TKCode 1.2.0 可用」（预览版加上「预览」标签）；
  - 正文：更新说明前几行，以及「查看完整说明」链接，点击后打开关于页的说明面板；
  - 按钮：**更新** / 稍后 / 跳过此版本；
  - 下载过程中卡片显示进度条；下载完成后变为 **安装并重启** / 稍后。

  点「更新说明」打开关于页并收起卡片（关于页有相同的操作按钮）。
- **常驻入口**：侧边栏底部的设置按钮旁加一个小圆点，`Available` 和 `Ready` 状态下显示，点击打开关于页。用户关闭卡片后仍然能找到入口。
- **关于页**新增「更新」一节：
  - 当前版本与 commit；
  - 「接收预发布版本更新」开关（默认开启）和「自动检查更新」开关；
  - 「检查更新」按钮，以及状态行（正在检查… / 已是最新版本，上次检查于 HH:mm / 发现 1.2.0 / 下载中 43% / 已就绪 / 失败原因 + 重试）；
  - 更新说明（Release 正文）用 StreamMark 的 `MarkdownView` 渲染，附「在 GitHub 上查看」。
  - 手动检查失败但已有发现 / 已下载的版本时，保留原状态（仍可安装），状态行附上失败原因。
- 所有文案都加中英文两套，写进 `Strings`。
- 关闭「接收预发布版本更新」时**不降级**：只提醒比当前版本更高的正式版。

### 9.1 系统托盘

- `Services/AppTray.cs`：Avalonia `TrayIcon`（Windows 通知区域、macOS 菜单栏、Linux StatusNotifier）。
- 菜单：显示 TKCode / 新会话 / 项目 ▸（最近项目）/ 更新项 / 打开设置 / 退出 TKCode。Dock 右键菜单与 Windows 任务栏跳转列表见 [system-menus.md](system-menus.md)。更新项随状态变化：「检查更新」→「下载 TKCode x」→「正在下载 …」（禁用）→「安装 TKCode x 并重启」；不支持应用内安装时为「TKCode x 可用」（打开窗口）。
- 关闭窗口：只拦截 `WindowCloseReason.WindowClosing`（关闭按钮、Alt+F4、⌘W），隐藏窗口；退出程序（`Shutdown`）、系统关机照常关闭。macOS 点 Dock 图标（`ActivationKind.Reopen`）重新显示窗口。
- 设置 › 通用：「关闭窗口时保留在托盘」（默认开启）。
- 单实例（`Services/SingleInstance.cs`）：隐藏在托盘时再启动 TKCode 只会显示已有窗口。应用内更新在 macOS / Linux 上启动新版本之前调用 `SingleInstance.Release()`，否则新进程会把命令转发给正在退出的旧进程。

## 10. 测试与验证

- **单元测试**：
  - SemVer 的比较与解析（包括预发布段）；
  - GitHub 返回结果的过滤（draft、prerelease、缺少清单）；
  - 清单验签，包括篡改和换钥两种情况；
  - RID 和 minOs 的选择；
  - 调度器（用假的 `TimeProvider` 验证 60 秒和 6 小时的节奏，以及手动检查后计时重置）；
  - 状态机的各种转换（跳过此版本、更新的版本作废已下载的包、ManualOnly 判定、上次更新未完成）。
- **下载测试**：用本地 HTTP 服务模拟 302、Range/206、200 回退、哈希不符和中途断线。
- **安装测试**：
  - 抽出一个启动安装程序的接口，单元测试只断言生成的命令行和参数，不真正启动安装程序；
  - Linux 的目录替换和回滚在临时目录中做集成测试；
  - Inno Setup 静默安装在 CI 的 Windows runner 上实跑：先装 0.9.0，再用 `/SILENT /UPDATE=1` 装 0.9.1，检查文件版本和自动启动；
  - macOS 的 zip 解压与 .app 替换在临时目录中做集成测试（`ditto` 打包，与发布脚本相同）。
- **端到端测试**：设置 `TKCODE_UPDATE_FEED=file:///…` 或 `http://localhost:…`，指向本地生成的清单和测试密钥签名（测试构建才接受测试公钥），在本机从 0.9.0 真实更新到 0.9.1，检查重启后的版本号。
- **UI**：用 headless 快照测试覆盖提醒卡片的各种状态和关于页「更新」一节的每个状态，不使用模拟输入。
- **AOT**：`TKCode.Desktop` 的 AOT 发布要 0 个 IL 警告，并且实际运行发布出来的二进制：`TKCode --smoke` 在进程内启动一个假的 GitHub（临时密钥签名），检查 `update.check`（列表、清单验签）、`update.notes`（StreamMark 渲染）、`update.download`（下载与 SHA-256）。`TKCode --version` 输出版本与 commit，发布 CI 用它做冒烟检查。

## 11. 发布 CI（`.github/workflows/release.yml`）

两种触发方式：推送 `v*` tag（创建 draft，人工检查后发布；tag 带 `-` 的是预发布版本），或手动运行（`workflow_dispatch`，输入版本号与是否预发布，直接在当前提交上创建 tag 并发布，首个版本 0.1.0 即这样发布为预发布版本）。

**StreamMark**（`AtomUICommunity/StreamMark`，子模块 `external/StreamMark`）以本地 NuGet 包引用：`streammark-native` 在各 RID 的原生 runner 上构建 RaTeX 动态库，`streammark-pack` 汇总后打包，各平台构建任务下载这些包。

**没有配置 macOS 证书与公证凭据时跳过 macOS**（StreamMark 与 TKCode 的 macOS 任务都不运行），只发布 Windows 与 Linux；凭据就绪后重新发布即可带上 macOS。

步骤：

1. **build（矩阵）**：macos-14 (osx-arm64)、macos-13 (osx-x64)、windows-latest (win-x64)、ubuntu-22.04 (linux-x64)。
   - 复用 terminal.yml 中构建 libghostty-vt 和 libtkcodepty 的步骤；
   - 执行 `dotnet publish -p:Version=…`，发布到 publish 目录（包含 agenthost/、ptyhost/）；
   - 运行冒烟测试：`--version` 和 `tkcode-ptyhost --self-test`。
2. **macOS**：
   - 从 secrets 导入 Developer ID Application 证书到临时 keychain；
   - **由内向外**逐个签名所有 Mach-O 文件（dylib、tkcode-ptyhost、tkcode-pty-spawn、TKCode.AgentHost），最后签整个 `.app`；全部加上 `--options runtime --timestamp`，**不使用 `--deep`**；
   - **内容相同的文件只签一份，其余复制已签名的字节**（`Contents/MacOS/` 与 `ptyhost/` 各有一份 `libghostty-vt.dylib`）：构建 ID 取该库文件的 SHA-256（04 §2.4），`--timestamp` 每次嵌入不同的时间戳令牌，分别签名会让两份字节不同，pty host 以 build mismatch 退出，终端打不开。`tools/package_macos_app.sh` 按签名前的 SHA-256 分组；
   - `tools/release/package-macos.sh`：.app 用 `xcrun notarytool submit --wait`（Apple ID + App 专用密码）公证并 staple（staple 要从 CloudKit 取票据，偶尔断网，脚本里重试 5 次），再生成 zip 和 dmg；dmg 签名后同样公证并 staple；
   - 需要先验证：AOT 程序在 hardened runtime 下是否需要额外的 entitlements（预期不需要 JIT 相关权限）。
3. **Windows**：用 `iscc` 编译 `packaging/windows/TKCode.iss`，生成 setup.exe。签名步骤预留开关，有 secret 时才执行。
4. **Linux**：打包 tar.gz，保留可执行权限位。
5. **release**：汇总所有产物，生成 `manifest.json`（大小、SHA-256），用 `UPDATE_SIGNING_KEY` 签名，用 `gh release create` 上传（预发布版本加 `--prerelease`；tag 触发时加 `--draft`，由人工检查后再点 Publish，发布前客户端看不到 draft 版本）。Release 正文取自 `CHANGELOG.md` 里该版本的段落（`tools/release/changelog-notes.py`，也就是客户端更新说明显示的内容）；没有对应段落时退回 `--generate-notes`。

需要的 secrets：`MACOS_APP_CERT_P12`（Developer ID Application，base64）、`MACOS_CERT_PASSWORD`、`APPLE_ID`、`APPLE_APP_SPECIFIC_PASSWORD`、`APPLE_TEAM_ID`、`UPDATE_SIGNING_KEY`（以及将来的 Windows 证书）。

`tools/package_macos_app.sh` 同步改为从参数读取版本，并在本地复用和 CI 相同的打包逻辑（签名步骤可选）。

## 12. 里程碑

| 阶段 | 内容 | 验收 |
|---|---|---|
| U0 | 版本号统一由 tag 决定；release.yml 在不签名的情况下产出 dmg、zip、setup.exe、tar.gz 和签名清单（draft） | 推送测试 tag 后得到完整的 draft Release |
| U1 | `UpdateService` 检查功能、设置与状态字段、提醒卡片、关于页「更新」一节 | 单元测试和快照测试通过；对着真实的 draft Release，能用 feed 覆盖检查到新版本 |
| U2 | 下载、断点续传、校验（含 macOS 的签名 / 公证检查和解压预检） | 下载测试全部通过 |
| U3 | 启动安装程序后退出：Inno Setup 脚本（等待 mutex、`/UPDATE` 自动启动）、macOS / Linux 的安装位置替换，以及会话运行中的确认流程和「上次更新未完成」 | 三个平台的端到端 0.9.0 → 0.9.1 测试都通过 |
| U4 | macOS 签名与公证（.app、dmg） | 公证后的 dmg 首次安装没有 Gatekeeper 拦截，应用内通过 zip 更新到下一版成功 |
| U5 | 打磨：失败提示、日志、预览版与稳定版切换、清理旧文件 | 走查 |

## 13. 待确认 / 风险

1. **macOS 标准用户**：`/Applications` 对非管理员不可写，此时只提醒、打开下载页面（§7.4）。2026-10-17 由 pkg 改为自己替换 .app：少一张 Installer 证书，管理员用户更新也不再要输密码。
2. **Windows 未签名**：首次安装会被 SmartScreen 拦截（需要点「仍要运行」）。应用内更新由 TKCode 自己下载，不会带上 MOTW 标记，因此不受影响。
3. **Linux 只提供 tar.gz**：不做 AppImage、deb、rpm，被包管理器安装的情况只提醒用户手动更新。
4. **签名密钥保管**：`UPDATE_SIGNING_KEY` 一旦丢失，旧客户端就无法验证新版本，只能让用户手动重装。私钥需要离线备份。
5. **更新前关闭终端**：更新前终端统一按 `revive` 方式退出，保持运行（keepAlive）的终端会被关闭。新版本启动后会从快照恢复界面，但正在运行的前台进程会被终止。确认框里要明确提示这一点。
