# 向用户提问：`ask_user` 工具与问答面板

状态：已实现（2026-10-18）。实现与设计的差异见 §13
关联文档：[builtin-agent-tools.md](builtin-agent-tools.md)、[composer.md](composer.md) §18、[external-agent-runtimes.md](external-agent-runtimes.md) §6、[mcp.md](mcp.md)

## 1. 目标

- 内置 Agent 遇到确实需要用户决定的问题时，可以向用户提出结构化的选择题，并等待回答，然后在同一轮里继续。
- 外部运行时自带的提问也在同一个面板里显示和回答：Claude 的 `AskUserQuestion`、Codex 的 `item/tool/requestUserInput`。ACP 的 `elicitation/create` 是通用表单，复用现有的 MCP 表单卡片。
- 顺带修掉审批和表单卡片的几个遗留问题（§10）。

## 2. 已确认的决策（用户，2026-10-18）

| 维度 | 决策 |
|---|---|
| 面板位置 | 待回答时，问答面板**占据输入框的位置**，键盘焦点在面板上。回答后，转录里留一张只读的问答卡片 |
| 范围 | TKCode 只给**内置 Agent** 加 `ask_user` 工具。外部运行时用它们自己的提问工具，TKCode 只负责显示和回答，不改它们的工具，也不为 Codex 开实验接口 |
| 可用模式 | 所有主会话模式（Agent / Plan / ACP）都有这个工具；子 Agent 没有 |
| 待回答时发消息 | **不允许**。必须先回答或拒绝回答（Esc），输入框才会回来 |

## 3. 调研结论（2026-10-18）

参考了 Claude Code 2.1.302 `AskUserQuestion`、Codex 0.163.0 `request_user_input`、Gemini CLI `ask_user`、OpenCode `question`、Cursor AskQuestion、Cline、Roo Code。这些实现的共同点如下：

- **形状**：每次 1–4 个问题。每个问题有一个短标签（12–30 字符），显示成标签页；有一句完整的问题；有 2–4 个选项，每个选项是标签加说明。推荐项放第一个，标签后加 "(Recommended)"。"其他 / 自由输入"由客户端自动加，不让模型写。
- **交互**：
  - 多个问题用标签页切换。
  - 数字键直接选择，回车进入下一题，最后一步汇总后提交。
  - Esc 拒绝回答。Codex 例外，它的 Esc 会中断整轮。
- **结果**：用一行自然语言回给模型，以问题原文为键，例如 `"问题"="答案"`。拒绝、未回答、用户自己写的答案，分别用不同的文本。
- **范围**：子 Agent 一律不能用。Codex 只在 Plan 模式下可用；Claude 在所有模式下都可用，但计划审批走另一个工具。
- **教训**：Cursor 把传输失败也当成"用户跳过"返回，模型因此误判。TKCode 中止和连接失败用各自的文本，不和"拒绝回答"混用。

外部协议的线格式见 §8，规格补充写进 `docs/protocols/`（§11 P4）。

## 4. 工具定义

### 4.1 Schema

工具名 `ask_user`，界面显示名 `Tool.AskUser`（"提问"）。

```json
{"type":"object","properties":{
  "questions":{"type":"array","minItems":1,"maxItems":4,"description":"The questions to ask, 1-4.","items":{
    "type":"object","properties":{
      "question":{"type":"string","description":"The complete question, clear and specific, ending with a question mark."},
      "header":{"type":"string","description":"A very short label shown as the question's tab, at most 12 characters, e.g. 'Database'."},
      "options":{"type":"array","minItems":2,"maxItems":4,"description":"2-4 distinct choices.","items":{
        "type":"object","properties":{
          "label":{"type":"string","description":"The choice itself, 1-5 words."},
          "description":{"type":"string","description":"What choosing it means, or its trade-off."}},
        "required":["label","description"],"additionalProperties":false}},
      "multiSelect":{"type":"boolean","description":"Let the user pick several options. Default false."}},
    "required":["question","header","options"],"additionalProperties":false}}},
 "required":["questions"],"additionalProperties":false}
```

