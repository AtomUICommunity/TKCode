# 项目：侧栏项目列表、创建项目弹窗、多文件夹

本文是设计稿，P1–P6 已实现（实现状态与偏差见 §16）。涉及的现有设计：侧栏与命令面板见 [architecture.md](../architecture.md)，工作区边界与审批见 [builtin-agent-tools.md](builtin-agent-tools.md) §10，外部运行时见 [external-agent-runtimes.md](external-agent-runtimes.md)，托盘菜单见 [system-menus.md](system-menus.md)。

## 1. 目标

1. 侧栏常驻显示项目列表，取代现在的「全部项目」下拉筛选。
2. 「新建项目」改为弹窗：项目名称、设备、源文件夹（可以有多个，其中一个为主要）。
3. 一个项目可以包含多个文件夹。Agent（内置的和外部运行时）在会话中能读写这个项目的所有文件夹。

## 2. 已确认的决策（用户，2026-10-22）

| 问题 | 决定 |
|---|---|
| 侧栏怎样组织会话 | **项目作筛选**：点击项目即选中它，下面的会话区只显示这个项目的会话；没有选中时显示全部会话。不做「项目下嵌套会话」的树 |
| 多个文件夹各起什么作用 | **照 Codex**：主文件夹就是 cwd，决定 git、AGENTS.md、`.agents`、`.mcp.json` 和记忆；其他文件夹可以读写、可以搜索，审批与工作区内相同，并列在提示词里 |
| 修改项目的文件夹之后 | **对项目内所有会话实时生效**（下一轮开始前）。更换主文件夹只影响新会话 |
| 本轮的 UI 范围 | 文件树多根、@ 文件跨文件夹搜索、分支选择器按文件夹切换、会话中临时追加文件夹，四项都做 |
| 组件 | 尽可能使用 TKCode.Arc 组件（§11） |
| 用词 | 沿用「会话」，不改成截图里的「任务」 |
| grep / glob / ls 的默认范围 | 不传路径时只搜索主文件夹 |
| 审批卡片「加入本会话」 | 本轮不做；只通过 `/add-dir` 和文件树添加或移除 |
| 首次启动自动发现仓库 | 去掉；项目都由用户自己创建 |

## 3. 参考：ChatGPT / Codex 桌面应用

截图里的弹窗来自 ChatGPT 桌面应用里的 Codex 项目。下面的文案和行为来自本机 `/Applications/ChatGPT.app` 的 `app.asar`（v26.1007）中的 zh-CN 字符串，以及 Codex 的公开文档。

- **创建项目**：名称 / 项目所在设备（此电脑）/ 源文件夹（添加文件夹、设为主要、主要、移除 {name}）。列表为空时提示「添加 Codex 可读取和编辑的文件夹」。「编辑项目」复用同一个弹窗，提交按钮是「保存」，并有「移除本地项目」。
- **主文件夹**：「主要」标记的提示是「Codex 会在主文件夹中查找 AGENTS.md、config.toml 和技能文件」。新会话在主文件夹中启动；git、worktree 和 PR 只针对主仓库。其他文件夹可以读写、参与搜索，但不参与这些文件的发现。
- **文件夹的变化对整个项目生效**：把会话移进项目时的确认文案是「{项目} 内的所有聊天都能访问这些文件夹」。
- **移除项目**：「这只会从应用中移除该项目。你电脑上的文件和现有聊天不会被删除。」
- **侧栏**：项目菜单里有编辑项目、置顶项目、打开文件夹。悬停卡片显示任务数、进行中和待处理的数量。

其他参考：
- **Claude Code**：`--add-dir` 加入的目录与 cwd 的权限规则相同，cwd 仍是 primary working directory。附加目录里的 CLAUDE.md 默认不加载。
- **ACP**：`additionalDirectories` 已稳定。cwd 是主目录，也是相对路径的基准；恢复会话时必须重发完整列表，会话中途不能修改。
- **Zed / VS Code**：多根时，路径和搜索结果都带上文件夹名前缀。

## 4. 现状

### 4.1 「项目 = 一个路径」的地方

- **Desktop 项目列表**：state.json 里的 `workspaces: string[]`。相关的还有：
  - `lastWorkspace`（路径）；
  - `recentWorkspaces`（路径 MRU，用于托盘、Dock 和跳转列表）；
  - `permissionModes`（按路径记住的权限档位）。
- **会话的身份是 `Cwd`**：
  - 存储目录：`sessions/<EncodeCwd>/`；
  - 会话头：`SessionHeader(SessionId, Cwd, Version)`；
  - 列表筛选：`session/query` 按 cwd 精确匹配；
  - 外部运行时只在原来的 cwd 下恢复（external-agent-runtimes.md §7）。
- **只看 cwd 的地方**：
  - 文件和路径：@ 文件索引（`FileSearchRequest(Cwd)`）、文件树（单个 `Root`）、Markdown 图片的允许目录、终端默认目录；
  - git：分支选择器（单仓库）；
  - 发现类：`.agents` 资产、MCP 的 `.mcp.json` 与信任、项目记忆、AGENTS.md；
  - 外部运行时：三个运行时都只传 cwd。
- **新建项目的三个入口**：侧栏的「…」、项目选择器的「新建项目…」、⌘K / ⇧⌘N 命令面板。它们最后都直接打开系统的文件夹选择框（单选）。

### 4.2 已经有的多目录基础

