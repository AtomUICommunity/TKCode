# TKCode 设置文件规格

TKCode 的数据目录默认为 `~/.tkcode`，可以用环境变量 `TKCODE_HOME` 改到别处。

| 文件 | 由谁写入 | 内容 |
|---|---|---|
| `settings.json` | 用户（手工编辑或通过设置界面） | 厂商与模型、主模型与快速模型、语言、主题 |
| `settings.schema.json` | TKCode 启动时生成 | JSON Schema，供编辑器自动补全 |
| `state.json` | 程序自动写入 | 收藏的模型、最近项目、上次使用的会话参数等 |
| `projects.json` | 程序自动写入 | 项目表：名称、设备与文件夹（第一个是主文件夹），Desktop 写、Host 读（docs/design/projects.md §5.1）；Agent 读写它需要审批 |
| `sessions/` | Agent Host | 会话记录（JSONL） |

## 1. 设计原则

1. **能省则省**：内置模型目录（裁剪后的 models.dev 快照，再叠加 TKCode 自己的补充）。
   - 内置厂商只需要填一个 Key；
   - 自定义厂商的模型只需要写 id，元数据缺失时使用保守的默认值。
2. **同一套写法覆盖内置项**：`providers` 的键与内置厂商 id 相同时，写下的每个字段都是对内置值的覆盖；键不同时就是自定义厂商。
3. **模型列表用以 id 为键的对象**：例如 `"glm-5": {}`，合并方便，也可以逐字段覆盖。
4. **宽容解析**：
   - 支持注释和末尾逗号（JSONC）；
   - 字段名不区分大小写，未知字段会被忽略并给出提示；
   - 解析失败时保留上一次正确的配置，并在设置界面和日志中显示错误。
5. **热重载**：Agent Host 监听 `settings.json`，改动在 500ms 防抖后生效，并把新的模型列表推送给界面。
   - 正在运行的请求不受影响；
   - 下一次请求起使用新的 Key / Base URL；
   - 已打开的会话中，同 id 的模型会更新为新配置。

## 2. 格式

```jsonc
{
  "$schema": "./settings.schema.json",

  // 主模型：新会话默认使用，可在会话内单独切换。
  "model": "anthropic/claude-opus-5-5",

  // 快速模型：子代理和会话标题使用。省略时跟随当前会话模型。
  "fastModel": "anthropic/claude-haiku-5-5",

  "language": "zh-CN",              // zh-CN | en-US
  "theme": "system",                // system | dark | light
  "uiScale": 1,                     // 界面缩放：0.75–2，文字、图标与间距一起缩放（设置页档位 0.8 / 0.9 / 1 / 1.15 / 1.3 / 1.5，⌘+ ⌘- ⌘0 调节）
  "reduceMotion": false,            // true：关闭界面动画（macOS 系统的“减少动态效果”同样生效）
  "closeToTray": true,              // false：关闭窗口即退出；默认隐藏到系统托盘，会话继续运行
  "autoCompact": true,              // 内置 Agent 接近上下文预算时自动压缩；false 时仍可用 /compact 手动压缩
  "autoMemory": true,               // 内置 Agent 的项目记忆（~/.tkcode/projects/<项目>/memory/）；对之后新建或打开的会话生效
  "originalImages": false,          // true：Markdown 图片按原始清晰度显示；默认按显示尺寸压缩解码以节省内存，点击图片查看的始终是原图
  "promptCacheTtl": "5m",           // Claude 提示缓存有效期：5m | 1h（1 小时的缓存写入按 2 倍计费）

  "providers": {
    // ① 内置厂商：只写 Key
    "anthropic": { "apiKey": "sk-ant-..." },

    // ② 引用环境变量（也接受旧写法 "$DEEPSEEK_API_KEY"）
    "deepseek": { "apiKey": "{env:DEEPSEEK_API_KEY}" },

    // ③ 内置厂商：只启用其中几个模型，并覆盖某个模型的元数据
    "openrouter": {
      "apiKey": "{env:OPENROUTER_API_KEY}",
      "models": {
        "anthropic/claude-opus-5-5": {},
        "qwen/qwen3-coder": { "contextWindow": 262144 }
      }
    },

    // ④ 自定义的 OpenAI 兼容端点
    "my-gateway": {
      "name": "My Gateway",
      "api": "chat",                 // chat | responses | messages
      "baseUrl": "https://gw.example.com/v1",
      "apiKey": "sk-...",
      "models": {
        "qwen3-coder": { "contextWindow": 262144, "reasoning": true },
        "glm-5": {}
      }
    },

    // ⑤ 暂时停用某个厂商（保留配置）
    "openai": { "enabled": false }
  }
}
```

