# Plan 模式：计划文件与审批（`exit_plan_mode` / `enter_plan_mode`）

状态：已实现（2026-10-19，用户确认按文档实现；§20 各点按文档默认处理）。实现与设计的差异见 §21
关联文档：[composer.md](composer.md) §10.3、§11、§18，[ask-user.md](ask-user.md)，[compaction.md](compaction.md)，[builtin-agent-tools.md](builtin-agent-tools.md) §10，[external-agent-runtimes.md](external-agent-runtimes.md)，[protocols/claude-code-stream-json.md](../protocols/claude-code-stream-json.md)

## 1. 目标

- Plan 模式产出一份**可审批的计划**：模型把计划写进计划文件，调用 `exit_plan_mode` 请求审批。审批面板取代输入框，用户在面板里批准、修改或退回。
- **批准后当场执行**：按用户选的方式切换模式和权限，在同一轮里继续，不用再手动切模式、再发一条消息。
- Agent 模式下遇到复杂任务，模型可以用 `enter_plan_mode` 申请先做计划，用户同意后才切换。
- Plan 模式真正只读：除计划文件外不能写；可能修改工作区的命令在任何权限档位下都要审批。
- Claude 运行时的 `ExitPlanMode` 进入同一个审批面板，并修复它与 TKCode 模式状态不同步的问题。

## 2. 已确认的决策（用户，2026-10-19）

| 维度 | 决策 |
|---|---|
| 审批机制 | 照 Claude Code：模型调用 `exit_plan_mode`，Host 阻塞等待（与 `ask_user` 相同）；面板取代输入框；批准后同一轮继续；"继续完善"的反馈作为工具结果交给模型 |
| 计划载体 | 计划文件，放在 `~/.tkcode/plans/`（与 Claude Code 默认 `~/.claude/plans` 一致）；Plan 模式下 write / edit 只放行这个文件 |
| 批准选项 | ① 批准，切到 Agent，权限设为自动批准；② 批准，切到 Agent，权限设为询问；③ 批准并交给 ACP；④ 清空上下文后执行。另外"继续完善（附反馈）"一定会有 |
| 首版附带 | Plan 模式可以派只读子 Agent（explore / plan），按 Claude Code 的分阶段流程工作；`enter_plan_mode` 工具（模型申请，用户确认）；在审批面板里直接编辑计划 |
| 不在首版 | Codex 和 ACP 运行时接入审批面板。Claude 运行时一定接入 |

## 3. 调研结论（2026-10-19）

已核实（官方文档：code.claude.com 的 permission-modes、tools-reference、settings-reference、agent-sdk，以及 CHANGELOG 2.1.211–2.1.301）：

- **进入**：Shift+Tab 在 `default → acceptEdits → plan` 之间循环；用 `/plan` 前缀发一条消息；`--permission-mode plan`；`defaultMode: "plan"`。模型可以调用 `EnterPlanMode`（tools-reference 标注为不需要权限；工具描述却写着需要用户同意）。
- **计划文件**：`plansDirectory` 默认 `~/.claude/plans`，目录在项目外时回退到默认目录。"本会话自己的计划文件"属于受保护路径的例外，是 plan 模式下唯一可写的位置。`ExitPlanMode` 不带计划参数，从文件读取；2.1.285 修了"在同一条回复里写文件并调用 ExitPlanMode 时，钩子和 SDK 回调看到旧计划"的问题，说明 CLI 会把计划附加到发给宿主的 input 里。
- **审批框选项**：
  - "Yes, and use auto mode"：auto 不可用时显示为 "Yes, auto-accept edits"；绕过权限的会话里显示为切换到 BYPASS PERMISSIONS。
  - "Yes, manually approve edits"：切到 `default`。
  - "No, keep planning"：留在 plan，告诉 Claude 要改什么。
  - 设置 `showClearContextOnPlanAccept` 后第一项是 "Yes, clear context and …"：清空对话、只带着计划开始执行；标签后半段是权限模式，并显示规划用掉了多少上下文。
  - Ctrl+G 在外部编辑器里修改计划后再批准。计划审批不会因空闲而自动通过。
- **SDK**：plan 模式下改文件、写入类 shell 命令一律经 `canUseTool`，不受允许规则影响（2.1.212 起，会修改文件的 Bash 在 plan 模式下需要确认）。宿主回复 allow（可带 `updatedPermissions`，其中 `setMode` 直接切换权限模式）或 deny（`message` 模型可见）。SDK 的 `planModeInstructions` 只替换工作流正文，CLI 仍然包上"只读前言"和"ExitPlanMode 约定"结尾。

未核实（本次网络抓取失败，来自记忆，实现前须对照源码或真实 CLI）：

- **Claude Code 的 plan 模式提醒**：前言是"不得做任何修改（计划文件除外）"；工作流分五个阶段：
  1. 用最多 3 个 Explore 子 Agent 并行调研；
  2. 用 Plan 子 Agent 设计方案；
  3. 复核关键文件，用 AskUserQuestion 澄清；
  4. 把最终计划写进文件，只写推荐方案，附上关键文件和验证步骤；
  5. 调用 ExitPlanMode。

  结尾是"回合只能以 AskUserQuestion 或 ExitPlanMode 结束；不要用其他方式询问计划是否可行"。批准后的工具结果包含 "User has approved your plan…"、计划文件路径和 "## Approved Plan"（用户改过时标为 edited）；退出 plan 的提醒带计划文件路径。