- **`WorkspaceRoots(cwd, additional)`**：`Contains` 判定已经支持多个根。审批（`PermissionPolicy`）以及 bash 的「是否停留在工作区」检查都用它。
- **提示词**：`cwd` 节会为每个附加目录写一行 `Additional workspace directory: …`。
- **限制**：附加目录目前只有全局的 `settings.permissions.additionalDirectories` 一个来源，只在会话创建或打开时读取。`WorkspaceRoots` 只能 `Add`，不能删除或替换。
- **子 Agent**：共用父会话的同一个 `WorkspaceRoots` 实例，所以会自动跟随父会话的变化。
- **协议**：docs/protocols 已经写了 Claude 的 `--add-dir`、Codex 的 `writableRoots`、ACP 的 `additionalDirectories`，都还没有接上。

## 5. 数据模型

### 5.1 `~/.tkcode/projects.json`

项目注册表从 state.json 移到单独的文件（`TKCODE_HOME` 下），由 `TKCode.Config` 的 `ProjectsFile` 读写。JSON 用源生成的 `JsonSerializerContext`。保存时先写临时文件，再 rename。

```json
{
  "version": 1,
  "projects": [
    {
      "id": "0193f1c2a4b87d6e9f00112233445566",
      "name": "routin-fax",
      "device": "local",
      "folders": ["/Users/me/code/routin-fax", "/Users/me/code/shared-lib"],
      "pinned": false,
      "createdAt": "2026-10-22T08:00:00Z"
    }
  ]
}
```

| 字段 | 说明 |
|---|---|
| `id` | `Guid.CreateVersion7().ToString("N")`，创建后不变。会话通过它归属到项目 |
| `name` | 显示名称，可以重复（托盘里重名时追加主文件夹的上级目录，沿用现有规则） |
| `device` | 现在只有 `"local"`。为以后的远程设备保留 |
| `folders` | 绝对路径，**第一个就是主文件夹**。「设为主要」就是把它移到第一位，其余文件夹保持原来的顺序 |
| `pinned` | 置顶 |
| `createdAt` | 创建时间 |

数组的顺序就是侧栏的顺序：新建的插到最前，置顶的项目显示在最前面（§8.3）。

为什么不放在 state.json：
- Host 也要读取项目表，用来实时同步文件夹（§6.3）和按项目筛选会话（§6.5）。
- state.json 是 Desktop 的界面状态，而且在 `WorkspaceRoots.IsSensitive` 名单里。

为什么不走 RPC 由 Host 持有：Desktop 启动时，侧栏要在 Host 连上之前就画出来；托盘菜单也要在那之前可用。

所以采用「Desktop 写、Host 读」，与 settings.json 相同。Host 按文件的 mtime 和长度缓存解析结果，每次用到时只做一次 `stat`。

**安全**：`projects.json` 加入 `IsSensitive` 名单。Agent 改写它就等于给自己扩大工作区，所以读写都要审批。

### 5.2 迁移

第一次启动时如果没有 projects.json：

1. state.json 里 `workspaces` 的每个路径生成一个单文件夹项目，名称取文件夹名。`workspaces` 为空时，项目列表就是空的：删除 `WorkspaceService.Discover()`，不再自动从 `~/code` 等目录发现仓库。
2. `lastWorkspace` 改为 `lastProjectId`，`recentWorkspaces` 改为 `recentProjects`（id 列表）。`permissionModes` 的路径键换成对应项目的 id，找不到对应项目的条目丢弃。
3. 旧字段读取后不再写回。
4. 旧会话没有 `ProjectId`，按 §5.3 的回退规则归属，不用改写会话文件。

### 5.3 会话归属

- 新会话在会话头里记录 `ProjectId`：`SessionHeader(SessionId, Cwd, Version, ProjectId?)`，可选字段，旧文件照常读取。cwd 仍然是**创建时的主文件夹**，以后不变。外部运行时的恢复以及 `sessions/<EncodeCwd>/` 的目录结构都不受影响。
- 会话归属的规则由 Host 计算：
  1. 如果 `ProjectId` 指向一个现存的项目，就归它；
  2. 否则（旧会话，或者项目已被移除），归到第一个**主文件夹等于会话 cwd** 的项目（按 projects.json 中的顺序，与置顶无关）；
  3. 都不满足时，这个会话不属于任何项目。
- 按这个规则，移除项目再重新添加同一个文件夹，原来的会话会重新出现在它下面，不需要额外处理。
- 不使用项目时（空状态选择「不使用项目」），cwd 仍是用户目录，`ProjectId` 为 null。

### 5.4 路径规则

- **保存**：`Path.GetFullPath`，去掉末尾的分隔符。
- **比较**：统一用 `PathUtil.SamePath`（Windows 和 macOS 不区分大小写）。`AgentHostServer.SamePath` 目前只在 Windows 上不区分大小写，这次一并改为调用 `PathUtil`。
- **重复的文件夹**：添加时按真实路径（解析符号链接）判断，重复的不再加入，并在弹窗里闪一下已有的那一行。
- **嵌套的文件夹**（一个文件夹位于另一个之内）：允许，但弹窗里显示警告（§9.2）。搜索结果按绝对路径去重。
- **同一个文件夹出现在多个项目中**：允许。旧会话按 §5.3 归属到第一个匹配的项目。
- **文件夹不存在**（被删除或磁盘未挂载）：
  - 项目不会因此被隐藏（现在的 `Load()` 会过滤掉不存在的路径，以后不再过滤）；
  - 侧栏的项目行显示警告图标；
  - Host 构建工作区时跳过不存在的文件夹；
  - 主文件夹不存在时不能新建会话，提示用户去编辑项目。

## 6. Host 与 RPC

### 6.1 RPC 变化

`RpcProtocol.Version` 从 24 升到 25。新增方法要改四处：接口、`RpcMethods`、`RpcJsonContext`、proxy + dispatcher。