### 2.1 顶层字段

| 字段 | 类型 | 默认值 | 说明 |
|---|---|---|---|
| `model` | string | 上次使用的模型或第一个可用模型 | 主模型，新会话默认使用，优先于上次使用的模型。`厂商/模型`，模型 id 本身可以包含 `/`（例如 OpenRouter），以第一个 `/` 作为分隔 |
| `fastModel` | string | 跟随会话 | 子代理与会话标题默认使用的快速模型，写法同 `model`。只支持内置 API 模型（含自定义厂商），不支持外部 CLI 运行时模型。未配置或不可用时使用当前会话模型；`agent` 工具显式指定 `model` 时优先使用该值 |
| `language` | `zh-CN` \| `en-US` | `zh-CN` | 界面语言 |
| `theme` | `system` \| `dark` \| `light` | `system` | 主题 |
| `uiScale` | number | `1` | 界面缩放比例，范围 0.75–2，超出时按边界处理并给出警告。整个窗口（文字、图标、间距、终端、代码区）等比缩放，菜单与选择器弹层随之缩放；工具提示与确认对话框由 AtomUI 放在独立窗口里，目前不随缩放（保持 100%）。设置页「通用」提供 0.8 / 0.9 / 1 / 1.15 / 1.3 / 1.5 六档，⌘+ / ⌘- 沿同一组档位调节、⌘0 恢复 1（Windows / Linux 为 Ctrl）；命令面板里也有对应条目。默认值不写入文件 |
| `promptCacheTtl` | `5m` \| `1h` | `5m` | 提示缓存断点（`cache_control`）的有效期，全局生效：所有 Anthropic Messages 端点，以及经 Chat 协议调用的 Claude 模型。5 分钟的条目每次读取自动续期；1 小时的写入按 2 倍计费，适合两次提问常隔 5–60 分钟的用法。Host 每次请求时读取，进行中的会话也立即生效。设置页「通用」可改。其它值给出警告并按 `5m` 处理 |
| `providers` | object | `{}` | 键为厂商 id |
| `webSearch` | object | 无 | 内置 Agent 的 `web_search` 后端，见 §2.6；不配置就不提供该工具 |
| `permissions` | object | 无 | `additionalDirectories`：除 cwd 外，也算作所有会话工作区的目录（项目自己的文件夹在项目里设置，docs/design/projects.md §6.2）。这些目录内的只读操作不需要审批；修改后在下一轮开始前生效 |
| `agent` | object | 无 | 内置 Agent 的设置。目前只有 `shell.profile`：bash 工具使用的 shell，见 §2.8 |
| `terminal` | object | 无 | 交互终端的设置。目前只有 `defaultProfile`：新建终端默认使用的 shell，见 §2.8 |
| `mcpServers` | object | `{}` | MCP 服务器，键为服务器名（`[A-Za-z0-9_-]{1,32}`），见 §2.7 |
| `defaultRole` | string | `agent` | 新会话的模式：`agent`、`plan`、`acp`，或自定义模式（`roles/*.md`）的名字。当前项目没有这个自定义模式时退回 `agent`。见 [design/composer.md](design/composer.md) §10.3 |
| `skills` | object | 无 | `autoDiscover`（默认 `true`）：在内置 Agent 的系统提示里列出 skill，让模型自己加载。设为 `false` 后只能在输入框里用 `/skill名` 显式调用。skill 的目录约定见 [design/composer.md](design/composer.md) §4 |
| `update` | object | 无 | 应用内更新：`prerelease`（默认 `true`：也接收 GitHub 上标记为 Pre-release 的版本；`false` 时只接收正式版）、`autoCheck`（默认 `true`：启动 60 秒后及之后每 6 小时检查；`false` 时只能在「设置 → 关于」手动检查）。见 [design/auto-update.md](design/auto-update.md) |

