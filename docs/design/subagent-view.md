# 子 Agent：卡片与右侧转录标签

关联文档：[builtin-agent-tools.md](builtin-agent-tools.md) §9、[external-agent-runtimes.md](external-agent-runtimes.md)、[plan-mode.md](plan-mode.md) §8、协议 [claude-code-stream-json.md](../protocols/claude-code-stream-json.md)、[codex-app-server.md](../protocols/codex-app-server.md)

## 1. 目标

1. 主转录里，子 Agent 显示为一张独立的小卡片，只有基本信息：**Agent 名称、所用模型、后台标记**（后台运行时才有）。卡片不订阅进度，不显示统计。
2. 点击卡片，在右侧工作台打开这个子 Agent 的标签。标签里是它的完整消息列表，**显示方式与主 Agent 完全一样**（同一套模板：用户消息、思考、活动组、正文 Markdown、错误卡片……），只是没有输入框、排队、审批面板等交互。
3. 运行中的子 Agent 实时更新；已结束的、重新打开会话后的、Host 重启后的，都能打开。
4. 内置 Agent、Claude Code、Codex 三种运行时都支持。ACP 没有子 Agent 协议，不做。
5. 顺带修复：父会话是完全授权时，子 Agent 仍然要求审批。

## 2. 已确认的决策（用户，2026-10-21）

| 问题 | 决定 |
|---|---|
| 卡片内容 | 只显示 Agent 名称、模型、后台标记；其余信息在右侧标签里看 |
| 标签组织 | 每个子 Agent 一个标签；再次点击激活已有标签；切换会话时关闭其他会话的子 Agent 标签 |
| 运行时范围 | 内置 + Claude Code + Codex |
| 审批 | 子 Agent 一律跟随父会话的权限档位（Plan 模式下也不再单独执行只读命令规则） |

## 3. 现状

- **卡片**：`agent` / `Task` 工具调用归为 `ToolCategory.Task`，只是活动组里的一行（`ToolStepView`），点击原地展开统计和结果。`ParsedToolDetails` 丢弃了 `SubagentDetails` 的 `agentId` / `agentType` / `background`，详情里也没有模型。
- **数据**：内置子 Agent 的完整对话写在 `~/.tkcode/subagents/<父会话 id>/<时间>_<名字>.jsonl`，但它的事件不经过 `SessionManager.Register`，**一个都不转发给 UI**；也没有任何 RPC 能读这个文件。父会话只收到 `agent` 工具调用上的进度（`ToolExecutionUpdated` + `SubagentDetails`）。
- **Claude Code**：没有传 `--forward-subagent-text`；`parent_tool_use_id` 非空的帧全部丢弃；`result` 之后（后台子 Agent）到达的帧也被丢弃；`task_*` 系统帧被忽略。
- **Codex**：`threadId` 不是主 thread 的通知全部丢弃；`collabAgentToolCall` 显示为通用工具，参数为空，结果只有 "Done."。
- **外部运行时的 Details 不落盘**：`TurnWriter.ToolCompletedAsync` 构造的 `ToolResultMessage` 没有带 `Details`，重新打开会话后丢失（本文顺带修复）。
- **转录模板**：全部内联在 `MainWindow.axaml` 的 `TranscriptView.DataTemplates` 里，并引用 `MainWindow` 的事件处理函数，无法复用。
- **审批**：`SessionManager.ApproveAsync` 对 explore / plan 子 Agent 传入 `readOnly: true`，非只读命令在完全授权下也询问（plan-mode.md §8 的规则）。

## 4. 标识

子 Agent 在 UI 与 Host 之间用 **（父会话 id，工具调用 id）** 标识。工具调用 id 是卡片天然持有的；子 Agent 结束、Host 重启后依然稳定。