| 位置 | 变化 |
|---|---|
| `CreateSessionRequest` | 新增 `ProjectId?` 和 `Directories?`（会话临时文件夹，§6.4）。`Cwd` 仍由 Desktop 传入主文件夹 |
| `SessionDto` | 新增 `ProjectId?` 和 `SessionDirectories`（会话临时文件夹） |
| `SessionSummaryDto` | 新增 `ProjectId?`（按 §5.3 解析后的结果，用于侧栏的项目徽标） |
| `QuerySessionsParams` | 新增 `ProjectId?`。`Cwd` 保留，以兼容旧调用 |
| `session/addDirectory`、`session/removeDirectory` | 新方法：`{sessionId, path}` → `SessionDto` |
| `FileSearchRequest` | 新增 `Roots?`（主文件夹之外要一起搜索的根）；结果项新增 `Root`（命中所在的根，绝对路径） |
| `ForkSessionAsync` | 复制 `ProjectId` 和会话临时文件夹 |

### 6.2 会话的工作区由什么组成

`WorkspaceRoots(cwd = 会话的主文件夹, additional = ①②③)`，按顺序去重：

1. 项目当前的其他文件夹（`folders` 中除会话 cwd 以外的所有文件夹，跳过不存在的）；
2. 会话临时文件夹（§6.4）；
3. settings 里的 `permissions.additionalDirectories`（全局，保持现状）。

会话的 cwd 永远是工作区的根。更换主文件夹之后，已有会话的 cwd 不变；新的主文件夹作为附加目录加入它们的工作区。原来的主文件夹即使已从项目中移除，仍是这些会话的 cwd。

`WorkspaceRoots` 新增 `Sync(IReadOnlyList<string> additional)`：
- 替换附加目录列表，同时重算真实路径；
- 返回列表是否有变化；
- 继续用 `_gate` 加锁。子 Agent 共用同一个实例，所以会一起更新。

### 6.3 实时同步：下一轮开始前

- **同步点**：内置 Agent 在每批用户输入之前已经会调用 `SystemPromptTracker.UpdatesAsync`，同步就放在它前面：
  1. 按 §6.2 重新计算附加目录（项目表走 mtime 缓存）；
  2. 调用 `WorkspaceRoots.Sync`；
  3. tracker 重算各节，发现 `cwd` 节变了，就追加一条 SystemMessage 补丁。
- **不破坏的东西**：开头的系统提示不变，缓存前缀不变，也符合「历史只追加、系统提示冻结、变化用追加的 SystemMessage 表达」的约束。
- **一轮进行中**不做同步；审批判定在这一轮里继续用旧的根，下一轮才切换。

Desktop 保存 projects.json 之后不需要通知 Host，因为 Host 每轮开始前都会检查一次。

### 6.4 会话临时文件夹

类似 Claude Code 的 `/add-dir`：只给当前会话加一个文件夹，不写进项目。

- **RPC**：`session/addDirectory`、`session/removeDirectory`。builtin-agent-tools.md §10 已经预留了这两个名字。
- **持久化**：新的会话级记录 `SessionDirectoriesEntry(IReadOnlyList<string> Directories) : SessionEntry`。
  - 每次都写完整列表，以最后一条为准，与 `SessionMetaEntry` 一样不属于对话树。
  - 重新打开会话时恢复。
  - fork 时复制。
- **新会话**：在空状态里追加的临时文件夹，随 `CreateSessionRequest.Directories` 一起传给 Host。
- **生效时间**：和项目文件夹一样，下一轮开始前（§6.3）。

### 6.5 会话列表按项目筛选

- **筛选**：`session/query` 带 `ProjectId` 时，按 §5.3 的规则过滤。`SessionIndexEntry` 新增 `ProjectId`（从会话头读取）。Host 用缓存的项目表计算回退归属。
- **不带 `ProjectId`** 时返回全部会话，与现在一样。
- **结果里的 `ProjectId`**：每个 `SessionSummaryDto` 都带上解析后的 `ProjectId`，供 Desktop 显示项目徽标和命令面板的副标题。

### 6.6 工具、路径与 @

**工具**
- **相对路径和 bash**：相对路径仍然以主文件夹为基准（`WorkspaceRoots.Resolve`），bash 的工作目录仍是主文件夹。与 ACP 和 Claude Code 一致。
- **grep / glob / ls**：没有传路径时只搜索主文件夹（已确认），与 Claude Code 一致，可以避免一次搜索就扫遍所有文件夹。附加文件夹在提示词里以绝对路径列出，模型需要时传绝对路径（§7.2）。
- **结果显示**：grep 等工具的结果在主文件夹内显示相对路径，在其他地方显示绝对路径。这是现在的行为，不用改。

**@ 文件索引**（`FileIndexService`）
- 每个根一个索引，按根的路径做 LRU。多个项目共用同一个文件夹时，也共用它的索引。
- 上限从 8 个提高到 16 个。
- 搜索时把各个根的结果按分数合并。

**@ 标签发出的文字**
- 主文件夹里的文件：相对路径（与现在相同）。
- 其他文件夹里的文件：绝对路径。

这样模型那边不会产生歧义，也和拖放文件夹时 `AttachedFiles.SegmentForLocalPath` 的现有行为一致。

### 6.7 发现：只看主文件夹

照 Codex，下面这些都只从**主文件夹**（会话 cwd）发现，**行为不变**：
- AGENTS.md：从 git 根到 cwd 逐级查找，读文件时加载 cwd 之下的嵌套 AGENTS.md；
- `.agents` 下的命令、技能和角色；
- `.mcp.json` 与 MCP 信任；
- 项目记忆。

项目根目录现在有三种算法：
- 记忆：worktree 映射到主仓库；
- 资产和提示词：`GitInfo` 根；
- MCP：自己向上找 `.git`，没有规范化路径。

