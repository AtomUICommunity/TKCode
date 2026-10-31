# TCode 消息功能分析与改进报告

日期：2026-10-30 · 状态：P0（R1–R5）已实现，P1 / P2 待做

## 结论

- **现状**：TCode 的 Agent 特有部分已经和 Claude Code、Codex、Cursor 持平，包括工具活动折叠、每回合改动卡片、排队插话、计划 / 提问面板、上下文卡片、子 Agent 视图。缺的是两类，而调研的十几个产品几乎都有：
    - **消息级操作**：编辑重发、回退、从消息分叉、出错重试、每条消息复制。
    - **长会话可用性**：⌘F 查找、提问间跳转、完成通知。
- **先做 5 件（P0）**：出错重试与可见的自动重试、系统通知、消息操作补全、⌘F 与提问跳转、编辑重发 / 回退 / 分叉。前四件不碰历史结构，其中通知、操作补全、查找三件只改 Desktop。
- **编辑 / 回退 / 分叉的实现**：建议走"在消息处分叉成新会话"。
    - Codex 和 Claude Code 内部都这么做，完全符合"历史只追加"。
    - 现有 `ForkSessionAsync` 只需加一个截止点参数。
    - 代码回退复用现有整回合 Revert，不做整仓快照（Zed 在 2026-10-26 刚因性能问题撤掉）。
- **出错重试很便宜**：失败 / 中止的助手消息在发请求时已被跳过（`MessageTransformer.cs` L30），重试就是用原历史再跑一轮。
- **Arc 组件**：
    - 当前用的 preview.1 核心包里就有 `CopyButton`、`Toast`、`JsonViewer`、`ExpandingSearch`、`ConfirmMorph`、`ShortcutKeyCap`，够支撑 P0。
    - AI 组件大多在 `TKCode.Arc.Special` 0.3.2-preview.2（今天发布）。值得用的是 `FeedbackActions`、`GenerationStats`、`ReasoningBlock`、`AgentStatus`。
    - `ToolCall`、`AgentPlan`、`ChatThread`、`AiChat` 不建议替换现有实现，只借鉴它们的交互。
- **进度**：P0（R1–R5）与 Arc 升级已实现；剩下的待决策见 §8。

## 1. 调研范围

- **TCode 现状**：通读 `src/TKCode.Desktop` 的对话区（TranscriptView、模板、ViewModel）、`TKCode.Rpc.Contracts` 的 RPC 与事件、`docs/design/*.md` 里已设计未实现的条目。
- **TKCode.Arc**：<https://docs.tkcode.si/llms.txt> 列出的全部组件文档，以及 NuGet 上 `TKCode.Arc` / `TKCode.Arc.Special` / `TKCode.Arc.Blocks` 的 0.3.2-preview.1 和 preview.2 两个版本的实际类型（XML 文档比对）。
- **同类产品**：编码 Agent（Claude Code 桌面版与 VS Code 插件、Codex app、Cursor、Windsurf、Zed、Copilot、Cline/Roo 等）和通用聊天客户端（ChatGPT、Claude.ai、Gemini、LobeChat、Cherry Studio、Open WebUI、LibreChat 等）。

## 2. TCode 现状盘点

### 2.1 已经做得好的（不需要重复投入）

| 能力 | 现状 | 位置 |
|---|---|---|
| Markdown 渲染 | StreamMark 流式渲染、按块虚拟化、跨消息选择；代码块有语言标签 / 复制 / 换行 / 下载 / 超 40 行折叠；diff 围栏三种视图；数学、Mermaid（全屏缩放）、表格复制、图片缓存 | `Controls/MarkdownSetup.cs`、`external/StreamMark/.../Rendering/*` |
| 思考过程 | 流式时 "Thinking…" 微光并自动展开，结束后折叠为 "Thought process"，显示时长与估算 token | `ViewModels/TranscriptItems.cs` L257-371 |
| 工具调用 | 连续工具调用合并为活动组（"Thought 2× · Ran 4 commands"），读/搜合并为 "Explore"，回合结束折叠为 "Worked for Xm Ys"；bash 输出实时跟尾；编辑显示 +/− 与 diff | `Controls/TranscriptAdapter.cs`、`Views/ToolStepView.axaml`、`Views/ToolCallDetailView.axaml` |
| 每回合文件改动 | "N files changed +X −Y" 卡片，整回合 Revert，单文件 Review / Open；会话级 Changes 面板逐文件 Revert（Host 先校验 SHA-256） | `Views/TurnChangesView.axaml`、`ViewModels/FileChangeViewModels.cs` |
| 排队与插话 | 运行中发送进入队列卡片，可编辑 / 立即插入（steer）/ 删除；出错后暂停并显示 Resume | `ViewModels/QueuedPromptViewModel.cs`、`MainWindow.axaml` L399-451 |
| 交互面板 | 计划审批面板、ask_user 面板、MCP 表单、工具审批卡片；计划与问答在对话流里留卡片 | `Views/PlanReviewPanelView.axaml`、`Views/QuestionPanelView.axaml` 等 |
| 上下文 | 压缩分隔条（N → M tokens，可展开摘要）、上下文圆环 + 六类分解悬浮卡 | `ViewModels/ContextUsageViewModel.cs` |
| 子 Agent 与任务 | 子 Agent 分组卡片，点开右侧工作台看完整实时记录；TaskOrb 浮球汇总运行中的 Agent / 后台 Shell / todo / 改动 | `Views/SubagentTabView.axaml`、`Views/TaskOrbView.axaml` |
| 附件与提及 | 粘贴 / 拖放（含拖到对话区）/ 选择图片，发送后气泡里保留 @文件、/命令等标签 | `Views/ComposerView.axaml`、`Views/UserMessageBubble.axaml` |
| 会话 | 重命名、置顶、整会话 fork、删除、草稿与 ↑↓ 历史、⌘K 按标题搜索 | `ViewModels/ConversationViewModel.cs` |

### 2.2 缺口（按影响排序）

| # | 缺口 | 现状细节 |
|---|---|---|
| G1 | **编辑并重发用户消息** | 没有 UI 也没有 RPC；`docs/design/composer.md` L323 只有一句话 |
| G2 | **重试 / 重新生成** | 没有 RPC；HTTP 层静默重试 2 次（`TKCode.Ai/Http/HttpWireApi.cs`），之后只剩红色错误卡片，没有重试按钮 |
| G3 | **回退到某条消息（对话 + 代码）/ 从某条消息分叉** | 只能整回合 Revert 文件、整会话 fork。会话 JSONL 本身是 id/parentId 树（`Sessions/SessionEntry.cs`），底层支持分支但没暴露 |
| G4 | **对话内搜索（⌘F）与消息间跳转** | StreamMark 已有 `TranscriptView.Search`（含虚拟化内容）和 `GetOutline()`，TCode 没接 |
| G5 | **完成 / 等待审批时的系统通知** | 没有 OS 通知、提示音、Dock / 托盘角标；`ask-user.md`、`plan-mode.md` 明确列为暂不做 |
| G6 | **每条消息的元数据** | 不显示模型、token、费用、TTFT / 速度；用户消息没有时间戳；工具耗时只在 tooltip；退出码已解析但不显示 |
| G7 | **消息操作不完整** | 复制只出现在每回合最后一条助手消息；用户消息没有复制按钮；Markdown 右键菜单（复制 / 复制为 Markdown / 查找）因 AtomUI 菜单主题问题被关掉（`MarkdownSetup.cs` `ContextMenu = false`），AtomUI 已移除，可重新评估 |
| G8 | **审批卡片能力弱** | 没有快捷键、没有"拒绝并说明原因"、文件编辑没有 diff 预览、没有"总是允许"；参数是原始 JSON 文本 |
| G9 | **错误与限流反馈** | 没有"过载，8 秒后重试（2/3）"之类的状态；Esc 不能停止 Agent |
| G10 | **会话导出 / 内容搜索** | 不能导出 Markdown / JSON；⌘K 只按标题匹配已加载的会话 |
| G11 | **引用选中文字到输入框** | 无 |
| G12 | **切换会话不保留滚动位置** | 每次切换都滚到底（`MainWindow.axaml.cs` L357） |