设置 → 通用 → 模型配置提供「主模型」与「快速模型」两个选择器。快速模型默认选中「跟随会话」；主模型可以选择外部智能体的模型。修改主模型不切换已有会话，修改快速模型对下一次子代理或标题请求生效。

新会话在收到第一条用户消息后异步生成一次短标题，通过 `sessionTitleChanged` 会话事件更新界面，并以 `meta.title` 追加到会话文件。生成请求使用独立上下文，不加入主会话历史或用量；API 请求不带工具。外部智能体在没有快速模型时使用同一运行时与模型的临时只读会话（不注入 MCP 桥接，审批全部拒绝）。请求失败、为空或超过 30 秒时保留首条消息标题；生成期间手动重命名（包括清空）始终优先，重新打开旧会话不重新生成。

### 2.2 厂商（`providers.<id>`）

| 字段 | 类型 | 内置厂商 | 自定义厂商 | 说明 |
|---|---|---|---|---|
| `apiKey` | string | 可选 | 可选 | 明文、`{env:VAR}` 或 `$VAR`。内置厂商省略时依次读取目录中登记的环境变量（如 `ANTHROPIC_API_KEY`） |
| `enabled` | bool | 可选 | 可选 | 默认 `true` |
| `name` | string | 可选 | 可选，默认为 id | 显示名称 |
| `api` | `chat` \| `responses` \| `messages` | 可选 | **必填** | 线协议：OpenAI Chat Completions / OpenAI Responses / Anthropic Messages |
| `baseUrl` | string | 可选 | **必填** | 例如 `https://api.deepseek.com` |
| `headers` | object | 可选 | 可选 | 附加请求头，值同样支持 `{env:VAR}` |
| `hidden` | bool | 可选 | — | `true`：从设置页与模型选择器中移除这个内置厂商。设置页移除“靠环境变量检测出来的”厂商时写入它（只删条目的话，环境变量会让它马上又出现）；重新添加并保存时清除。自定义厂商直接删除条目，不会写它 |
| `models` | object | 可选 | 可选 | 见 2.3。自定义 / 本地厂商省略时自动从 `GET {baseUrl}/models` 获取并全部启用 |
| `compat` | object | 可选 | 可选 | 厂商差异的兜底开关，见 2.4，通常不需要 |

**模型的启用规则**
- 自定义厂商与本地服务（Ollama、LM Studio）省略 `models` 时：Host 通过 `GET {baseUrl}/models`（Anthropic 协议为 `{baseUrl}/v1/models`）获取模型列表并全部启用。
  - 结果会缓存，设置页中可以手动刷新；
  - 获取失败且没有 `models` 时，该厂商显示为“配置错误”，**不影响其它配置**；
  - 发现的模型 id 若在内置目录中存在（例如代理转发的 `deepseek-v4-pro`），自动沿用目录元数据。
- 目录中声明了模型发现的内置厂商（目前只有 NextCoWork，见 §3）同理：填入 Key 后才会请求 `/models`（没有 Key 时网关只会返回 401），发现的模型全部启用，目录中声明为非对话的前缀（图像模型）除外。
- 内置厂商省略 `models` 时，启用目录中的**推荐模型**：TKCode 维护的推荐列表；没有推荐列表时，取最近发布、支持工具调用、未弃用的 8 个模型。
- 写了 `models` 时，**只启用列出的模型**。值是对目录元数据的覆盖，`{}` 表示完全使用目录中的元数据。

**什么情况下厂商会出现在模型选择器中**
1. `enabled` 不是 `false`；
2. 能得到 Key：配置里有，或者内置厂商登记的环境变量已设置；也可以是本地服务（目录中标记为无需 Key，例如 LM Studio，或者自定义厂商的 `apiKey` 写成 `""`）。

### 2.3 模型（`providers.<id>.models.<modelId>`）

