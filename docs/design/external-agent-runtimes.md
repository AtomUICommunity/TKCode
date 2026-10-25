# 外部 Agent 运行时设计（Claude Code / Codex / ACP Agent）

状态：设计已确认（2026-10-16），尚未实现
关联文档：[architecture.md](../architecture.md)，协议规格目录 [docs/protocols/](../protocols/)

## 1. 目标

在 TKCode 自带的 Agent 运行时之外，支持用用户本机已安装的 Agent 客户端作为运行时：Claude Code、Codex，以及原生支持 ACP 的 Agent（Gemini CLI、GitHub Copilot CLI、Cursor、OpenCode、Qwen Code、Kimi CLI、Goose、pi 等）。

- **默认免配置**：检测到已安装的客户端，就直接作为可用运行时加载。用户不需要做任何配置，TKCode 也不经手凭据。
- **统一体验**：外部运行时与内置运行时共用同一套 UI、会话列表、`SessionEvent` 事件流和 `tool/approve` 审批。
- **整个客户端保持 NativeAOT**：外部 CLI 是独立进程，TKCode 只负责启动进程和解析 JSON，不引入任何 Node 或 SDK 依赖。

## 2. 已确认的决策

| 维度 | 决策 | 说明 |
|---|---|---|
| 接入策略 | **原生协议为主，ACP 兜底** | Claude Code 走 `claude -p` 的 stream-json 双向流和 stdio 控制通道；Codex 走 `codex app-server`；通用 ACP 驱动只接原生支持 ACP 的 Agent，**不经过 npx 适配器** |
| 审批 | **接入 TKCode 审批** | 写文件、执行命令等操作默认弹出审批（允许一次 / 本会话允许 / 拒绝）；Codex 使用 `workspace-write` 沙箱；会话级可切换为自动放行；审批请求丢失时一律拒绝 |
| 登录与条款 | **全部直接启用**（用户决策） | 检测到即启用，直接复用 CLI 已有的登录状态（订阅或 API Key），TKCode 不读写凭据。见 §8 风险 |
| 历史会话 | **先只显示 TKCode 创建的会话** | TKCode 用自己的 JSONL 记录转录，并保存原生会话 ID 用于恢复；导入 CLI 已有的历史留到后续版本 |
| 命名 | 不在界面或产品中使用 "Claude Code" 名称 | Anthropic 品牌指南的要求；显示为 "Claude Agent"、"Codex" 等 |

为什么不全部走 ACP：zeron（github.com/zeronsh/zeron）在 2026-08 把 Claude 和 Codex 改成通过 ACP 适配器接入，后来又改回原生协议，原因有三：
- **轮次结束出错**：适配器会把轮次一直挂着，等后台工作完成，制造出"完成状态"类 bug。
- **子 Agent 数据有损失**。
- **npm 安装问题**：`npx` 经常失败，依赖树可达 570 MB。

原生协议还能提供 ACP 给不了的东西：工具参数的流式增量、逐轮用量、思考内容。

## 3. 架构

```
TKCode.Desktop ──JSON-RPC──▶ TKCode.AgentHost
                               └─ IAgentRuntime（按会话选择）
                                  ├─ BuiltinRuntime     TKCode 自己的 Agent（Chat / Responses / Messages 适配器）
                                  ├─ ClaudeRuntime      claude -p（stream-json + control_request）
                                  ├─ CodexRuntime       codex app-server（JSON-RPC，按行分帧）
                                  └─ AcpRuntime         <agent> --acp（ACP v1，按行分帧）
```

- 外部运行时**全部放在 Agent Host 内部**。UI 继续使用现有的 `session/*` 方法，不写任何与具体 Agent 相关的逻辑。
- **契约扩展**：
  - `models/list` 的结果按运行时分组。
  - `session/create` 增加 `runtime` 字段。
  - 新增 `runtimes/list`，返回每个运行时的安装状态、版本、认证方式和能力。
- 每个活跃会话对应一个外部进程（Codex app-server 可以在一个进程里承载多个 thread）。