统一它们会改变 worktree 下 `.mcp.json` 的查找结果，另开任务处理（§15），这次不动。

### 6.8 外部运行时

| 运行时 | 新会话 / 恢复 | 文件夹变化之后 |
|---|---|---|
| Claude Code | 每个附加目录传一个 `--add-dir <dir>` | stream-json 没有主动修改目录的控制请求（`addDirectories` 只能出现在 `can_use_tool` 的应答里）。下一轮开始前如果发现变化且会话空闲，就用 `--resume <原生 id>` 和新的参数重启 CLI 进程 |
| Codex | `thread/start` 只带 cwd | 每次 `turn/start` 都带 `sandboxPolicy: {type:"workspaceWrite", writableRoots:[…]}`（稳定 API，跟随下一轮）。只读档位与完全授权档位不需要传。Codex 会把每个可写根下的 `.git` 设为只读，所以在附加仓库里提交仍要审批（写入文档） |
| ACP | Agent 声明了 `sessionCapabilities.additionalDirectories` 时，在 new / load / resume 中发送完整列表 | 协议不支持会话中途修改。下一轮开始前如果发现变化且会话空闲，并且 Agent 支持 `resume`，就重新 `session/resume` 并发送完整列表。否则等下次打开会话时生效。不支持附加目录的 Agent，在文件夹弹层里注明 |

`RuntimeSessionRequest` 新增 `AdditionalDirectories`。各运行时都在「下一轮开始前」检查同一份目录列表，与 §6.3 的时机一致。

## 7. 提示词（草稿，待审）

按惯例，提示词先在这里定稿，代码中的文字与本节保持一致。

### 7.1 `cwd` 节

**没有附加目录**时保持现在的写法，只有一个路径，这样现有会话恢复时不会多出一条补丁：

```
<cwd>
/Users/me/code/routin-fax
</cwd>
```

**有附加目录**时：

```
<cwd>
Primary working directory: /Users/me/code/routin-fax
Additional working directories (read, search and edit them just like the primary one; refer to their files by absolute path, because relative paths and shell commands resolve against the primary working directory):
- /Users/me/code/shared-lib
- /Users/me/code/design-docs (added for this session only)
</cwd>
```

- 说明文字只在有附加目录时出现，单文件夹的会话不会多出这些字。
- `(added for this session only)` 标出会话临时文件夹，模型由此知道它不属于项目。
- 设置里的全局附加目录不加标注。现在的 `Additional workspace directory:` 格式由上面这种格式取代；只有用了这个设置的会话，恢复时会多一条补丁。

### 7.2 规则与工具说明

规则（`GeneralRules` 中关于 @ 的一条）：

```
旧：In user messages, @path refers to a file or directory in the workspace, relative to the working directory. Read it with your tools when you need its contents; the message only carries the path.
新：In user messages, @path refers to a file or directory in the workspace: a relative path is relative to the primary working directory, an absolute path is used as is. Read it with your tools when you need its contents; the message only carries the path.
```

工具说明（只影响新会话，因为工具集在会话开始时冻结）：

| 工具 | 旧 | 新 |
|---|---|---|
| read / write / edit | relative to the workspace root | relative to the primary working directory |
| grep / glob | defaults to the workspace root | defaults to the primary working directory; pass an absolute path to search an additional working directory |
| ls | Defaults to the workspace root | Defaults to the primary working directory; pass an absolute path to list an additional working directory |
| bash | runs in the workspace root | runs in the primary working directory |

`environment` 节里的 git 信息仍然只写主文件夹，与 Claude Code 和 Codex 相同。附加仓库的分支不写进提示词：否则每次切换分支都会产生一条补丁。

注意：规则一节的文字改了以后，已有会话在恢复后的第一轮会收到一条 `rules` 补丁。这是一次性的。

## 8. 侧栏

### 8.1 布局

```
◧  ←  →  +                         ← 顶栏不变
项目                       ▾    +   ← 分区标题：折叠、+ 新建项目（弹窗）
📁 routin-fax              …   ⊕   ← 悬停或菜单打开时显示操作；选中时高亮
📁 CoWork                ⚠         ← 主文件夹不存在
📁 NextCoWork
📁 DataFoundry
   展开更多（12）                   ← 超过 8 个时
会话 · routin-fax          ✕   ▾   ← 筛选中：标题带项目名，✕ 清除筛选
● 修复登录超时              3 分钟
● 重构 HttpClient           5 小时
   展开更多
                                    ← 底栏不变
```

### 8.2 筛选

- **点击项目行**：
  - 选中这个项目，会话区只显示它的会话（`session/query` 带 `ProjectId`，分页规则不变）；
  - 同时把它设为新会话的默认项目，与现在 `SetFilter` 的行为相同。
- **再次点击已选中的项目**，或者点击会话区标题上的 ✕：清除筛选，显示全部会话。
- **没有选中项目时**：
  - 会话区标题是「会话」，显示全部会话，包括不属于任何项目的；
  - 会话行显示项目徽标，首字母取自**项目名**，不再取自文件夹名；
  - 没有项目的会话不显示徽标。
- **筛选中**：会话行隐藏项目徽标，因为这时它是多余的。
- **记住选择**：选中的项目写入 state.json 的 `lastProjectId`，重启后恢复。

### 8.3 项目行

- **控件**：`TKCode.RowButton`。Arc 没有侧栏列表控件；Arc `TreeView` 的行只能放标题和图标，放不下悬停操作。
- **内容**：
  - 文件夹图标；
  - 项目名（过长时截断）；
  - 项目里有正在运行的会话时，右侧显示一个小的 `DotSpinner`。