所有字段都可选。目录中有该模型时用目录值作为默认，没有时用下表的“缺省”列。

| 字段 | 类型 | 缺省 | 说明 |
|---|---|---|---|
| `api` | `chat` \| `responses` \| `messages` | 跟随厂商 | 该模型使用的线协议，例如同一网关下的 Claude 模型走 Anthropic Messages |
| `baseUrl` | string | 跟随厂商 | 该模型的 Base URL |
| `name` | string | 模型 id | 显示名称 |
| `description` | string | — | 选择器中的一句话说明 |
| `contextWindow` | int | 128000 | 上下文窗口 |
| `defaultContextWindow` | int | 272000（gpt-6.1-sol 为 500000） | 新会话默认的上下文预算。模型窗口更大时，选择器提供“默认预算 / 模型最大窗口”两档；预算也是自动压缩的依据 |
| `maxTokens` | int | 16384 | 单次输出上限 |
| `reasoning` | bool | false | 是否为推理模型 |
| `efforts` | string[] | 按协议推导 | 可选的 effort 档位：`none` / `minimal` / `low` / `medium` / `high` / `xhigh` / `max` 的子集 |
| `images` | bool | false | 是否支持图片输入 |
| `fastMode` | bool | false | 是否支持 fast mode |
| `compat` | object | — | 同 2.4，只对这个模型生效 |

模型使用 `messages` 协议且 Base URL 以 `/v1` 结尾时，会自动去掉末尾的 `/v1`。这是因为 Messages 适配器自己会拼 `/v1/messages`：网关常把 Base URL 写成 `…/v1` 给 Chat / Responses 用，同一个地址下的模型改用 Messages 时不需要改 URL。

```jsonc
"gw": {
  "api": "chat", "baseUrl": "http://127.0.0.1:17321/v1",
  "models": {
    "glm-5.3-flash": {},                         // Chat Completions
    "claude-opus-5-5": { "api": "messages" },    // → http://127.0.0.1:17321/v1/messages
    "gpt-6.1-sol": { "api": "responses" }
  }
}
```

### 2.4 `compat`（兜底）

一般由 TKCode 根据 Base URL 和目录自动判断。只有遇到确认的厂商差异时才需要手写。字段与代码中的 `ModelCompat` 一一对应，常用的有：

- `maxTokensField`：`max_tokens` \| `max_completion_tokens`
- `thinkingFormat`：`OpenAIEffort` \| `ThinkingType` \| `ThinkingTypeAndEffort` \| `EnableThinking` \| `OpenRouter` \| `None`
- `requiresReasoningContentOnAssistant`：带工具时，历史助手消息是否必须带 `reasoning_content`
- `supportsDeveloperRole`、`supportsUsageInStreaming`、`supportsDisabledThinking`、`authHeader`（`x-api-key` \| `bearer`）、`anthropicBetas`
- 提示缓存（默认值见 [protocols/anthropic-messages.md](protocols/anthropic-messages.md) §8.3、[protocols/openai-chat-completions.md](protocols/openai-chat-completions.md) §9）：
  - `supportsPromptCacheKey`：Chat 协议发送按会话的 `prompt_cache_key`。默认对所有端点发送，只有 Gemini 的 OpenAI 兼容端点例外（未知字段会 400）。某个端点因为这个字段报错时设为 `false`。
  - `supportsCacheControl`：在内容块上放 `cache_control` 断点。Messages 协议默认对所有端点开启；Chat 协议默认只对 Claude 模型与百炼的 Qwen 模型开启。端点不接受时设为 `false`。

### 2.6 网页搜索（`webSearch`）

```jsonc
"webSearch": { "provider": "brave", "apiKey": "{env:BRAVE_API_KEY}" }
```

| 字段 | 说明 |
|---|---|
| `provider` | `brave` \| `tavily` \| `exa` \| `searxng` |
| `apiKey` | 写法同模型 Key（明文、`{env:VAR}` 或 `$VAR`）。SearXNG 不需要 |
| `baseUrl` | 自定义端点。使用 SearXNG 时必须填写，值为实例地址；实例必须在 `search.formats` 中开启 `json` |

