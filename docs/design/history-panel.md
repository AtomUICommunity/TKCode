# 历史面板：当前分支的 Git 提交记录

状态：开发中（2026-11-11），待验证；集成验证完成后在 §13 补充与设计的偏差
关联文档：[architecture.md](../architecture.md) §7.0（布局）、[projects.md](projects.md) §10.3（分支选择器按文件夹切换）。[message-features-report.md](message-features-report.md) 中的「历史」指会话的消息历史（编辑重发、分叉、⌘F、导出等），与本文的 Git 提交历史不是同一个功能，两者互不依赖。

## 1. 目标

1. 工作台的「历史」标签列出**当前分支**的提交，按时间从新到旧显示标题、短哈希、作者与时间。
2. 点击一条提交，在原位展开它的完整正文、改动文件列表与逐文件 diff；同一时间只展开一条。
3. 可以复制一条提交的完整哈希。
4. 多文件夹项目里有多个 Git 仓库时，在面板顶部切换仓库；默认主文件夹的仓库。
5. 面板严格只读：不执行任何会写仓库或访问网络的 git 命令。

## 2. 非目标

- 不做 revert、checkout、cherry-pick、reset、stage / unstage，也不从历史里「打开这个版本的文件」。
- 不做 fetch / pull / push，不打开远程仓库、PR 或提交网页。
- 不列出所有分支，不做提交图（`--all` / `--graph`）、分支筛选或提交搜索。
- 不做跨会话、跨项目的搜索；⌘F 与消息历史属于 [message-features-report.md](message-features-report.md) 那条线。
- 不解析 `.git/objects`，不自己计算 diff；一切以本机 `git` 的输出为准。
- 不做提交签名验证展示、评论、逐块暂存或并排（side-by-side）diff。

## 3. 现状

- 工作台的「+」菜单与空状态磁贴里已经有「历史」（`WorkbenchTabKind.History`），打开后显示的却是 `PlaceholderTabViewModel`，末尾是一句「即将推出」。`strings.tsv` 里的 `HistoryDescription` 已经承诺了「展示项目的提交记录，并可查看每次改动」。
- 「变更」面板（`ChangesTabViewModel` / `ChangesTabView`）已经实现「单栏列表 + 点击一行原位展开 diff」的交互，历史面板沿用同一套语言，`DiffView` 直接复用。
- `GitRepository` 目前只做**不用进程**的读取：用 `GitLayout` 找到 `.git`（普通仓库、worktree、submodule、`--separate-git-dir`），读 `HEAD` 得到分支名，读 `refs/heads` / `packed-refs` 得到本地分支；不启动 git。
- `GitService.SwitchAsync` 是 Desktop 直接运行本机 git 的先例：`ProcessStartInfo("git")`、参数逐个传入、`GIT_TERMINAL_PROMPT=0`、超时。
- 没有任何读取提交的 RPC；Host 不参与本功能，也没有为它升协议版本。

## 4. 已确认的决策（2026-11-11）

| 维度 | 决策 |
|---|---|
| 数据来源 | Desktop 直接运行本机 `git` 进程，与 `GitService.SwitchAsync` 一致；不经过 Agent Host，不解析对象库 |
| 范围 | 当前分支（`HEAD`）可达的提交；合并提交相对**第一个父提交**显示改动 |
| 展示 | 单栏手风琴：同一时间只展开一条提交，文件在提交内部再展开 diff |
| 加载 | 两阶段：列表只取元数据；展开提交取正文 + `--numstat`；展开文件才取该文件的 patch |
| 刷新 | 标签激活时重新加载；`GitBranchWatcher` 监视选中仓库；顶部另有刷新按钮 |
| 分页 | 每次显示 50 条，向 git 请求 51 条，多出的一条只用来判断是否还有下一页 |
| 只读 | 不提供任何写操作，也不在仓库里留下锁或配置改动 |

## 5. 数据来源

### 5.1 为什么不经过 Host

