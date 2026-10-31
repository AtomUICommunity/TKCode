# 消息编辑、回退与分叉

调研和优先级见 [message-features-report.md](message-features-report.md) §6 R5。出错重试（R1）另写，不在本文。

## 1. 目标

1. **编辑并重发**：在用户消息上修改内容再发送，从这条消息起重新来过。
2. **回退到这里**：把对话、代码或两者恢复到这条用户消息之前，原提示回到输入框。
3. **从这里分叉**：在某个助手回合结束处另开一个会话继续，原会话不动。

## 2. 已确认的决策（用户，2026-10-30）

| 问题 | 决定 |
|---|---|
| 实现方式 | 一律"在消息处分叉成新会话"，不做同一会话内的分支和"< 1/2 >"版本切换 |
| Arc | 升级到 0.3.2-preview.2 并引入 `TKCode.Arc.Special` |
| 原会话 | 保留在会话列表里；新会话顶部显示「分叉自…」 |
| 只恢复代码后 | 追加系统消息告诉 Agent 哪些文件被回退；现有整回合 Revert 和 Changes 面板逐文件 Revert 一并改成这样 |
| 文件回退部分失败 | 继续分叉，Toast 列出没能回退的文件 |

理由：
- 历史只追加（AGENTS.md），分叉成新会话不改任何已有记录，也不用在运行中的 Agent 里重建上下文。
- 同类产品内部也是这么做的：Codex app-server 用 `thread/fork(lastTurnId)` 取代了 `thread/rollback`；Claude Code 桌面版的回退是 `--fork-session --resume-session-at`。

## 3. 现状

- **会话文件**是 id/parentId 的只追加树（`Sessions/SessionEntry.cs`），但 `SessionStore.Read` 把最后一条记录当作叶子，所以界面上只有一条分支。
- **`ForkSessionAsync`**（`AgentHostServer.cs` L232）把当前分支整条复制到同项目的新会话，依次复制：
    - 目录、计划文件、子 Agent 记录；
    - 模型和参数；
    - 自定义标题；
    - 压缩前的消息、压缩记录和之后的消息。

  外部运行时的会话会抛异常。RPC 参数是 `SessionFileParams(FilePath)`。
- **历史下标**：`AgentSession.History` = 本次打开后压缩掉的消息 + `Agent.Messages`，下标只增不减，分页依赖它。但是：
    - `HistoryMessageDto` 不带下标，`ToHistory` 还会跳过系统消息等，所以 Desktop 不能靠数数推出下标；
    - `MessageStarted` 事件也不带下标。
- **文件回退由 Desktop 驱动**：`RevertFileRequest(Path, ExpectedSha256, RestoreText)`，Host 先校验 SHA-256。Agent 不知道文件被回退了（现有的整回合 Revert 也是这样）。
- **消息还原成草稿**：`InputHistory.ToDraft(UserMessageItemViewModel, …)` 已经能把用户消息（含 tag 和图片）还原成可编辑的 `ComposerDraft`（[composer-drafts.md](composer-drafts.md) §5.5）。
- **提示缓存**：`prompt_cache_key` 取会话 id（`PromptCache.Key(options.SessionId)`），新会话第一轮会换一个缓存键。

## 4. 交互

### 4.1 入口

| 位置 | 操作 |
|---|---|
| 用户消息，悬停出现的操作条 | 复制 · **编辑** · **回退…**（下拉三项，见 4.3） |
| 助手回合页脚（复制按钮旁） | **从这里分叉** |

以下情况不显示入口：
- 外部运行时（Claude Code / Codex / ACP）的会话；
- 子 Agent 记录；
- 不是用户输入的消息（`Source` 不为空，如后台任务通知）。

Agent 运行中入口置灰，提示"先停止当前回合"。

### 4.2 编辑并重发

1. 点"编辑"：原提示（含 tag、图片）用 `InputHistory.ToDraft` 回填到输入框。输入框上方出现一条横幅："正在编辑第 N 条消息，发送后在新会话中继续，原会话保留 · 取消"。
2. 点"取消"：恢复编辑前的草稿。
3. 发送时：
    - 如果这条消息及之后的回合改过文件，先问"这条消息之后改过 4 个文件，要回退吗？"，三个按钮：**回退文件 / 保留文件 / 取消**。这与 Cursor、Cline、Roo 的二选一相同，另加取消。
    - 之后按 §5.2 分叉（截止到这条用户消息之前），切到新会话，把编辑后的内容作为第一条新消息发出。