- **悬停操作**（悬停时，或者菜单打开期间显示）：
  - `…` 是 `arc:DropdownMenu`，`Trigger` 是一个小号的幽灵按钮，并设置 `AutomationProperties.Name`；
  - `⊕` 是「在 {项目} 中新建会话」：先选中这个项目，再执行新会话。
- **菜单**（`…` 与右键 `arc:ContextMenu` 共用同一组 `MenuEntry`）：
  - 编辑项目…
  - 在 Finder 中显示（Windows 上是「在资源管理器中显示」）：打开主文件夹，复用 `ExplorerPlatformActions`
  - 复制路径：主文件夹
  - 置顶项目 / 取消置顶
  - ─────
  - 移除项目…（danger）
- **提示**：用 `arc:Tooltip`，列出全部文件夹，主文件夹标「主要」。不存在的文件夹标「不存在」。
- **顺序**：置顶的在前，按置顶的先后；其余按 projects.json 中的顺序，新建的在最前。本轮不做拖动排序。
- **项目很多时**：默认显示前 8 个，加一个「展开更多 / 收起」。选中的项目即使排在 8 个之外也始终显示。
- **空状态**：没有项目时，分区里显示一行「还没有项目」和「新建项目」入口。优先用 `arc:EmptyState`（只设 Title 和 Action）；在 256px 宽的侧栏里如果显得太重，就退回一行文字。以快照测试的结果为准。

### 8.4 其他入口

- **空状态的芯片**（输入框上方的「设备 ▾ / 项目 ▾」）保留。项目选择器的「新建项目…」改为打开弹窗。删除 `ProjectPickerMode.Filter` 和侧栏上那个 `ProjectPickerView`。
- **命令面板**：「新建项目」直接打开弹窗；设备在弹窗里选择，所以去掉 `DevicesPage` 这一步。⇧⌘N 同样。
- **托盘、Dock、跳转列表**：
  - 菜单显示最近使用的项目（state.json 的 `recentProjects`，存的是 id）；
  - 点击后的效果等同于在侧栏里选中这个项目；
  - `TrayAction.OpenProject` 仍携带主文件夹路径，与 `--open-project` 共用同一套查找规则（见下一条）。
- **`--open-project <path>`**（跳转列表与单实例转发用）：
  - 参数仍然是路径，因为跳转列表的参数需要长期稳定；
  - 按「主文件夹等于这个路径 → 某个文件夹等于这个路径」的顺序查找项目；
  - 都找不到时，新建一个单文件夹项目。

## 9. 创建 / 编辑项目弹窗

### 9.1 布局

```
┌ 创建项目 ──────────────────────────────────── ✕ ┐
│ 项目可以包含多个文件夹，Agent 可以读写其中的所有文件。 │   ← Description
├──────────────────────────────────────────────────┤
│ 项目名称                                          │
│ [ routin-fax                                   ]  │   ← arc:Input（占位：主文件夹名 / 新项目）
│ 设备                                              │
│ [ 🖥 此电脑                                   ▾ ]  │   ← arc:Select（暂时只有一项）
│ 源文件夹                                          │
│ ┌──────────────────────────────────────────────┐ │
│ │ 📁 routin-fax  ~/code/routin-fax  [主要]   ✕ │ │   ← arc:Badge；悬停提示主文件夹的作用
│ │ 📁 shared-lib  ~/code/shared-lib  设为主要 ✕ │ │   ← 「设为主要」悬停时显示
│ │ ⊕ 添加文件夹                                  │ │
│ └──────────────────────────────────────────────┘ │
│ ⚠ shared-lib 位于 routin-fax 之内                  │   ← arc:Alert（警告 / 错误）
│ [移除项目]                        [取消] [创建项目] │   ← 编辑时左侧有「移除项目」，右侧是「保存」
└──────────────────────────────────────────────────┘
```

尺寸 `Medium`（440px）。

### 9.2 交互与校验

- **项目名称**：
  - 留空时，提交时使用主文件夹的名称；
  - 加入第一个文件夹时，如果名称还是空的，自动填入它的文件夹名；
  - 最长 80 个字符；
  - 在名称框里按 Enter，等同于提交。
- **设备**：现在只有「此电脑」一项。保留这个字段，是为了与参考一致，也为远程设备留出位置。
- **添加文件夹**：
  - `StorageProvider.OpenFolderPickerAsync(AllowMultiple = true)`，起始位置是上次选择文件夹时的上级目录；
  - 也接受把文件夹拖放到列表上；
  - 第一个加入的文件夹自动成为主要。
- **设为主要**：把这个文件夹移到第一位。
  - 移除主文件夹后，下一个文件夹成为主要。
  - 在编辑模式下更换主文件夹时，显示一条提示：「已有会话仍在原来的主文件夹中运行，新会话从新的主文件夹开始。」
- **校验**：
  - 至少要有一个文件夹，否则提交按钮不可用；
  - 重复的文件夹不加入，并闪一下已有的那一行；
  - 嵌套的文件夹显示警告，但允许提交；
  - 编辑时，不存在的文件夹标为「不存在」，用户可以移除它。
- **提交**：
  1. 写入 projects.json；
  2. 关闭弹窗；
  3. 侧栏选中这个项目，并把它设为新会话的默认项目。

  写入失败时在弹窗里显示错误，弹窗不关闭，输入也保留，与 `ExplorerFileDialog` 相同。
- **编辑**：只修改字段，`id` 不变，因此会话的归属不变，文件夹的变化按 §6.3 生效。

### 9.3 实现