- 提交历史只与本机工作区有关，与模型、会话无关。Desktop 自己就能读，没必要让 Host 起一个会话或新增 RPC（还要升协议版本、改四处代理与分发）。
- `GitService.SwitchAsync` 已经确立了「Desktop 直接调本机 git」的做法；历史面板复用同一套进程调用约定。
- **不解析 `.git/objects`**：packfile、delta、replace refs、alternates、grafts 都要自己处理，工作量大且容易与 git 的行为不一致。让 git 输出文本、我们只做解析，成本最低。
- `GitRepository` 的职责保持不变：只用文件读取做**仓库发现**与**分支名**；提交读取放进新的 `GitHistoryService`。仓库列表复用 `GitRepository` 新增的发现方法（§10）。

### 5.2 进程调用

- `ProcessStartInfo("git")`，`ArgumentList` 逐个传参（不拼命令行字符串，路径里的空格、引号、中文都不需要额外转义），`WorkingDirectory` 设为所选仓库的根目录。
- 重定向 stdout / stderr，`StandardOutputEncoding` 与标准错误都用 UTF-8；`CreateNoWindow = true`。
- 环境变量：
  - `GIT_TERMINAL_PROMPT=0`：缺凭据时直接失败，绝不弹凭据窗口；
  - `GIT_OPTIONAL_LOCKS=0`：只读命令不抢 index 锁，不干扰同时在跑的终端或 Agent。
- 每次调用一个 20 秒的超时（`CancellationTokenSource`，与调用方的取消令牌链接）。超时或取消时 `Kill(entireProcessTree: true)`，然后按「取消」或「超时」处理。历史读取是纯读，20 秒足够；`GitService.SwitchAsync` 的 30 秒是另一回事（切换要等待工作区写入）。
- **预期的失败不抛异常**：`Win32Exception`（git 未安装 / 不在 PATH）、非零退出、超时都收敛为 `GitHistoryResult<T>` 的 `Error`（值可为空），由面板显示；UI 线程上不留未处理异常。

### 5.3 命令

三条命令都固定 `--no-pager`、`--no-color`，并用 `-c core.quotePath=false` 让中文路径直接以 UTF-8 输出，而不是 `\344\270\255` 这样的八进制转义。

```text
# 1) 提交列表（分页）
git -c core.quotePath=false --no-pager log --no-color --no-show-signature \
    --date=iso-strict --max-count=<count> --skip=<skip> \
    --format=%H%x1f%h%x1f%an%x1f%aI%x1f%P%x1f%s HEAD

# 2) 一条提交的正文与文件列表
git -c core.quotePath=false --no-pager show --no-color --no-ext-diff --first-parent \
    --format=%B%x00 --numstat -M -z <sha>

# 3) 单个文件的 patch（重命名时 oldPath 与 path 两个路径都传）
git -c core.quotePath=false --no-pager show --no-color --no-ext-diff --no-textconv \
    --src-prefix=a/ --dst-prefix=b/ --first-parent --format= --patch -M <sha> \
    -- [<oldPath>] <path>
```

| 参数 | 作用 |
|---|---|
| `-c core.quotePath=false` | 非 ASCII 路径不做八进制转义，解析与显示都用原始 UTF-8 |
| `--no-pager` / `--no-color` / `--no-show-signature` | 输出与用户的 pager、颜色、签名配置无关，保证可解析 |
| `--date=iso-strict` + `%aI` | 作者时间带时区，`DateTimeOffset.TryParse` 可直接解析；显示时再转本地时间 |
| `%x1f` | 以 0x1F 分隔字段；只切前 5 个分隔符，剩下的全部算标题，标题里即使出现控制字符也不会打乱字段 |
| `%B%x00` + `--numstat -z` | 提交正文以 NUL 与统计区隔开；NUL 不会出现在提交正文或文件路径里 |
| `--first-parent` | 合并提交相对第一个父提交显示改动（§5.4） |
| `--no-ext-diff` / `--no-textconv` | 不执行用户配置的外部 diff 或 textconv，拿到 git 的原始 unified diff |
| `-M` | 打开重命名检测；numstat 与 patch 使用同一阈值 |
| `-z` | numstat 的路径以 NUL 结尾，路径中的空格、引号、换行不会打乱解析 |
| `--` | 后面全部是路径，避免形似 revision 的路径被 git 当成提交名 |
| `--src-prefix=a/` / `--dst-prefix=b/` | 固定 `---` / `+++` 头的前缀，解析器不依赖用户的 diff 前缀配置 |
| `--format=`（文件 diff） | 只输出 patch，不重复输出提交正文与统计 |