### 3.1 运行时抽象（已实现，`src/TKCode.Runtimes`）

```csharp
// TKCode.Agent：内置 Agent 与外部运行时共用的循环接口
public interface IAgentLoop { Model; ThinkingLevel; FastMode; IsRunning; Messages; LoadHistory; Subscribe; PromptAsync; Steer; FollowUp; Abort; WaitForIdleAsync; }
public interface IExternalAgentLoop : IAgentLoop, IAsyncDisposable { RuntimeId; NativeSessionId; event NativeSessionIdChanged; }

// TKCode.Runtimes
public interface IAgentRuntime
{
    string Id { get; }                     // claude-code / codex / acp:<agent>，同时是其模型的 Provider 与 Api
    string DisplayName { get; }            // “Claude Agent”、“Codex”
    RuntimeDescriptor Descriptor { get; }  // 状态、路径、版本、认证方式、能力、模型（最近一次探测结果）
    Task<RuntimeDescriptor> ProbeAsync(CancellationToken ct);
    Task<IRuntimeSession> StartSessionAsync(RuntimeSessionRequest request, CancellationToken ct);  // cwd、TurnOptions、resume id、IRuntimeHost
}

public interface IRuntimeSession : IAsyncDisposable
{
    string? NativeSessionId { get; }
    // 运行一整轮，直到 CLI 的终止帧；取消 = 协议层中断。事件写进 TurnWriter。
    Task RunTurnAsync(UserMessage message, TurnOptions options, TurnWriter writer, CancellationToken ct);
    ValueTask<bool> SteerAsync(UserMessage message, CancellationToken ct);   // false → 调用方排队
}

public interface IRuntimeHost   // 会话 → TKCode
{
    ValueTask<ApprovalDecision> RequestApprovalAsync(RuntimeApprovalRequest request, CancellationToken ct);  // AllowOnce / AllowForSession / Deny / Cancel
    void OnNativeSessionId(string id);
}
```

- **`ExternalAgent`** 实现 `IExternalAgentLoop`：
  - CLI 进程在第一次提问时启动，并在轮次之间保留。
  - 出错后丢弃该进程，下一次提问时用原生会话 id 重启并恢复。
  - 不支持 steering 的运行时，steer 退化为 follow-up。
  - 自动放行（`SessionOptionsDto.AutoApprove`）在审批时直接返回 AllowOnce。
  - UI 缺席、断连或出错时一律拒绝。
- **`TurnWriter`** 把驱动写入的增量组装成规范消息：
  - 文本、思考、工具调用累积在一条“草稿”助手消息里。
  - 工具结果到达时先关闭草稿，保证历史中先有工具调用、后有结果。
  - 没有事先声明的工具调用会自动补上。
  - 轮次结束时，最后一条草稿带上整轮用量和停止原因（errors-as-messages）。
- 因为事件与内置 Agent 完全一致，`AgentSession` 的持久化、`SessionManager` 的 RPC 映射和历史恢复都可以直接复用。
- `RuntimeRegistry` 在后台并行探测所有运行时，完成后推送 `models/changed`。设置 `TKCODE_EXTERNAL_RUNTIMES=0` 可以全部关闭（测试用）。
- 外部 CLI 一律通过 `CliProcess` 启动：
  - stderr 只保留最后 40 行。
  - 退出时先关闭 stdin，3 秒后对整个进程树执行 Kill。
- 可执行文件由 `ExecutableLocator` 查找。
- 会话文件新增 `runtimeSession` 记录（runtime 与 nativeSessionId）。
- 契约（协议 v7）：
  - 新增 `runtimes/list`。
  - `ModelDto.Runtime`、`SessionDto.Runtime`、`SessionSummaryDto.Runtime`。
  - `SessionOptionsDto.AutoApprove`。
  - `ToolApprovalRequest` 增加 Kind、Title、Reason、CanAllowForSession。
  - `ToolApprovalResponse.ForSession`。
- 会话内只能切换到同一运行时的模型；内置会话不能切换到外部运行时的模型。