- **ExitPlanMode 经 `can_use_tool` 到达时的 input**：大致是 `{plan, planFilePath, allowedPrompts?}`。
- **claude-code-acp 的映射**：ExitPlanMode 映射为 `session/request_permission`，选项 `acceptEdits` / `default` / `plan`，分别对应 "Yes, and auto-accept edits" / "Yes, and manually approve edits" / "No, keep planning"；批准时附带 `setMode`。
- **Codex**：Plan 协作模式下，模型用 `<proposed_plan>` 块输出计划，app-server 把它变成 `plan` 条目；TUI 随后问 "Implement this plan?"，批准后切到 Default 并发送 "Implement the plan."。
- **Cursor**：计划是带待办的 Markdown 文件（`~/.cursor/plans`，可以"保存到工作区"），"Build" 按钮切到 Agent 执行。

## 4. 现状与问题

| # | 问题 | 位置 |
|---|---|---|
| 1 | Plan 模式以一段 Markdown 回复结束回合，提示词禁止用 ask_user 询问计划是否可行；执行要靠用户手动切模式再发消息 | `SessionRole.cs:26-43` |
| 2 | **Plan 模式并不只读**：只读过滤只去掉 write / edit / agent / MCP，bash 还在；自动批准档位下任何命令直接放行，完全授权档位下所有调用直接放行。explore / plan 子 Agent 也一样 | `SessionRole.cs:20,184`，`PermissionPolicy.cs:42,81`，`BuiltinTools.cs:23` |
| 3 | **Claude 运行时状态不同步**：`ExitPlanMode` 没有特殊处理，询问档位下显示成通用审批卡（原始 JSON），其他档位下静默放行。放行后 CLI 已经退出 plan，TKCode 仍认为在 plan，下一轮也不会重发 `set_permission_mode`（读代码推断，未实测） | `ClaudeSession.cs:603-628`、`161-173` |
| 4 | Host 无法通知界面"模式或权限变了"；界面只在 `SetOptionsAsync` 的返回值里刷新选择器 | `SessionEvents.cs`，`ConversationViewModel.cs:1162-1165` |
| 5 | 回合进行中切换模式，要等到下一批用户输入才会更新工具和模式节；工具调用之间不会刷新 | `Agent.cs:245,297,313`，`AgentSession.cs:236-263` |
| 6 | 没有计划文件，转录里也没有计划卡片 | — |
| 7 | 文档过时：composer.md:301 和 `Dtos.cs:159` 的注释里还有 Ask 模式 | — |

## 5. 整体流程

```
用户（Plan 模式）："给 X 加上 Y"
  模型：read / grep / agent(explore ×N，并行) → [ask_user 澄清] → write(计划文件) → exit_plan_mode
    Host：读计划文件 → plan/review 请求 → 界面：审批面板取代输入框
      ├ 批准（①②③）→ Host 写回编辑过的计划 → 切换模式和权限 → 推送 optionsChanged
      │              → 工具结果"已批准 + 计划全文" → 刷新工具和模式节 → 同一轮继续执行
      ├ 批准（④）  → 同上 → 下一次请求前用"交接消息"替换历史（§11）→ 继续执行
      ├ 继续完善    → 工具结果"未批准 + 反馈" → 模型改计划文件 → 再次 exit_plan_mode
      └ Esc / 停止  → "暂不批准，结束本轮" / "Operation aborted."
```

`enter_plan_mode` 方向相反：Agent 模式下模型调用它 → 问答面板问用户"是否先做计划" → 同意后切到 Plan，刷新工具和模式节，同一轮继续调研。

## 6. 计划文件

- **路径**：`~/.tkcode/plans/<会话文件名去掉扩展名>.md`，例如 `~/.tkcode/plans/20261019T101530_<id>.md`，和会话 JSONL 的文件名一一对应。`ToolContext.PlanFile` 提供路径；子 Agent 没有。`TKCODE_HOME` 照常生效。
- **生命周期**：每个会话一个文件，在 Plan 模式下由模型第一次写入时创建。同一会话再次进入 Plan 时沿用这个文件，模式节会提示"已有计划，按需修改"。删除会话时不删除计划文件（用户可能还要看）；fork 出新会话时，如果计划文件存在，就复制成新会话对应的文件。
- **权限**：Plan 模式下，write / edit 的目标等于 `PlanFile` 时，任何档位都免审批；其他路径直接返回错误结果，不弹审批（§12.7）。其他模式下，计划文件就是工作区外的普通文件，按现有策略处理。
- **edit 的先读后改**照常生效：模型修改已有的计划文件前必须先读。
- 不放进项目目录，也就不进 git status（用户选择 2A）。

## 7. 工具

### 7.1 `exit_plan_mode`

- 显示名 `Tool.ExitPlanMode`（"请求批准计划"）；只在 Plan 模式下出现，子 Agent 没有。
- Schema：`{"type":"object","properties":{},"additionalProperties":false}`。计划从 `PlanFile` 读取，与 Claude Code 一致。
- `DescribePermission` 返回 `NoAccess`；`Concurrency = Exclusive`。同一条回复里先写文件再调用它时，串行执行保证读到的是新内容。
- 校验：计划文件不存在或为空时，返回错误结果（§12.3），不打扰用户。
- 通过 `ToolServices.PlanReview`（新增，`IPlanReviewService?`）发出请求，用工具自己的 `cancellationToken` 等待；没有 UI 时返回错误（§12.3）。
- 批准后，由 Host 按选择切换模式和权限（§10），工具再请求刷新上下文（§9）。
- **Details**：`{"kind":"planReview","path":…,"plan":<面板展示的、或用户编辑后批准的全文>,"edited":bool,"outcome":"approved|revise|dismissed|aborted","choice":"auto|ask|acp|clear"?,"feedback":…?}`。计划全文存一份快照，这样计划文件之后再改，转录卡片仍然显示当时的版本。

### 7.2 `enter_plan_mode`