校验（不通过时返回错误结果，不打扰用户）：
- 问题数 1–4，问题原文互不相同。
- 每个问题 2–4 个选项，同一问题内选项标签互不相同。
- `header` 超过 12 个字符时截断显示，不算错误。
- 选项少于 2 个时，返回的文本参考 Claude 的写法："The questions were rejected and the user never saw them: every question needs at least two real options. If there is only one reasonable path, state it as the approach you are taking and continue. Do not invent a filler second option."

### 4.2 行为

- `DescribePermission` 返回 `NoAccess`，不经过审批，任何权限档位下都可用。提问在任何档位下都不会被自动回答（`IAgentLoop.cs:40` 的约定不变）。
- `Concurrency = Exclusive`：同一批里有 `ask_user` 时，整批串行执行，避免问答面板和审批卡片同时出现。
- 工具通过 `ToolServices.UserInput`（新增，`IUserInputService?`）向 Host 发出请求，并用工具自己的 `cancellationToken` 等待回答。Abort 时请求被取消，工具结果是现有的 "Operation aborted."。
- **子 Agent 不提供**：`AgentCoordinator.SpawnAsync` 传给子 Agent 的 `ToolServices` 去掉 `UserInput`，工具列表里就没有 `ask_user`。
- **没有 UI 时**：Host 没有连接的客户端，或者请求发送失败（不包括取消），返回错误结果 "ask_user is unavailable: no user is attached to this session. Continue with your best judgment and state your assumptions."。
- **Details**：结果附带 `QuestionDetails`（kind `question`），内容是问题、每个问题的回答和结局，供转录卡片显示，并随会话文件持久化（§7.4）。

## 5. 提示词（待评审）

按 composer.md §18.1 的原则写：只引用实际存在的工具，英文书写，代码文本和这里保持一致。

### 5.1 工具描述（`Description`）

```
Ask the user one to four multiple-choice questions and wait for the answers. Use it only when you are blocked on a decision that is genuinely the user's to make: one you cannot resolve from the request, the code, or sensible defaults.

- Investigate first. Do not ask what the workspace, the conversation or a quick check can answer.
- Give each question 2-4 distinct options. If you recommend one, put it first and add " (Recommended)" to its label.
- Do not add an "Other" option: the user can always type their own answer.
- Set multiSelect when the choices are not mutually exclusive, and phrase the question to match.
- Do not use it to ask for approval of a plan or for permission to proceed, and do not ask again what the user has already answered.
```

### 5.2 tools 节与 rules 节

- `PromptSnippet`：`Ask the user multiple-choice questions when a decision is genuinely theirs`
- `PromptGuidelines`：`When you need the user to choose between approaches, ask with ask_user instead of ending your turn with questions in text.`

### 5.3 模式提示词的改动（`SessionRole`，同步修改 composer.md §18）

Plan 第 2 步改为：

```
2. If a choice would materially change scope, behavior, compatibility, data or user experience, and the code does not settle it, ask the user with ask_user. Ask only what you cannot find out yourself, and do not write the plan until the answers are in.
```

Plan 第 3 步末尾新增一项：

```
   Do not use ask_user to ask whether the plan is acceptable; end your turn with the plan.
```

ACP 第 2 步改为：

```
2. If a choice would materially change scope or behavior and the code does not settle it, ask the user with ask_user before dispatching any work.
```

composer.md §18.1 第 4 条改为："TKCode 内置 Agent 用 `ask_user` 向用户提问；外部运行时的模式说明不提工具名（见 §18.8）。"

外部运行时的 ExternalPlan 和 ExternalAcp **不改**：它们不提 TKCode 的工具名，现有措辞（"ask the user"）已经允许运行时使用自己的提问工具，而这些提问现在能正常显示了。