能力（`RuntimeCapabilities`）：`SupportsSteering`、`SupportsResume`、`SupportsModelSwitch`、`StreamsToolArguments`。

## 4. 自动发现（免配置）

查找顺序，参考 zeron 与官方 SDK 的实现：
1. 环境变量覆盖：`TKCODE_CLAUDE_PATH`、`TKCODE_CODEX_PATH`。
2. 当前进程的 `PATH`。
3. **登录 shell 的 PATH 快照**。从 Finder 或 Dock 启动时继承不到 shell 的 PATH，需要启动一次 `$SHELL -ilc 'echo $PATH'`，缓存结果。
4. 已知安装目录：
   - 通用：`~/.local/bin`、`~/.claude/local`、`/opt/homebrew/bin`、`/usr/local/bin`。
   - npm 全局前缀，以及 nvm、fnm、volta、pnpm、bun 的目录。
   - Windows：`%APPDATA%\npm`、`%LOCALAPPDATA%\Programs`。
5. ACP Agent：对照 ACP Registry 中的命令名（`gemini`、`copilot`、`cursor-agent`、`opencode`、`qwen`、`kimi`、`goose` 等）。只启用能直接启动的原生二进制，跳过需要 `npx` 的条目。

探测（结果缓存，以可执行文件路径和版本作为键）：
- **Claude**：
  - `claude --version` 获取版本。
  - `claude auth status` 获取认证方式（JSON 中的 `authMethod`）。
  - 模型和斜杠命令：启动一个短时进程，发送 `initialize` 控制请求读取，超时 10 秒，不触发模型轮次。
- **Codex**：
  - `initialize` 返回的 `userAgent` 获取版本。
  - `account/read` 获取认证方式。
  - `model/list` 获取模型。
- 子进程的 PATH 把可执行文件所在目录放在最前面，这样 `#!/usr/bin/env node` 一类的启动脚本在 GUI 启动时也能运行。

## 5. 事件映射（映射到 TKCode 已有的增量事件）

| TKCode 事件 | Claude（stream-json） | Codex（app-server） | ACP v1 |
|---|---|---|---|
| agentStarted | `system/init`（记录 `session_id`；同一会话重复出现时去重） | `turn/started` | 发出 `session/prompt` 时生成 |
| messageStarted / messageEnded | `stream_event` 的 `message_start` / `message_stop` | `agentMessage` 类型 item 的 `item/started` / `item/completed`（以 `item.text` 为准） | 由 chunk 类型切换时生成 |
| textDelta | `content_block_delta.text_delta` | `item/agentMessage/delta` | `agent_message_chunk` |
| thinkingDelta | `thinking_delta` | `item/reasoning/summaryTextDelta` / `textDelta` | `agent_thought_chunk` |
| toolCallStarted / Delta / Ended | `content_block_start(tool_use)` / **`input_json_delta`** / `content_block_stop` | `item/started`（参数一次给全，Started、Delta、Ended 连续发出） | `tool_call` / `tool_call_update`（无参数流） |
| toolExecutionStarted | `can_use_tool` 批准后（自动放行模式下在 toolCallEnded 时） | 命令或文件变更 item 开始执行 | `status: in_progress` |
| toolExecutionEnded | `user` 消息里的 `tool_result`（**保留输出**，zeron 丢弃了这部分） | `item/completed`（`exitCode`、`aggregatedOutput`；命令输出增量来自 `outputDelta`） | `completed` / `failed` + content / diff |
| agentEnded + 用量 | `result`（`subtype` → 停止原因；`modelUsage`；`total_cost_usd` 为累计值） | `turn/completed`；`thread/tokenUsage/updated` | `session/prompt` 的 `stopReason`；`usage_update` |
| 计划 / 待办 | `TodoWrite` 工具 | `turn/plan/updated` | `plan` |
| 子 Agent | 非空的 `parent_tool_use_id` | `collabAgentToolCall` 及其子 thread | 草案中的 subagent 能力 |