- **`Controls/ScaledModal`**：从 `ExplorerFileDialog` 中抽出来的可复用部分。
  - 用 `ZoomHost` 包一层 `arc:DialogSurface`，再用 `ModalPresence` 挂到窗口上；
  - 按 `UiZoom.Scale` 限制最大尺寸，并跟随 `MotionSettings`；
  - 提交进行中不允许关闭（窗口关闭也会被拦住）；
  - 通过 `TaskCompletionSource` 返回结果。

  `ExplorerFileDialog` 改为使用它，行为不变。
- **`Views/ProjectDialog.axaml`**：根元素是 `arc:DialogSurface`，配 `ProjectDialogViewModel`：
  - `Name`、`Device`；
  - `Folders`，类型 `ObservableCollection<ProjectFolderItemViewModel>`，每项有 `IsPrimary`、`IsMissing`；
  - `Warnings`、`CanSubmit`；
  - 命令 `AddFolders`、`MakePrimary`、`Remove`、`SubmitAsync`。

  使用编译绑定（`x:DataType`），保证 NativeAOT 安全。
- **移除确认**：通过 `ScaledModal` 显示一个确认用的 `arc:DialogSurface`，确认按钮用 `Danger` 变体。
  - 文案：「这只会从 TKCode 中移除该项目，电脑上的文件和已有会话不会被删除。」
  - 不直接用 Arc 的 `Dialog.ConfirmAsync`：`ModalOverlay` 不属于 `ArcPopup`，不跟随 `ArcZoom`，uiScale 不等于 1 时尺寸会不对。按「先完成 Arc 再发版」的规则，Arc 这边的修复另开任务。
- **移除之后**：
  - 从 projects.json 删除这一项；
  - 如果它正被选中，就清除筛选；
  - 它的会话不删除，按 §5.3 变成不属于任何项目，在「全部」视图里照常可以看到。

## 10. 多文件夹的 UI

### 10.1 文件树多根

- **单文件夹项目**：和现在一样，直接显示文件夹里的内容，没有根节点。
- **多个文件夹**（包括会话临时文件夹）：每个文件夹一个顶层节点。
  - 主文件夹排在最前，名称后跟一个淡色的「主要」；
  - 会话临时文件夹后跟「本会话」；
  - 主文件夹默认展开，各根的展开状态分别记住。
- **`FileExplorerViewModel` 的改动**：
  - `Root` 换成 `Roots` 加上 `RootOf(path)`；
  - `TryGetRelativePath`、`RevealCoreAsync`、`GetOrCreate`、`CaptureContext` 都改用路径所在的根；
  - `ExplorerContextTarget.Root` 取所在的根，`IsRoot` 改为「这个节点是某个根」；
  - 在空白处打开的右键菜单作用于主文件夹；
  - 根节点的右键菜单在会话临时文件夹上多一项「从本会话移除」。
- **搜索**：在所有根里搜索，结果上带根的名称。「刷新」遍历所有根。`ExplorerFileOperations.EnsureInside` 本来就按上下文里的根判断，不用改。
- **编辑区**：面包屑和 `RetargetAsync` 使用文件所在的根。「+ 终端」默认在主文件夹打开。
- **Markdown**：图片的允许目录包括所有项目的所有文件夹，以及当前会话的临时文件夹。`MarkdownCodePaths.Directory` 用会话的 cwd。

### 10.2 @ 文件跨文件夹搜索

- 补全列表在所有根里搜索（§6.6）。
- **显示**：有多个根时，路径写成「文件夹名/相对路径」，文件夹名淡色显示。只有一个根时与现在相同。
- **插入的标签**：主文件夹内是相对路径，其他文件夹内是绝对路径（§6.6），标签上显示的仍是带文件夹名的短路径。
- **最近使用**：`recentMentions` 仍然按根的路径保存，键的格式不变。

### 10.3 分支选择器按文件夹切换

- **芯片**：显示主文件夹的分支。项目里还有其他 git 仓库时，在后面加一个淡色的 `+N`。
- **弹层**：
  - 顶部列出项目里的各个 git 仓库：文件夹名和当前分支；
  - 位于同一个仓库里的多个文件夹按 git 目录去重；
  - 默认选中主文件夹；
  - 选中某个仓库后，下面显示它的分支搜索列表，与现在相同。
- **切换**：作用于选中的仓库。忙碌判断从「同一个 cwd 有会话正在运行」改为「这个项目里有会话正在运行」，因为所有会话都能访问所有文件夹。
- **监视**：每个仓库一个 `GitBranchWatcher`。`WorkspaceInfo.ReadBranch` / `ReadBranches` 移到按仓库的 `GitRepository` 辅助类里。

### 10.4 会话临时文件夹的入口

- **内置命令 `/add-dir`**：加入输入框 `/` 列表的内置命令组。
  - 带路径参数时直接加入（相对路径以主文件夹为基准，支持 `~`）；
  - 不带参数时打开文件夹选择框。
  - 在空状态里使用时，文件夹先暂存，创建会话时一起传给 Host（§6.4）。
- **显示与移除**：临时文件夹显示在文件树里（§10.1），在那里移除。
- 「工作区外」的审批卡片本轮保持现状，不增加「把这个文件夹加入本会话」。

### 10.5 其他只看 cwd 的地方

- **MCP 设置与资产设置页**：仍然以当前会话的 cwd（主文件夹）为准。项目分组的标题改用项目名。
- **权限档位**：按项目 id 记住（§5.2）。
- **会话标题与命令面板**：副标题从「文件夹名 @ 设备」改为「项目名 @ 设备」。

## 11. Arc 组件对照