### 5.4 合并提交与 `--first-parent`

- 列表命令**不带** `--first-parent`：当前分支可达的提交全部列出，包括合并提交；`%P` 给出父提交，数量大于 1 即合并提交，展开区据此显示合并提示。
- 详情与文件 diff 带 `--first-parent`：合并提交相对**第一个父提交**（分支主线）显示改动。这既避免了 git 默认的 combined diff（`--cc`）格式，也符合「这条提交给当前分支带来了什么」的阅读习惯。
- 相对第一个父提交没有改动时（例如空合并），详情返回零个文件，面板显示「此提交没有改动」，不是错误。

## 6. 解析

### 6.1 提交列表

- 按行切分，再按 `\x1f` 切成 6 个字段：`%H`（完整哈希）、`%h`（短哈希）、`%an`（作者）、`%aI`（作者时间）、`%P`（父提交，空格分隔）、`%s`（标题）。只切前 5 个分隔符，剩余的整段都算标题。
- 时间用 `DateTimeOffset.TryParse`（InvariantCulture）解析。解析失败时该行不显示时间，但不让整个列表失败。
- 标题按原样显示，不在解析层截断；界面用 `TextTrimming` 与 `ToolTip` 处理。
- 列表**只含元数据**：不取正文、不取文件、不取 diff，所以首屏只有一次 `git log`。

### 6.2 提交详情（正文与文件）

- 在第一个 `\x00` 处切成两段：前面是 `%B` 的完整正文（去掉末尾换行），后面是 numstat 区。提交对象里不能包含 NUL，所以这个分隔不会与正文冲突。
- `%x00` 之后 git 还会输出一个空行；加上 `-z` 后它是额外的 `\0` 加换行。切开后先跳过开头的空项（NUL / 换行），再进入 numstat 解析。
- numstat 区按 NUL 结尾的项依次解析：
  - **普通项**：`added\tremoved\tpath\0`；
  - **重命名项**：`added\tremoved\t\0old\0new\0`——第三个字段为空表示「后面还有两个路径」，先 old 后 new。
- 只从左侧切前两个 `\t`，剩下的全部算路径：文件路径本身可以包含制表符。
- `added` / `removed` 为 `-` 时表示二进制文件或子模块：两项都记为 `null`，标 `IsBinary`，**不请求 patch**，行内显示「二进制文件」。
- 没有 numstat 行（空提交、空合并）时文件列表为空，界面显示「此提交没有改动」。

### 6.3 单文件 diff

- `--patch` 的输出包含 `diff --git`、`index`、`similarity index`、`rename from` / `rename to`、`---` / `+++`、`@@` 等行。`GitHistoryService.FileDiffAsync` 先用 `PatchBody` 去掉 git 的前导元信息：从第一行以 `--- ` 或 `@@` 开头的行开始截取 body；`UnifiedDiffParser` 再跳过 `--- ` / `+++ `、空行与 `\ No newline at end of file`，解析 `@@` 块头得到新旧起始行号，此后逐行递增。纯重命名没有 `---` 也没有 `@@`，body 为空；服务再结合 patch 里的 `rename from` / `rename to` 标 `IsRenameOnly`。
- 解析器有行数上限（默认 400 行）。超出时在末尾追加一行 `…`（`DiffLineKind.Hunk`），并通过返回值的 `Truncated` 让界面显示「改动过多，只显示开头部分」。同一提交的其他文件仍可单独展开，不受影响。
- **纯重命名**没有 hunk：git 只输出 `diff --git` / `similarity index` / `rename from` / `rename to`（实测没有 `---` / `+++` / `@@`）。body 为空，`Lines` 为空列表，标 `IsRenameOnly`，界面显示「仅重命名，没有内容改动」。
- 二进制文件不请求 patch；即使输出了 `Binary files ... differ`，解析器也不会把它当成 diff 行。

### 6.4 空仓库与错误