`web_fetch` 不需要配置。对外的网络请求在 Ask 档会按域名请求审批。

### 2.7 MCP 服务器（`mcpServers`）

设计见 [design/mcp.md](design/mcp.md)，协议规格见 [protocols/mcp.md](protocols/mcp.md)。

```jsonc
"mcpServers": {
  "filesystem": { "command": "npx", "args": ["-y", "@modelcontextprotocol/server-filesystem", "${workspaceFolder}"] },
  "github": { "url": "https://api.githubcopilot.com/mcp/", "headers": { "Authorization": "Bearer {env:GITHUB_TOKEN}" } },
  "linear": { "url": "https://mcp.linear.app/mcp" },                 // 无 Authorization 头：401 时进入 OAuth 登录
  "sentry": { "url": "https://mcp.sentry.dev/mcp", "enabled": false }
}
```

| 字段 | 说明 |
|---|---|
| `command` / `args` / `env` / `cwd` / `envFile` | 本地（stdio）服务器。`cwd` 默认为会话的项目目录；`envFile` 为 dotenv 文件，其中的变量合并进 `env`（`env` 中写明的优先） |
| `url` / `headers` | 远程服务器。默认使用 Streamable HTTP，不通时自动回退到旧版 HTTP+SSE |
| `type` | `stdio` \| `http` \| `sse`，通常可以省略；写 `sse` 表示强制使用旧版 SSE |
| `oauth` | `clientId` / `clientSecret`（预注册的客户端）、`callbackPort`（固定的回调端口）、`scopes` |
| `enabled` | 默认 `true` |
| `disabledTools` | 不暴露给模型的工具（MCP 原名） |
| `alwaysAllow` | Ask 档下不需要审批的工具；`"*"` 表示整个服务器 |
| `startupTimeout` / `toolTimeout` | 秒，默认分别为 30 和 600 |
| `name` | 显示名 |

- **变量**：所有字符串都支持 `{env:VAR}`、`$VAR`、`${VAR}`、`${VAR:-default}`、`${env:VAR}`、`${workspaceFolder}`、`${userHome}`。变量未设置时展开为空串，并在设置页提示。
- **项目级配置**：项目根目录（git 根）的 `.mcp.json`，格式与 Claude Code 相同（顶层为 `mcpServers`），覆盖同名的全局服务器。项目服务器需要用户确认信任后才会启动；信任记录保存在 `~/.tkcode/mcp/trust.json`，配置改变后需要重新确认。
- **工具名**：模型看到的名字是 `mcp__<服务器>__<工具>`，长度上限 64。
- **错误处理**：单个服务器的配置有误时，只停用该服务器，不影响整个文件。

### 2.8 默认 shell（`agent.shell.profile`、`terminal.defaultProfile`）

两项取值规则相同，省略或写 `auto` 表示按默认顺序选：

| 取值 | 含义 |
|---|---|
| `auto` / 省略 | Windows：Agent 为 PowerShell 7 最新稳定版 → Git Bash → Windows PowerShell；终端为 PowerShell 7 → Windows PowerShell → cmd。macOS / Linux：Agent 用 `$SHELL`（限 bash / zsh）→ `/bin/bash` → `/bin/sh`，终端用登录 shell。PowerShell 7 的预览版不参与 auto |
| 探测到的 id | Windows：`pwsh`（最新稳定版）、`pwsh-preview`、`powershell`（5.1）、`git-bash`、`cmd`。macOS / Linux：`/etc/shells` 里各 shell 的文件名（`bash`、`zsh`、`fish`……）与 PATH 里的 `pwsh`。同一个 id 对应多个安装时取排名最高的：先比 pwsh 的大版本号，同一大版本内 Store > Scoop > dotnet tool > Program Files > 32 位 |
| 程序名 | 在 PATH 里查找，例如 `nu` |
| 绝对路径 | 可执行文件的完整路径，可带引号；按文件名判断类型（`pwsh.exe`、`bash.exe`……），其余按 `-c` 风格处理 |