| 位置 | 组件 |
|---|---|
| 创建 / 编辑项目弹窗、移除确认 | `arc:DialogSurface`，经 `ScaledModal`（`ZoomHost` + `ModalPresence`）显示 |
| 项目名称 | `arc:Input`（Label、Placeholder、ErrorMessage） |
| 设备 | `arc:Select` |
| 「主要」标记 | `arc:Badge`（Neutral、Small） |
| 添加文件夹、设为主要、移除、取消、提交 | `arc:Button`（Ghost / Secondary / Primary / Danger，配 `IsLoading`），图标用 Arc 的 `LucideIcon`，放在按钮的内容里 |
| 警告与错误 | `arc:Alert` |
| 项目行的 `…` 菜单 | `arc:DropdownMenu`（自定义 `Trigger`）+ `MenuEntry` |
| 项目行右键 | `arc:ContextMenu`（`OpensOnClick=false`） |
| 项目行提示 | `arc:Tooltip` |
| 侧栏没有项目时 | `arc:EmptyState`（§8.3，以快照为准） |
| 文件树 | 已经是 Arc `TreeView` |

**不换的部分**：
- 侧栏的行和分区标题继续用 `TKCode.RowButton`；
- 现有的分支、权限、角色、模型选择器继续用 `motion:Popover` + `ZoomHost`。

Arc 的弹出层通过 `ArcZoom` 跟随 uiScale，不需要再包一层 `ZoomHost`，也不计入 `UiZoomTests` 对 `motion:Popover` 的计数。

## 12. 本地化

在 `strings.tsv` 中新增以下键，然后运行 `python3 -I tools/gen_strings.py`。

| 键 | en-US | zh-CN |
|---|---|---|
| `ProjectsSection` | Projects | 项目 |
| `CreateProject` | Create project | 创建项目 |
| `EditProject` | Edit project | 编辑项目 |
| `ProjectDialogDescription` | A project can include several folders; the agent can read and edit all of them. | 项目可以包含多个文件夹，Agent 可以读写其中的所有文件。 |
| `ProjectName` | Project name | 项目名称 |
| `ProjectNamePlaceholder` | New project | 新项目 |
| `ProjectDevice` | Device | 设备 |
| `ThisComputer` | This computer | 此电脑 |
| `SourceFolders` | Source folders | 源文件夹 |
| `AddFolder` | Add folder | 添加文件夹 |
| `PrimaryFolder` | Primary | 主要 |
| `MakePrimary` | Make primary | 设为主要 |
| `RemoveFolderFormat` | Remove {0} | 移除 {0} |
| `PrimaryFolderTip` | TKCode looks for AGENTS.md, .agents and .mcp.json in the primary folder, and new sessions start there. | TKCode 在主文件夹中查找 AGENTS.md、.agents 和 .mcp.json，新会话也从这里启动。 |
| `ProjectCreateAction` | Create project | 创建项目 |
| `ProjectSaveAction` | Save | 保存 |
| `ProjectSaveFailed` | Couldn't save the project | 项目保存失败 |
| `ProjectRemove` | Remove project… | 移除项目… |
| `ProjectRemoveTitleFormat` | Remove {0}? | 移除 {0}？ |
| `ProjectRemoveText` | This only removes the project from TKCode. Files on your computer and existing sessions are kept. | 这只会从 TKCode 中移除该项目，电脑上的文件和已有会话不会被删除。 |
| `ProjectPin` | Pin project | 置顶项目 |
| `ProjectUnpin` | Unpin project | 取消置顶 |
| `ProjectNewSessionFormat` | New session in {0} | 在 {0} 中新建会话 |
| `NoProjects` | No projects yet | 还没有项目 |
| `SessionsInProjectFormat` | Sessions · {0} | 会话 · {0} |
| `ClearProjectFilter` | Show all sessions | 显示全部会话 |
| `FolderMissing` | Not found | 不存在 |
| `PrimaryFolderMissing` | The primary folder doesn't exist | 主文件夹不存在 |
| `NestedFolderWarningFormat` | {0} is inside {1} | {0} 位于 {1} 之内 |
| `PrimaryChangedNote` | Existing sessions keep running in the previous primary folder; new sessions start in the new one. | 已有会话仍在原来的主文件夹中运行，新会话从新的主文件夹开始。 |
| `SessionFolderSuffix` | This session | 本会话 |
| `RemoveSessionFolder` | Remove from this session | 从本会话移除 |
| `AddDirCommandDescription` | Add a folder to this session | 为本会话添加文件夹 |
| `AgentNoAdditionalDirectories` | This agent can't access additional folders | 此 Agent 不支持附加文件夹 |

**复用现有的键**：`ExplorerShowInFinder` / `ExplorerShowInExplorer`、`CopyPath`、`Cancel`、`Close`、`SessionsLoadMore`。

**可以删除的键**：`AllProjects`。它只被侧栏筛选使用，实施时再确认一遍。

## 13. 实施阶段