- `git log` 非零退出时，再执行一次 `git rev-parse --verify -q HEAD`：
  - 探测失败：这是尚未有提交的空仓库（unborn branch），返回**空列表**，界面显示「当前分支还没有提交」，不是错误；
  - 探测成功：说明仓库存在但 `log` 失败（权限、损坏、`safe.directory` 等），把 `git log` 的 stderr 作为错误返回。
- 不是 Git 仓库的项目由 `GitRepository.Repositories` 返回空列表，ViewModel 因此根本不启动 git，直接显示「此项目不是 Git 仓库」。

## 7. 交互

### 7.1 布局

```text
┌ 历史 ─────────────────────────────────────────────────┐
│ [ 仓库 ▾ ]   ⑂ feature/history                    ⟳   │
├───────────────────────────────────────────────────────┤
│ ⌄ 修复历史面板的展开状态                11-11 14:22   │
│   3f2a9c1 · 张三                                      │
│ › 为历史面板补测试                      11-10 09:10   │
│   88b1d04 · 李四                                      │
│                                                       │
│                    [ 加载更多 ]                        │
└───────────────────────────────────────────────────────┘
```

展开一条提交后：

```text
│ ⌄ 修复历史面板的展开状态                11-11 14:22   │
│   3f2a9c1 · 张三                         [复制哈希]   │
│   ┌─────────────────────────────────────────────────┐ │
│   │ 提交正文（可选中、可复制）                      │ │
│   └─────────────────────────────────────────────────┘ │
│   3 个文件已更改                                       │
│   ⌄  HistoryTabViewModel.cs   ViewModels/  +42 −7   │
│     ┌───────────────────────────────────────────────┐ │
│     │ @@ -10,6 +10,8 @@                             │ │
│     │ …                                             │ │
│     └───────────────────────────────────────────────┘ │
│   ›  HistoryTabView.axaml     Views/      +120       │
│   ›  OldName.cs → NewName.cs                         │
```

- 顶部只占一行：仓库选择（仅多仓库时可见）、当前分支（`GitBranch` 图标 + 分支名或 detached 短哈希）、刷新按钮。
- 提交列表在 `arc:ScrollArea` 里；底部「加载更多」只在 `HasMore` 时显示。
- 所有颜色走 `TKCode.*` 动态资源；不新增 `motion:Popover`，仓库选择用 `arc:Select` 自带弹层（Arc 弹层跟随 `ArcZoom`，见 [projects.md](projects.md) §11），因此不需要再包 `ZoomHost`，也不影响 `UiZoomTests` 的计数。

### 7.2 提交行与展开

- **两行提交行**（整行是一个 `TKCode.RowButton`）：
  - 第一行：左侧 chevron（`motion:Turn.IsOn` 跟随展开状态）、提交标题（`CharacterEllipsis`，`ToolTip` 显示全文）、右侧时间（本地时间：当年 `MM-dd HH:mm`，往年 `yyyy-MM-dd`）；
  - 第二行：短哈希（等宽字体）、作者。
- 点击一行展开它；再次点击收起；展开另一条时自动收起上一条——**同一时间只有一条展开**。展开时按需加载详情，同一提交在一次打开期间只加载一次。
- 展开区从上到下：完整提交正文（`SelectableTextBlock`）、合并提交的「相对第一个父提交」提示、文件数（复用 `ChangesFilesChanged`）、文件列表。
- 每个文件一行：chevron、文件类型图标、文件名、目录、增删行数（`+42` / `−7`，二进制文件不显示行数）；点击首次展开时加载该文件的 patch 并缓存，之后收起再展开不重复请求。
- 文件展开区下面是 `views:DiffView`（`MaxContentHeight="0"`，让外层滚动区统一滚动）。`IsDiffTruncated` 时在 diff 下方显示截断提示；`IsRenameOnly` 时显示「仅重命名」；两者不与错误提示同时出现。
- **复制哈希**：展开区的操作按钮把完整 SHA 写入剪贴板；剪贴板不可用或写入失败时静默，不弹错误。

### 7.3 顶部：仓库、分支、刷新