### 5.4 结果文本

答案统一以问题原文为键，多选项之间用 `", "` 连接。用户自己输入的文本原样作为答案。

| 结局 | 文本 |
|---|---|
| 全部回答，且都是选项 | `The user answered your questions: "Q1"="A1", "Q2"="A, B". Continue with these answers in mind.` |
| 有用户自己输入的答案 | `The user answered: "Q1"="A1", "Q2"="<typed text>". Some answers are in the user's own words; read them carefully and follow what they actually say, even if it asks for clarification, a change of course, or that you not proceed.` |
| 部分未回答 | 未回答的问题写成 `"Q"=(no answer)`，其余同上 |
| 拒绝回答（Esc） | `The user declined to answer these questions. Do not proceed on assumptions about them: end your turn now and wait for the user's next message.` |
| 中止（Stop） | `Operation aborted.`（现有文本） |
| 没有 UI 或发送失败 | 见 §4.2 |

拒绝回答不标记为错误（`IsError=false`），和用户拒绝审批时的做法区分开：前者是用户的正常选择，后者表示工具没有执行。

## 6. Host→UI 契约：`input/request`

新增 UI 方法 `input/request`，RPC 协议版本 19 → 20。

```csharp
record UserInputRequest(string SessionId, string RequestId, string Source, string? ToolCallId, string? AgentName,
                        IReadOnlyList<UserQuestionDto> Questions);
record UserQuestionDto(string Id, string Header, string Question, IReadOnlyList<UserQuestionOptionDto> Options,
                       bool MultiSelect, bool AllowOther = true, bool IsSecret = false);
record UserQuestionOptionDto(string Label, string? Description);
record UserInputResponse(string Outcome /* answered | declined */, IReadOnlyList<UserAnswerDto>? Answers);
record UserAnswerDto(string QuestionId, IReadOnlyList<string> Selected, string? Text);
```

- `Source` 取 `builtin`、`claude-code` 或 `codex`，决定面板标题（"TKCode 提问"、"Claude Agent 提问"、"Codex 提问"），也决定回答如何回写（§8）。
- `Options` 为空时，这个问题只有文本框（Codex 允许 `options: null`）。`IsSecret` 时文本框掩码显示。
- **取消**：Host 取消请求时（Abort、Claude `control_cancel_request`、Codex `serverRequest/resolved`），JSON-RPC 的 `$/cancelRequest` 到达 UI，UI **必须移除面板**，恢复输入框（§10 修复项 1 的同一机制）。
- **没有 UI**：Host 没有客户端时，内置工具返回 §4.2 的错误；外部运行时按 §8 中各自"拒绝"的方式回复。

## 7. 界面

### 7.1 面板

待回答期间，`DockedComposer` 的位置换成 `QuestionPanelView`。输入框里的草稿保留不动，面板关闭后原样恢复。

```
┌──────────────────────────────────────────────────────────┐
│ ? TKCode 提问                         [数据库] [认证] [提交] │  标题 + 标签页（单个问题时没有标签页）
│                                                          │
│ 用哪个数据库？                                             │
│ ┌──────────────────────────────────────────────────────┐ │
│ │ 1  Postgres (Recommended)   功能完整，需要单独部署        │ │  当前高亮行
│ └──────────────────────────────────────────────────────┘ │
│   2  SQLite                    零部署，适合单机             │
│   3  其他  [在这里输入你的答案…                     ]       │
│                                                          │
│ ↑↓ 选择 · 1-9 直接选 · ←→ 切换问题        拒绝回答 Esc  下一题 ↵ │
└──────────────────────────────────────────────────────────┘
```