- 显示名 `Tool.EnterPlanMode`（"申请进入 Plan 模式"）；只在 Agent 模式下出现。ACP 模式、Plan 模式、自定义模式和子 Agent 都没有。
- Schema：`{"type":"object","properties":{"reason":{"type":"string","description":"One sentence shown to the user: why planning first helps with this task."}},"additionalProperties":false}`。
- `NoAccess`、`Exclusive`。**任何权限档位下都要用户确认**（用户选择"用户确认后切换"），不会自动通过。
- 确认界面复用 `input/request` 问答面板：只有一个单选问题，标题"进入 Plan 模式？"，问题正文是 `reason`，选项是"先做计划"和"直接执行"，不提供"其他"。这样不需要新的 RPC 和界面。
- 同意后，Host 把模式切到 Plan（与用户自己切换走同一条路径），并推送 `optionsChanged`；工具请求刷新上下文（§9）。

### 7.3 Plan 模式下的 `agent`

- Plan 模式的工具里加回 `agent`、`send_message`、`list_agents`；只能派 `explore` 和 `plan` 两种只读子 Agent（`ToolSet.ReadOnly`）。
- `subagent_type` 缺省时，在 Plan 模式下取 `explore`；传 `general-purpose` 时返回错误结果（§12.7）。schema 不变（工具定义不随模式变形，避免为此再造一份定义）。
- 子 Agent 的只读命令规则见 §8。

## 8. 模式、工具过滤与只读保障

- **Plan 模式的工具**：`["readonly", "write", "edit", "agent", "send_message", "list_agents", "exit_plan_mode"]`。`readonly` 已经包含 ask_user、task_*、web_*、skill 等；write / edit 由 §6 的路径规则限制到计划文件。
- **模式专属工具**：`SessionRole.Allows` 特殊处理两个工具名：`exit_plan_mode` 只在 Plan 模式下允许，`enter_plan_mode` 只在 Agent 模式（包括 `AgentResumed`）下允许，与 `Tools` 列表无关。自定义模式两个都没有。
- **只读命令规则**（修复 §4 问题 2）：执行上下文是"只读"时（主会话处于只读模式），`PermissionPolicy.Evaluate` 对 `Execute` 做如下处理：
  - 只有 `ReadOnlyCommands.IsReadOnly(command) && StaysInWorkspace(...)` 才免审批；
  - 其他命令**在所有档位（包括完全授权）都要询问**，审批卡标题加上"Plan 模式：此命令可能修改工作区"；
  - 只能"允许一次"，不提供也不采用"本会话允许"（防止 Agent 模式下允许过的 `git commit` 在 Plan 模式下直接执行）。

  实现方式是给 `Evaluate` 加一个 `readOnly` 参数，由 `SessionManager.ApproveAsync` 根据会话模式传入。子 Agent（包括 explore / plan）不适用这条规则，一律跟随父会话的权限档位（2026-10-21 用户决定，见 [subagent-view.md](subagent-view.md) §5.7）；它们的工具集仍然没有 write / edit。
- **MCP 工具**：Plan 模式下仍然去掉，不变。
- **用户手动切换**（Shift+Tab、下拉框）仍然可用，和现在一样在下一批用户输入前生效。从 Plan 手动切回 Agent 时，模式节换成 `AgentResumed`，其中带上计划文件路径（§12.5）。

## 9. 回合中途切换模式（agent loop 改动）

`Agent.Tools` / `SystemPrompt` 目前只在 `WithContextUpdatesAsync` 里替换（每批用户输入之前）。新增一个由工具触发的刷新：

- `Agent.RequestContextRefresh()`：设置一个标记；`ToolContext.RequestContextRefresh` 由 `AgentSession` 接到 agent 上。
- 在 `RunAsync` 内层循环里，`ExecuteToolCallsAsync` 和 `TurnEndEvent` 之后：如果有这个标记，就调用一次 `ContextUpdates`（不带新的用户输入），把返回的 `SystemMessage` 补丁放在 `pending` 的最前面，排在 steering 消息之前，然后清除标记。
- 补丁紧跟在工具结果后面发给模型，适配器已经支持这种顺序：steering 用户消息走的就是同一个位置。系统提示开头保持冻结，变化仍然用追加 `SystemMessage` 表达，不违反 AGENTS.md 的协议约束。
- 只有 `exit_plan_mode` 批准时和 `enter_plan_mode` 同意时会请求刷新。用户在回合中途手动切换模式的行为不变。
- 同步更新 `Agent.Tools` / `SystemPrompt` 的注释（"同一轮内不变" → "只在两次模型请求之间替换"），以及 architecture.md 中相应的说明。

## 10. 批准选项与效果

| 选项 id | 面板文字（中文） | 模式 | 权限档位 | 其他 | 何时出现 |
|---|---|---|---|---|---|
| `auto` | 批准，自动执行 | Agent | 自动批准；当前是完全授权时保持完全授权，文字改为"批准，完全授权执行" | — | 总是 |
| `ask` | 批准，逐项审批 | Agent | 询问 | — | 总是 |
| `acp` | 批准，交给 ACP 执行（显示当前权限档位） | ACP | 不变 | — | 运行时支持子 Agent |
| `clear` | 清空上下文，批准并自动执行 · 规划用掉 N% 上下文 | Agent | 同 `auto` | 下一次请求前替换历史（§11） | 仅内置 Agent |
| — | 继续完善：[反馈输入框] | Plan | 不变 | 反馈可以为空 | 总是 |

- 第一项默认选中。顺序为 `clear`、`auto`、`ask`、`acp`、继续完善；`clear` 放在最前，与 Claude Code 开启 `showClearContextOnPlanAccept` 后一致。是否改为默认选中 `auto`，见 §19。
- Host 负责执行效果：通过 `SessionOptions.Apply` 切换模式和权限，用 `AppendOptionsChangeAsync` 持久化，然后推送 `optionsChanged`（§13.2）。界面上的权限按项目记忆，与用户手动选择时的处理一致。
- 用户编辑过计划时，Host 先把编辑后的版本写回计划文件，再返回工具结果。