| 运行时 | 卡片对应的工具调用 | Host 内部的子 Agent 键 | 两者如何关联 |
|---|---|---|---|
| 内置 | `agent` | `agentId`（子会话 id） | 派生时 `SubagentRequest.ToolCallId` |
| Claude Code | `Agent` / `Task`（tool_use id） | `parent_tool_use_id` | 相同 |
| Codex | `collabAgentToolCall`（`tool == spawnAgent`）的 item id | 子 thread id | `receiverThreadIds[0]`；子 thread 的通知可能先于关联到达，先按 thread id 缓存 |

`send_message` / `sendInput` 让已结束的子 Agent 继续运行时，仍写入同一份转录，卡片和标签不变。

## 5. Host

### 5.1 `SubagentHub`

新增 `TKCode.AgentHost/SubagentHub.cs`，按父会话管理子 Agent 转录，三种运行时共用：

```
SubagentTranscript
  Key, ParentSessionId, ToolCallId?, Name, Model, Background, FilePath
  History: IReadOnlyList<Message>     // 内置：子会话的 History；外部：Hub 自己累积
  Replay: List<SessionEvent>          // 最近一条消息结束以后的事件（见 5.2）
  IsRunning, Seq, Watched
```

- **内置**：`AgentCoordinator.SpawnAsync` 订阅子会话事件后交给 Hub（`Hub.Attach(parentId, toolCallId, agentId, session)`）。持久化仍由子会话自己完成。
- **外部**：运行时会话通过 `IRuntimeHost.BeginSubagent(RuntimeSubagentInfo)` 拿到一个 `TurnWriter`，把子 Agent 的帧写进去；`TurnWriter` 产生的 `AgentEvent` 进入 Hub，`MessageEndEvent` 时累积进 History 并写入 `SessionStore`（文件规则同内置）。`EndSubagent(key, status)` 结束。
- **索引**：每个父会话目录下有 `index.jsonl`，每行 `{toolCallId, agentId, file}`，关联建立时追加。按工具调用 id 查找转录先查内存，再查索引；旧的内置转录没有索引，回退为用父会话工具结果 Details 中的 `agentId` 匹配文件头的 `sessionId`。
- **事件映射**：沿用 `SessionManager.Map`，与主会话完全相同。
- **生命周期**：父会话关闭时 Hub 释放它的所有转录。删除会话时一并删除 `subagents/<id>/`；分叉会话时复制该目录（`index.jsonl` 原样复制，工具调用 id 不变）。

### 5.2 打开时的一致性

标签可能在子 Agent 正在输出时打开。打开请求在转录的锁内同时完成三件事：取已结束消息的快照（History），取 Replay，然后置 `Watched = true`。此后的事件在同一把锁内分配序号并转发。

Replay 是“最近一次 `MessageEndEvent` 之后”的全部映射事件：正在流式输出的消息的 `MessageStarted` 与增量、正在执行的工具的 `ToolExecutionStarted` 与最新一条 `ToolExecutionUpdated`（同一工具只保留最新一条）。内置 Agent 在发出 `MessageEndEvent` 之前已把消息加入 `Messages`（`Agent.cs`），所以 History 与 Replay 不重不漏。UI 先 `LoadHistory`，再按顺序 `Apply` Replay。

### 5.3 RPC（协议版本 +1）

| 方法 | 方向 | 参数 / 结果 |
|---|---|---|
| `subagent/open` | UI → Host | `SubagentOpenParams(SessionId, ToolCallId)` → `SubagentOpenResult(AgentId, Name, Model?, History, Replay, IsRunning)`；找不到时报错 |
| `subagent/close` | UI → Host | `SubagentCloseParams(SessionId, ToolCallId)`：停止转发 |
| `subagent/event` | Host → UI（通知） | `SubagentEventNotification(SessionId, ToolCallId, Seq, Event)`，`Event` 与 `session/event` 的 `SessionEvent` 相同 |

只转发被打开的子 Agent：卡片不需要进度，没打开的标签不占带宽。按 AGENTS.md 的规定，新增方法同时修改接口、`RpcMethods`、`RpcJsonContext`、代理与分发。