- 仓库列表来自 `GitRepository.Repositories(cwd, additionalRoots)`，与分支选择器完全相同的发现与去重规则（§10）。
- 只有 `HasMultipleRepositories` 时才显示 `arc:Select`；只有一个仓库时不显示选择器，顶部直接从分支文字开始。
- `arc:Select` 的项是带 `Path` 与 `Name` 的仓库项，显示名是文件夹名（`Project.FolderName`，与分支选择器一致），用 `ToString()` 显示（不依赖 `DisplayMemberBinding`，AOT 安全）。选中项只在路径真的变化时重载，因为 `arc:Select` 重建 `ItemsSource` 时可能重新设置选中项。
- 切换项目 / 会话导致仓库列表变化时：保留仍然存在的选中仓库，否则退回第一个；列表不变时只刷新分支文字，不重载提交。
- 分支文字用 `GitRepository.ReadBranch`：普通分支显示分支名，detached HEAD 显示 7 位短哈希，空仓库（unborn）仍显示 `HEAD` 指向的分支名。
- 刷新按钮重载第一页；与「激活时加载」「watcher 触发」共用同一条 `LoadAsync` 路径。

### 7.4 加载、刷新与分页

- **激活时加载**：标签被激活（`IsActive` 变为 true）时重载第一页。因此从其他标签切回历史面板也会刷新，而不仅限于第一次打开。
- **关闭标签**：工作台移除历史标签时调用 `OnClosed()`：取消进行中的加载、释放 watcher、把 `IsActive` 置回 false（否则下次打开时属性没有变化，激活钩子不会触发）。数据可以保留到下次打开，但下次激活仍会重载。
- **分页**：每次向服务请求 51 条、显示 50 条（与侧栏会话列表 20/21 的约定相同），多出的一条只用于设置 `HasMore`。「加载更多」以 `skip = 已显示条数` 追加下一页。
- **刷新**：取消上一轮请求，重载第一页，并重新展开刷新前的 SHA（若它还在第一页里）。之前翻到第二页以后才展开的提交，刷新后不在第一页，保持收起——这是分页刷新的已知取舍。
- **watcher**：选中仓库变化时释放旧的 `GitBranchWatcher` 并新建一个。Watcher 监视 `HEAD`、`packed-refs` 与 `refs/heads`（worktree 的 HEAD 与公共 git 目录分别监视），在终端或 Agent 里提交、切换分支后会触发。
  - 回调经 `post`（默认 `Dispatcher.UIThread.Post`，测试里直接执行）回到 UI 线程；
  - 标签可见时重新加载；不可见时**只标记 stale**，不启动 git，等下次激活时再加载。
- **竞态**：切换项目 / 仓库、刷新、关闭标签都会取消上一轮 `CancellationTokenSource`，并用递增的 generation 丢弃过期结果，避免旧仓库的提交出现在新仓库的列表里。
- 历史标签与「变更」一样常驻，换项目时重定向，不随项目销毁。

### 7.5 空状态、加载与错误

| 状态 | 显示 |
|---|---|
| 项目里没有仓库（`HasNoRepository`） | `arc:EmptyState`：「此项目不是 Git 仓库」 |
| 仓库存在但没有提交（`IsEmpty`） | 「当前分支还没有提交」，顶部仍显示分支名 |
| 首屏加载中（`IsLoading`） | `controls:DotSpinner` + 「正在读取提交…」 |
| 加载失败（`Error`） | `arc:Alert` 或空状态 + 「无法读取历史：{0}」；已有列表保留，不整块替换 |
| 提交详情失败 | 错误显示在展开区内，提交行仍在 |
| 单个文件 diff 失败 | 错误显示在该文件行下方，其他文件不受影响 |

「加载更多」失败时保留已加载的提交，只在列表底部显示错误，并允许再次点击。

## 8. 宽度与布局取舍

- 编辑区最窄只有 **320px**（窗口最小宽度 880，侧栏、文件树、工作台可以同时出现），历史面板必须在这个宽度下可用。
- **不做左右分栏**（左提交列表、右详情）：两栏各自需要可读宽度，320px 下只能显示其中一栏，还会出现横向滚动；单栏手风琴在原位展开，与「变更」面板的交互一致，窄宽度下不需要额外模式。
- 320px 下的具体取舍：
  - 提交行固定两行，标题与目录分别截断，`ToolTip` 补全全文；
  - 增删行数右对齐、用小字号，不换行；
  - diff 不折行，由 `DiffView` 自己横向滚动，外层不出现横向滚动条；
  - 文件路径在展开区占满整行，必要时截断中间（保留文件名）。