## 11. 清空上下文后执行（`clear`）

- 复用压缩的历史替换机制（compaction.md §8）：新增触发类型 `CompactionTrigger.PlanHandoff`。**不调用模型生成摘要**，摘要消息直接用 §12.6 的交接消息。
- 时机：`exit_plan_mode` 的结果追加之后，先按 §9 刷新上下文（模式变成 Agent），然后在下一次模型请求之前执行替换。这符合"历史替换只发生在两次请求之间"的约束。
- 替换后的模型上下文是"重新生成的系统提示（含 `AgentResumed` 模式节）+ 交接消息 + 任务清单（如果有未完成项，格式同 compaction.md §7.3）"。不附带最近读过的文件，模型按计划重新读。`FileStateTracker` 清空，与压缩一致。
- JSONL 写一条 `compaction` 记录（`trigger: "planHandoff"`，`summary` 为交接消息），之后照常追加 `systemPrompt` 和消息。重新打开会话时，上下文只从这里开始。
- 界面：转录里显示压缩分隔线，文字为"已清空上下文，按计划执行"。完整历史仍然可以回看。

## 12. 提示词（待评审）

按 composer.md §18.1 的原则写：结构依次是"这个模式是什么、按什么顺序工作、硬性限制、交付什么"；只引用该模式下实际存在的工具；英文书写；代码中的文本与这里一致。定稿后，composer.md §18.2 / §18.4 改为引用本节。

### 12.1 Plan 模式节（替换 composer.md §18.2）

`{planPath}` 是计划文件的绝对路径。`{planFileState}` 二选一：文件不存在时是 "It does not exist yet; create it with write."；存在时是 "A plan from earlier in this session is already there: read it, then update it rather than starting over."。

```
You are in Plan mode. The user wants to review a plan before anything is changed. Investigate, settle open decisions with the user, write the plan to the plan file, and then ask for approval with exit_plan_mode. Do not implement the plan.

Plan file: {planPath}
{planFileState} This is the only file you may write or edit in this mode.

Work in this order:
1. Understand the request and the code it touches. Read files yourself for focused questions. For broad questions, launch up to three explore subagents in parallel with the agent tool, each with its own focus. Keep facts you verified separate from assumptions.
2. For a design with several reasonable options, you may launch a plan subagent with what you found, then check its proposal against the code.
3. If a choice would materially change scope, behavior, compatibility, data or user experience, and the code does not settle it, ask the user with ask_user. Ask only what you cannot find out yourself.
4. Write the plan to the plan file in Markdown:
   - Context: the problem, and what will be true when the work is done.
   - Approach: the recommended design and why. Mention a rejected alternative only in a sentence, and only if the user is likely to ask about it.
   - Changes: every file to create or modify, and what changes in it.
   - Edge cases and risks.
   - Verification: the tests and checks that will prove it works.
   Keep it short enough to scan and specific enough to carry out without this conversation.
5. Call exit_plan_mode. If the user asks for changes, update the plan file and call exit_plan_mode again.

While you are planning a change, end your turn only by calling ask_user or exit_plan_mode. Do not ask whether the plan is acceptable in any other way: not in text, and not with ask_user. If the user says in a message that they approve, call exit_plan_mode so they can choose how to proceed. If the user only asks a question or wants research rather than a change, answer directly; do not write a plan or call exit_plan_mode.

Constraints: apart from the plan file you cannot write or edit files or use MCP tools in this mode, and subagents you launch are read-only. Commands you run must not modify the workspace; any command that might will need the user's approval. Never claim that something was changed.
```

与现在的版本相比：
- 交付物从"一段回复"改成"计划文件加 `exit_plan_mode`"；
- 加入子 Agent 调研（第 1、2 步）；
- 计划结构加了 Context，去掉 Open questions（未决问题要在写计划之前用 ask_user 问清楚）；
- 加上回合结束方式的约定，以及"纯提问直接回答"的例外。

### 12.2 `exit_plan_mode` 的工具描述

```
Ask the user to approve your plan, and wait for their decision. Available only in Plan mode.

- First write the complete plan to the plan file named in your Plan mode instructions. This tool reads the plan from that file and shows it to the user, who may edit it before approving.
- Call it only when the plan is finished and the open questions are settled. Do not call it for questions or research that do not lead to a change.
- This is the only way to ask whether the plan is acceptable: do not ask in text or with ask_user.
- If the user approves, Plan mode ends and you continue in the same turn under the mode they chose. If they ask for changes, update the plan file and call this tool again.
```

tools 节的 `PromptSnippet`：`Ask the user to approve the plan in the plan file (Plan mode only)`。没有 `PromptGuidelines`，规则写在模式节里。

### 12.3 `exit_plan_mode` 的结果文本

| 结局 | 文本 |
|---|---|
| 批准（`auto` / `ask`） | 见下方代码块 A |
| 批准（`acp`） | 见下方代码块 B |
| 批准（`clear`） | `The user approved the plan. The conversation is being cleared; you will continue from the plan alone.`（替换历史后模型看不到这条结果，它只留在转录里） |
| 继续完善，有反馈 | 见下方代码块 C |
| 继续完善，没有反馈 | `The user did not approve the plan yet and wants to keep planning. Stay in Plan mode: end your turn now and wait for the user's next message.` |
| Esc（关闭面板） | 同上一行 |
| 停止 | `Operation aborted.`（现有文本） |
| 计划文件不存在或为空 | `No plan to review: write the complete plan to {planPath} first, then call exit_plan_mode again.`（`IsError`） |
| 没有 UI | `exit_plan_mode is unavailable: no user is attached to this session. End your turn; the plan is saved at {planPath}.`（`IsError`） |

A（`{editedNote}` 只在用户编辑过时出现：` The user edited the plan before approving it; follow the version below.`）：