- **流式增量与完整消息去重**：Claude 的 `stream_event` 增量和完整的 `assistant` 消息会描述同一个块，以 `message.id` 加块序号、或 `tool_use.id` 对账，避免重复渲染。
- **子 Agent**：子 Agent 的帧不混进父轮次，写入各自的转录（`IRuntimeHost.OpenSubagent` → `RuntimeSubagent`），UI 在右侧标签中查看。见 [subagent-view.md](subagent-view.md) §5.5、§5.6（2026-10-21 实现，取代原先"`AgentEvent` 加 `ParentToolCallId`"的设想）。
- **轮次结束**：只以 CLI 的终止帧为准（`result`、`turn/completed`、`session/prompt` 的响应），**不用静默计时器之类的启发式判断**。zeron 已经因为这个问题废弃了这种做法。

## 6. 审批

| 来源 | 映射 |
|---|---|
| Claude `control_request: can_use_tool {tool_name, input, permission_suggestions}` | → `tool/approve` → `{behavior:"allow", updatedInput}` 或 `{behavior:"deny", message}`；"本会话允许"时附带 `updatedPermissions` |
| Codex `item/commandExecution/requestApproval`、`item/fileChange/requestApproval` | → `tool/approve` → `accept`、`acceptForSession`、`decline` 或 `cancel`；收到 `serverRequest/resolved` 时关闭对话框 |
| ACP `session/request_permission {toolCall, options}` | → `tool/approve`，选项按 `allow_once`、`allow_always`、`reject_once`、`reject_always` 渲染；取消时一律回复 `cancelled` |
| Claude `AskUserQuestion`、Codex `item/tool/requestUserInput` | 这些是**提问**而不是审批：经 Host→UI 方法 `input/request` 交给问答面板（[ask-user.md](ask-user.md) §8） |
| ACP `elicitation/create`（form） | 复用 MCP 表单卡片（`mcp/elicit`）；只声明 form 模式 |

- **默认模式**：
  - Claude：`--permission-mode default`。注意 stdin 必须保持打开，否则控制通道收不到 `can_use_tool`。
  - Codex：`approvalPolicy:"on-request"`，配合 `workspace-write` 沙箱。
  - 会话级可以切换为自动放行：Claude 用 `bypassPermissions`，Codex 用 `never`。
- **安全规则**：UI 没有响应、连接断开或超时时，一律**拒绝**，绝不默认放行。

### 6.1 权限档位（用户决策，2026-10-16）

替代原来的会话级“自动放行”开关，所有会话（包括内置 Agent）共三档：

| 档位 | 内置 Agent | Claude Agent | Codex | ACP |
|---|---|---|---|---|
| **询问**（默认） | 非只读工具逐项审批 | `--permission-mode default`，`can_use_tool` 逐项审批 | `on-request` + `workspace-write`（不联网） | `request_permission` 逐项审批 |
| **自动放行**（保留沙箱） | 全部允许 | `default`，TKCode 自动允许审批（CLI 自身的 deny 规则仍生效） | `never` + `workspace-write` + 联网 | 自动选 allow_once |
| **完全授权**（无沙箱） | 全部允许 | `bypassPermissions`（启动时总带 `--allow-dangerously-skip-permissions`，以便中途用 `set_permission_mode` 切换） | `never` + `danger-full-access` | 同自动放行 |

规则：
- 提问类请求（AskUserQuestion、requestUserInput、非规范形状的 ACP 权限请求）在任何档位下都不会被自动回答。
- **契约**：`SessionOptionsDto.PermissionMode` 取 `ask`、`autoApprove`、`fullAccess`（协议 v9；旧的 `AutoApprove` 字段已删除）。
- **持久化**：档位写入会话文件的 `optionsChange` 记录。重新打开会话、以及在新会话中继续时，沿用记录的档位，包括 `fullAccess`。
- **界面**：
  - 选择器放在输入框底栏。
  - 每次切换到“完全授权”都要二次确认。
  - 按项目记住上次的档位（包括“完全授权”，本项目的新会话沿用），由桌面端保存。