- 不做提交图 / 分支线：那需要固定宽度的左侧轨道，在 320px 下会挤压标题。

## 9. 边界与风险

| 场景 | 行为 / 取舍 |
|---|---|
| git 未安装或不在 PATH | `Process.Start` 抛 `Win32Exception` → `HistoryLoadFailed`；其他面板不受影响，刷新可重试 |
| 不是仓库 / 没有项目 | `HasNoRepository` 空状态，不启动任何进程 |
| 空仓库（unborn branch） | `log` 失败后用 `rev-parse --verify -q HEAD` 探测，显示「还没有提交」 |
| detached HEAD | `log HEAD` 正常；顶部显示短哈希；面板不提供切换分支，所以没有额外操作 |
| worktree / submodule / separate git dir | 由 `GitLayout` 覆盖；命令在所选仓库文件夹中执行；watcher 分别监视各自的 HEAD 与 common dir |
| 合并提交 | `--first-parent`；相对第一个父没有改动时显示「没有改动」，不是错误 |
| 重命名 | numstat 的 old / new 两个路径；patch 时两个路径都传，否则 git 会当成新文件；纯重命名显示「仅重命名」 |
| 二进制文件 / 子模块 | numstat 为 `-`，不取 patch，显示「二进制文件」 |
| 中文与带空格的路径 | `core.quotePath=false` + UTF-8 + `ArgumentList` + 路径前的 `--` |
| 非 UTF-8 的旧提交信息 | 按 UTF-8 解码，乱码只影响显示；不猜测 `i18n.logOutputEncoding` |
| 超大提交 / 超大 diff | 每次只取一个文件的 patch，单个文件最多 400 行；列表不预载文件内容 |
| 提交很多、HEAD 移动 | 每页 50 条 + 加载更多；`--skip` 在新提交、rebase 后可能重复或漏掉个别提交，刷新即可恢复，v1 接受 |
| 加载竞态 | 取消 + generation 丢弃过期结果（§7.4） |
| 标签打开但未激活时 watcher 触发 | 只标 stale，不启动 git；关闭标签时释放 watcher |
| 超长提交正文 | 正文不单独截断，随 `git show` 一起返回；遇到异常大的正文再考虑折叠（目前未做） |
| 仓库权限 / `safe.directory` | git 非零退出，显示错误；面板不写入 git 配置 |
| AOT | 只用 `Process` 与字符串解析，无反射、无新 NuGet 包；`arc:Select` 的项用 `ToString()`；发布必须 0 个 `warning IL####` |
| 本地化 | 新文案只走 `strings.tsv` + `tools/gen_strings.py`；`LanguageResourceProbeTests` 检查枚举都有文案 |

## 10. 实现落点