```
The user approved the plan.{editedNote} Plan mode is over: you can now edit files and run commands to carry it out.

The plan is saved at {planPath}; refer back to it as you work.

## Approved plan

{plan}

Start implementing now. If the work has several steps, track them with task_create first.
```

B：

```
The user approved the plan.{editedNote} The session is now in ACP mode: carry the plan out by delegating it to subagents, as your ACP mode instructions describe. Do not implement it yourself.

The plan is saved at {planPath}; give each subagent the parts of it that it needs.

## Approved plan

{plan}
```

C：

```
The user did not approve the plan yet. Stay in Plan mode. Their feedback:

{feedback}

Update the plan file to address it (ask with ask_user if the feedback is unclear), then call exit_plan_mode again.
```

### 12.4 `enter_plan_mode` 的描述与结果

```
Ask the user to switch this session to Plan mode before you start a change, and wait for their decision. In Plan mode you investigate and write a plan for the user to approve, without changing anything.

Use it before starting a change when the right approach is unclear and a wrong start would waste significant work, for example:
- a new feature, or a change to existing behavior that touches several files;
- several reasonable designs or technologies to choose between;
- requirements you must explore before you can say what to change.

Do not use it for small or clear changes (a typo, an obvious fix, a single function with a clear spec), when the user has already given detailed instructions or a plan, or for questions and research.
```

- `PromptSnippet`：`Ask the user to switch to Plan mode before a large or unclear change`。
- 同意：`The user switched the session to Plan mode. Follow your Plan mode instructions: investigate, write the plan to the plan file, and ask for approval with exit_plan_mode. Do not change anything yet.`（模式节补丁随后追加）
- 拒绝：`The user declined to switch to Plan mode. Continue with the task in Agent mode, and do not call enter_plan_mode again for it.`
- 停止：`Operation aborted.`；没有 UI：`enter_plan_mode is unavailable: no user is attached to this session. Continue in Agent mode.`（`IsError`）

确认面板的文字（界面文本，走 strings.tsv）：标题"进入 Plan 模式？"；正文是 `reason`；选项"先做计划（推荐）：调研后写计划，你批准后再动手"和"直接执行"。

### 12.5 切回 Agent（替换 composer.md §18.4）

`{planLine}` 只在计划文件存在时出现：` The plan for this session is saved at {planPath}.`

```
You are in Agent mode again: you can read, edit and write files and run commands to carry out the user's task.{planLine} If a plan was agreed earlier in this conversation, follow it unless the user says otherwise, and say where you deviate from it.
```

### 12.6 清空上下文后的交接消息

```
The user approved the plan below in Plan mode and cleared the earlier conversation. Implement it now.

{plan}

The plan is saved at {planPath}. If you need specific details from the planning conversation (exact code snippets, error messages or findings), read the full transcript at: {transcriptPath}
```

### 12.7 Plan 模式下的拦截文本

- write / edit 的目标不是计划文件：`In Plan mode you can only write the plan file ({planPath}). Put the change in the plan instead; it will be made after the user approves.`（`IsError`）
- `agent` 指定了 `general-purpose`：`In Plan mode only read-only subagents are available: use subagent_type "explore" or "plan".`（`IsError`）

### 12.8 外部运行时

- **Claude**：plan 模式由 CLI 原生实现，有它自己的提示词、计划文件和 ExitPlanMode。所以 TKCode 切到 Plan 时**不再发送 ExternalPlan 提醒**，避免两套指令打架（新增 `RuntimeCapabilities.NativePlanMode`）。切回 Agent 时：经审批切回的不发提醒（CLI 自己处理）；用户手动切回的照常发 `ExternalAgentResumed`。
- 交给 ACP 时：批准 ExitPlanMode 后，立即用 steer 方式（`priority: "next"`）发送一条 `ExternalReminder(plan → acp)` 文本，确保模型在继续之前看到 ACP 说明。这条提醒视为已发送，不会在下一轮重复。
- **Codex / ACP 运行时**：保持现状，仍然发送 composer.md §18.8 的 ExternalPlan，用户手动切换。

## 13. Host↔UI 契约（协议版本 22 → 23）

新增 RPC 方法时按 AGENTS.md 改四处：接口、`RpcMethods`、`RpcJsonContext`、代理和分发器。

### 13.1 `plan/review`（Host → UI 请求）

```csharp
record PlanReviewRequest(string SessionId, string RequestId, string Source /* builtin | claude-code */, string? ToolCallId,
                         string PlanPath, string Plan, IReadOnlyList<string> Choices /* clear, auto, ask, acp 的子集 */,
                         string PermissionMode /* 当前档位，决定文字 */);
record PlanReviewResponse(string Outcome /* approved | revise | dismissed */, string? Choice, string? Feedback, string? EditedPlan);
```

- `EditedPlan` 只在用户改过计划时有值。
- 取消（停止、Claude 撤回请求）：`$/cancelRequest` 到达 UI 后移除面板，恢复输入框，机制与 ask-user.md §6 相同。
- 没有 UI：内置工具返回 §12.3 的错误；Claude 运行时回复 deny，message 用同一句。

### 13.2 `optionsChanged`（会话事件）

`SessionOptionsChanged(SessionOptionsDto Options)` 加入 `SessionEvent` 的多态列表。由 Host 主动改变模式或权限时推送：plan 审批、`enter_plan_mode`、Claude CLI 上报的模式变化（§15）。界面据此更新模式和权限选择器，**不再**回调 `SetOptionsAsync`。

### 13.3 `enter_plan_mode`

复用 `input/request`，`Source = "builtin"`，不新增方法。

## 14. 界面

### 14.1 审批面板（取代输入框，同 `QuestionPanelView` 的位置）