- 指定的 shell 找不到时退回 `auto`，不会让设置失效；设置页会把它标成“不可用”，并说明改用了哪个 shell。
- `agent.shell.profile` 不能是 `cmd`（它的引号规则与逐个传参不兼容），写了也按 `auto` 处理。
- **生效时机**：`agent.shell.profile` 对修改之后执行的命令生效；系统提示的 `Shell:` 一行随之更新，模型在下一轮收到一条更新说明。`terminal.defaultProfile` 只影响之后新建的终端，已打开的标签不变。
- 设置页「通用」里的两个下拉框列出本机探测到的 shell，可选 `auto`，也可以填一个自定义路径。
- PowerShell 作为 Agent 的 shell 时，命令以 UTF-8 输出（命令前加 `[Console]::OutputEncoding=[System.Text.Encoding]::UTF8;`），避免中文系统上的乱码。
- 完整的 profile 体系（`terminal.profiles`、WSL / MSYS2 / VS 开发者命令行、shell 集成）见 [terminal/07-shell-profiles-config.md](terminal/07-shell-profiles-config.md)，以上两项的键名与它一致。

## 3. 内置目录

- **来源**：models.dev（`https://models.dev/api.json`）的快照，由 `tools/gen_catalog.py` 裁剪后作为嵌入资源放在 `src/TKCode.AgentHost/Catalog/catalog.json`。
- **只保留能映射到三种协议的厂商**：

  | npm 包 | 协议 |
  |---|---|
  | `@ai-sdk/openai-compatible` | chat |
  | `@openrouter/ai-sdk-provider` | chat |
  | `@ai-sdk/xai` / `groq` / `cerebras` / `deepinfra` | chat，并补上 Base URL |
  | `@ai-sdk/openai` | responses |
  | `@ai-sdk/anthropic` | messages |

- **只保留**支持工具调用、未弃用、输出为文本的模型。
- **字段映射**：

  | models.dev | TKCode |
  |---|---|
  | `reasoning_options.effort.values` | `efforts` |
  | 存在 `toggle` | 可以关闭思考（`supportsDisabledThinking`） |
  | `interleaved.field == "reasoning_content"` | `requiresReasoningContentOnAssistant` |
  | `limit.context` / `limit.output` | `contextWindow` / `maxTokens` |
  | `modalities.input` 含 `image` | `images` |

- **TKCode 补充**（`BuiltInCatalog.Overlay`）：各厂商的推荐模型、一句话说明、fast mode、个别模型的默认上下文预算（`DefaultContextWindows` 表，按去掉路由前缀与 `@`/`:` 后缀的模型 id 匹配所有厂商），以及 models.dev 没有覆盖的默认 Base URL。
- **NextCoWork**（`nextcowork`，<https://nextco.work>）：TKCode 自己登记的内置厂商，models.dev 没有收录它。
  - 默认协议 `responses`，Base URL `https://nextco.work/v1`（Bearer 鉴权），环境变量 `COWORK_API_KEY`；
  - **Claude 系列（`claude-` 前缀）固定走 `messages`**，网关的 Anthropic 接口是 `https://nextco.work/v1/messages`，所以这些模型的 Base URL 会去掉 `/v1`（见 2.3）。固定只是模型的默认协议：设置页或 `models.<id>.api` 仍然可以为单个模型覆盖，设置页里“跟随厂商”指的就是这个默认协议；
  - 模型列表在填入 Key 后通过 `GET /v1/models` 获取；`gpt-image-`、`grok-imagine-` 开头的图像模型不能对话，会被排除；
  - 目录里的 `ModelApis`（模型 id 前缀 → 协议）、`Discover`、`ExcludedModels` 就是为此增加的 TKCode 补充字段。
- **优先级**：目录 → TKCode 补充 → `settings.json`，后者覆盖前者。

### 3.1 更新目录（手动）

目录**从不自动更新**，只有用户确认后才会应用：

1. **检查更新**：设置 › 通用 › 模型目录。Host 下载 `https://models.dev/api.json`（约 5.6MB），按与 `tools/gen_catalog.py` 相同的规则裁剪（`Catalog/ModelsDevTrimmer.cs`），然后与当前目录比较。结果暂存在 Host 内存中，**当前目录不变**。
   - 已应用过更新时会带上 `If-None-Match`；目录没有变化时服务端返回 304，界面显示“目录已是最新”。