| 文件 | 内容 | 状态 |
|---|---|---|
| `src/TKCode.Desktop/Services/GitHistoryService.cs` | `IGitHistory`（`LogAsync` / `ShowAsync` / `FileDiffAsync`）、记录类型（`GitCommitInfo`、`GitCommitDetail`、`GitFileChange`、`GitFileDiff`、`GitHistoryResult<T>`）、进程调用、三条命令、numstat 与字段解析、超时与取消 | 新增 |
| `src/TKCode.Desktop/Services/UnifiedDiffParser.cs` | 从 `ParsedToolDetails.DiffLinesOf` 抽出的 unified diff 解析；跳过 `--- ` / `+++ `、空行与 `\ No newline at end of file`，解析 `@@`，400 行上限与 `Truncated` | 新增 |
| `src/TKCode.Desktop/Services/GitRepository.cs` | 新增 `Repositories(string? cwd, IReadOnlyList<string> additionalRoots)`：用 `GitLayout.Find` 发现仓库，按 git 目录的完整路径去重（`PathRules.Comparer`，保持 Windows / macOS 忽略大小写），保持「主文件夹在前」的顺序；`ReadBranch` / `ReadBranches` 不变 | 修改 |
| `src/TKCode.Desktop/ViewModels/ToolDetailsViewModels.cs` | `ParsedToolDetails.DiffLinesOf` 改为调用 `UnifiedDiffParser`，输出与 400 行上限不变；`HunkStart` 移入解析器 | 修改 |
| `src/TKCode.Desktop/ViewModels/HistoryTabViewModel.cs` | `RepositoryItemViewModel`、`GitCommitViewModel`（展开、正文、文件、复制）、`GitFileViewModel`（按需加载并缓存 diff）、`HistoryTabViewModel`（仓库、分页、刷新、generation、watcher、`OnActivated` / `OnClosed`） | 新增 |
| `src/TKCode.Desktop/ViewModels/WorkbenchViewModel.cs` | `History` 常驻实例；`AddTool` 的 `History` 分支改为 `ShowHistory()`；`WorkbenchTabViewModel` 增加 `OnActivated` 虚方法（`IsActive` 变化时调用）；`RemoveTab` 移除历史标签时调用 `History.OnClosed()`；`SetHistoryRepositories`；`CopyTextAsync` 转发 | 修改 |
| `src/TKCode.Desktop/ViewModels/MainWindowViewModel.cs` | 订阅 `Conversation.WorkspaceRootsChanged`（已覆盖切换会话、切换项目、文件夹变化）→ 调 `Workbench.SetHistoryRepositories(Conversation.CurrentCwd, Conversation.CurrentAdditionalRoots)`；构造结束时调用一次，与 `SyncExplorerRoot()` 并列 | 修改 |
| `src/TKCode.Desktop/Views/HistoryTabView.axaml`（+ 只含 `InitializeComponent` 的 code-behind） | 顶部仓库选择 / 分支 / 刷新，提交列表、展开区、文件行、`DiffView`、加载更多与各种空状态 | 新增 |
| `src/TKCode.Desktop/Views/MainWindow.axaml`、`MainWindow.axaml.cs` | `ContentControl.DataTemplates` 增加 `HistoryTabViewModel` 的模板（`PlaceholderTabViewModel` 模板留给浏览器）；把 `Workbench.CopyTextAsync` 接到窗口剪贴板 | 修改 |
| `src/TKCode.Desktop/Localization/strings.tsv` | 新增 §11 的键，运行 `python3 -I tools/gen_strings.py`；不手改生成文件 | 修改 |

`ConversationViewModel.Repositories()` 现有的私有实现改为调用 `GitRepository.Repositories`，行为不变；分支选择器与历史面板因此共用同一份仓库列表。历史标签不实现 `IDisposable`：`Workbench.RemoveTab` 会释放 `IDisposable` 标签，那会让常驻实例在再次打开时无法使用；它用显式的 `OnClosed()` 做清理。

## 11. 本地化

新增的键（en-US / zh-CN），其余复用现有文案：

| 键 | en-US | zh-CN |
|---|---|---|
| `HistoryNoRepository` | This project is not a Git repository. | 此项目不是 Git 仓库 |
| `HistoryNoCommits` | No commits on this branch yet | 当前分支还没有提交 |
| `HistoryLoading` | Loading commits… | 正在读取提交… |
| `HistoryLoadMore` | Load more | 加载更多 |
| `HistoryLoadFailed` | Couldn't read the history: {0} | 无法读取历史：{0} |
| `HistoryRefresh` | Refresh | 刷新 |
| `HistoryRepository` | Repository | 仓库 |
| `HistoryCopyHash` | Copy commit hash | 复制提交哈希 |
| `HistoryNoChanges` | This commit has no changes | 此提交没有改动 |
| `HistoryRenameOnly` | Renamed without content changes | 仅重命名，没有内容改动 |
| `HistoryMergeFirstParent` | Merge commit — showing changes against the first parent | 合并提交：显示相对第一个父提交的改动 |
| `HistoryBinary` | Binary file | 二进制文件 |
| `HistoryDiffTruncated` | Diff is too long; only the beginning is shown | 改动过多，只显示开头部分 |