- 外观沿用输入框的卡片样式，包括圆角、背景和边框，宽度与输入框一致。
- 单选显示序号，多选显示复选框。"其他"行始终在最后，选中它即可直接输入。行内文本框的样式与输入框一致。
- 多个问题时，标签页上标出已答和未答。最后一个标签页是"提交"，列出所有问答，未答的问题用警示色标出。
- 只有一个问题时没有"提交"页：单选时选中即提交；多选时按回车提交。
- 底部按钮：左边是"拒绝回答"，右边是"下一题"或"提交"。同一会话有多个待回答请求时，标题旁显示"+N"，按先来先答处理。
- 子 Agent 不会提问，所以标题不需要显示 `[agentName]`。字段保留，留给以后外部运行时的子 Agent 使用。

### 7.2 键盘

| 按键 | 行为 |
|---|---|
| ↑ / ↓ | 在选项间移动（包括"其他"） |
| 1–9 | 单选：选中并进入下一题；多选：切换勾选 |
| Space | 多选：切换当前行的勾选 |
| Enter | 单选：选中当前行并进入下一题；多选：进入下一题；"提交"页：提交 |
| ← / → / Tab / Shift+Tab | 切换问题（正在"其他"文本框里输入时不响应） |
| Cmd/Ctrl+Enter | 任何位置直接提交 |
| Esc | 在"其他"文本框里时先退出文本框；否则拒绝回答 |

在"其他"行上直接打字，会自动进入文本框并选中这一项。面板打开时自动获得焦点；切换到别的会话再切回来，焦点回到面板。

### 7.3 会话与侧边栏

- 待回答的请求挂在 `SessionViewModel.PendingQuestions` 上，与审批分开存放。切换会话时，已选的答案和输入的文本都保留。
- 侧边栏橙点（`IsWaitingApproval`）同时考虑审批、MCP 表单和提问，提示文字统一为"等待你的操作"。自动更新同样把这些会话视为忙碌。
- 删除会话时，取消它所有待处理的请求。
- 系统通知、提示音与 Dock / 任务栏 / 托盘未读角标见 [notifications.md](notifications.md)：提问属于「需要你处理」。

### 7.4 转录里的问答卡片

- `ask_user`、Claude `AskUserQuestion`、Codex 提问都显示为**独立的转录项**，不并入活动组，因此轮次结束后也不会被折叠进"已工作 …"。
- 内容是每个问题的标签、问题和答案：多选答案用逗号分隔，自由输入的答案加引号，未回答的写"未回答"。整体结局为"已拒绝回答"或"已中止"时，在卡片头部标出。
- 待回答期间，卡片显示"等待回答…"。
- 数据来源：
  - 内置工具：工具结果的 `QuestionDetails`，随会话文件持久化，重新打开会话后照样显示。
  - 外部运行时：`ExternalAgent` 按 `ToolCallId` 记下问答结果，在对应的工具结束事件上附带同样的 `QuestionDetails`。Codex 的提问没有对应的工具项，由 `ExternalAgent` 合成一对 `request_user_input` 的开始和结束事件。

## 8. 外部运行时的映射

`IRuntimeHost` 新增 `RequestInputAsync(RuntimeInputRequest, CancellationToken)`，返回 `RuntimeInputResult`（回答 / 拒绝 / 取消）。这条路径**不经过**权限档位：`ExternalAgent.RequestApprovalAsync` 在非"询问"档位下会直接放行，提问不能走它。

| 运行时 | 收到 | 回答 | 拒绝 | 取消 |
|---|---|---|---|---|
| Claude | `can_use_tool`，`tool_name == "AskUserQuestion"`，问题在 `input.questions` 里（问题 id 取问题原文） | `allow`，`updatedInput` = 原 input 加 `answers`：以问题原文为键，值为 `", "` 连接的字符串；标签里含 `", "` 或 `"` 时加引号，自由输入的文本原样作为值 | `deny`，message 为 §5.4 的拒绝文本 | 沿用 `DecideAsync` 现有逻辑：被撤回或轮次结束时不应答 |
| Codex | `item/tool/requestUserInput`，问题带 `id`、`isOther`、`isSecret`，`options` 可以为 null | `{"answers":{"<id>":{"answers":["<label>", …, "user_note: <自由输入>"]}}}`，未回答的问题给空列表 | 所有问题都给空列表 | 收到 `serverRequest/resolved` 时取消 UI 请求，不应答 |
| ACP | `elicitation/create`（mode `form`） | 复用 `McpElicitationViewModel` 表单卡片，回复 `{"action":"accept","content":{…}}` | `{"action":"decline"}` | `{"action":"cancel"}` |