- **Codex**：以 `thread/start` 响应中实际生效的 `approvalPolicy`、`sandbox`、`serviceTier` 为基线，每轮只在档位不同时显式覆盖。这样可以避免用户 `config.toml` 里的设置（例如 `service_tier = "priority"`）在 TKCode 关闭对应选项时仍然生效。

## 7. 会话、恢复与持久化

- TKCode 自己的 JSONL 仍然是 UI 转录的唯一来源。会话头部新增 `{runtime, nativeSessionId, cwd}`。
- 恢复方式：
  - Claude：`--resume=<id>`；创建会话时用 `--session-id <uuid>` 指定 ID。
  - Codex：`thread/resume {threadId}`，失败时退回 `thread/start` 并在界面上提示。
  - ACP：有 `session/resume` 时用它，否则用 `session/load` 重放历史。
- **只在同一个 cwd 下恢复**，因为 Claude 是按项目目录存放会话的。会话的 cwd 是创建时项目的主文件夹，之后不变；项目的其他文件夹与本会话追加的文件夹通过 Claude `--add-dir`、Codex 每轮的 `writableRoots`、ACP 的 `additionalDirectories` 传给运行时，变化后在下一轮之前生效（Claude / ACP 用原生会话 id 重启会话，docs/design/projects.md §6.8）。
- 不把 TKCode 的历史回放给外部 Agent，也不解析它们的存储文件（`~/.claude/projects`、Codex rollout）。

## 8. 风险与对策

| 风险 | 对策 |
|---|---|
| Claude 控制协议（`control_request` / `control_response`）**没有公开文档**，只能从官方 Python SDK 源码推断 | 实现前先整理规格 `docs/protocols/claude-code-stream-json.md`；锁定最低 CLI 版本；用录制的真实 JSONL 夹具做契约测试；解析时宽松处理，忽略未知的 `type`、`subtype` 和字段 |
| Codex app-server 标为 experimental，大约每几天发一版，破坏性变更常见 | 用 `codex app-server generate-json-schema` 按锁定版本生成契约；检查 `userAgent` 版本；未知通知、未知 item 和错误变体全部容忍 |
| **订阅条款**：Anthropic 明确不允许第三方产品提供 claude.ai 订阅登录或订阅额度（除非事先获批）；OpenAI 没有官方的第三方条款说明 | 用户已决定**全部直接启用**，风险由用户自行评估或与厂商确认。TKCode 本身不提供登录、不读写凭据，只启动用户自己的 CLI |
| 两个 CLI 都在后台自动更新，版本随时可能变化 | 启动时探测版本；低于支持范围时在 UI 上提示，而不是静默失败 |
| 进程管理：中断、清理、Windows 进程树 | 先发协议层中断，然后 SIGTERM，再 SIGKILL，作用于整个进程组（通过 `LibraryImport` 调用 `setsid`、`kill`，AOT 安全）；Windows 用 Job Object；持续读取 stderr，只保留最后几行并脱敏，用于错误提示 |
| ACP v2 草案即将去掉 fs / terminal 方法 | 初始化时就声明 `fs:false, terminal:false`；TKCode 自己的工具以 MCP 服务器的形式传给 Agent |

## 9. 实施步骤（建议）

1. **规格**：按"先读完整协议文档"的约定，整理三份规格：
   - `docs/protocols/claude-code-stream-json.md`：stream-json 输出格式与控制协议。
   - `docs/protocols/codex-app-server.md`：基于锁定版本生成的 schema。
   - `docs/protocols/acp-v1.md`。
2. **基础设施**：
   - `IAgentRuntime` 抽象，以及 `BuiltinRuntime`（包装现有 Agent）。
   - 运行时发现服务（含登录 shell PATH 快照）。
   - `runtimes/list` RPC 和 `session/create.runtime`。
   - `JsonRpcConnection` 增加按行分帧模式，供 Codex 和 ACP 使用。