### 5.4 卡片信息：`SubagentDetails`

`SubagentDetails` 增加 `Model`（显示名）。三种运行时都在工具调用**一开始**就推送一次 `ToolExecutionUpdated`，携带 `SubagentDetails`，卡片立即拿到名称、模型、后台标记；结束时 `ToolExecutionEnded` 再带一次（落盘，重新打开会话后卡片信息不丢）。

| 字段 | 内置 | Claude Code | Codex |
|---|---|---|---|
| 名称 | 唯一名（`name` 或类型的 slug） | 参数 `name` → `subagent_type` → `general-purpose` | 子 thread 的 `agentNickname` → `agentRole` → `agent` |
| 模型 | 实际使用的模型名 | `tool_use_result.resolvedModel` → 参数 `model` → 父会话模型 | item `model` → `subAgentActivity.model` → 父会话模型 |
| 后台 | 参数 `run_in_background` | 参数 `run_in_background`，或结果为 `async_launched` / `detachedToolCall` | 不显示（Codex 的子 Agent 本来就是异步的，标记没有区分意义） |

### 5.5 Claude Code

- 启动参数加 `--forward-subagent-text`（CLI ≥ 2.1.211；协议文档 §2.2 已列出，之前漏传）。
- `HandleFrameAsync`：`parent_tool_use_id` 非空的 `assistant` / `user` / `stream_event` 帧交给该子 Agent 的 `TurnWriter`（会话级状态，不随主轮次结束而丢弃）。子 Agent 的第一条 `user` 帧是它的 prompt，按用户消息写入。嵌套子 Agent（`parent_tool_use_id` 指向另一个子 Agent 内部的调用）归入最外层子 Agent 的转录。
- 结束：前台子 Agent 以父会话里对应的 `tool_result` 结束；后台子 Agent 以 `task_notification`（`completed` / `failed` / `stopped`）或 `task_updated`（`killed`）结束。只结束已知的 Agent 调用（后台 Bash 也用这些帧）。
- 主轮次没有在运行时（后台子 Agent 在 `result` 之后继续工作）到达的子 Agent 帧照常写入。
- `can_use_tool` 带 `agent_id` 时，审批标题前缀为 `[子 Agent 名]`。
- Agent 工具的 `tool_use_result`（`AgentOutput`）规范化为 `SubagentDetails`。

### 5.6 Codex

- `collabAgentToolCall` 中 `tool == spawnAgent` 的显示为工具 `spawn_agent`（归入 `ToolCategory.Task`），参数取 `prompt` / `model`；其余 collab 工具显示为通用步骤，参数同样补上。
- `thread/started` 的 `parentThreadId` 等于主 thread 时登记子 thread（记下 `agentNickname` / `agentRole` / `model`）。
- 子 thread 的 `item/*`、增量与 `turn/*` 通知交给该子 Agent 的 `TurnWriter`，复用主 thread 的条目映射（`OnItemStartedAsync` / `OnItemCompletedAsync`）。子 thread 的 `turn/completed` 结束一次运行；被 `sendInput` 唤醒时再开始下一次，写入同一份转录。
- 子 thread 的审批请求（`threadId` 不是主 thread）标题前缀为 `[子 Agent 名]`。

### 5.7 审批（修复）

`SessionManager.ApproveAsync` 只在**主会话**处于只读上下文（Plan 模式）时传 `readOnly: true`；子 Agent 一律按父会话的档位判定。explore / plan 子 Agent 的工具集仍然没有 write / edit，但 bash 跟随父会话档位：完全授权直接放行，自动批准按自动批准处理。plan-mode.md §8 相应修改。

## 6. Desktop

### 6.1 转录模板抽取