补充说明：
- **Claude**：
  - `AskUserQuestion` 只在设置了 `--permission-prompt-tool stdio` 时启用（TKCode 已经设置），在任何权限模式下都会经 `can_use_tool` 发来。
  - 只回原 input 加 `answers`，不附加其他字段，这样在 CLI 的严格模式下也能通过。
  - 不开启 preview 和 extended 变体（相应的环境变量都不设）。
- **Codex**：在 TKCode 当前的设置下（`experimentalApi:false`、Default 协作模式），只有用户在自己的配置里打开了 `default_mode_request_user_input`，这个请求才会出现。我们照样接好，但不主动开启。
- **ACP**：
  - 在 `clientCapabilities` 中声明 `"elicitation":{"form":{}}`，不声明 `url`。
  - 表单卡片的标题来源从"MCP 服务器"扩展为"ACP 运行时"。
  - 非规范形状的 `session/request_permission`（例如 Cursor 的 ask_question 回退形式）维持现状，不在本期范围内。

## 9. 待回答时的约束

- 输入框被面板替换，用户无法发送消息，也就不需要处理待回答期间的插话（steer）。
- 停止按钮跟着输入框一起被面板替换，所以面板上要有"停止"入口：放在面板标题栏右侧的图标按钮（现在没有停止快捷键，本期也不新增）。停止的效果是中止整轮，提问请求被取消（结果文本见 §5.4）。
- 切换权限档位时，`ApplyPermissionAsync` 只自动处理审批，不碰提问（提问在单独的集合里）。

## 10. 一并修复的遗留问题

1. **取消后卡片残留**：`AgentHostConnection` 的审批和 elicitation 处理器不响应 `$/cancelRequest`，Stop 之后卡片一直留着，点击也没有反应。修复办法：监听 `cancellationToken`，取消时调用 `vm.Cancel()`，把卡片从队列里移除。提问面板用同一套机制。
2. **侧边栏橙点**：待处理的 MCP / ACP 表单也要点亮橙点（§7.3）。
3. **删除会话**：取消该会话所有待处理的审批、表单和提问。

## 11. 实施阶段

| 阶段 | 内容 |
|---|---|
| P1 | 契约：`input/request` 的 DTO、`RpcMethods`、`IAgentHostClient`、代理与分发、source-gen 注册，协议 v20；Host 端的 `IUserInputService`（SessionManager 实现，无客户端时报错）；`ask_user` 工具、校验、结果文本、`QuestionDetails`；子 Agent 去掉该工具；`SessionRole` 和 composer.md §18 的提示词改动 |
| P2 | 界面：`QuestionPanelViewModel` / `QuestionPanelView`，替换输入框，键盘操作；转录问答卡片；侧边栏橙点；取消时移除（§10 修复项 1 一并完成）；中英文本地化 |
| P3 | 外部运行时：`IRuntimeHost.RequestInputAsync`，Claude、Codex 的映射与合成工具项；ACP 声明 elicitation，复用表单卡片 |
| P4 | 文档：补全 `docs/protocols/` 三份规格的缺口（Claude 的启用条件、限制、回答编码与结果文本；Codex 的门控、`isBlocking`、`user_note` 约定；ACP 的 schema 细节，以及稳定版本应为 1.7.0 而不是 1.6.0），更新 external-agent-runtimes.md 的 §6 和"已知限制" |

## 12. 测试