```
┌──────────────────────────────────────────────────────────────┐
│ ✓ 计划待批准 · 20261019T101530_ab12.md        [编辑] [↗] [■] │  标题：来源 + 文件名；编辑、在编辑器中打开、停止
│ ┌──────────────────────────────────────────────────────────┐ │
│ │ ## Context                                               │ │  StreamMark 渲染的计划；最高约窗口的 55%，可滚动
│ │ …                                                        │ │  编辑状态下换成等宽的 Markdown 源码编辑框
│ └──────────────────────────────────────────────────────────┘ │
│  1  清空上下文，批准并自动执行 · 规划用掉 38% 上下文          │
│  2  批准，自动执行                                           │
│  3  批准，逐项审批                                           │
│  4  批准，交给 ACP 执行（询问）                               │
│  5  继续完善 [告诉 Agent 要改什么…                         ] │
│ ↑↓ 选择 · 1-5 直接选 · ↵ 确认 · Esc 暂不批准       [确认 ↵] │
└──────────────────────────────────────────────────────────────┘
```

- 外观、宽度、草稿保留、"等待你的操作"橙点、待处理期间不能发消息、停止按钮在标题栏：与 ask-user.md §7、§9 相同。
- **编辑**：点"编辑"或按 `E` 进入源码编辑；再次点击回到渲染视图并显示"已编辑"。改过的内容随批准一起返回。选"继续完善"时编辑内容不保存（避免与模型接下来的修改冲突），切换前提示一次。
- **在编辑器中打开**：用系统默认程序打开计划文件（对应 Ctrl+G）。面板获得焦点时重新读取文件，有变化就刷新显示，并按"已编辑"处理。
- **上下文比例**：取会话现有的上下文占用数据（界面已有）。
- **键盘**：↑/↓ 移动；1–5 直接选（数字选到"继续完善"时进入反馈框）；Enter 确认；在反馈框里打字会自动选中"继续完善"；Cmd/Ctrl+Enter 在任何位置确认；Esc 先退出编辑框或反馈框，否则就是"暂不批准"。
- 来源是 Claude 运行时时，标题为"Claude Agent 计划待批准"，不显示 `clear` 项。

### 14.2 转录里的计划卡片

- `exit_plan_mode` 和 Claude 的 `ExitPlanMode` 显示为独立的转录项（与问答卡片一样不并入活动组）。卡片头部是"计划"、文件名和结局：
  - "已批准 · 自动执行 / 逐项审批 / 交给 ACP / 清空上下文后执行"；
  - "已退回"，下面引用反馈；
  - "未批准"；
  - "已中止"。
- 正文默认折叠，显示前 8 行，点击展开为完整的渲染计划（来自 Details 快照），附"在编辑器中打开"——在右侧工作区打开计划文件（与 read 工具同一条 `OpenFileRequested` 通路），不交给系统默认程序；审批面板里的同名按钮才用系统默认程序，因为那里要回读外部编辑。
- 等待审批期间，卡片显示"等待批准…"。
- `enter_plan_mode` 沿用问答卡片显示。

### 14.3 选择器

收到 `optionsChanged` 后，模式和权限选择器立即更新。批准后模式芯片从 Plan 变成 Agent / ACP，用户能直接看到。

### 14.4 本地化

新文本全部加进 `strings.tsv`（中英两列），然后运行 `python3 -I tools/gen_strings.py`。

## 15. Claude 运行时的映射

| 收到 | 处理 |
|---|---|
| `can_use_tool`，`tool_name == "ExitPlanMode"` | 与 AskUserQuestion 一样**绕开权限档位**：通过新增的 `IRuntimeHost.RequestPlanReviewAsync` 进入审批面板。计划取 `input.plan`；没有时读 `input.planFilePath`；都没有就回复 deny，message 为 "No plan was provided." |
| 批准 `auto` / `ask` | `allow`，`updatedInput` 为原 input（用户编辑过时替换 `plan`，并写回 `planFilePath`）；`updatedPermissions: [{"type":"setMode","mode":<按 PermissionModeFor 映射新档位>,"destination":"session"}]`。TKCode 的模式切到 Agent，档位更新，`_appliedPermissionMode` 同步为新值，推送 `optionsChanged` |
| 批准 `acp` | 同上，模式切到 ACP，然后按 §12.8 用 steer 发送 ACP 说明 |
| 继续完善 / Esc | `deny`，message 为 §12.3 对应的文本（不提 TKCode 的工具名，措辞适配 Claude：`call ExitPlanMode again`）；`interrupt: false` |
| 停止或撤回 | 沿用 `DecideAsync`：不应答 |
| `system/status.permissionMode`、后续的 `system/init.permissionMode` | 同步 `_appliedPermissionMode`。值变为 `plan` 而 TKCode 不在 Plan 时（模型自己调用了 EnterPlanMode），TKCode 切到 Plan 并推送 `optionsChanged`；离开 `plan` 时同理切回 Agent |

- 待核实（P4 第一步）：ExitPlanMode 的 input 字段、EnterPlanMode 是否经过 `can_use_tool`、`setMode` 的生效时机。要用真实 CLI 抓帧，**会跑真实轮次，开始前先征得用户同意**。核实后补进 `claude-code-stream-json.md`。
- Codex、ACP：本期不改（§2）。

## 16. 持久化与恢复

- 计划审批的结果在工具结果的 Details 里（§7.1），随 `message` 记录写入 JSONL。重新打开会话后，卡片照常显示。
- 模式和权限的变化沿用 `AppendOptionsChangeAsync`。重新打开时恢复为批准后的模式和权限，包括完全授权。
- 等待审批时关闭会话或退出程序，请求被取消，工具结果是 "Operation aborted."，计划文件保留。重新打开后，用户可以说"批准"，模型会再次调用 `exit_plan_mode`（§12.1 已约定）。

## 17. 不在本期