- 新增 `Views/TranscriptTemplates.axaml`：根元素为 `DataTemplates`（x:Class，编译 XAML，AOT 安全），内容为原 `MainWindow.axaml` 中的全部转录模板。
- 模板中的事件处理（`OnMessageImageTapped`、`OnCopyMessageClick`）移入 `TranscriptTemplates.axaml.cs`，用 `TopLevel.GetTopLevel(sender)` 取窗口与剪贴板。
- 新增 `Controls/SessionTranscript`（`UserControl`）：包含 `sm:TranscriptView` 与 `TranscriptAdapter`，构造时加入 `TranscriptTemplates`，提供 `Attach(SessionViewModel?)`。主窗口继续使用自己的 `TranscriptView x:Name="Transcript"`（滚动、分页、拖放等行为不动，测试按名字查找），只把模板改为代码加入同一个 `TranscriptTemplates`；右侧标签使用 `SessionTranscript`。

### 6.2 卡片

- `TranscriptAdapter.IsCard` 增加：`ToolCategory.Task` 且能识别为子 Agent 的工具调用（内置 `agent`、Claude `Agent` / `Task`、Codex `spawn_agent`）提为独立卡片，不进活动组。
- 连续的子 Agent（之间没有正文、思考或其他工具）合为一个条目 `SubagentGroupViewModel`（`SubagentGroupView`）：标题“启动了 N 个子 Agent”（Bot 图标），下面在引导线旁逐行列出，行高 28、行距 2，不再每个子 Agent 占一个转录条目（条目间距会让多个子 Agent 显得稀疏）。组按第一个工具调用缓存。
- `SubagentCardView`：组里的一行 —— 状态（前台运行中转圈、失败警示、其余小圆点）、名称、模型（`arc:Badge`）、后台标记（`arc:Badge` Info，仅后台时显示）、右侧 `↗` 图标。整行是一个 `arc:Button`（Ghost）负责悬停、按下、焦点与点击；Arc 按钮的标签区按内容宽度居中，所以行内容叠在按钮上方且不接收指针。数据来自 `ToolCallBlockViewModel` 的参数与 `SubagentDetails`，没有就回退到参数。
- 点击：`SessionViewModel.SubagentRequested(toolCallId)` → `WorkbenchViewModel.OpenSubagent(session, card)`。

### 6.3 右侧标签

- `WorkbenchTabKind.Subagent`；`SubagentTabViewModel : WorkbenchTabViewModel, IDisposable`，持有（父会话 id，工具调用 id）与一个子 `SessionViewModel`（`IsSubagent = true`：不生成本轮改动卡片，不参与排队）。
- 打开：已存在则激活；否则加标签，调用 `subagent/open`，`LoadHistory` + `Apply(Replay)` + `SetRunning`。`subagent/event` 由 `ConversationViewModel` 按（会话 id，工具调用 id）路由到对应标签。
- 标签标题：名称；图标 `Bot`，运行中显示转圈。关闭或会话切换时 `subagent/close` 并释放。
- 内容：`SessionTranscript`，没有输入框。

## 7. 实施与验证

| 阶段 | 内容 | 验证 |
|---|---|---|
| P1 审批 | `ApproveAsync` 只对主会话传只读；文档 | AgentHost 测试：完全授权下 explore 子 Agent 执行非只读命令不询问；主会话 Plan 模式仍询问 |
| P2 Host 内置 | `SubagentHub`、Replay、索引、RPC 三个方法、`SubagentDetails.Model`、删除 / 分叉时处理目录、`TurnWriter` 落盘 Details | AgentHost 测试：运行中打开（Replay 不重不漏）、结束后打开、Host 重启后从索引打开、旧转录回退；RPC 端到端 |
| P3 Desktop | 模板抽取、`SessionTranscript`、卡片、标签与路由 | headless 快照：卡片（前台 / 后台）、标签内转录与主转录一致；现有转录快照测试不回归 |
| P4 Claude | 转发参数、子 Agent 帧路由、结束、审批前缀、Details 规范化 | 帧夹具测试（前台、后台在 `result` 之后、嵌套、审批） |
| P5 Codex | 子 thread 登记与路由、spawn 关联、collab 参数、审批前缀 | 通知夹具测试（子 thread 先于关联到达、sendInput 唤醒） |