- 复用 `ChangesFilesChanged`（`{0} files changed` / `{0} 个文件已更改`）显示文件数。
- `TabHistory`、`HistoryDescription` 保留：前者仍是标签标题；后者不再由历史面板显示（占位页已不再使用它），但删除键要另起一次清理，不在本功能里做。
- 文案只能在 `strings.tsv` 里改；XAML 用 `{loc:TKCodeLangResource Key}`，C# 用 `Strings.Get` / `Strings.Format`。

## 12. 测试计划

全部为无头测试，不驱动真实桌面、不合成系统输入。

- **`tests/TKCode.Desktop.Tests/GitHistoryServiceTests.cs`**（临时仓库，复用 `BranchRefreshTests` 的 `NewRepository` / `Git` 写法，仓库内设置 `core.autocrlf=false`）：
  - 列表：顺序、短哈希、作者、日期、标题、父提交数量；分页（60 条 → 首页 50、第二页 10）；
  - 详情：提交正文、文件列表、增删行数、重命名（old / new）、二进制、空提交；
  - 合并提交：`--first-parent` 的差异；
  - 文件 diff：新增 / 修改 / 删除 / 重命名（纯重命名没有 hunk）、中文路径、`\ No newline at end of file` 不出现在行里、扩展头（`diff --git` / `index` / `similarity index` / `rename from|to`）不出现在行里；
  - 空仓库返回空列表；非仓库由 `GitRepository.Repositories` 返回空列表，ViewModel 因此不调用 git；
  - 取消或超时的路径返回错误而不是抛异常（可选，需要可注入的超时）。
- **`tests/TKCode.Desktop.Tests/HistoryTabTests.cs`**（用假的 `IGitHistory`）：
  - 无仓库 / 有仓库但为空两种空状态；多仓库时保留选中项；
  - 展开一条提交只加载一次，并折叠上一条；展开文件只加载一次；
  - 加载更多追加；刷新保留展开的 SHA；错误显示在行内；
  - 切换仓库后旧请求的结果被丢弃（generation）。
- **`tests/TKCode.Desktop.Tests/HistorySnapshotTests.cs`**：headless 渲染 `history-tab`（一条提交展开、一个文件展开 diff）与 `history-empty`；检查 520px 与 320px 两个宽度不溢出。
- **回归**：`ToolDetailsParseTests`、`ChangeViewSnapshotTests` 继续通过（解析器抽取不改变现有行为）；`LanguageResourceProbeTests` 覆盖新增文案；`UiZoomTests` 不受影响（没有新增 `motion:Popover`）。
- **命令**：
  - `dotnet build TKCode.slnx`
  - `dotnet test tests/TKCode.Desktop.Tests`
  - `dotnet test TKCode.slnx`
  - `dotnet publish src/TKCode.Desktop -c Release -r win-x64`，日志 0 个 `warning IL####`
  - `artifacts/publish/win-x64/TKCode --smoke`（本机 RID 对应路径）
- **手工检查**：在有合并、重命名、二进制文件、中文路径的仓库里打开历史面板；在终端里 `git commit` 后确认面板自动刷新；切换项目与仓库，确认选中项与列表正确；320px 下不出现横向滚动。

## 13. 实现状态

**开发中（2026-11-11）；集成验证完成后补充偏差。**

本文是设计稿，与并行进行的实现同步写作。§10 的路径与类型名是约定的落点，**不代表代码已经合并或验证通过**；测试尚未运行，AOT 发布与 `--smoke` 也尚未执行。实现若与本文不一致，以实现为准，并在这里记录。

待验证：

1. `dotnet build TKCode.slnx` 与 `dotnet test TKCode.slnx` 通过；
2. `GitHistoryServiceTests`、`HistoryTabTests`、`HistorySnapshotTests` 通过（含 §6.3 的扩展头与纯重命名判定），既有解析 / 变更快照测试不回归；
3. AOT 发布 0 个 `warning IL####`，发布产物 `--smoke` 通过；
4. §12 的手工检查全部通过。

验证完成后在本节补充：实际落点与差异、测试数量、AOT 与冒烟结果、已知问题。在此之前，不得把本功能描述为「已实现」。