| 阶段 | 内容 | 依赖 |
|---|---|---|
| P1 数据层 | `TKCode.Config.ProjectsFile` 与 JSON context；迁移；路径规则；`IsSensitive` 加入 projects.json；Desktop 用 `ProjectService` 取代 `WorkspaceService`（`ProjectInfo` 带 `Folders`），分支读取移到 `GitRepository`；state.json 改为 `lastProjectId` / `recentProjects`，`permissionModes` 按 id 保存 | — |
| P2 Host 与 RPC | 会话头加 `ProjectId`；`SessionDirectoriesEntry`；会话索引与按项目查询（含回退归属）；创建、打开、fork 带上项目与临时文件夹；`WorkspaceRoots.Sync` 与每轮同步；`session/addDirectory` / `removeDirectory`；多根文件索引；§7 的提示词（审过之后再写进代码）；协议版本升到 25 | P1 |
| P3 外部运行时 | Claude `--add-dir` 与变化后重启；Codex 每轮 `writableRoots`；ACP `additionalDirectories` 与重新 resume | P2 |
| P4 侧栏 | 项目分区、筛选、行操作（Arc 菜单）、会话区标题与 ✕；删除筛选模式的选择器；托盘、Dock、跳转列表、`--open-project`、命令面板 | P1、P2 |
| P5 弹窗 | 抽出 `ScaledModal`（`ExplorerFileDialog` 改用它）；`ProjectDialog`（创建 / 编辑 / 移除）；把三个入口都接到弹窗 | P1 |
| P6 多根 UI | 文件树多根；@ 跨文件夹；分支选择器按文件夹；`/add-dir`；Markdown 图片的允许目录 | P2、P4 |
| P7 收尾 | 更新文档（architecture.md 的侧栏与命令面板、settings.md 的 projects.json、system-menus.md、builtin-agent-tools.md §10、external-agent-runtimes.md、composer.md 中 @ 的规则）；重新生成 README 截图；AOT 发布要求 0 个 IL 警告，并用 `--smoke` 验证 | 全部 |

P3 与 P4、P5 可以并行。

## 14. 测试

- **P1**：
  - 迁移：从 `workspaces`、`lastWorkspace`、`recentWorkspaces` 和按路径保存的 `permissionModes` 迁移；
  - 路径：去重、嵌套检测、Windows 和 macOS 下的大小写；
  - 保存：原子写入，以及写入过程中失败的情况；
  - 安全：projects.json 的读写需要审批。
- **P2（TKCode.AgentHost.Tests）**：
  - 修改项目文件夹后，下一轮出现 `cwd` 补丁，并且审批判定跟随变化；子 Agent 一起更新；
  - 一轮进行中不同步；
  - 按项目查询：`ProjectId` 命中、旧会话按 cwd 回退、项目移除后的归属；
  - fork 复制项目和临时文件夹，临时文件夹在重新打开会话后恢复；
  - 多根文件搜索的合并与去重；
  - RPC 端到端测试，包括针对 AOT 发布的 Host（`TKCODE_TEST_HOST_EXE`）运行。
- **P3**：
  - Claude 的启动参数里包含 `--add-dir`，文件夹变化后用 resume 重启；
  - Codex 的 `turn/start` 带上 `writableRoots`；
  - ACP 只在 Agent 声明了能力时发送列表，变化后重新 resume。

  这些都用现有的假 CLI 和假 Agent 测试夹具，不跑真实的轮次。
- **P4 / P5 / P6（TKCode.Desktop.Tests 无头快照）**：
  - 侧栏：选中、筛选、清除、悬停操作、菜单、项目超过 8 个、文件夹不存在；
  - 弹窗：创建和编辑、警告与错误，各拍中文、英文、深色、浅色以及 uiScale 1.5 的快照；
  - 文件树：多根；
  - @ 补全：带文件夹前缀；
  - 分支弹层：多仓库；
  - 交互测试：添加文件夹、设为主要、移除、校验、提交失败后保留输入。
- **需要更新的现有测试**：SessionSidebarTests、SystemMenuTests、MainWindowSnapshotTests、UiZoomTests（`motion:Popover` 少了一个）、PermissionMemoryTests、BranchRefreshTests、ReadmeScreenshotTests 和文件树的四组测试。大约 20 个测试用 `Workspaces.Add` 准备数据，改为统一的辅助方法 `TestProjects.Seed(path)`。SmokeMode 同步修改。

## 15. 未决问题

1. **§7 的提示词草稿**需要审阅。
2. **统一项目根目录**的三种算法（记忆、资产与提示词、MCP），另开任务处理。
3. **Arc 的 `Dialog`**（`ModalOverlay`）不跟随 `ArcZoom`。按「先完成 Arc 再发版」的规则，另开任务修复。

已经确定的问题（用词、搜索范围、审批卡片、自动发现）见 §2。

## 16. 实现状态（2026-10-22）

P1–P6 已实现，P7 的文档已更新。

**测试**：
- Agent.Tests 的 `ProjectTests`；
- AgentHost.Tests 的 `ProjectSessionTests`；
- Runtimes.Tests 的 `ProjectDirectoriesTests`，以及 `CodexFixtureTests.AdditionalDirectories_*`；
- Desktop.Tests 的 `ProjectSidebarTests`、`MultiFolderTests`，以及调整过的 SystemMenu / PermissionMemory / PlanReview / MainWindowSnapshot / ExplorerFileDialog 测试。

**与设计稿不同的地方**：
- **托盘菜单**：条目仍携带主文件夹路径，没有改成项目 id（§8.4）。这样与 `--open-project` 共用一套查找逻辑。
- **会话归属的回退顺序**：按 projects.json 中的顺序，不按侧栏顺序（§5.3）。否则置顶会改变旧会话的归属。
- **侧栏空状态**：没有项目时显示一行文字「还没有项目」，没有用 `arc:EmptyState`。EmptyState 的图标、标题与内边距在 256px 宽的侧栏里过重（§8.3 允许这样退回）。
- **`ScaledModal` 的宽度**：取 Arc 预设宽度（Small 360 / Medium 440 / Large 560）与窗口可用宽度中较小的一个。原来的写法用本地 `MaxWidth` 盖住了预设，弹窗会撑满窗口宽度。文件树的新建 / 重命名 / 移到废纸篓弹窗因此也变成了 440 宽。

**还没做的**：
- **ACP 不支持附加文件夹时的说明**：§6.8 的「此 Agent 不支持附加文件夹」，现在只有字符串，界面上还没有显示的位置。
- **嵌套文件夹的文件树显示**：嵌套的文件夹在文件树里会出现两次；搜索结果已经按绝对路径去重。