- **单元测试（Agent.Coding）**：schema 校验（问题数、选项数、重复）；§5.4 每种结局的结果文本；多选连接与引号转义。
- **Host（AgentHost.Tests）**：用假模型发出 `mcp:ask_user {...}`，经 `RecordingClient` 回答、拒绝、超时取消；子 Agent 的工具列表里没有 `ask_user`；没有客户端时返回错误。
- **Desktop 无头测试**：
  - 端到端：真实 Host 加假模型。断言面板替换了输入框，用键盘选择、输入"其他"、在标签页间切换并提交；Esc 拒绝；Stop 后面板消失、输入框和草稿恢复；重新打开会话后问答卡片仍在。
  - 快照：单个问题（单选）、多选、多问题的提交页、"其他"输入中、转录问答卡片、侧边栏橙点。
- **Runtimes 夹具测试**：
  - Claude：`AskUserQuestion` 回答后的 `updatedInput.answers`；拒绝时的 deny；收到 `control_cancel_request` 时不应答。
  - Codex：`requestUserInput` 的 answers 结构；收到 `serverRequest/resolved` 时不应答。
  - ACP：`initialize` 声明了 elicitation；`elicitation/create` 收到 accept、decline、cancel 三种回复。
- **NativeAOT**：AOT 发布零 IL 警告，并运行发布出的二进制做一次端到端检查。

## 13. 实现状态（2026-10-18）

P1–P4 全部完成。代码位置：

| 部分 | 位置 |
|---|---|
| 共用模型、回答编码、Details JSON | `src/TKCode.Agent/Questions/UserQuestions.cs` |
| `ask_user` 工具（描述、schema、校验、结果文本） | `src/TKCode.Agent.Coding/Tools/Questions/AskUserTool.cs` |
| Host：`IUserInputService`、`input/request`、ACP 表单转发 | `src/TKCode.AgentHost/SessionManager.cs`（`RequestUserInputAsync`、`ElicitExternalAsync`） |
| 运行时接口 | `src/TKCode.Runtimes/IAgentRuntime.cs`（`RequestInputAsync`、`RequestElicitationAsync`）、`ExternalAgent.cs` |
| Claude / Codex / ACP 映射 | `ClaudeSession.AskAsync`、`CodexSession.RequestUserInputAsync`、`AcpSession.ElicitAsync` |
| 问答面板 | `src/TKCode.Desktop/ViewModels/QuestionRequestViewModel.cs`、`Views/QuestionPanelView.axaml(.cs)` |
| 转录问答卡片 | `ViewModels/QuestionCardViewModel.cs`、`Views/QuestionCardView.axaml`、`Controls/TranscriptAdapter.cs` |

与设计的差异：
- 协议版本是 19 → 20（设计稿写成了 18 → 19）。
- 已结束的一轮里，问答卡片放在“已工作 …”组之后、最后一段正文之前，而不是保留在组内原来的位置（组内的内容会被折叠）。
- ACP 的表单卡片标题使用运行时的显示名（“{运行时} 请求信息”）。

测试：
- `tests/TKCode.Agent.Tests/AskUserToolTests.cs`：校验、编码、结果文本、Details。
- `tests/TKCode.AgentHost.Tests/AskUserTests.cs`：回答、拒绝、无客户端、中止、子 Agent 没有该工具。
- `tests/TKCode.Runtimes.Tests`：`ClaudeFixtureTests.AskUserQuestion_*`、`CodexFixtureTests.RequestUserInput_*`、`AcpFixtureTests.UnknownAgentRequests_*`（url 模式 -32602，form 模式经宿主回答）。
- `tests/TKCode.Desktop.Tests/QuestionPanelTests.cs`：真实 Host 加假模型的端到端测试，覆盖键盘回答、“其他”输入、提交页、草稿保留、侧边栏橙点、Esc 拒绝、Stop 收起。快照：`question-panel`、`question-panel-multi`、`question-panel-review`、`question-card`。