- Codex（`collaborationMode` / `plan` 条目）和 ACP（`switch_mode` 权限请求、`session/set_mode`）接入审批面板。
- 计划里的待办一键转成任务清单（Cursor 风格）；目前由模型在执行时用 `task_create` 自己拆。
- 计划文件放在项目目录、`plansDirectory` 设置。

计划审批的系统通知与未读角标已在 [notifications.md](notifications.md) 中实现（属于「需要你处理」）。

## 18. 实施阶段

| 阶段 | 内容 |
|---|---|
| P1 核心 | `ToolContext.PlanFile`；Plan 模式的工具表、模式专属工具、计划文件写入规则；`PermissionPolicy` 的 `readOnly` 参数（主会话和 explore / plan 子 Agent）；`agent` 的类型限制；`Agent.RequestContextRefresh`；`exit_plan_mode` / `enter_plan_mode`、结果文本、Details；`IPlanReviewService`；`PlanHandoff` 替换；§12 的提示词写入 `SessionRole` 等处 |
| P2 契约与 Host | `plan/review` 改四处；`SessionOptionsChanged`；协议版本 23；`SessionManager` 实现审批服务、执行选择、持久化和推送；`enter_plan_mode` 走 `input/request` |
| P3 界面 | `PlanReviewViewModel` / `PlanReviewPanelView`（渲染、编辑、外部编辑器、选项、反馈、键盘）；转录计划卡片；选择器响应 `optionsChanged`；橙点；压缩分隔线文字；本地化 |
| P4 Claude 运行时 | 抓帧核实（先征得同意）→ `ExitPlanMode` 映射、`permissionMode` 同步、`NativePlanMode` 能力、ACP steer；补全 `claude-code-stream-json.md` |
| P5 文档 | composer.md §10.3、§18.2、§18.4、§18.8 改为引用本文；修正 Ask 模式的过时说明；architecture.md 中工具集冻结的说明（§9）；本文写上实现状态 |

## 19. 测试

- **Agent.Tests**：
  - Plan 模式的工具过滤，以及模式专属工具的出现和消失；
  - 计划文件之外的写入被拦截；`general-purpose` 子 Agent 被拒绝；
  - `PermissionPolicy` 在 `readOnly` 下：自动批准和完全授权都会询问非只读命令，不采用"本会话允许"；
  - 各种结局的结果文本和 Details；
  - 回合中刷新：假模型执行 plan → `exit_plan_mode` → 批准，下一次请求带上 write 工具和模式节补丁，且补丁排在工具结果之后；
  - `PlanHandoff`：替换后的历史、JSONL 记录和恢复。
- **AgentHost.Tests**：`RecordingClient` 依次回答 `plan/review` 的每个选项、"继续完善"、Esc 和停止；`optionsChanged` 推送；重新打开后的模式；没有客户端时的错误；子 Agent 没有这两个工具；`enter_plan_mode` 的同意和拒绝。
- **Runtimes.Tests（Claude 夹具）**：ExitPlanMode 进入审批而不是自动放行；批准时回复的 `setMode`；退回时的 deny；`status.permissionMode` 的同步。
- **Desktop 无头测试**：
  - 端到端（真实 Host 加假模型）：面板取代输入框，键盘选择，编辑后批准，填写反馈退回，停止后面板收起，选择器更新，卡片在重新打开后仍在。
  - 快照：浅色和深色的面板、编辑状态、Claude 来源、计划卡片（折叠和展开）、清空上下文的分隔线。
  - `UiZoomTests` 照常通过。
- **NativeAOT**：AOT 发布零 IL 警告；运行发布出的程序：冒烟测试 `role.plan` 扩展为"写计划文件 → `exit_plan_mode` → 批准"。

## 20. 需要评审时确认的点

1. 面板默认选中哪一项：`clear`（与 Claude Code 开启该选项后一致），还是 `auto`？
2. Esc 的含义：现在设计为"暂不批准，结束本轮"（和 ask_user 的拒绝一样）。Claude Code 的 Esc 会直接中断本轮。
3. `acp` 选项保持当前权限档位不变，是否合适？
4. 计划文件的命名沿用会话文件名（可以对应到会话，但文件名不好读），还是像 Claude Code 那样用随机单词？
5. `enter_plan_mode` 在所有档位下都要确认，包括完全授权。

## 21. 实现状态（2026-10-19）

§20 的五点用户未另行指定，按文档默认处理：面板默认选中第一项（有 `clear` 时就是它）、Esc 为"暂不批准，结束本轮"、`acp` 保持当前档位、计划文件沿用会话文件名、`enter_plan_mode` 在所有档位下都要确认。