全部完成后：AOT 发布 Desktop（0 个 IL 警告），运行 `--smoke`。

## 8. 实现说明（2026-10-21）

| 部分 | 位置 |
|---|---|
| 审批修复 | `SessionManager.ApproveAsync`（只读规则只给主会话）；测试 `AgentHost.Tests/PlanModeTests.ReadOnlySubagents_FollowTheParentsFullAccess` |
| Hub、Replay、索引、RPC | `TKCode.AgentHost/SubagentHub.cs`；`AgentHostServer.OpenSubagentAsync/CloseSubagentAsync`；`RpcMethods.OpenSubagent/CloseSubagent/SubagentEvent`，协议版本 24；删除会话时删除 `subagents/<id>/`，分叉时复制 |
| 内置 | `AgentCoordinator.SpawnAsync` → `Hub.AttachBuiltin`；`SubagentRequest.ToolCallId`、`SubagentHandle.Model`、`SubagentDetails.Model`；前台调用一开始就推送一次 Details |
| 外部运行时契约 | `TKCode.Runtimes/RuntimeSubagent.cs`（`RuntimeSubagent`、`SubagentDetailsJson`）；`IRuntimeHost.OpenSubagent`；`ExternalAgent.SubagentHandler` → `Hub.OpenExternal` |
| Claude | `ClaudeSession`：`RegisterAgentCallsAsync`、`SubagentFor`、`OnSubagentFrameAsync`、`OnTaskSystemAsync`、`AgentResultDetails`、`SubagentOfApproval`；`--forward-subagent-text`；测试 `Runtimes.Tests/ClaudeSubagentTests` |
| Codex | `CodexSession`：`OnThreadStartedAsync`、`OnSpawnAsync`、`OnSubAgentActivityAsync`、`OnChildNotificationAsync`（先到的通知暂存于 `_orphans`）、子线程审批前缀；测试 `Runtimes.Tests/CodexSubagentTests` |
| 顺带修复 | `TurnWriter.ToolCompletedAsync` 把 Details 写进 `ToolResultMessage`（外部运行时重新打开会话后卡片信息不丢）；`ToolProgressAsync` 可带 Details |
| Desktop | `Views/TranscriptTemplates.axaml(.cs)`（共用模板）；`SubagentCardViewModel` / `SubagentCardView`；`SubagentTabViewModel` / `SubagentTabView`；`WorkbenchViewModel.OpenSubagent`、`WorkbenchTabKind.Subagent`、`WorkbenchTabViewModel.IsBusy`；`AgentHostConnection.SubagentEvent`；`SessionViewModel.IsSubagent` / `SubagentRequested` |
| UI 验证 | `Desktop.Tests/SubagentViewTests`（真实 Host + Fake 模型：前台 / 后台卡片、点击打开标签、再次点击只激活、切换会话关闭标签）；快照 `subagent-cards`、`subagent-tab` |

与 §6.1 的差异：没有单独的 `SessionTranscript` 控件，标签直接用 `SubagentTabView`（`TranscriptView` + `TranscriptAdapter` + `TranscriptTemplates`）。

未做：
- Claude 与 Codex 只用协议夹具验证，没有用真实 CLI 跑过带子 Agent 的轮次（会产生费用，需要用户确认）。
- Codex 子线程的 `thread/started` 是否总是推送给父线程的连接没有实测；`spawnAgent` 完成时的 `receiverThreadIds` 同样能登记子线程，作为兜底。
- 子 Agent 转录只在 Host 内存中保留到父会话关闭；之后从 `~/.tkcode/subagents/<父会话>/` 读取，只读、不再转发。