用输入框而不是在气泡里原地编辑，是为了直接复用 tag、附件、@ 补全和草稿逻辑。

### 4.3 回退到这里

这是一个下拉菜单，有三项，与 Claude Code 的 rewind 菜单一致：

| 选项 | 做什么 |
|---|---|
| 恢复对话和代码 | 回退这条消息起各回合改过的文件（§5.3），再分叉（截止到这条消息之前），原提示回填到输入框（不发送） |
| 只恢复对话 | 分叉 + 回填，不动文件 |
| 只恢复代码 | 只回退文件，不分叉，留在当前会话 |

每项旁边用一行小字写清楚范围："只回退 TKCode 编辑工具改过的文件；命令行、MCP 和子 Agent 改的文件不会恢复"（Claude Code、Kiro 的文档都写明了这一点）。

### 4.4 从这里分叉

在助手回合页脚点"从这里分叉"：分叉的截止点是这个回合结束处（下一条用户消息之前），不动文件，然后切到新会话。之后弹出 Toast："已在新会话中继续 · 返回原会话"。

### 4.5 分叉出来的会话

- 原会话保持原样，留在会话列表里。
- 新会话记录它分叉自哪里（§5.2 的 `ForkedFrom`）。记录顶部显示一条分隔线："分叉自「原标题」"，点击打开原会话。
- 标题沿用现有 fork 的做法：有自定义标题就复制，否则按第一条用户消息自动生成。

## 5. Host 改动

### 5.1 消息下标

| 位置 | 改动 |
|---|---|
| `HistoryMessageDto` | 新增 `int Index`：这条消息在 `AgentSession.History` 中的位置 |
| `MessageStarted` | 新增 `int? Index`：用户消息追加时的位置（助手消息为 null） |

Desktop 把 `Index` 存在 `UserMessageItemViewModel` 上。不用时间戳或文本去匹配，因为不保证唯一。

### 5.2 `ForkSessionAsync` 支持截止点

RPC `session/fork` 的参数改为 `ForkSessionParams(FilePath, Before)`；`Before` 为 null 时与以前完全一样（整条复制）。按 AGENTS.md 改了四处：接口 `ForkSessionAsync(filePath, before)`、`RpcMethods`（方法名不变）、`RpcJsonContext`、proxy 与 dispatcher。

复制完整历史 `[0, Before)`。设 `E` 为最近一次压缩之前的消息数：**截止点必须在最近一次压缩之后（`Before > E`）**，新会话 = 压缩前全部 + 压缩记录 + 压缩后的 `[0, Before − E)`。

压缩之前的消息不能分叉：要复制它们，就得重建当时那次压缩的摘要；Claude Code 压缩之后也不能回退到压缩之前。界面上，压缩分隔线之前的消息不显示编辑 / 回退 / 分叉。

截止点必须落在回合边界，Host 校验以下任一条件成立，否则拒绝：
- 完整历史第 `Before` 条是用户输入的 `UserMessage`（`Source == null`）；
- `Before` 等于历史长度（整条复制）。

这保证不会切在工具调用和它的结果之间。其他规则：
- 会话正在运行时拒绝。
- 外部运行时照旧报错。
- 新会话追加一条 `ForkedFromEntry(SessionId, FilePath, Title, Before)`。它像 `SessionMetaEntry` 一样不属于对话树，`SessionDto.ForkedFrom` 带出来给顶部分隔线用。

### 5.3 代码回退（Desktop 驱动）

- **算法**：取截止回合及之后各回合的文件改动，按文件合并：
    - 恢复内容 = 最早那个回合改之前的内容；
    - 期望 hash = 最后一次改之后的 hash。

  逐个调用现有的 `RevertFileAsync`，复用 Changes 面板逐文件回退的逻辑。