| 部分 | 位置 |
|---|---|
| 共用模型（选项、结局、Details） | `src/TKCode.Agent/Plans/PlanReview.cs` |
| 回合中途刷新、交接 | `Agent.RequestContextRefresh` / `RequestHandoff`（`src/TKCode.Agent/Agent.cs`）；`CompactionTrigger.PlanHandoff`、`CompactionRequest.Handoff`；`Compactor.HandoffAsync` |
| 工具 | `src/TKCode.Agent.Coding/Tools/Plan/`（`ExitPlanModeTool`、`EnterPlanModeTool`、`IPlanModeService`、`PlanFiles`） |
| 模式、过滤、计划文件写入 | `SessionRole`（`Plan`、`PlanWithoutApproval`、`SectionFor`、`Allows`）、`ToolContext`（`PlanFile`、`InPlanMode`、`IsReadOnly`）、`WriteTool` / `EditTool`、`AgentTool`、`AgentSession.CreateBuiltinLoop` |
| 只读命令规则 | `PermissionPolicy.Evaluate(readOnly)`、`PermissionPolicy.ReadOnlyCommandKind`；`SessionManager.ApproveAsync` 传入 `session.Tools.IsReadOnly` |
| RPC（协议 23） | `PlanReviewRequest` / `PlanReviewResponse`、`RpcMethods.ReviewPlan`、`IAgentHostClient.ReviewPlanAsync`、`SessionOptionsChanged` |
| Host | `SessionManager`（`IPlanModeService`、`ReviewPlanAsync`、`ApplyPlanChoiceAsync`、`ApplyHostOptionsAsync`、`SyncRuntimePlanModeAsync`）；fork 复制计划文件（`AgentHostServer.ForkSessionAsync`）；AOT 自检（`SelfTest`） |
| Claude 运行时 | `ClaudeSession.ReviewPlanAsync`、`OnPermissionModeReported`；`RuntimeCapabilities.NativePlanMode`；`ExternalAgent`（`PlanReviewHandler`、`PlanModeChanged`、`IRuntimeHost.RequestPlanReviewAsync`）；`AgentSession.Role` 对原生 plan 运行时不发 Plan 说明 |
| 界面 | `PlanReviewViewModel`、`Views/PlanReviewPanelView.axaml(.cs)`（审批面板）；`PlanCardViewModel`、`Views/PlanCardView.axaml(.cs)`（转录计划卡片）；`AgentHostConnection.PlanReviewHandler`；`ConversationViewModel.HandlePlanReviewAsync`、`IsReviewingPlan`；`SessionViewModel`（`PendingPlanReviews`、`SessionOptionsChanged` → `HostOptionsChanged`、`plan-handoff` 分隔线）；`ApprovalRequestViewModel`（`readOnlyCommand`）；`QuestionRequestViewModel` / `QuestionCardViewModel`（`enterPlanMode` 的本地化标题、选项与卡片）；`ToolPresentation`；`SmokeMode`（`plan.panel`、`plan.approved`） |

与设计的差异：
- 用户编辑过的计划由工具（内置）或运行时会话（Claude）写回计划文件，而不是 Host（§10 原写 Host）：文件归工具所有，Host 只负责切换模式和权限。
- 清空上下文后，除未完成的任务外，也附上仍在运行的后台任务（与压缩的 §7.3 一致），以免模型不知道它们还在跑。
- 没有计划审批服务的会话（没有宿主，例如单元测试）退回到原来的 Plan（`SessionRole.PlanWithoutApproval`）。
- 界面：数字键只选中一行，Enter 或 Cmd/Ctrl+Enter 才确认（避免一次按键就批准计划）；单击选中、双击确认。完全授权时 `clear` 的文字也随之改为"清空上下文，批准并完全授权执行"。折叠的计划卡片把前 8 行源码按 Markdown 渲染。已结束的一轮在中途的压缩 / 清空上下文分隔线处拆开，分隔线位于规划与执行之间。切换权限档位时不再自动放行待处理的 `readOnlyCommand` 审批（它只能逐次允许）。工具显示名的 key 为 `ToolExitPlanMode` / `ToolEnterPlanMode`（生成器不支持 key 中的点）。
- `exit_plan_mode` / `enter_plan_mode` 只在提供了 `IPlanModeService` 且有计划文件的主会话里创建；`AgentSession` 对所有模式（包括 Agent）都经过 `Allows` 过滤，模式专属工具因此在 `all` 下也被正确过滤。

测试：
- `tests/TKCode.Agent.Tests/PlanModeTests.cs`（15 个）：工具过滤与模式节、没有服务时的回退、计划文件写入限制、批准后同一轮切换（补丁紧跟工具结果）、编辑后批准、继续完善、关闭、没有计划文件、清空上下文（历史、系统提示、会话文件）、`enter_plan_mode` 同意与拒绝、只读命令规则、Details。
- `tests/TKCode.AgentHost.Tests/PlanModeTests.cs`（12 个）：`plan/review` 的请求内容与四种选项的效果、完全授权保持、未知选项、没有客户端、中止撤回、`optionsChanged` 推送与持久化、`enter_plan_mode` 经 `input/request`、拒绝 general-purpose 子 Agent、只读命令在完全授权下也询问、子 Agent 没有 Plan 工具。
- `tests/TKCode.Runtimes.Tests/ClaudeFixtureTests.cs`：ExitPlanMode 批准（`setMode`、编辑后替换并写回）、继续完善的 deny、`system/status.permissionMode` 进入 plan 的同步。
- `AgentAssetTests.Role_ResolvesBuiltinAndCustom_AndFiltersTools` 按新的 Plan 语义更新。
- `tests/TKCode.Desktop.Tests/PlanReviewPanelTests.cs`：真实 Host 加假模型的端到端（enter_plan_mode、继续完善与丢弃编辑的提示、Esc、Stop 后恢复草稿、编辑后批准、清空上下文、重新打开），以及 Claude 来源、完全授权的文字、外部编辑、按键与卡片各状态。快照（浅色与深色）：`plan-review-panel`、`plan-review-panel-editing`、`plan-review-panel-claude`、`plan-card`、`plan-card-expanded`、`plan-handoff-divider`、`enter-plan-mode-question`、`plan-card-transcript`。
- AOT：Agent Host 与 Desktop 发布均为零 IL 警告；Host `--self-test` 通过（含 4 项 Plan 检查）；Desktop `--smoke` 的 `role.plan`、`plan.panel`、`plan.approved` 通过（本机缺少终端原生库，冒烟在终端一步失败，与本功能无关）。
- 已知问题（与本功能无关）：AtomUI 6.2.6 迁移后，无头渲染里 XAML 的 `{loc:TKCodeLangResource …}` 文字为空（审批面板的"确认"、"已编辑"标签，以及侧边栏、问答面板中原有的文字），由代码设置的文字正常。

待完成：
- 用真实 Claude CLI 抓帧核实 `ExitPlanMode` 的 input 字段、`EnterPlanMode` 的上报方式与 `setMode` 的生效时机（会跑真实轮次，需用户同意）。