2. **查看差异**：按厂商列出新增、移除、变更的模型，以及字段变化（名称、上下文、输出上限、推理、图片、推理档位、可否关闭思考、输入 / 输出价格、厂商的 Base URL 与协议）。
   - 默认只显示用户已添加的厂商，可以切换为显示全部；
   - 每个厂商最多显示 20 个模型。
3. **应用更新或放弃**：应用后写入 `~/.tkcode/cache/catalog.json`，替换目录，重载模型列表并推送 `models/changed`。放弃则丢弃暂存结果。

- **失败处理**：网络错误、格式错误或结果为空时，不产生暂存结果，设置页显示错误。
- **启动加载**：有有效缓存就用缓存（即上次应用的更新），否则用嵌入快照。
- **规则一致性**：Python 与 C# 两份裁剪规则必须同步修改。用同一份 api.json 设置 `TKCODE_MODELSDEV_FILE` 后运行 `TrimmerParityTests`，可以验证两者输出一致。

### 2.5 错误的级别

| 级别 | 示例 | 影响 |
|---|---|---|
| 文件错误 | JSON 语法错误、类型错误、`api` 取值非法 | 整个文件不生效，**沿用上一次正确的配置**，设置页顶部显示红色横幅 |
| 厂商错误 | 自定义厂商缺少 `api` / `baseUrl`，没有可用模型 | 只停用该厂商，在它的详情页显示原因 |
| 警告 | 未知字段 | 忽略该字段，写入日志 |

## 4. 开发用 Fake 厂商

满足以下任一条件时会出现 Fake 厂商：
- 设置了环境变量 `TKCODE_FAKE_PROVIDER=1`；
- 没有任何其它可用模型。

这样离线也能体验完整流程，配置了真实厂商后它不会混在模型列表里。

## 5. 界面与写回

- **入口**：设置页（侧栏 ⚙、⌘,、命令面板“打开设置”）。左侧导航：通用 / 模型与厂商 / 关于。
- **模型与厂商**：主从双栏。
  - 左栏：搜索；“已启用”组（已配置或检测到环境变量 Key 的厂商）；“可添加”组（推荐厂商，搜索时为整个目录中的匹配项）；底部是“自定义厂商”入口。
  - 右栏：连接表单（协议、Base URL、Key 统一一个“保存”按钮，“测试连接”可以使用未保存的值）。
  - 模型表：逐个开关、全部启用 / 停用、从 `/models` 刷新；每行显示能力标签（推理 / 图片 / fast mode）、上下文、输出上限和价格，点击编辑可以覆盖单个模型。
  - 只有已写入 settings.json 的厂商显示启用开关；只选中未配置的厂商不会写文件。
  - **移除**：每个厂商的右上角都有垃圾桶。“添加厂商”选出来、还没保存的草稿，它是“放弃”（不确认、不写文件）；其余厂商先确认再删除 settings.json 里的条目。靠环境变量检测出来的内置厂商写成 `{ "hidden": true }`，确认框里会点明变量名，环境变量本身不会被修改。
- **测试连接**：先请求 `/models`，不消耗 tokens；端点不支持时，用第一个启用的模型发一次最多 16 tokens 的请求。
- **写回方式**：界面修改按 JSON 节点写回 `settings.json`，只改动涉及的字段，其余字段和键的顺序保持不变。
  - **注释会丢失**：界面保存时会提示这一点；
  - 写入后把文件权限设为 0600，因为文件里可能有明文 Key。
- **模型启用开关**：某个内置厂商第一次在界面上切换模型启用时，先把当前启用的推荐模型写进 `models`，再做增删。
- **迁移**：
  - 旧的 `models.json` 中的 Provider 会在启动时合并进 `settings.json`，原文件改名为 `models.json.bak`；
  - 旧的 `desktop.json` 中，语言和主题移到 `settings.json`，其余内容移到 `state.json`。