- **数据完整**：截止点是用户点到的消息，分页只会往前加载，所以它之后的回合都已在 Desktop 里，数据齐全。
- **部分失败**（文件在外部被改过）：分叉照常进行，Toast 列出"已回退 3 个文件，1 个在外部被改过，未回退"。
- **通知 Agent**：凡是回退后留在当前会话的情况（"只恢复代码"、整回合 Revert、Changes 面板逐文件 Revert），`RevertFileRequest` 都带上会话 id。Host 把路径记在 `ToolContext.RevertNotices` 里，下一批输入前（`Agent.ContextUpdates`）合成一条系统消息追加进历史，只发一次。分叉出去的新会话不需要，因为它的历史本来就停在回退点之前。

  发给模型的文字（`RevertNotices.Text`，**请审阅**）：

  ```text
  <system-reminder>
  The user reverted these files to how they were before your earlier edits, so those edits are gone. Read a file again before relying on its content or editing it:
  - /abs/path/a.cs
  - /abs/path/b.cs
  </system-reminder>
  ```

  编辑工具本来就按内容比对检查"先读后改"（`FileStateTracker.CheckFresh`），所以就算模型忽略这条提示，也不会覆盖用户回退后的文件。

## 6. Desktop 改动

| 文件 | 改动 |
|---|---|
| `ViewModels/TranscriptItems.cs` | `UserMessageItemViewModel.Index`；`CanRewind`（内置 Agent、用户输入、不在运行） |
| `Views/UserMessageBubble.axaml` | 悬停操作条：`arc:CopyButton`、编辑、回退下拉（`arc:DropdownMenu`） |
| `Views/TranscriptTemplates.axaml` | 助手页脚加"从这里分叉" |
| `ViewModels/ConversationViewModel.cs` | `EditAsync`、`RewindAsync(mode)`、`ForkFromAsync`；分叉后切换会话、回填或发送 |
| `ViewModels/ComposerViewModel` 一带 | 编辑横幅与取消（复用排队卡片的样式） |
| 新对话框 | 文件回退三选一（`arc:DialogSurface`） |
| `Localization/strings.tsv` | 所有新文案，跑 `tools/gen_strings.py` |

## 7. 实施阶段

1. Host：消息下标（§5.1）+ 带截止点的 fork（§5.2）+ `ForkedFrom`，并写 Host 测试。
2. Desktop："从这里分叉"，最简单，用来验证整条链路。
3. Desktop："编辑并重发"和"只恢复对话"。
4. Desktop：代码回退和三项回退菜单。
5. AOT 发布 0 个 IL 警告 + `--smoke`。

## 8. 测试

- **Host**：
    - 截止点校验：用户消息、末尾、工具结果中间（应拒绝）、非用户输入（应拒绝）；
    - 压缩前后两种复制方式；
    - 外部运行时拒绝；
    - `ForkedFrom` 写入并带出。
- **RPC 端到端**：带截止点的 fork。
- **Desktop 无头快照**：
    - 悬停操作条、回退下拉、编辑横幅、文件对话框的截图；
    - 编辑流程把草稿回填到输入框；
    - 分叉后切到新会话，顶部显示分隔线。

## 9. 实现状态（2026-10-30）

已按本文实现：
- Host：消息下标、带截止点的 fork、`ForkedFrom`、回退通知。
- Desktop：用户消息悬停的编辑 / 回退（三项）、助手页脚的"从这里分叉"、编辑横幅、文件回退确认、顶部"分叉自…"、Toast。

测试：
- `tests/TKCode.Agent.Tests/RpcForkTests.cs`：真实 Host 进程。
- `tests/TKCode.Desktop.Tests/MessageRewindTests.cs`：无头 MainWindow + Fake 厂商，含快照 `fork-from-reply`、`edit-message-banner`。

待评审：§5.3 发给模型的回退通知文字。

## 10. 不做的事

- 同一会话内的分支、"< 1/2 >"版本切换、分支图。
- 编辑助手消息或推理：思考签名必须原样回放。
- 整仓快照，以及回退命令行、MCP、子 Agent 改的文件。Zed 在 2026-10-26 因为性能问题移除了整仓快照。
- v1 不支持外部运行时会话的编辑、回退、分叉。