## 3. 同类产品怎么做

> 来源多为官方文档、更新日志和 2026 年的第三方拆解；开源项目的文案取自 `main` 分支源码和 en-US 语言文件。标"未核实"的只有第三方或无来源。2026 年的趋势是聊天应用和 Agent 合流：ChatGPT 桌面版合并 Chat/Work/Codex（2026-07），Claude 合并聊天与 Cowork（2026-09-16），开源客户端（LibreChat、LobeHub、Cherry Studio、Chatbox）都加了 steer/queue、工作区改动、审批模式等编码 Agent 功能。

### 3.1 编码 Agent

对比对象的变化：Codex app 已并入 ChatGPT 桌面版的 "Codex" 模式（2026-07-09）；Windsurf 改名 Devin Desktop，Cascade 被 "Devin Local" 取代；Roo Code 于 2026-05-15 停止维护（仍可参考）；**Zed 在 2026-10-26 移除了 Agent checkpoint**（[PR #65989](https://github.com/zed-industries/zed/pull/65989)：大仓库每回合给所有 git 仓库做快照要几十秒、Windows 上会卡住，且"很少有人用"），保留了编辑消息和拒绝改动。

| 产品 | 值得借鉴的消息级功能 | 来源 |
|---|---|---|
| Claude Code（桌面 "Code" 标签 + VS Code 插件） | **回退**：每条消息 "Rewind to here"（也可 Esc Esc、`/rewind`），菜单 "Restore code and conversation / Restore conversation / Restore code / Summarize from here / Never mind"，恢复后原提示回到输入框；VS Code 悬停菜单 "Fork conversation from here / Rewind code to here / Fork conversation and rewind code"；内部实现是**在该消息处分叉会话**（`--fork-session --resume-session-at`）；bash 和子 Agent 的改动不进 checkpoint。**队列**：排队行 "Edit in composer / Send now / Remove"，可拖动排序，⌘⏎ 立即发送并打断，排队和发送失败的消息重启后仍在。**视图**：Transcript view 三档 Normal / Thinking / Verbose（Ctrl+O 切换）；"Thought for 12s"；压缩为可折叠 "Compacted chat" 卡片；回合结束有简短回顾 + 下一步按钮。**审批与提问 2026-10 起改为在对话流内联显示**，不再压在输入框上方。**导航**：⌘F 搜全部记录；CLI 全屏模式 `{` `}` 在提问间跳转、**当前提问吸顶**、"N new messages" 回到底部；"Bookmark response"、"Pin as chapter" 目录。**会话**：Pin / Mark as unread / Rename / Fork / Archive，筛选 "Needs input / Working / Completed"，标签页圆点（蓝 = 等待授权、橙 = 隐藏时已完成）；`/export`。**错误**："Try again"、"Retry"，瞬时 API 错误重试最多 10 次并显示 "will retry in …"，额度重置后 "Continuing automatically at 3:45pm · Esc to cancel"。**通知**：会话完成且你不在看时发系统通知。其他：`/btw` 旁路提问、Tab 接受灰色建议提示、VS Code 显示 prompt cache 倒计时 | [Desktop](https://code.claude.com/docs/en/desktop)、[Checkpointing](https://code.claude.com/docs/en/checkpointing)、[VS Code](https://code.claude.com/docs/en/vs-code)、[CHANGELOG](https://github.com/anthropics/claude-code/blob/main/CHANGELOG.md) |
| Codex（ChatGPT 桌面 Codex 模式） | 可从更早的消息分叉（不回退文件）；CLI Esc Esc = "编辑上一条用户消息并从那里分叉"；**app-server 删除 `thread/rollback`，改用 `thread/fork(lastTurnId)`**；没有重新生成和版本箭头。**Steer vs Queue**："Follow-up behavior" 设默认值，快捷键对单条反转；排队消息可编辑、排序、发送、删除。活动聚合为 "Edited 3 files, explored 2 files, 2 searches"、"Ran 2 commands"（用户反复要求"总是展开"选项）。Review 面板范围 Unstaged / Staged / Commit / Branch / **Last turn**，逐文件逐块 stage / revert，行内 "+" 评论。⌘F / ⌘G 查找、消息间键盘跳转、搜索覆盖历史会话内容；选中文字 "Ask Codex"；`/export` 导出 Markdown；分享为只读快照（脱敏、不含工具调用）。**通知**：回合完成 never / 仅后台 / always，审批和提问单独开关，提示音可自定义，铃铛活动面板 "Mark all as read"。连接错误给 Retry 且不丢已保存消息 | [Changelog](https://learn.chatgpt.com/docs/changelog?type=codex-app)、[Steering](https://learn.chatgpt.com/docs/prompting#steering-and-queuing)、[Notifications](https://learn.chatgpt.com/docs/notifications) |
| Cursor 3.x | 编辑旧消息重发会丢弃之后的消息，弹窗 **"Revert files to this message?" → "Revert Files / Keep Files"**；悬停 "Restore Checkpoint"（只回退文件）；⋯ 菜单 "Fork Chat"。输入框上方 Review 条 "N Files" + **"Keep All / Undo All"**，逐文件 Keep / Undo；Changes 有 "Last Agent Turn" 范围。"Explored N …"、"Thought for 1s"、"Worked for <duration>"；**对话密度 Compact / Balanced / Detailed**；审批卡片说明原因（"This edits a file outside your workspace"）。Enter 排队，"Send now" 在下一次工具调用时插话（2026-08 起不再打断当前步骤）。**⌘↑/↓ 在自己的消息间跳转**，⌘F 带计数，⌘K 搜历史记录内容；"Copy Message"、"Copy Table"；文件链接右键 "Add to Chat"；选中文字 "Ask in Side Chat"。错误："This run hit an unexpected error. Retry or send a follow-up below."，Retry 不发新消息直接续跑。**系统通知**（失焦时合并、通知内可直接批准 / 拒绝）、"Completion Sound"、Dock 未读数 | [Agent](https://cursor.com/docs/agent/overview)、[Release notes](https://cursor.com/docs/release-notes)、[3.4](https://cursor.com/changelog/3-4) |
| VS Code Copilot（v1.109–1.144） | 编辑请求确认 "This will remove all subsequent requests and undo edits made to {N} files…"；"Retry"；**"Restore Checkpoint"（恢复工作区和对话）+ "Redo"**；"Fork Conversation"；运行中发送按钮是下拉 **"Add to Queue / Steer with Message / Stop and Send"**。三级 Keep / Undo（逐块、逐文件、全部）。自动生成的工具组摘要 "Read {N} files"、"Ran {N} commands"、"{N} tool calls failed"，**失败自动展开**；思考 "Thinking…" → "Finished with {N} steps"。终端审批 "Allow / Skip / **Explain**"，通用审批 "Allow Once / Allow in this Session / Always Allow"。输入框上方 "Todos (2/5)"。上下文条悬停显示 "Session Cost" 和 "Compact Conversation"；回复页脚悬停显示各模型输入 / 缓存 / 输出 token。"Copy All"、**"Copy Final Response"（不含思考和工具）**；"Export Chat..."（JSON / Zip）。**"Find in Chat"（⌘F，支持正则）、"Previous/Next User Prompt"（⌘⌥↑/↓）、当前提问吸顶（默认开）、提问时间轴**（"Go to prompt {i} of {n}"）。通知 `notifyWindowOnResponseReceived` / `notifyWindowOnConfirmation`（off / 窗口失焦 / always）+ 提示音 | [Checkpoints](https://code.visualstudio.com/docs/copilot/chat/chat-checkpoints)、[Review](https://code.visualstudio.com/docs/agents/run/review-code-edits)、[v1.110](https://code.visualstudio.com/updates/v1_110) |
| Zed Agent Panel | 点任意用户消息即可编辑（"Editing will restart the thread from this point."），无版本历史。**错误分类卡片**："Connection Interrupted / Rate Limit Reached / Context Too Large / Output Limit Reached / Authentication Required"，按钮 "Retry / Retry Generation / Copy Error Message"，**自动重试倒计时 "Retrying. Next attempt in N seconds (Attempt X of Y)"**；未确认送达的消息可 "Copy / Restore"。排队 "In Queue / Next in Queue"，逐条 "Steer" 开关。"Edits" 折叠区 "Reject All / Keep All"，拒绝后 "Undo" 提示。终端卡片 "Exited with code N"、"Stop This Command"；审批 **"Allow once / Allow for this thread / Allow always / Deny"**，提示滚出视野时浮出审批行。回复页脚 "Copy This Agent Response"、**"Scroll to User Message"**、"Open thread as Markdown"，可选回合耗时与 token（默认关）。右键 "Copy as Markdown"；"Search this thread…"。通知 `notify_when_agent_waiting`、`play_sound_when_agent_done`（never / when_hidden / always） | [Agent panel](https://zed.dev/docs/ai/agent-panel)、`crates/agent_ui` 源码 |
| Cline（VS Code + Cline Desktop） | "Edit and regenerate from here"，编辑时两个按钮 **"Reset Chat"（回退对话、保留代码）/ "Reset Code"（回退对话和代码）**，没有 checkpoint 时禁用；Desktop 助手消息 "Fork session"。回合收据 "Review last turn changes: N files, X additions, Y deletions"；"Worked for 4m 12s and made 14 tool calls"。API 失败 **"Retry / Start New Task"**，发送失败文字和附件回到输入框。**当前提问吸顶**（"Click to scroll to your message"）、Desktop 左侧提问大纲（悬停预览、点击跳转）。**选中文字 "Quote selection in reply"** 变成输入框上方的引用块。历史可按费用 / token 排序 | [CHANGELOG](https://github.com/cline/cline/blob/main/CHANGELOG.md)、[Checkpoints](https://docs.cline.bot/core-workflows/checkpoints) |
| Devin Desktop（原 Windsurf） | Enter 插话、⌘⏎ 排队；发送后立刻出现回退按钮，运行中也能回退；页脚 "Duplicate session"、"Response statistics"；选中文字 "Add to chat"（⌘L）；Changes 有 "Since last seen" 范围；完成或需要输入时每会话一条系统通知 | [Changelog](https://docs.devin.ai/desktop/changelog) |
| Kiro | 每个提示一个 checkpoint，"Restore" 回退代码和上下文；输入框上方 "Revert" 只撤销上一回合文件；"Export Chat" 导出 zip；Focus Mode 把所有会话待处理的提问 / 审批做成可就地回答的卡片 + 标题栏铃铛计数；Crew 有回合小地图 | [Checkpoints](https://kiro.dev/docs/checkpoints/)、[Focus mode](https://kiro.dev/docs/ide/experimental/focus-mode/) |
| Warp | 任意回复 "Fork conversation from here"、`/fork-from` 可搜索选择；设置 "Interrupt response / Queue until response finishes"；输入框占位文字随状态变化（"Steer the running agent" / "Ask a follow up"）；**⌘↑ 在提问间跳转**；`/export-to-file`；通知 Complete / Request / Error + 铃铛收件箱 | [Forking](https://docs.warp.dev/agent-platform/warp-agents/interacting-with-agents/conversation-forking)、[Queue](https://docs.warp.dev/agents/local-agents/interacting-with-agents/prompt-queueing/) |
| Antigravity 2.x | 每回合 Undo 弹出 "Confirm Undo" 列出文件，可选只回退对话；"N files changed" → Review "For Turn"；Retry "resumes the task directly, without adding an artificial 'Continue' message"，退避重试约 12 分钟并显示次数；**提问和审批停靠在输入框上方，可以继续输入或排队**（v2.26）；**对话导航条**（左侧每回合一个点，悬停显示提问、回复预览和时间，v2.28.1，2026-10-28）；选中文字 "Comment" / "Quote"（⌘L） | [Changelog](https://antigravity.google/docs/changelog/)、[Review](https://antigravity.google/docs/review-changes/) |
| JetBrains（AI Assistant / Junie / Air） | "Regenerate this response"；Agent 改动文件面板逐文件 revert + "Rollback"；Air 行内评论 → "Send Comments" | [AI chat](https://www.jetbrains.com/help/ai-assistant/ai-chat.html)、[Air](https://www.jetbrains.com/help/air/review-changes.html) |

**编码 Agent 的共性（按普及度排）**：
1. 排队 / 插话，队列可编辑（12/12）。
2. 编辑旧提示重发，并明确问"文件要不要回退"（约 9 家）。
3. 每回合改动摘要 + Keep / Undo（约 10 家）。
4. 工具活动折叠成一句话摘要 + 思考时长 + 回合时长（约 10 家）。
5. 上下文环 + 分解 + 一键压缩（约 12 家）。
6. "完成"与"需要处理"的通知（约 12 家）。
7. 错误恢复卡片：可见的重试倒计时、按错误类型给操作、Retry 不注入假的 "Continue"（约 10 家）。

其次是：
- 从任意消息分叉；
- 长会话导航：⌘F、提问间跳转、当前提问吸顶、提问导轨；
- 不阻塞输入框的行内审批 / 提问；
- 复制 / 导出的各种变体。

**每条消息显示 token / 费用在编码 Agent 里并不普遍**，多数是悬停或可选（Zed 默认关，Copilot 悬停），只有聊天客户端（LobeHub、Cherry、T3）放在页脚。

### 3.2 通用聊天客户端

| 产品 | 值得借鉴的消息级功能 | 来源 |
|---|---|---|
| ChatGPT | 编辑用户消息即分支；重试生成版本 `< N/N >`；回复 ⋯ 菜单 "Branch in new chat"（继承到此为止的历史，原对话不变）；"Ask to change response"（Try again / Add details / More concise / Think longer）；选中文字浮出 "Ask ChatGPT" 引用到输入框；思考中可 "Answer now"，运行中可 "Update" 追加上下文；快捷键 ⌘⇧C 复制最后回复、⌘⇧; 复制最后代码块。**没有**每条时间戳、对话内查找、大纲 | [UI 拆解](https://aiuxplayground.com/teardowns/chatgpt/output)、[Branch](https://itdaily.com/news/software/chatgpt-branch-conversations/)、[搜索](https://help.openai.com/en/articles/10056348-how-do-i-search-my-chat-history-in-chatgpt) |
| Claude.ai / 桌面 | 重试保留原回复，1/N 切换；编辑即分支；选中文字出现 **"Reply"**，变成输入框上方可删除的引用块；思考折叠为摘要；分享是快照链接（之后的消息不公开，MCP 原始输出不含） | [UI 拆解](https://aiuxplayground.com/teardowns/claude/output)、[分享](https://support.claude.com/en/articles/10593882-sharing-and-unsharing-chats) |
| Gemini | Regenerate 只对最新回复，箭头切换版本；任意消息 "Branch in new chat"（2026-04）；"Answer now" 跳过长推理但不换模型；⋯ → "Response details" 显示 Auto 下实际回答的模型 | [版本](https://support.google.com/gemini/answer/14262426)、[分支](https://www.androidpolice.com/this-small-gemini-update-solves-a-surprisingly-big-problem/)、[Answer now](https://9to5google.com/2026/01/18/gemini-answer-now/) |
| Copilot | "Try Again / Switch Model"（换模型重答，2026-10）；分享为快照，接收方发言即生成自己的副本 | [Release notes](https://learn.microsoft.com/en-us/copilot/microsoft-365/release-notes?tabs=all) |
| Perplexity | "Rewrite" 可选模型；选中文字 "Add to follow-up"、"Check sources"（跳到证据）；**Side Chat**（`/btw`）：对运行中任务的上下文快照提只读问题，不进主线程；任务只在"有新内容"时通知 | [Thread](https://www.perplexity.ai/help-center/en/articles/10354769-what-is-a-thread)、[Changelog](https://www.perplexity.ai/changelog/effort-mode-gpt-6-astra-and-skills-marketplace) |
| LobeHub v2.2 | 编辑最后一条用户消息即新分支；Codex Agent 编辑时提示 **"Sends in a new topic; the original stays as it is. Files Codex already changed are not reverted."**；"Restore to input" 把旧提示连附件放回输入框；选中文字 "Add to conversation"；页脚 "model · tok/s" + TTFT，"Generation Details" 卡片（缓存读写、推理、平均单价，费用 ≥ $0.20 才显示）；**⌘F "Find in Topic" 能搜未加载的历史**；长用户消息吸顶，完成的 Agent 回合折叠为摘要；压缩显示 "Compressed N earlier turns · View summary"，可切 "History summary / Original conversation"；导出 Markdown（可选含工具消息）/ PDF / JSON；完成时桌面通知 + 提示音 | [github.com/lobehub/lobehub](https://github.com/lobehub/lobehub) |
| Cherry Studio v2.1 | 消息存成树；`< i/N >` 版本切换，"Switch model answer"；"Branch Management" 画出分支图；**带签名的推理不能编辑**（"cannot be replayed after a change"）；Agent Fork 提示 "File changes will not be undone"；Token 详情卡：TTFT、模型 TPS 与端到端吞吐、费用标注 "Billed by provider / Estimated"、**耗时拆成 Model / Tool / Approval / Other**；⌘F 查找条（可含用户提问、大小写、全词）、⌘⇧F 全局消息搜索可跳到消息；"Message Anchor" 跳到第 N 回合；**只在成功且耗时 > 30 秒时通知**，内容是会话名 + 回复最后一句 | [docs.cherryai.com.cn](https://docs.cherryai.com.cn)、[Releases](https://github.com/CherryHQ/cherry-studio/releases) |
| Open WebUI v0.12 | 编辑：Save / Cancel / Send，↑ 编辑上一条；版本计数可双击跳号；"Fork chat"；"Overview" 分支树（压缩节点标 "Context compacted"）；选中文字 "Ask / Explain"；运行中队列 "Send now"；**快捷键允许 / 拒绝工具调用 ⌘⌥⏎ / ⌘⌥⌫**；只在用户离开时发通知 | [功能](https://docs.openwebui.com/features/chat-conversations/chat-features)、[快捷键](https://docs.openwebui.com/features/chat-conversations/chat-features/keyboard-shortcuts) |
| LibreChat v0.8.9 | 编辑："Save" 或 "Update & rerun"；截断回复显示 "Continue"；Fork 选项（仅可见消息 / 含相关分支 / "Start fork here" / "Leave out files"）；"Add to chat" 最多 10 段摘录；悬停显示模型与时间戳；上下文表显示"按当前增速还能聊 ≈N 轮"、"现在压缩可回收 ≈N tokens"、"最大的工具输出"；**消息导轨**（每条一个刻度，悬停预览、点击跳转、拖动）；设置项 "While generating, Enter will: Steer / Interrupt / Queue"，插话回执 "Applied. The agent received this message while it was responding." | [消息操作](https://www.librechat.ai/docs/features/message_actions)、[Fork](https://www.librechat.ai/docs/features/fork)、[用量](https://www.librechat.ai/docs/configuration/token_usage) |
| Chatbox | 失败后 **"Retry from last step" / "Retry whole message"**；**删除旧消息或换模型前提示会让缓存失效**；可开关的元数据小标签（模型、tokens、cached、首 token 延迟、时间）；"Worked for {time}"、"Paused after N steps"；小地图 "Jump to message N"；**每个会话记住滚动位置**；插话标注 "Interjected · seen by the model" | [Changelog](https://chatboxai.app/en/help-center/changelog)、[源码](https://github.com/chatboxai/chatbox) |
| Msty Studio | "Edit as Branch"、Branch Explorer 可并排 diff 两个分支；书签栏；长消息滚动时操作条悬浮；复制可选 Markdown / HTML / 富文本并记住；⌘F 会话内、⌘⇧F 跨会话搜索；小地图 | [Docs](https://docs.msty.ai/studio/conversations/responses) |
| T3 Chat | 页脚 "GPT-5.4 (High) · 59.17 tok/sec · 3276 tokens · Time-to-First: 4.3 sec · 1 tool call"；"branch off with another model"（换模型分叉拿第二意见） | [issue](https://github.com/pingdotgg/t3code/issues/2518) |

**共性（按普及度排）**：编辑重发 + 重试 + 版本切换（几乎全部）→ 从任意消息分叉（ChatGPT、Gemini、LibreChat、Open WebUI、Cherry）→ 选中文字引用到输入框（Claude、ChatGPT、LibreChat、LobeHub、Open WebUI、Perplexity）→ 每条回复的统计页脚（LobeHub、Cherry、Chatbox、T3、LibreChat）→ 长会话导航：⌘F、跨会话跳到消息、导轨 / 小地图（Cherry、LobeHub、Chatbox、Msty、LibreChat）→ 智能完成通知（Cherry、Open WebUI、LobeHub、LibreChat）。

## 4. 差距矩阵

✓ 有 · ◐ 部分 · ✗ 没有 · ? 未核实 · — 不适用。"聊天类"指 ChatGPT / Claude.ai / Gemini，"开源"指 LobeHub / Cherry Studio / LibreChat / Open WebUI。

| 功能 | TCode | Claude Code | Codex | Cursor | Copilot | Zed | 聊天类 | 开源 |
|---|---|---|---|---|---|---|---|---|
| 排队 / 插话 | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | ◐ | ✓ |
| 工具活动折叠 + 思考 / 回合时长 | ✓ | ✓ | ✓ | ✓ | ✓ | ◐ | — | ◐ |
| 每回合改动卡片 + 回退 | ✓ | ✓ | ◐ | ✓ | ✓ | ✓ | — | ◐ |
| 上下文用量 + 分解 | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | ✗ | ✓ |
| 计划 / 提问 / 子 Agent 视图 | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | — | ◐ |
| **编辑旧消息并重发** | ✗ | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ |
| **回退到某条消息（对话 / 代码）** | ◐ 仅整回合文件 | ✓ | ◐ | ✓ | ✓ | ✗ 已移除 | — | — |
| **从某条消息分叉** | ◐ 仅整会话 | ✓ | ✓ | ✓ | ✓ | ✗ | ✓ | ✓ |
| 重新生成 + 版本切换 1/N | ✗ | ✗ | ✗ | ✗ | ◐ Retry | ✗ | ✓ | ✓ |
| **出错重试按钮 + 重试倒计时** | ✗ | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ |
| **完成 / 待处理系统通知 + 提示音** | ✗ | ✓ | ✓ | ✓ | ✓ | ✓ | ◐ | ✓ |
| **对话内查找 ⌘F** | ✗ | ✓ | ✓ | ✓ | ✓ | ✓ | ✗ | ◐ |
| **提问间跳转 / 提问导轨** | ✗ | ✓ | ✓ | ✓ | ✓ | ✓ | ✗ | ✓ |
| 当前提问吸顶 | ✗ | ◐ CLI | ? | ✗ | ✓ | ✗ | ✗ | ◐ |
| 每条消息复制 / 复制为 Markdown | ◐ 仅最后一条 | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ | ✓ |
| 选中文字引用到输入框 | ✗ | ? | ✓ | ◐ | ✗ | ✗ | ✓ | ✓ |
| 每条消息模型 / token / 耗时 | ◐ 时间戳 | ◐ | ✗ | ✗ | ◐ 悬停 | ◐ 可选 | ◐ | ✓ |
| 审批：总是允许 / 快捷键 / 原因说明 | ◐ 仅一次 / 本会话 | ✓ | ✓ | ✓ | ✓ | ✓ | — | ◐ |
| 审批 / 提问不阻塞输入框 | ✗（刻意设计） | ✓ | ◐ | ◐ | ◐ | ◐ | — | — |
| 导出 Markdown / JSON | ✗ | ✓ | ✓ | ? | ✓ | ✓ | ◐ | ✓ |
| 跨会话内容搜索 | ✗ 仅标题 | ? | ✓ | ✓ | ? | ◐ | ✓ | ✓ |
| 会话未读 / 需处理状态 | ◐ 等待圆点 | ✓ | ✓ | ✓ | ✓ | ? | ✗ | ◐ |
| 旁路提问 `/btw` | ✗ | ✓ | ✓ | ✓ | ✓ | ✗ | ◐ | ✗ |
| Diff 行内评论批量发送 | ✗ | ✓ | ✓ | ? | ✓ | ✗ | — | ✗ |
| 逐块 Keep / Undo | ✗ | ✓ | ✓ | ✓ | ✓ | ✓ | — | — |
| 下一步建议（Tab 接受） | ✗ | ✓ | ◐ beta | ? | ◐ 按钮 | ✗ | ◐ | ✓ |
| 回复点赞 / 点踩 | ✗ | ✗ | ✗ | ? | ✓ | ✓ | ✓ | ✓ |

**读法**：前五行是 Agent 特有能力，TCode 已经跟上。粗体行是**几乎所有竞品都有而 TCode 没有**的，这就是"缺很多实用功能"的来源，集中在两类：消息级操作（编辑 / 回退 / 分叉 / 重试 / 复制）和长会话可用性（查找 / 跳转 / 通知）。

## 5. TKCode.Arc 组件盘点

### 5.1 版本与包结构（先看这个，决定能用哪些）

- TCode 当前用 `TKCode.Arc 0.3.2-preview.1`（`Directory.Packages.props` 的 `ArcVersion`），只引用了 `TKCode.Arc`、`TKCode.Arc.Icons.Lucide`、`TKCode.Arc.Terminal`。
- 文档描述的是今天（2026-10-30）发布的 **0.3.2-preview.2**。
- **AI 组件全部在单独的 `TKCode.Arc.Special` 包里**，TCode 还没引用：
    - preview.1 只有 5 个：`ToolCall` / `ToolCallGroup`、`AgentStatus`、`AgentPlan`、`AgentTimeline`、`ContextWindow`。
    - 其余约 46 个（`ReasoningBlock`、`ResponseFeedback`、`GenerationStats`、`CodeSuggestion`、`SourceCards`、`StarterPrompts`、`SpeechPlayer` 等）**只在 preview.2**。preview.2 的 CHANGELOG 没提这些组件。
- 整块的 AI 界面（`AiChat`、`AiComposer`、`ModelPicker` 等）在 `TKCode.Arc.Blocks`，需要 `UseArcBlocks()`，DLL 10.5 MB 并依赖 `TKCode.Arc.Audio`。llms.txt 说 Blocks 还没上 NuGet，实际已经上了。
- 升级到 preview.2 有一个**破坏性变更**：内联选项类型（`SegmentedOption`、`HoverCardStat`、`ReactionOption` 等）改为 `AvaloniaObject`，要重新编译并在 UI 线程创建。TCode 有 12 处 `SegmentedControl`。
- 通用组件（`CopyButton`、`Toast`/`ToastStack`、`JsonViewer`、`ConfirmMorph`、`IconMorph`、`ShortcutKeyCap`、`ExpandingSearch`、`HoverCard`、`Timeline`、`UsageMeter`、`FloatingButtonGroup` 等）在核心包里，**preview.1 已经有**，不用升级就能用。

### 5.2 AI 组件 → TCode 映射

结论：**逐个借用组件，不整体替换对话区**。`ChatThread` 面向人与人聊天（最多保留 64 个渲染体、顶部插入旧消息不保持位置），`AiChat` 自带会话与分支模型，都和 TCode 的 StreamMark 虚拟化记录、Host 事件流冲突（之前接入 Arc 时也因此没换 ChatThread / Markdown）。

| Arc 组件 | 包 / 版本 | 对应 TCode 功能 | 建议 |
|---|---|---|---|
| `FeedbackActions`（`ResponseFeedback` 的行） | Special p2 | 助手消息操作行：复制 / 重新生成 / 👍👎；`Leading` 放按钮，`Trailing` 放时间或 token | **用**。关闭评分（BYOK 客户端没有回传对象）或只本地记录，主要拿它的操作行和 `RegenerateRequested` |
| `GenerationStats` | Special p2 | 每条回复的 TTFT、tokens/s（带迷你曲线）、输出 token、费用，展开看延迟条和缓存命中 | **用**（`Variant=Inline`），配合 G6。费用需要 Host 回传模型与用量 |
| `ReasoningBlock` | Special p2 | 思考块：计时、两行流式预览、按 `**标题**` 分步骤、复制、Stop / Retry | **可替换**现有思考模板。现有实现已不错，收益是"分步骤 + 复制 + 出错重试"，优先级中 |
| `ToolCall` / `ToolCallGroup` | Special p1 | 工具调用行：状态含 approval / denied、耗时、Input/Output 带复制、JSON 输出折叠、Retry；组"Used 4 tools"自动折叠 | **借鉴为主**。现有 Explore 合并、bash 跟尾、diff、和 StreamMark 虚拟化的集成比它强；可借它的"行内审批"、"耗时常显"、"输出复制"、"失败重试"四点，或先用在子 Agent 页验证 |
| `AgentStatus` | Special p1 | 状态胶囊：当前步骤 + 计时 + 暂停 / 停止 / 批准，展开看最近步骤 | 可替换输入框上方的 "Thinking" 微光 + 秒数，把 Stop 也放进去；优先级中 |
| `AgentPlan` | Special p1 | 可编辑计划清单（改名、拖动、增删），批准后变实时清单，失败步骤 Retry / Skip | TCode 的计划是 Markdown 文件（Claude Code 式），结构不匹配；**适合 TaskOrb 里的 todo 列表**（运行态） |
| `AgentTimeline` | Special p1 | 多 Agent 时间轴：思考 / 工具 / 等待区段、交接、token 与费用 | 子 Agent 多时的可视化，P2 |
| `ContextWindow` | Special p1 | 200 格上下文网格 + 压缩阈值线 | 现有圆环 + 分解卡已实现，不替换 |
| `CodeSuggestion` | Special p2 | 单文件逐块 Accept / Reject 的内联 diff | 用在"审批卡片的编辑预览"或未来"先审后写"模式；文档明确不适合多文件大 diff |
| `SourceCards` / `SourceCitation` | Special p2 | 引用来源卡片 + 正文 `[n]` | Fetch / 搜索类工具结果的来源展示，P2 |
| `StarterPrompts` | Special p2 | 空会话的开场卡片 | 新会话空状态，P2 |
| `SpeechPlayer` / `DictationButton` | Special p2 | 朗读回复 / 语音输入 | 需要接平台语音能力（AOT 下要单独验证），P2 |
| `AiSpark` | Special p2 | 轻量的状态动画标记 | 可用于侧栏运行状态，非必要 |
| `AiChat`（Blocks） | Blocks p1 | 完整聊天面：编辑 / 重试生成新版本，"2 / 2" 版本切换 | **不整体采用**，但它的版本切换交互是 G1/G2 的参考 |

### 5.3 通用组件（核心包，preview.1 已有）

| 组件 | 用在哪 |
|---|---|
| `CopyButton` + `IconMorph` | 替换手写的复制按钮（复制后变对勾），给用户消息、每条助手消息、工具输出加复制 |
| `Toast` / `ToastStack` | "已复制"、"已回退 3 个文件"、"已导出"、限流重试提示 |
| `JsonViewer` | 审批卡片和工具详情里的参数（现在是 120px 的原始 JSON 文本），支持折叠、搜索、复制路径；吃 `JsonNode`，AOT 安全 |
| `ConfirmMorph` | 行内确认 + 撤销：回退回合、回退到此处 |
| `ShortcutKeyCap` | 审批按钮旁显示快捷键（如 "允许一次 ⏎"） |
| `ExpandingSearch` / `SearchField` | ⌘F 对话内查找条（数据来自 StreamMark `SearchModel`） |
| `FloatingButtonGroup` | 悬停时浮出的消息操作条 |
| `Timeline` / `Stepper` | 回退点（checkpoint）列表 |
| `UsageMeter` | 会话累计 token / 费用 |
| `InlineEdit` | 用户消息原地编辑（G1 的一种做法） |

### 5.4 接入注意事项

1. **注册**：Special 要加包并在 `Program.UseTKCodeArc()` 里调 `UseArcSpecial()`（Blocks 是 `UseArcBlocks()`）。AGENTS.md 要求所有启动 `App` 的 `AppBuilder` 都走这里（桌面、无头测试、smoke、启动 bench）。
2. **版本同步**：除 5 个 p1 组件外都要把所有 Arc 包一起升到 preview.2，并处理上面的破坏性变更。
3. **本地化**：AI 组件的文字都在 `*Labels` 对象里（`ToolCallLabels`、`ReasoningBlockLabels` 等），只有英文，Special 没有语言目录。TCode 要从 `strings.tsv` 生成 Labels，并在切换语言时替换。文档还提醒：启用的包缺少声明的语言时启动会失败，需要实测 en-US + zh-CN。
4. **AOT**：文档说无反射、可裁剪；仍按 AGENTS.md 做一次 0 个 `IL####` 警告的发布 + `--smoke`。Special p2 是 5.7 MB 的 DLL。
5. **模拟数据**：很多 Special 组件没接数据时会自己播放模拟（如 `ResponseFeedback` 没有 `FeedbackHandler` 时假装发送、`ToolCall` 的 Allow/Retry 未处理时走模拟）。必须自己驱动状态并设 `Handled`。
6. **缩放**：带弹层的组件（`SourceCards` 悬停预览、`HoverCard` 等）要过 `ZoomHost` / `UiZoomTests` 规则。

## 6. 推荐功能清单

工作量：S = 只改 Desktop 的小改动；M = 涉及新 RPC / 事件或多个视图；L = 需要先写设计文档。

### 6.1 P0：先做

#### R1 出错重试 + 可见的自动重试（M）

- **交互**：
    - 错误卡片加"重试"（用现有历史续跑，不注入"继续"消息）和"复制错误"。
    - 按错误类型给文案和动作：
        - 限流 / 过载：倒计时"8 秒后重试（2/3）"。
        - 上下文超限："压缩后重试"。
        - 认证失败："打开设置"。
        - 网络中断：重试。
    - Esc 停止 Agent。
- **可行性**：失败 / 中止的助手消息在构造请求时已被跳过（`TKCode.Ai/Transform/MessageTransformer.cs` L30），所以重试 = 让 Agent 以现有历史再跑一轮，不改历史，符合只追加约束。超窗时已有"压缩后重试一次"（`TKCode.Agent/Agent.cs` L299）。
- **改动**：
    - 新增"续跑"RPC（或允许 Prompt 空内容续跑），按 AGENTS.md 改四处。
    - 新增 `RetryScheduled` 事件（次数、延迟、原因），让 `HttpWireApi` 现在静默的重试显示出来。
    - Desktop 错误卡片。
- **Arc**：`Alert`（已用）、`Toast`、`CopyButton`。
- **参考**：
    - Zed 的错误分类卡片和 "Retrying. Next attempt in N seconds (Attempt X of Y)"。
    - Antigravity 的 Retry "without adding an artificial 'Continue' message"。
    - Cursor 的 "Retry or send a follow-up below"。
    - Chatbox 的 "Retry from last step / Retry whole message"。

#### R2 系统通知 + 提示音 + 未读（M–L）

- **交互**：
    - 窗口失焦时，三种情况发系统通知：回合完成、需要审批、有提问。"完成"和"需要处理"分开设置：从不 / 仅后台 / 总是。
    - 可选完成提示音。点通知跳到对应会话。
    - Dock / 托盘显示未读数。
    - 默认只在"成功且耗时超过 30 秒"时通知完成（Cherry 的做法），阈值可配。
- **改动**：以 Desktop 为主，三个平台各自接：
    - macOS：`UNUserNotificationCenter`（ObjC runtime P/Invoke，AOT 需验证）。
    - Windows：Toast，需要 AppUserModelID / COM。跳转列表的 COM 代码也还没在真机验证过。
    - Linux：D-Bus `org.freedesktop.Notifications`。

  可以先做"提示音 + Dock 跳动 / 角标 + 应用内 Toast"，系统通知再按平台跟进。`ask-user.md` L199 和 `plan-mode.md` §17 里"暂不做"的说法要同步改。
- **Arc**：`ToastStack`、`Badge`。
- **参考**：
    - Codex 的通知设置和铃铛面板。
    - Copilot 的 `notifyWindowOnResponseReceived` / `notifyWindowOnConfirmation`。
    - Zed 的 `when_hidden`。
    - Cursor 失焦时合并通知，通知里可直接批准。

#### R3 消息操作补全（S）

- **悬停操作条**：每条助手消息（现在只有每回合最后一条）和每条用户消息都出现操作条，含复制、复制为 Markdown。助手回合再加"复制最终回复（不含思考和工具）"（Copilot "Copy Final Response"）。
- **右键菜单**：重新打开 Markdown 右键菜单（复制 / 复制为 Markdown / 全选 / 查找），文案已经接好。
    - 当初关掉，是因为 AtomUI 主题下 `MenuFlyoutPresenter` 没有模板。
    - AtomUI 已移除，需验证 Arc 主题是否提供这个模板；不提供就让 StreamMark 使用可注入的菜单（Arc `ContextMenu`）。
- **工具行与时间**：
    - 工具行常显耗时和退出码（退出码已解析但没显示）。
    - 工具输出加复制。
    - 用户消息悬停显示时间。
- **跳转与快捷键**：
    - 回复页脚加"跳到对应提问"（Zed "Scroll to User Message"）。
    - 快捷键：⌘⇧C 复制最后回复、⌘⇧; 复制最后代码块、Esc 停止。
- **Arc**：`CopyButton` + `IconMorph`（复制后变对勾）、`FloatingButtonGroup`、`Tooltip`、`ShortcutKeyCap`。升级到 preview.2 后，操作条可换成 `FeedbackActions`（`Leading` 放复制，`Trailing` 放时间 / token，`AllowsRegenerate`）。

#### R4 ⌘F 查找 + 提问间跳转（S–M）

- **⌘F 查找条**：
    - 输入即高亮，⌘G / ⌘⇧G 切换下一个 / 上一个，显示计数"3/12"，可选"包含我的提问"。
    - 数据来自 StreamMark 已有的 `TranscriptView.Search`，覆盖虚拟化内容。
    - 待确认：分页还没加载的更早历史能不能搜到（LobeHub 能）。不能的话，要么先加载全部，要么在 Host 侧搜。
- **提问间跳转**：⌘⌥↑/↓ 在自己的提问之间跳转，用 `TranscriptView.GetOutline()` + `ScrollToPoint`。
- **滚动位置**：切换会话时记住滚动位置。现在总是滚到底（`MainWindow.axaml.cs` L357）。
- **Arc**：`ExpandingSearch` 或 `SearchField`、`ShortcutKeyCap`。
- **参考**：Copilot "Find in Chat" 和 "Previous/Next User Prompt"、Cursor ⌘↑/↓、Warp ⌘↑、Cherry 的查找选项、Chatbox 记住滚动位置。

#### R5 编辑重发 / 回退到此 / 从此分叉（L，先写设计文档）

- **交互**（悬停在用户消息上）：
    - **编辑并重发**：原地编辑，或回填到输入框（Claude Code 回退后的做法、LobeHub "Restore to input"）。提交时问"同时回退这之后的文件改动吗？回退文件 / 保留文件"，Cursor、Cline、Roo 都是这个二选一。
    - **回退到这里**：Claude Code 式三选一"恢复代码和对话 / 只恢复对话 / 只恢复代码"，恢复后原提示回到输入框。
    - **从这里分叉**：新会话，包含到这条为止的历史（ChatGPT / Gemini "Branch in new chat"、Codex、Cursor "Fork Chat"）。可选"换个模型分叉"（T3）。
- **实现方案**（待决策，见 §8）：
    - **方案 A**：一律"在该消息处分叉成新会话"。
        - 先例：Codex 把 `thread/rollback` 换成了 `thread/fork(lastTurnId)`，Claude Code 桌面版的回退内部也是 `--fork-session --resume-session-at`。
        - 改动小：现有 `ForkSessionAsync`（`AgentHostServer.cs` L232）已经复制整条分支，加一个"截止消息 id"参数即可。
        - 完全符合只追加约束，不动 Agent 内存状态。
        - 代价：会话列表多出分叉会话（可以折叠在原会话下，或标"分叉自…"）。
    - **方案 B**：同一会话内分支（树的叶子切换），像 ChatGPT / Claude.ai 那样出现"< 1/2 >"。
        - 会话文件本身就是 id/parentId 树（`SessionEntry.cs` 注释："分支 = 从某个旧节点继续追加"）。
        - 但 `SessionStore.Read` 现在把最后一行当叶子。要做 B，需要：新增"当前叶子"记录、历史 DTO 带上分支信息、Agent 重建上下文、UI 加版本切换。
        - 工作量大一档，而且调研的编码 Agent 里没有一家做版本切换。
    - **推荐 A 先行**，B 视反馈再做。
- **代码回退**：
    - 复用现有整回合 Revert（SHA-256 校验），对选中消息之后的回合倒序回退。
    - 只覆盖 TCode 自己的编辑工具，bash / MCP / 子 Agent 改的文件不在内，UI 要说清楚（Claude Code、Kiro 都在文档里写明）。
    - **不做整仓快照**：Zed 刚因性能问题撤掉了这种做法。
- **约束**：
    - 只允许编辑用户消息。思考签名必须原样回放，不允许改助手的推理（Cherry："cannot be replayed after a change"）。
    - 外部运行时（Claude Code / Codex / ACP）现在调用 fork 会直接报错，先隐藏入口；以后可以映射到它们自己的 fork 能力。
- **Arc**：`ConfirmMorph`（行内确认 + 撤销）、`DialogSurface`（文件二选一）、`Toast`（"已回退 4 个文件"）、`InlineEdit`。

### 6.2 P1：第二批

| # | 功能 | 交互要点 | 参考 | Arc 组件 | 改动范围 | 量 |
|---|---|---|---|---|---|---|
| R6 | 审批卡片增强 | 快捷键（⏎ 允许一次 / ⌘⏎ 本会话 / Esc 拒绝）并标在按钮上；"拒绝并说明原因"；"总是允许"（规则存储需设计，`mcp.md` L383 已列）；文件编辑显示 diff 预览；参数用可折叠 JSON；说明为什么要审批 | Zed、Copilot "Always Allow"、Open WebUI ⌘⌥⏎、Cursor 原因说明 | `JsonViewer`、`ShortcutKeyCap` | Desktop + 设置 | M |
| R7 | 每回合统计页脚 | 一行显示"模型 · 耗时 · 输入 / 输出 / 缓存 token · tok/s · TTFT · 估算费用"；悬停详情卡把耗时拆成模型 / 工具 / 审批；设置可关 | T3、Cherry、LobeHub、Zed `show_turn_stats`、Copilot 悬停 | `GenerationStats`（p2）或自绘 + 现有 HoverCard | Host：`MessageEnded` 和历史 DTO 加模型 id、TTFT；Desktop 用模型目录价格算费用 | M |
| R8 | 选中文字引用到输入框 | 选中对话文字时浮出"引用"按钮，选中内容变成输入框上方可删除的引用块 | Claude "Reply"、Cline "Quote selection in reply"、Codex "Ask Codex"、LibreChat "Add to chat" | `FloatingButtonGroup`；ComposerEditor 新增 quote 标签 | Desktop（StreamMark 已有 `Selection.Changed`、`GetSelectedMarkdown()`） | M |
| R9 | 导出与跨会话搜索 | 会话菜单"导出 Markdown（可选含思考 / 工具）/ JSON"、"复制整个对话"；⌘K 能搜会话内容，结果可跳到具体消息 | Claude `/export`、Zed "Open Thread as Markdown"、Copilot "Export Chat"、Codex、Cherry ⌘⇧F | `SearchField`、`Toast` | 导出只改 Desktop（GetHistory）；内容搜索要在 Host 加 RPC 扫 JSONL | M |
| R10 | 当前提问吸顶 + 提问导轨 | 滚动时顶部显示当前回合的提问，点击回到它；右侧窄导轨每回合一个点，悬停预览提问和时间 | Copilot（默认开）、Cline、Antigravity 导航条、LibreChat、Chatbox | `HoverCard`；导轨自绘 | Desktop（GetOutline） | M |
| R11 | 排队增强 | 拖动排序；设置"运行中 Enter = 排队 / 插话"，⌘⏎ 对单条反转；"停止并发送"；插话显示"已送达"；排队消息重启后保留（现在只在内存里） | Codex、Copilot 三选一、Claude Code、LibreChat、Chatbox | 现有 `ExpandableText` | Desktop | S–M |
| R12 | 思考块与运行状态换 Arc 组件 | 思考块换 `ReasoningBlock`（分步骤、复制推理、Stop / Retry）；输入框上方的"Thinking + 秒数"换 `AgentStatus`（当前步骤 + 计时 + 停止） | Copilot "Finished with N steps"、Claude "Thought for 12s" | `ReasoningBlock`（p2）、`AgentStatus`（p1） | Desktop，`ReasoningBlock` 需先升级 Arc | M |
| R13 | 会话状态与未读 | 侧栏"标为未读"、"隐藏时已完成"标记、按"需要处理 / 运行中 / 已完成"筛选 | Claude Code、Codex、Copilot | `Badge` | Desktop | S |
| R14 | 缓存感知提示 | 编辑旧消息、换模型、分叉前提示"会让提示缓存失效，下一次请求更慢更贵"；可选显示缓存剩余时间 | Chatbox、Claude Code VS Code 版的缓存倒计时 | `Tooltip`、`Alert` | Desktop（缓存 TTL 已有设置） | S |

### 6.3 P2：之后再看

| # | 功能 | 说明 | 参考 | Arc 组件 | 量 |
|---|---|---|---|---|---|
| R15 | 旁路提问 `/btw` | 基于当前上下文的快照提问，回答不进主历史，天然符合只追加约束；需要 Host 新 RPC（一次性请求，不落盘） | Claude Code、Codex `/side`、Cursor、Perplexity Side Chat | 侧边面板 | M |
| R16 | Diff / 计划行内评论批量发送 | 在 diff 行或计划段落上写评论，一次作为一条消息发出 | Claude Code 桌面版、Codex、Antigravity、JetBrains Air | `CommentThread` | L |
| R17 | 逐块 Keep / Undo | Changes 面板逐块撤销，或"先审后写"模式 | Cursor、Copilot、Zed | `CodeSuggestion`（仅单文件） | L |
| R18 | 视图密度 | 紧凑 / 均衡 / 详细三档，控制工具组默认展开程度 | Cursor 3.4、Claude Code Transcript view | `SegmentedControl` | S–M |
| R19 | 下一步建议 | 回合结束后输入框里出现灰色建议，Tab 接受；需要额外一次模型调用 | Claude Code、Codex "Composer predictions" | `GhostText`（p2） | M |
| R20 | 审批 / 提问不阻塞输入框 | 2026 年的趋势：Claude Code 桌面版 10 月起改为内联显示，Antigravity 停靠在输入框上方并允许继续输入 / 排队；与 TCode 现有"面板替换输入框、等待时不能发送"的决定相反 | Claude Code、Antigravity | — | M |
| R21 | 其他 Arc AI 组件 | TaskOrb 的 todo 列表换 `AgentPlan` 运行态；子 Agent 多时用 `AgentTimeline`；新会话空状态用 `StarterPrompts`；Fetch 结果用 `SourceCards`；朗读用 `SpeechPlayer`；只在本地记录的点赞 / 点踩用 `FeedbackActions` | — | 见 §5.2 | 各 S–M |

## 7. 实施路线与风险

### 7.1 建议顺序

1. **阶段 1：只改 Desktop，只用 preview.1 已有组件**
    - R3 消息操作补全。
    - R4 查找与跳转。
    - R13 未读状态。
    - R11 中不涉及持久化的部分。
    - R14 缓存感知提示。
2. **阶段 2：小幅 Host / RPC 改动**
    - R1 出错重试（续跑 RPC + `RetryScheduled` 事件）。
    - R2 通知。
    - R7 统计页脚（DTO 加模型、TTFT）。
3. **阶段 3：先写设计文档，再实现**
    - R5 编辑 / 回退 / 分叉，设计文档写在 `docs/design/message-rewind.md`。
    - R6 审批增强（含"总是允许"规则）。
    - R8 引用。
4. **与以上并行：Arc 升级**
    - 升到 0.3.2-preview.2，加入 `TKCode.Arc.Special`。
    - 处理 `SegmentedOption` 等的破坏性变更，从 `strings.tsv` 生成 `*Labels`。
    - AOT 发布验证。
    - R7 的 `GenerationStats`、R12、`FeedbackActions` 都依赖这一步。
5. **阶段 4**：P2 按反馈挑选。

### 7.2 风险与约束

- **历史只追加**：
    - R5 走分叉。
    - R1 的重试不改历史：失败消息本来就在请求时跳过。
    - R15 的旁路提问不落盘。
    - "删除消息"、"隐藏消息"、"解除压缩"这类改写历史的功能都不做。压缩只提供只读的原文查看。
- **文件回退范围**：只覆盖 TCode 编辑工具的改动，UI 必须说明。不做整仓快照（Zed 的教训）。
- **提示缓存**：编辑、分叉、换模型都会让分叉点之后的缓存失效，需要提示（R14）。
- **外部运行时**：编辑、回退、分叉、重试先只支持内置 Agent，外部运行时的入口隐藏或置灰。
- **AOT**：通知要接各平台原生接口；Special 新增 5.7 MB 的 DLL。每一步都要 0 个 `IL####` 警告并跑 `--smoke`。
- **Arc preview.2**：今天刚发布，有破坏性变更。Special 的文字只有英文，没接数据时会自己跑模拟。带弹层的组件要过 `UiZoomTests`。
- **验证方式**：按 AGENTS.md，用无头快照测试验证 UI，不驱动真实桌面。
- **盘点时发现的过期文档**：
    - `docs/architecture.md` L199 仍说 Markdown 不支持表格和图片。
    - `docs/design/external-agent-runtimes.md` L3 仍写"尚未实现"。

## 8. 待决策问题

已定（用户，2026-10-30）：
- 编辑 / 回退 / 分叉一律分叉成新会话，设计见 [message-rewind.md](message-rewind.md)。
- 现在就升级到 Arc 0.3.2-preview.2 并引入 `TKCode.Arc.Special`（已完成）。
- 系统通知三个平台一起做，设计见 [notifications.md](notifications.md)。

仍待定（P1 / P2 开始前再定）：

1. **每回合统计页脚默认开还是关？费用要不要显示？** 自带 Key 时费用只能按模型目录估算。
2. **审批 / 提问要不要改成不阻塞输入框？** 这会推翻 `ask-user.md` 现有的决定。
3. **点赞 / 点踩要不要做？** 没有回传对象，只能本地记录。