3. **CodexRuntime**：协议是 JSON-RPC，最规整，适合先做。
4. **ClaudeRuntime**。
5. **AcpRuntime**：先接 Gemini CLI 和 Copilot CLI。
6. **UI**：运行时和模型选择器按运行时分组；`input/request` 提问面板；会话级自动放行开关。
7. **测试**：每个运行时都要有假 CLI 脚本和录制的真实 JSONL 夹具；AOT 发布后验证零警告，并运行 AOT 产物。

## 9.1 实现状态（2026-10-16）

| 部分 | 状态 | 位置 |
|---|---|---|
| 规格 | 完成：三份规格均带测试夹具，夹具已用 schema 校验 | `docs/protocols/{codex-app-server,claude-code-stream-json,acp-v1}.md` |
| 公共基础 | 完成 | `src/TKCode.Runtimes`：`IAgentRuntime` / `ExternalAgent` / `TurnWriter` / `RuntimeRegistry` / `ExecutableLocator` / `CliProcess` / `RuntimeCache` |
| 连接层 | `JsonRpcConnection` 新增以下选项 | `src/TKCode.Rpc.Contracts` |
| CodexRuntime | 完成：夹具 C-a…C-e 全部通过 | `Codex/` |
| ClaudeRuntime（界面名 Claude Agent） | 完成：夹具 (a)–(e2) 全部通过 | `Claude/` |
| AcpRuntime | 完成：夹具 A–G 全部通过 | `Acp/` |
| Host 接入 | 完成：协议 v8，见下方说明 | `TKCode.AgentHost` |
| 界面 | 待做（a7 负责） | 运行时分组、"本会话允许"按钮、自动放行开关、运行时状态页、提问面板 |

**连接层新增的选项**：
- 按行分帧。
- 不写 `jsonrpc` 字段。
- 请求 id 起始值可配置。
- 请求内联分派。
- 取消方法名可配置。
- 回退处理器。
- 抑制回复。

**Host 接入**：
- 协议 v8 中的 `runtimes/list`。
- `ModelDto.Runtime`、`SessionDto.Runtime`。
- `SessionOptionsDto.AutoApprove`。
- 审批请求与回复的扩展字段。
- 会话文件中的 `runtimeSession` 记录。

**ACP**：
- 模型列表在第一个会话启动后才能得知，之后缓存到 `~/.tkcode/cache/runtimes/`。
- 首次只提供 “Default”。

验证：
- 测试共 27 个，覆盖三份规格的全部夹具。
- AgentHost 的 NativeAOT 发布零警告。
- 用 AOT 版 Host 对本机做了只读探测，自动发现 Claude Agent 2.1.301（13 个模型）、Codex 0.162.1（32 个模型）、Cursor Agent、OpenCode。

已知限制：
- 尚未用真实 CLI 跑过模型轮次，需要用户确认后再进行。
- 提问已经接入问答面板（2026-10-18，[ask-user.md](ask-user.md)）。Codex 的 requestUserInput 只有在用户自己打开 `default_mode_request_user_input` 特性时才会出现；TKCode 不开启 Codex 的实验接口（Plan 协作模式）。
- 子 Agent 帧已接入（Claude、Codex，[subagent-view.md](subagent-view.md)）。后台唤醒轮次、计划 / 命令列表 / 上下文用量等状态类更新暂不展示。
- 工具执行中的输出增量（`ToolExecutionUpdateEvent`）还没有映射到 SessionEvent。

## 10. 参考

- zeron：`crates/harness/src/{lib.rs, claude/, codex/, acp/}`、`docs/research/acp.md`、`crates/proto/src/agent.rs`
- Claude Code：code.claude.com/docs 的 headless、cli-reference、agent-sdk、authentication；claude-agent-sdk-python 的 `_internal/query.py`、`transport/subprocess_cli.py`
- Codex：learn.chatgpt.com/docs/app-server；openai/codex 的 `codex-rs/app-server-protocol`
- ACP：agentclientprotocol.com；github.com/agentclientprotocol/agent-client-protocol 的 `schema/v1/schema.json`；ACP Registry
