# OpenAI Chat Completions 协议规格（TKCode 实现用）

> 版本基线：2026-10-16 抓取的官方文档（`.md` 版本）+ openai-python v3.31.0（commit `9998c1a`，2026-10-15）。
> 用途：TKCode（C#/.NET 10）以**流式**方式调用 `POST /v1/chat/completions`，并兼容 DeepSeek、Kimi、智谱 GLM、阿里云百炼（DashScope）、OpenRouter、Ollama、LM Studio 等 OpenAI 兼容端点。
> 所有结论都标了出处。标为「未证实」的条目是官方文档没有写明、只来自经验或推断的说法，实现时要做容错，不要硬依赖。

## 0. 来源

| 代号 | 来源 |
|---|---|
| [Ref-Chat] | https://developers.openai.com/api/reference/resources/chat.md（Create / Domain Types / Streaming 示例；网页版为 https://developers.openai.com/api/docs/api-reference/chat） |
| [Ref-Chunk] | https://developers.openai.com/api/reference/resources/chat/subresources/completions/streaming-events.md |
| [G-FC] | https://developers.openai.com/api/docs/guides/function-calling.md |
| [G-SO] | https://developers.openai.com/api/docs/guides/structured-outputs.md（Supported schemas 一节） |
| [G-Reasoning] | https://developers.openai.com/api/docs/guides/reasoning.md |
| [G-Cache] | https://developers.openai.com/api/docs/guides/prompt-caching.md |
| [G-Tokens] | https://developers.openai.com/api/docs/guides/token-counting.md |
| [G-Vision] | https://developers.openai.com/api/docs/guides/images-vision.md |
| [G-Errors] | https://developers.openai.com/api/docs/guides/error-codes.md |
| [G-RateLimits] | https://developers.openai.com/api/docs/guides/rate-limits.md |
| [SDK] | `github.com/openai/openai-python`：`src/openai/types/chat/*.py`、`src/openai/types/completion_usage.py`、`src/openai/types/shared_params/function_definition.py`、`src/openai/types/shared/reasoning_effort.py`、`src/openai/_streaming.py`、`src/openai/_base_client.py`、`src/openai/_constants.py`、`src/openai/_exceptions.py`、`src/openai/lib/streaming/_deltas.py` |
| [DeepSeek] | https://api-docs.deepseek.com/guides/thinking_mode 、https://api-docs.deepseek.com/api/create-chat-completion |
| [Kimi] | https://platform.kimi.ai/docs/guide/use-kimi-k2-thinking-model 、https://platform.kimi.ai/docs/api/chat |
| [GLM] | https://docs.bigmodel.cn/cn/guide/capabilities/thinking-mode.md 、https://docs.bigmodel.cn/cn/guide/capabilities/thinking 、https://docs.bigmodel.cn/cn/guide/capabilities/stream-tool 、https://docs.bigmodel.cn/api-reference/模型-api/对话补全.md |
| [DashScope] | https://help.aliyun.com/zh/model-studio/deep-thinking 、https://help.aliyun.com/zh/model-studio/qwen-function-calling 、https://help.aliyun.com/zh/model-studio/compatibility-of-openai-with-dashscope |
| [OpenRouter] | https://openrouter.ai/docs/use-cases/reasoning-tokens 、https://openrouter.ai/docs/api/reference/streaming 、https://openrouter.ai/docs/use-cases/usage-accounting |
| [Ollama] | https://docs.ollama.com/api/openai-compatibility ；源码 `github.com/ollama/ollama` 的 `openai/openai.go`、`middleware/openai.go`（main 分支，2026-10-16） |
| [LMStudio] | https://lmstudio.ai/docs/developer/openai-compat/chat-completions 、https://lmstudio.ai/docs/developer/openai-compat/tools 、https://lmstudio.ai/changelog/lmstudio-v0.4.8 |

> 注意：[G-FC]、[G-Reasoning]、[G-Cache] 的 `.md` 版本**正文以 Responses API 为主**，Chat Completions 的流式工具调用增量格式没有单独示例。本文对 Chat 工具调用增量的描述以 [Ref-Chunk] 的 schema 和 [SDK] 的累加实现为准。

---

## 1. Endpoint、认证与请求头

- **Endpoint**：`POST https://api.openai.com/v1/chat/completions` [Ref-Chat]。SDK 默认 base URL 为 `https://api.openai.com/v1`，也可以用环境变量 `OPENAI_BASE_URL` 覆盖 [SDK `_client.py`]。
- **官方定位**：新项目推荐使用 Responses API。Chat Completions 仍受支持，但新能力（推理条目回传、`reasoning.mode`、加密推理等）只在 Responses 提供 [Ref-Chat；G-Reasoning]。
- **硬限制**：**GPT-6 Astra 和 GPT-6.1 Sol 在 Chat Completions 上不支持 function calling**，需要用 Responses API [G-FC 开头；G-Reasoning「Reasoning effort」节]。TKCode 选择这两个模型时，应路由到 Responses 适配器，或者禁止在 Chat 中带 `tools`。
- **请求头**
  - `Authorization: Bearer <API_KEY>`：必填 [Ref-Chat 示例；SDK `auth_headers`]。
  - `Content-Type: application/json` [Ref-Chat 示例]。
  - `OpenAI-Organization: <org_id>`、`OpenAI-Project: <project_id>`：可选，只在配置了组织或项目时发送 [SDK `default_headers`]。
  - 流式响应的 `Content-Type` 为 `text/event-stream`（SSE）[Ref-Chat `stream` 参数说明]。
- **兼容端点的 base URL**（实现上统一拼成 `{baseUrl}/chat/completions`）：
  - DeepSeek：`https://api.deepseek.com`（前缀续写 Beta 版用 `https://api.deepseek.com/beta`）[DeepSeek]
  - 智谱：`https://open.bigmodel.cn/api/paas/v4/` [GLM 示例]
  - 百炼：`https://{WorkspaceId}.{region}.maas.aliyuncs.com/compatible-mode/v1`（API Key 与地域绑定，跨地域会返回 401 `invalid_api_key`）[DashScope]
  - Ollama：`http://localhost:11434/v1`；LM Studio：`http://localhost:1234/v1`（默认端口，未在本次抓取的页面中逐字确认）

---

## 2. 请求体

### 2.1 字段总表（与 agent 客户端相关的部分）

类型与描述取自 [SDK `completion_create_params.py`] 和 [Ref-Chat Body Parameters]。两处一致。

| 字段 | 类型 | 取值、默认值与约束 | TKCode 建议 |
|---|---|---|---|
| `model` | string | 必填，例如 `gpt-6-astra`、`gpt-5.6-terra`、`o3` | 必发 |
| `messages` | array | 必填，见 §3 | 必发 |
| `stream` | bool \| null | `true` 时以 SSE 流式返回 | 固定 `true` |
| `stream_options` | object \| null | **只在 `stream:true` 时设置** [Ref-Chat]。`include_usage: bool`：在 `data: [DONE]` 之前多发一个 usage chunk，其 `choices` 恒为 `[]`，其它 chunk 的 `usage` 为 `null`。`include_obfuscation: bool`：默认开启，设为 `false` 可以省带宽 [SDK `chat_completion_stream_options_param.py`] | `{"include_usage": true}`；官方端点可加 `"include_obfuscation": false` |
| `max_completion_tokens` | int \| null | 生成 token 上限，**包含可见输出和推理 token** [SDK] | OpenAI 用这个字段 |
| `max_tokens` | int \| null | **已弃用**，被 `max_completion_tokens` 取代，**与 o 系列推理模型不兼容** [SDK] | 只发给需要它的兼容厂商 |
| `reasoning_effort` | `"none"\|"minimal"\|"low"\|"medium"\|"high"\|"xhigh"\|"max"` \| null | 不同模型支持的取值不同 [SDK `reasoning_effort.py`；Ref-Chat]。GPT-6 Astra 传 `none` 返回 **HTTP 400**；GPT-6.1 Sol 不支持 `none` 和 `minimal`，默认 `medium`；`gpt-5.5`、GPT-5.6、GPT-6 Sol/Luna 省略时默认 `medium` [G-Reasoning] | 按模型能力表映射；不能把「关闭思考」等同于「省略字段」（见 §8） |
| `tools` | array | function tool 或 custom tool，见 §4 | 有工具才发 |
| `tool_choice` | `"none"\|"auto"\|"required"` 或对象 | 没有 tools 时默认 `none`，有 tools 时默认 `auto` [SDK] | 一般省略 |
| `parallel_tool_calls` | bool | 是否允许并行调用函数；设为 `false` 时**只会调用零个或一个**工具 [G-FC「Parallel function calling」] | 一般省略（默认允许并行） |
| `store` | bool \| null | 是否保存输出以供蒸馏和 evals 使用；图片输入超过 8MB 会被丢弃 [SDK]。官方文档**没有写默认值** | 官方端点显式发 `false` |
| `prompt_cache_key` | string \| null | 缓存路由与分账键，取代 `user` 字段 [SDK]。GPT-5.6 之前的模型靠它提高命中率；GPT-5.6 及以后可选，主要用于按用户或会话分账、防止跨用户的缓存探测 [G-Cache] | 按会话发送，默认所有端点（Gemini 除外，见 §9） |
| `prompt_cache_options` | `{mode?: "implicit"\|"explicit", ttl?: "30m"}` | **GPT-5.6 及以后**支持。`implicit`（默认）会自动建 1 个隐式断点，并写入最近 3 个显式断点；`explicit` 不建隐式断点，写入最近 4 个显式断点；`ttl` 只能是 `30m` [SDK] | 暂不使用 |
| `prompt_cache_retention` | `"in_memory"\|"24h"` \| null | **已弃用**，改用 `prompt_cache_options.ttl`；`gpt-5.5` 及以后只支持 `24h` [SDK] | 不发 |
| `safety_identifier` | string \| null | 最长 128 字符，建议使用哈希值 [Ref-Chat] | 可选 |
| `user` | string | 已弃用，被 `safety_identifier` 和 `prompt_cache_key` 取代 [SDK] | 不发 |
| `verbosity` | `"low"\|"medium"\|"high"` \| null | 默认 `medium` [SDK] | 可选 |
| `temperature` | number \| null | 0 到 2 [SDK]。参数支持随模型而异，推理模型请查 reasoning guide [Ref-Chat 开头] | 推理模型不发 |
| `top_p`、`frequency_penalty`、`presence_penalty`、`logit_bias`、`seed`、`logprobs`、`top_logprobs`(0–20) | — | 标准采样参数 [SDK] | 不发 |
| `stop` | string \| string[] \| null | 最多 4 个；**o3、o4-mini 不支持** [SDK] | 不发 |
| `n` | int \| null | 候选数量；agent 场景保持 1 [SDK] | 不发 |
| `response_format` | text / json_schema / json_object | Structured Outputs [SDK] | agent 主循环不用 |
| `service_tier` | `"auto"\|"default"\|"flex"\|"scale"\|"priority"\|"fast"` \| null | 默认 `auto`；请求 `fast` 时，响应里显示为 `priority` [SDK] | 可选 |
| `metadata` | map | 最多 16 对，key ≤64 字符，value ≤512 字符 [SDK] | 不发 |
| `moderation` | `{model, policy?}` | 开启 moderated completions 后，流中会出现带 `moderation` 字段的 chunk [SDK] | 不发 |
| `prediction`、`audio`、`modalities`、`web_search_options` | — | 预测输出、音频、网页搜索 | 不发 |
| `functions`、`function_call` | — | **已弃用**，改用 `tools` 和 `tool_choice` [SDK] | 不发 |

### 2.2 最小流式请求示例

```json
{
  "model": "gpt-5.6-terra",
  "stream": true,
  "stream_options": { "include_usage": true },
  "store": false,
  "max_completion_tokens": 32000,
  "reasoning_effort": "medium",
  "prompt_cache_key": "tkcode-session-7f3a",
  "messages": [
    { "role": "developer", "content": "You are TKCode, a coding agent." },
    { "role": "user", "content": "列出 src 目录" }
  ],
  "tools": [
    {
      "type": "function",
      "function": {
        "name": "list_dir",
        "description": "List files in a directory.",
        "parameters": {
          "type": "object",
          "properties": { "path": { "type": "string" } },
          "required": ["path"]
        }
      }
    }
  ]
}
```

---

## 3. `messages`：各角色的精确结构

`ChatCompletionMessageParam` 是以下 6 种消息的联合类型：developer、system、user、assistant、tool，以及已弃用的 function [SDK `chat_completion_message_param.py`；Ref-Chat]。

### 3.1 developer / system

```jsonc
{ "role": "developer", "content": "string" | [ { "type": "text", "text": "...", "prompt_cache_breakpoint"?: { "mode": "explicit" } } ], "name"?: "string" }
{ "role": "system",    "content": 同上, "name"?: "string" }
```
- 数组形式**只支持 `text` 部件** [Ref-Chat]。
- 「With o1 models and newer, `developer` messages replace the previous `system` messages」[Ref-Chat；SDK]。官方模型推荐用 `developer`。兼容厂商大多只认 `system`（见 §10）。

### 3.2 user

```jsonc
{ "role": "user", "content": "string" | [ContentPart...], "name"?: "string" }
```
`ContentPart` 有 4 种 [SDK `chat_completion_content_part_param.py`]：

| type | 结构 | 说明 |
|---|---|---|
| `text` | `{type:"text", text, prompt_cache_breakpoint?}` | |
| `image_url` | `{type:"image_url", image_url:{url, detail?: "auto"\|"low"\|"high"\|"original"}, prompt_cache_breakpoint?}` | `url` 可以是图片 URL，也可以是 base64 data URL（`data:image/png;base64,...`）。`detail` 省略时为 `auto` [G-Vision]。支持 PNG、JPEG、WEBP、非动画 GIF；单个请求最多 1500 张图、总负载 512MB；每张图经缩放后超过 30,000 patch 时，**请求会被拒绝** [G-Vision] |
| `input_audio` | `{type:"input_audio", input_audio:{data, format:"wav"\|"mp3"}}` | |
| `file` | `{type:"file", file:{file_data?, file_id?, filename?}}` | |

### 3.3 assistant（回放模型的历史输出）

```jsonc
{
  "role": "assistant",
  "content": "string" | [ {type:"text",...}... ] | [ {type:"refusal", refusal:"..."} ] | null,
  "refusal"?: "string" | null,
  "tool_calls"?: [
    { "id": "call_abc", "type": "function", "function": { "name": "list_dir", "arguments": "{\"path\":\"src\"}" } },
    { "id": "call_def", "type": "custom",   "custom":   { "name": "code_exec", "input": "print(1)" } }
  ],
  "name"?: "string",
  "audio"?: { "id": "..." } | null,
  "function_call"?: ...  // 已弃用
}
```
- **`content` 可以省略或为 `null`，前提是给了 `tool_calls`（或 `function_call`）** [Ref-Chat：Required unless `tool_calls` or `function_call` is specified]。
- 数组形式的 content：「one or more of type `text`, or exactly one of type `refusal`」[Ref-Chat]。
- `function.arguments` 是 **JSON 字符串**，不是对象。回放时要原样发回模型生成的那个字符串 [SDK]。
- 官方 schema **没有** `reasoning_content` 或 `reasoning` 字段。OpenAI 不会通过 Chat Completions 返回推理内容，也没有加密推理回传机制（这些只在 Responses 中提供）[G-Reasoning]。兼容厂商的扩展字段见 §10。

### 3.4 tool（工具结果）

```jsonc
{ "role": "tool", "tool_call_id": "call_abc", "content": "string" | [ { "type": "text", "text": "..." } ] }
```
- **tool 消息的 content 只能是文本**：「For tool messages, only type `text` is supported」[Ref-Chat]。Chat Completions 的工具结果**不能带图片**。图片要放进随后的一条 `user` 消息（Responses 的 `function_call_output` 可以带图片和文件数组 [G-FC「Formatting results」]，Chat 不行）。
- 官方 schema 中 tool 消息**没有 `name` 字段** [SDK `chat_completion_tool_message_param.py`]，部分兼容厂商接受或要求这个字段（见 §10）。
- 工具没有返回值时，也要回一个表示成功或失败的字符串 [G-FC]。

### 3.5 多轮与工具循环的顺序规则

1. 带 `tool_calls` 的 assistant 消息之后，必须**紧跟**若干条 `tool` 消息，**每个 `tool_call_id` 恰好对应一条**。中间不能插入 user、system、assistant 消息。（官方文档对这一点写得分散：[G-FC]「Handle zero, one, or multiple calls per response」「append one result per call」；违反时的 400 错误措辞在本次抓取的文档中没有逐字出现，**未证实**，但这是公认的硬约束。）
2. `tool` 消息的前面必须有声明了对应 id 的 assistant `tool_calls`。孤立的 tool 消息会被拒绝（**未证实**，同上）。
3. 多个 tool 结果之间的顺序不限，但它们必须连续。工具产生的图片放在**整组 tool 消息之后**的一条 user 消息里。
4. `tool_call_id` 必须与模型返回的 `id` 完全一致。官方文档**没有规定 id 的长度或字符集**。业界实践把它截到 40 字符以内，TKCode 的 `ChatToolCallId` 也这样做，这条限制**未证实**。
5. 历史中出现过工具调用、但本轮不再提供 `tools` 时：官方文档没有说明是否必须仍然提供 tools。部分代理（如 LiteLLM）要求 `tools` 至少存在。对 OpenAI 官方发 `tools: []` 是否会因为数组为空被拒，**未证实**，见 §11 的检查清单。
6. 推理模型 + 工具：Chat Completions 无法回传推理条目，每一轮推理都从头开始。这是 Chat 与 Responses 的本质差别 [G-Reasoning「function calling」一节只描述了 Responses 的做法]。

---

## 4. `tools` 与 `tool_choice`

### 4.1 function tool（Chat 采用嵌套结构）

```json
{
  "type": "function",
  "function": {
    "name": "get_weather",
    "description": "Retrieves current weather for the given location.",
    "parameters": { "type": "object", "properties": { "location": { "type": "string" } }, "required": ["location"] },
    "strict": false
  }
}
```
- `name`：「Must be a-z, A-Z, 0-9, or contain underscores and dashes, with a maximum length of 64」，即 `^[a-zA-Z0-9_-]{1,64}$` [SDK `function_definition.py`；Ref-Chat]。
- `parameters` 是可选的 JSON Schema 对象。「Omitting `parameters` defines a function with an empty parameter list」[SDK]。
- `strict`：可选。**Chat Completions 默认非 strict**（Responses 则会尽量自动规范成 strict）[G-FC「Strict mode」]。
- 注意与 Responses 的区别：Responses 的 tool 是扁平结构（`{type, name, parameters}`），Chat 需要多套一层 `function`。
- 工具定义会注入上下文，按输入 token 计费。建议一轮开始时可用的函数少于 20 个 [G-FC「Token Usage」]。官方文档没有给出工具数量的硬上限（智谱的硬上限是 128 [GLM 对话补全]）。

### 4.2 strict 模式的 schema 规则 [G-FC；G-SO「Supported schemas」]

- 每个 object 都必须写 `additionalProperties: false`。
- `properties` 里的**所有字段都必须列入 `required`**。可选字段用 `"type": ["string","null"]` 表示。
- 根节点必须是 object，不能是 `anyOf`。
- 支持的类型：string、number、boolean、integer、object、array、enum、anyOf。支持 `$defs`/`$ref` 和递归。
- 支持的关键字：string 的 `pattern`、`format`（date-time、time、date、duration、email、hostname、ipv4、ipv6、uuid）；number 的 `multipleOf`、`maximum`、`exclusiveMaximum`、`minimum`、`exclusiveMinimum`；array 的 `minItems`、`maxItems`。
- 不支持的关键字：`allOf`、`not`、`dependentRequired`、`dependentSchemas`、`if`、`then`、`else`。
- 规模限制：最多 5000 个属性、最多 10 层嵌套；属性名、定义名、enum 值和 const 值的字符串总长不超过 120,000；enum 值总数不超过 1000。
- 带 `strict:true` 但 schema 不合规时，请求会**被拒绝**，并返回缺失约束的细节 [G-FC]。
- **TKCode 建议**：MCP 或第三方工具的 schema 常常不满足这些规则，所以**默认不发 `strict`**，保持非 strict，并在本地对参数做校验。

### 4.3 custom tool

```json
{ "type": "custom", "custom": { "name": "code_exec", "description": "...", "format": { "type": "text" } } }
```
`format` 可以是 `{type:"text"}`，也可以是 `{type:"grammar", grammar:{syntax:"lark"|"regex", definition}}`。模型调用时，assistant 的 `tool_calls[i]` 为 `{type:"custom", custom:{name, input}}` [SDK]。注意：流式 chunk 的 `ChoiceDeltaToolCall.type` 只声明了 `"function"` [SDK `chat_completion_chunk.py`]，**custom tool 的流式增量格式没有文档**。TKCode 不应该在 Chat 中使用 custom tool。

### 4.4 tool_choice [SDK `chat_completion_tool_choice_option_param.py`]

| 取值 | 含义 |
|---|---|
| `"none"` | 不调用工具（没有 tools 时的默认值） |
| `"auto"` | 由模型决定（有 tools 时的默认值） |
| `"required"` | 必须调用一个或多个工具 |
| `{"type":"function","function":{"name":"x"}}` | 强制调用指定函数 |
| `{"type":"custom","custom":{"name":"x"}}` | 强制调用指定 custom tool |
| `{"type":"allowed_tools","allowed_tools":{"mode":"auto"\|"required","tools":[{"type":"function","function":{"name":"x"}}]}}` | 限定可用子集，不改动 `tools` 列表，所以不会破坏 prompt cache [G-FC；G-Cache] |

---

## 5. 流式协议（SSE）

### 5.1 帧格式

- 标准 SSE：每个事件都是 `data: <JSON>\n\n`。Chat Completions **不使用 `event:` 字段**。流以一行 `data: [DONE]` 结束 [Ref-Chat Streaming 示例；`include_usage` 描述中提到「before the `data: [DONE]` message」]。
- 示例中出现了以 `:` 开头的注释行（`: Intermediate chat completion chunks omitted.`）[Ref-Chat]。OpenRouter 会发送 `: OPENROUTER PROCESSING` 保活注释 [OpenRouter]。**解析器必须忽略注释行**。
- SDK 的处理方式 [SDK `_streaming.py`]：
  - `sse.data.startswith("[DONE]")` 时停止。
  - 其它每个事件都按 JSON 解析。**只要 JSON 对象里有非空的 `error` 键，就抛出 `APIError`**，message 取 `error.message`，取不到时使用 "An error occurred during streaming"。
  - SSE 解码遵循 WHATWG 规范：多行 `data:` 用 `\n` 拼接，遇到空行时分发事件，不限制行长。

### 5.2 `chat.completion.chunk` 字段 [Ref-Chunk；SDK `chat_completion_chunk.py`]

| 字段 | 类型 | 说明 |
|---|---|---|
| `id` | string | 每个 chunk 的值相同 |
| `object` | `"chat.completion.chunk"` | |
| `created` | int | Unix 秒；每个 chunk 的值相同 |
| `model` | string | |
| `choices` | Choice[] | `n>1` 时有多个元素；**开启 `include_usage` 后，最后一个 chunk 的 `choices` 为空数组 `[]`** |
| `usage` | CompletionUsage \| null | 只在开启 `include_usage` 时出现：最后一个 chunk 有值，其余 chunk 为 `null`。流中断时可能收不到 |
| `system_fingerprint` | string? | |
| `service_tier` | string? | 实际使用的服务档位 |
| `obfuscation` | string? | 为防止侧信道攻击而填充的随机串；默认存在，`include_obfuscation:false` 时省略。**内容无意义，直接忽略** |
| `moderation` | object? | 只在请求了 moderated completions 时出现：`{input, output}`，每一项是 `moderation_results` 或 `{type:"error",code,message}` |

`Choice`：

| 字段 | 类型 | 说明 |
|---|---|---|
| `index` | int | 候选序号。**应该按 `index` 区分候选，不能按数组位置** |
| `delta` | ChoiceDelta | 见下文 |
| `finish_reason` | `"stop"\|"length"\|"tool_calls"\|"content_filter"\|"function_call"` \| null | 生成期间为 `null`；结束时出现在最后一个带 choices 的 chunk 上。音频的最终更新 chunk 不带此字段 |
| `logprobs` | object \| null | `{content, refusal}` |

`ChoiceDelta`（所有字段都是可选的）：

| 字段 | 类型 | 说明 |
|---|---|---|
| `role` | `"developer"\|"system"\|"user"\|"assistant"\|"tool"` | 通常只在第一个 chunk 出现，值为 `"assistant"` |
| `content` | string \| null | 文本片段。首个 chunk 常为 `""` |
| `refusal` | string \| null | **拒答片段**。它与 `content` 是两条并行的通道，必须单独累加和展示 |
| `tool_calls` | ChoiceDeltaToolCall[] | 见 §5.3 |
| `function_call` | `{name?, arguments?}` | 已弃用 |
| `audio` | `{id?, data?, expires_at?, transcript?}` | 音频输出 |

### 5.3 工具调用增量

`ChoiceDeltaToolCall` = `{ index: int (必有), id?: string, type?: "function", function?: { name?: string, arguments?: string } }` [SDK；Ref-Chunk]。

- **`index` 是唯一必有的字段，也是累加的主键**。SDK 的 `accumulate_delta` 按 `index` 把 delta 合并到同一个对象中：**字符串字段直接拼接**（`id`、`name`、`arguments` 一视同仁），`index` 和 `type` 直接覆盖 [SDK `lib/streaming/_deltas.py`]。一个 chunk 中可能有同一个 index 的多个片段，SDK 也是逐个合并。
- OpenAI 的典型行为（官方没有 Chat 版的逐字示例，以下结论由 schema、SDK 与兼容厂商的文档互相印证）：某个 index 的**第一个片段**带 `id`、`type:"function"`、`function.name` 和 `arguments:""`，后续片段只带 `index` 与 `function.arguments` 片段 [LMStudio tools 示例；DeepSeek create-chat-completion：「`id`, `type`, and `function` on the first chunk of each call. Later chunks carry only argument fragments」]。
- 并行调用时，每个调用使用不同的 `index`（0、1、2…）。`arguments` 是 JSON 字符串片段，**拼接完成后才能解析**。官方明确说明 arguments「does not always generate valid JSON, and may hallucinate parameters」，所以必须先校验再执行 [SDK]。
- 兼容厂商的变体（实现时都要容错）：
  - 后续片段的 `id` 可能是空字符串 `''`，也可能重复同一个 id [DashScope function-calling]。
  - 整个调用一次性下发（arguments 完整，不分片）：Ollama [Ollama `ToToolCalls`]；智谱在 `tool_stream` 为 false（默认）时也一次性下发 [GLM]。
  - 推理内容、正文与工具调用可能出现在同一个 chunk 中；Ollama 会把它们拆成两个 chunk [Ollama `ToStreamChunks`]。
- 流结束时，`finish_reason` 通常为 `"tool_calls"`。但最终判断应该**以是否真的累加出了工具调用为准**，同时也要尊重 `length`（见 §5.5）。

### 5.4 典型事件序列

纯文本 [Ref-Chat 示例]：
```
data: {"id":"chatcmpl-123","object":"chat.completion.chunk","created":1694268190,"model":"gpt-6-astra","system_fingerprint":"fp_44709d6fcb","choices":[{"index":0,"delta":{"role":"assistant","content":""},"logprobs":null,"finish_reason":null}],"obfuscation":"r4N7vQ2m"}

data: {"id":"chatcmpl-123",...,"choices":[{"index":0,"delta":{"content":"Hello"},"logprobs":null,"finish_reason":null}]}

data: {"id":"chatcmpl-123",...,"choices":[{"index":0,"delta":{},"logprobs":null,"finish_reason":"stop"}]}

data: {"id":"chatcmpl-123",...,"choices":[],"usage":{"prompt_tokens":20,"completion_tokens":5,"total_tokens":25,"prompt_tokens_details":{"cached_tokens":0},"completion_tokens_details":{"reasoning_tokens":0}}}

data: [DONE]
```
（最后一个 usage chunk 只在 `include_usage:true` 时出现 [SDK `include_usage` 描述]。）

工具调用（由 schema 与 SDK 推导，id 和片段内容仅作示意）：
```
data: {..."choices":[{"index":0,"delta":{"role":"assistant","content":null,"tool_calls":[{"index":0,"id":"call_A","type":"function","function":{"name":"read","arguments":""}}]},"finish_reason":null}]}
data: {..."choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"function":{"arguments":"{\"pa"}}]},"finish_reason":null}]}
data: {..."choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"function":{"arguments":"th\":\"a\"}"}}]},"finish_reason":null}]}
data: {..."choices":[{"index":0,"delta":{"tool_calls":[{"index":1,"id":"call_B","type":"function","function":{"name":"read","arguments":""}}]},"finish_reason":null}]}
data: {..."choices":[{"index":0,"delta":{"tool_calls":[{"index":1,"function":{"arguments":"{\"path\":\"b\"}"}}]},"finish_reason":null}]}
data: {..."choices":[{"index":0,"delta":{},"finish_reason":"tool_calls"}]}
data: {..."choices":[],"usage":{...}}
data: [DONE]
```

### 5.5 `finish_reason` 语义与处理

| 值 | 含义 [SDK/Ref-Chunk] | TKCode 处理 |
|---|---|---|
| `stop` | 自然结束或命中 stop 序列 | Stop（如果累加出了工具调用，则为 ToolUse） |
| `tool_calls` | 模型调用了工具 | ToolUse |
| `length` | 达到 `max_completion_tokens`/`max_tokens` 上限 | **Length。即使已经累加出部分工具调用，也不能当作 ToolUse 去执行**（参数 JSON 很可能被截断） |
| `content_filter` | 内容被过滤，部分输出被省略 | 以明确的「内容被过滤」错误或停止原因上报，不重试 |
| `function_call` | 已弃用 | 按 ToolUse 处理 |
| 兼容厂商扩展 | DeepSeek：`insufficient_system_resource`（推理资源不足而中断）、`aborted` [DeepSeek]；智谱：`sensitive`、`network_error`、`model_context_window_exceeded` [GLM]；OpenRouter：`error` [OpenRouter] | `insufficient_system_resource` 和 `network_error` 可以重试；`sensitive` 等同于 content_filter；`model_context_window_exceeded` 归为上下文溢出 |

### 5.6 流中错误

- **OpenAI 官方**：HTTP 200 之后发生的错误以 `data: {"error": {...}}` 事件下发。SDK 只要看到顶层 `error` 就抛错 [SDK `_streaming.py`]。错误对象的字段与 HTTP 错误体相同（`message`、`type`、`param`、`code`）[SDK `APIError`]。
- **OpenRouter**：状态码保持 200，错误 chunk 形如 `{"id":...,"object":"chat.completion.chunk","error":{"code":"server_error","message":"..."},"choices":[{"index":0,"delta":{"content":""},"finish_reason":"error"}]}`，发送后流随即终止。**200 加上一个只有错误的 chunk 也属于失败** [OpenRouter streaming]。
- **Ollama**：请求被受理之后再出错时，`writeError` 会直接写出一段 JSON（不带 `data:` 前缀）[Ollama `middleware/openai.go`]。按 SSE 规范，这一行会被当作无效字段丢弃，客户端只会看到流在没有 `finish_reason` 的情况下结束（由源码推断，**未证实**）。
- **截断检测**：如果流在没有 `finish_reason` 和 `[DONE]` 的情况下结束，应视为截断（可重试，但只能在**尚未向用户展示输出**时自动重试；官方 rate-limits 指南写明「Don't automatically replay a request after you've consumed output」）。
- 官方警告：流中断时可能收不到最后的 usage chunk [SDK]。

---

## 6. Usage 字段与计数口径

`CompletionUsage` [SDK `completion_usage.py`]：

```jsonc
{
  "prompt_tokens": 1200,            // 输入总数（含缓存命中与缓存写入）
  "completion_tokens": 300,         // 生成总数（含 reasoning 和格式 token）
  "total_tokens": 1500,             // = prompt + completion
  "prompt_tokens_details": {
    "cached_tokens": 1024,          // 缓存命中（读取）
    "cache_write_tokens": 0,        // 本次写入缓存（"unadjusted number of prompt tokens written to cache"）
    "audio_tokens": 0, "image_tokens": 0, "text_tokens": 0
  },
  "completion_tokens_details": {
    "reasoning_tokens": 128,        // 推理 token（包含在 completion_tokens 内）
    "accepted_prediction_tokens": 0, "rejected_prediction_tokens": 0,
    "audio_tokens": 0, "text_tokens": 0
  }
}
```

- **`completion_tokens` 包含 reasoning**：Chat Completions 以 `completion_tokens` 报告模型生成的全部 token，其中包括不可见的推理 token 和格式 token。即使 `reasoning_tokens` 为 0，`completion_tokens` 也可能大于可见 token 数 [G-Tokens「output token counts」]。`max_completion_tokens` 限制的也是全部生成 token [G-Tokens；SDK]。rejected prediction tokens 同样计入 completion [SDK]。
- **`cached_tokens` 和 `cache_write_tokens` 包含在 `prompt_tokens` 内**：[G-Cache] 的计费公式为 `ordinary_input = input_tokens - cached_tokens - cache_write_tokens`（以 Responses 字段名写成，Chat 的 `prompt_tokens_details` 与之同构）。Kimi 明确说明三者互斥且之和等于 `prompt_tokens` [Kimi chat]。所以**不含缓存的输入 = `prompt_tokens - cached_tokens - cache_write_tokens`**。
- GPT-5.6 之前的模型：`cached_tokens` 会扣除隐藏的系统 token，并向下取整到 128 的倍数；GPT-5.6 及以后报告精确值 [G-Cache]。缓存写入按未缓存输入单价的 1.25 倍计费，读取按 0.1 倍计费（GPT-6.1 Sol 为 0.05 倍）。GPT-5.6 之前的模型不收写入费 [G-Cache]。
- 缓存最小长度：GPT-5.6 及以后为 1,024 个可见输入 token [G-Cache]。
- 兼容厂商的写法见 §10（DeepSeek 用 `prompt_cache_hit_tokens`/`prompt_cache_miss_tokens`，Kimi 额外有顶层 `usage.cached_tokens`，OpenRouter 额外有 `cost`）。

---

## 7. 错误响应、重试与限流

### 7.1 错误体

```json
{ "error": { "message": "...", "type": "invalid_request_error", "param": "messages[3].content", "code": "..." } }
```
SDK 从 `body.code`、`body.param`、`body.type` 读取这些字段 [SDK `_exceptions.py`]。计费类错误**要看 `error.code`**，因为 `error.type` 可能只是笼统的 `insufficient_quota` [G-Errors]。

### 7.2 状态码 [G-Errors；SDK `_exceptions.py`]

| 状态 | 场景 | 是否重试 |
|---|---|---|
| 400 BadRequest | 参数错误（包括不合法的 `service_tier`） | 否 |
| 401 Authentication | key 无效、IP 不在白名单、不属于任何组织等 | 否 |
| 403 PermissionDenied | 不支持的国家或地区 | 否 |
| 404 NotFound | 模型不存在或无权限 | 否 |
| 409 Conflict | 资源冲突 | SDK 会重试 |
| 422 Unprocessable | 请求格式正确但无法处理 | 否（[G-Errors] 的文字写的是「Retry it」，SDK 不会自动重试） |
| 429 `rate_limit_error`/`slow_down` | 速率限制 | **是**，遵守 `Retry-After`，并做指数退避加抖动 |
| 429 `credit_balance_exhausted`、`organization_spend_limit_exceeded`、`project_spend_limit_exceeded`、`organization_usage_limit_exceeded`、type=`insufficient_quota` | 额度或账单问题 | **否**：「Retrying billing, spend, or quota errors won't restore API access」 |
| 500 | 服务端错误 | 是 |
| 503 `service_unavailable_error`/`server_is_overloaded` | 过载 | 是，遵守 `Retry-After` |

### 7.3 SDK 的重试算法（建议对齐）[SDK `_base_client.py`、`_constants.py`]

- 默认 `max_retries = 2`，默认超时 600s，连接超时 5s。
- 判断是否重试的顺序：
  1. `Retry-After` 超过 120s 时**不重试**。
  2. 响应头 `x-should-retry: true/false` 优先，直接按它决定。
  3. 408、409、429 和 ≥500 重试。
  4. 连接错误和超时也会重试。
- 等待时长：优先使用 `retry-after-ms`（毫秒），其次是 `retry-after`（秒，允许小数；也可以是 HTTP 日期）。取值在 0 到 120s 之间时直接采用。否则用指数退避：`min(0.5 * 2^n, 8)` 秒，再乘以 `(1 - 0.25*random)`。
- 限流指南的补充 [G-RateLimits]：把 `Retry-After` 当作**下限**，再加一点随机延迟；失败的请求同样计入每分钟限额；**已经消费过的流输出不要自动重放**。

### 7.4 限流响应头 [G-RateLimits]

`x-ratelimit-limit-requests`、`x-ratelimit-limit-tokens`、`x-ratelimit-remaining-requests`、`x-ratelimit-remaining-tokens`、`x-ratelimit-reset-requests`、`x-ratelimit-reset-tokens`；只在存在项目级限额时出现的 `x-ratelimit-limit-project-tokens`、`x-ratelimit-remaining-project-tokens`、`x-ratelimit-reset-project-tokens`；以及 `Retry-After`（可能出现在 429 和 503 上）。

---

## 8. 推理（reasoning）相关规则汇总

- OpenAI 官方 Chat Completions **不返回推理文本**，只在 `usage.completion_tokens_details.reasoning_tokens` 中报告数量 [G-Reasoning；SDK]。
- `reasoning_effort` 的合法值集合因模型而异。「关闭思考」要发 `"none"`，前提是模型支持它；**省略该字段并不等于关闭思考**，因为模型会用自己的默认值（多为 `medium`）[G-Reasoning]。GPT-6 Astra 不接受 `none`（返回 400），GPT-6.1 Sol 不接受 `none` 和 `minimal`。
- 给推理和输出预留足够空间：起步时至少预留 25,000 token [G-Reasoning，原文针对 `max_output_tokens`]。
- 中途修改 reasoning effort 会改写隐藏指令，从而破坏缓存（GPT-6 在 Responses 中用 `configuration_update`，Chat 没有对应机制）[G-Cache]。

---

## 9. Prompt caching 要点（Chat）

- 只做前缀匹配：「Cache reuse requires the entire rendered prefix to match」[G-Cache]。稳定内容（developer 指令、工具定义）放在前面，历史只追加不改写。不要临时增删工具，需要限制时用 `tool_choice:"none"` 或 `allowed_tools` [G-Cache]。
- 显式断点：可以在 text、image、audio、file 部件上加 `prompt_cache_breakpoint: {mode:"explicit"}`（GPT-5.6 及以后），再配合 `prompt_cache_options` 使用 [SDK；Ref-Chat]。**注意：Kimi 遇到这个字段会返回 400** [Kimi chat]。
- `prompt_cache_key`：GPT-5.6 之前用于路由（每个 key 约 15 rpm 时效果最好）；GPT-5.6 及以后主要用于分账和隔离 [G-Cache]。

**TKCode 的做法**

- `prompt_cache_key: "tkcode:<会话 id>"` 默认发给**所有** Chat 端点：中转 / 网关（OneAPI、NewAPI、LiteLLM 等）会把同一会话分散到多个上游，缓存随之碎片化，转发这个键的网关据此保持亲和；OpenRouter 在没有 `session_id` 时也用它做粘性路由。不认识的厂商通常忽略未知字段。例外是 Gemini 的 OpenAI 兼容端点（`*.googleapis.com`），它对未知字段直接 400（`Unknown name ...: Cannot find field`）。个别端点报错时用 `compat.supportsPromptCacheKey:false` 关闭。
  - Gemini 的缓存与这些字段无关：Gemini 2.5 及以后默认开启**隐式缓存**，无需任何请求字段，前缀稳定即可命中（最少 2,048 / 4,096 tokens，视模型而定）；**显式缓存**是另一套资源——先用原生接口 `POST /v1beta/cachedContents` 创建缓存（`ttl` 默认 1 小时，按小时收存储费），再在请求里引用（OpenAI 兼容层为 `extra_body.google.cached_content`），目前是 Beta，Gemini 4 Argon 及以后不支持。TKCode 只依赖隐式缓存，不创建显式缓存；`promptCacheTtl` 对 Gemini 不起作用 [Gemini caching；Gemini OpenAI compat]。
- `stream_options.include_obfuscation:false` 是 OpenAI 专有选项，只发给官方端点，与缓存键无关。
- 只认请求头的会话亲和：xAI 的 Chat 用 `x-grok-conv-id`（Responses 才用 `prompt_cache_key`）；OpenRouter 用 `x-session-id`；OpenCode Go（`opencode.ai`）用 `x-opencode-session`（Responses、Messages 端点同样要带，见各自协议文档）。值与缓存键相同。
- Claude 模型（OpenRouter、转发到 Anthropic 的中转）与百炼 Qwen 的显式缓存需要 content 上的 `cache_control`，否则不缓存。布局同 Messages 协议（anthropic-messages.md §8.3）：developer / system 消息一个断点，最后一条 user / tool 消息一个（分叉请求取最后一条之前的那条）；字符串 content 改写成单个 text part 才能挂断点，助手消息保持字符串不放断点。Claude 用全局 `promptCacheTtl`；百炼显式缓存只有 5 分钟，不发 `ttl`。默认判断：模型 id 含 `claude`，或模型 id 含 `qwen` 且主机是百炼（`dashscope` / `aliyuncs`）；`compat.supportsCacheControl` 可以覆盖。注意 Kimi 遇到 content 里的 `prompt_cache_breakpoint` 会 400，`cache_control` 只发给上面这两类模型。

---

## 10. 兼容厂商差异

> 通用结论：兼容厂商都使用 `data: ...` + `data: [DONE]` 的分帧方式，`delta.content` 和 `delta.tool_calls[].index` 的语义也与 OpenAI 一致。差异集中在 **推理字段**、**思考开关**、**推理内容回传**、**usage 写法**和 **finish_reason 扩展值**上。

### 10.1 DeepSeek（`api.deepseek.com`）[DeepSeek]

| 项 | 规则 |
|---|---|
| 模型 | `deepseek-flash`、`deepseek-v4-pro`（截至 2026-10） |
| 思考开关 | `"thinking": {"type": "enabled"\|"disabled"}`，**默认 enabled** |
| 思考强度 | OpenAI 格式下 `reasoning_effort` 只有 `low`/`high`/`max`，默认 `high`；服务端映射：`minimal`→low，`medium`/`xhigh`→high，`ultra`→max。关闭思考用 `thinking.type:"disabled"`（`none` 只出现在 Anthropic 格式中）[DeepSeek thinking_mode，2026-10-16 复核] |
| 推理输出 | `delta.reasoning_content`（与 `content` 同级） |
| **推理回传** | **不带 tools 时**：不需要回传，回传了也会被忽略。**带 tools 时**：后续所有请求都必须完整回传每条 assistant 消息的 `reasoning_content`，**包括没有调用工具的那几轮**，否则返回 **400**。TKCode 实现：同一 Provider 内跨模型（如 flash ↔ v4-pro）也原样回传明文 `reasoning_content`（`ReplayReasoningAcrossProviderModels`）；来自其它厂商的 assistant 轮次补空串 `""` |
| max_tokens | 字段名为 `max_tokens`，取值 1 到 393216；默认值：非思考模式 8K、思考模式 64K、`max` 强度 128K |
| tool_choice | 思考模式下，`required` 和指定函数**都会返回 400** |
| 无效参数 | 思考模式下 `temperature`、`presence_penalty`、`frequency_penalty` 不生效（不报错）；`top_p` 下限被抬到 0.95 |
| stream_options | 支持 `include_usage`；不开流式却传它会返回 400。文档称「no separate usage-only chunk is emitted」：usage 附在最后一个内容 chunk 上（choices 非空） |
| usage | `prompt_cache_hit_tokens` + `prompt_cache_miss_tokens` = `prompt_tokens`；同时提供 `prompt_tokens_details.cached_tokens` 和 `completion_tokens_details.reasoning_tokens` |
| finish_reason 扩展 | `insufficient_system_resource`、`aborted` |
| 工具调用增量 | 首个片段带 `id`、`type`、`function`，后续片段只带 arguments |

### 10.2 Moonshot Kimi（`api.moonshot.cn`/`platform.kimi.ai`）[Kimi]

| 项 | 规则 |
|---|---|
| kimi-k3 | 始终思考，并始终保留历史思考。**不要传 `thinking`**。`reasoning_effort` 可取 `low`/`high`/`max`，默认 `max` |
| kimi-k2.7-code(-highspeed) | 始终思考；`thinking.type` 只接受 `enabled`，传 `disabled` 会报错；**不支持 `reasoning_effort`**；`thinking.keep` 省略或为 `"all"`，其它值会报错 |
| kimi-k2.6 | `thinking.type` 为 `enabled`（默认）或 `disabled`；`thinking.keep` 为 `null`（默认，忽略历史推理）或 `"all"`；不支持 `reasoning_effort` |
| 推理输出 | `reasoning_content`；流式时总是先于 `content` 出现 |
| **推理回传** | K3 和 K2.7-code 必须原样回传每条历史 assistant 的 `reasoning_content`；K2.6 在同一个工具任务内也应全部回传。文档写的是「must」，但没有说明缺失时的具体报错（百炼文档称 Kimi 思考模式缺少该字段会报错 [DashScope function-calling]） |
| max tokens | 用 **`max_completion_tokens`**，`max_tokens` 已标记为弃用；输入加 max 超出上下文窗口时返回 `invalid_request_error`。工具场景建议设为 16000 以上 |
| temperature | k2.6 和 k2.7-code 不可修改，不要传 |
| tool_choice | 支持 `auto`/`none`/`required`/指定函数（百炼文档称部分 Kimi 模型在思考模式下只支持 `auto` 和 `none`） |
| tool 消息 | `name` 可选 |
| usage | 位于顶层（开启 include_usage 时，最后一个 chunk 的 choices 为空）；`usage.cached_tokens`、`prompt_tokens_details.cached_tokens` 与 `cache_write_tokens` 互斥，三者与未缓存部分之和等于 `prompt_tokens` |
| finish_reason | `stop`/`length`/`tool_calls` |
| 不兼容项 | content 中带 `prompt_cache_breakpoint` 会返回 400；支持 `partial: true` 的前缀续写 |

### 10.3 智谱 GLM（`open.bigmodel.cn`/`api.z.ai`）[GLM]

| 项 | 规则 |
|---|---|
| 思考开关 | `"thinking": {"type": "enabled"\|"disabled", "clear_thinking"?: bool}`，默认 enabled。**GLM-5.3 和 GLM-5.3-FLASH 是强制思考，传 `disabled` 会报错**；GLM-4.7 和 GLM-4.5V 也是强制思考 |
| 思考强度 | `reasoning_effort` 仅 GLM-5.2 及以上支持，默认 `max`。GLM-5.3 系列只接受 `max`/`high`/`low`；GLM-5.2 接受全部 7 个值（`none` 和 `minimal` 表示不思考） |
| 推理输出 | `delta.reasoning_content` |
| **推理回传** | 交错思考（工具调用之间继续思考）默认开启，「当您在使用"交错思考 + 工具"时，必须显式保留 Reasoning content，并在返回工具结果时一并返回」。保留式思考需要设置 `clear_thinking: false`（Coding Plan 端点默认开启），并且必须**完整、未修改、按原顺序**回传历史 `reasoning_content`。默认 `clear_thinking: true` 时，服务端会忽略历史轮次的推理内容 |
| tool_stream | `tool_stream: true` 时工具参数**分片流式下发**（必须同时设置 `stream: true`），默认 `false`，即整段一次性下发。仅 GLM-5.3/5.2/5.1/5/5-Turbo/4.7/4.6 支持 |
| tool_choice | **只支持 `auto`** |
| tools | 最多 128 个函数 |
| stop | 最多 4 个 |
| finish_reason 扩展 | `sensitive`（被安全审核拦截）、`network_error`（推理异常）、`model_context_window_exceeded` |
| usage | `prompt_tokens`、`completion_tokens`、`total_tokens`、`prompt_tokens_details.cached_tokens` |

### 10.4 阿里云百炼 DashScope（`*.maas.aliyuncs.com/compatible-mode/v1`）[DashScope]

| 项 | 规则 |
|---|---|
| 思考开关 | 顶层 `enable_thinking: bool`（不是 OpenAI 标准字段）。默认值随模型而异：Qwen3.5 到 3.8 多数默认开启；Qwen3 Plus 和 Flash 默认关闭；纯思考模型无法关闭。MiniMax-M3 改用 `thinking: adaptive\|disabled` |
| 思考预算 | `thinking_budget`（单位 token） |
| 推理输出 | `delta.reasoning_content`，先于 `content` 出现 |
| 推理回传 | 设置 `preserve_thinking: true` 后才会读取历史 `reasoning_content`；历史中没有该字段时不报错。回传内容按输入计费 |
| 仅流式 | 部分开源模型（如 qwen3.5-122b-a10b）只能流式调用，非流式会报错 `parameter.enable_thinking only support stream call` |
| tool_choice | Qwen 系列**不支持 `required`**（思考模式下明确不支持）；部分 Kimi 模型在思考模式下只支持 `auto` 和 `none` |
| parallel_tool_calls | 不设置时通常只返回一个工具调用；设为 `true` 才会返回多个 |
| 工具调用增量 | 首个片段带 `id` 和 name；后续片段的 `id` 可能是 `''`，也可能重复同一个 id。**必须按 `index` 拼接** |
| stream_options | 设置 `include_usage` 后，最后一个 chunk 的 `choices=[]` 并带有 usage；`reasoning_tokens` 位于 `completion_tokens_details` |
| messages 限制 | 兼容文档写明「system 只能是 messages[0]」（该页面明显过时，例如它还说 tools 不能与 stream 同时使用；**未证实**是否仍然生效。保守做法是把中途的 system 改写成 user） |
| 错误 | `{error:{message,type,param,code}}`；429 同时用于限流和欠费 |

### 10.5 OpenRouter（`openrouter.ai/api/v1`）[OpenRouter]

| 项 | 规则 |
|---|---|
| 思考参数 | `reasoning: {effort?: "max"\|"xhigh"\|"high"\|"medium"\|"low"\|"minimal"\|"none", max_tokens?: int, exclude?: bool, enabled?: bool}`。`effort` 与 `max_tokens` 只能二选一；`none` 关闭思考。旧字段 `include_reasoning` 已不推荐使用 |
| 推理输出 | 明文放在 `delta.reasoning`（非流式为 `message.reasoning`）；结构化推理放在 `delta.reasoning_details[]`，元素类型为 `reasoning.text`（`text`、`signature?`）、`reasoning.summary`（`summary`）或 `reasoning.encrypted`（`data`），并带有 `id`、`format`、`index?`。流式时加密内容可能显示为 `[REDACTED]` |
| **推理回传** | 有工具调用时，要把 assistant 消息的 **`reasoning_details` 原样回传**（与 `tool_calls` 放在同一条 assistant 消息里），不得重排、删减或修改。也可以只回传明文 `reasoning`（入参中 `reasoning_content` 是它的别名），但加密或摘要类推理建议使用 `reasoning_details`。Claude 和 Gemini 后端依赖这些数据 |
| Anthropic 后端 | 预算至少 1024；`max_tokens` 必须严格大于推理预算 |
| 保活 | 会发 `: OPENROUTER PROCESSING` 注释行 |
| 流中错误 | 顶层 `error` 加上 `finish_reason:"error"`，HTTP 状态保持 200（见 §5.6） |
| usage | **现在总是返回**（`stream_options.include_usage` 已弃用且不起作用）。最后的 usage chunk **choices 不为空**，会重复 `finish_reason`，不能把它当作第二个终止事件。字段包括 `prompt_tokens_details.cached_tokens`、`cache_write_tokens`，`completion_tokens_details.reasoning_tokens`，以及 `cost`。cached 是否包含在 prompt_tokens 内，文档没有说明（**未证实**） |
| 取消 | 断开连接即可取消；对 OpenAI、Anthropic、DeepSeek 等后端会停止计费，对 Groq、Google、Mistral 不会 |
| 截断 | 推理用光预算时返回 `finish_reason:"length"`，content 为空 |

### 10.6 Ollama（`/v1/chat/completions`）[Ollama]

| 项 | 规则 |
|---|---|
| 支持的字段 | `model`、`messages`、`frequency_penalty`、`presence_penalty`、`response_format`、`seed`、`stop`、`stream`、`stream_options`（仅 `include_usage`）、`temperature`、`top_p`、`max_tokens`、`tools`、`reasoning_effort`、`reasoning` |
| **不支持** | `tool_choice`、`logit_bias`、`user`、`n`、logprobs |
| 思考 | `reasoning_effort` 或 `reasoning.effort`。只支持开关的模型：`none` 表示关，其它值表示开；没有元数据的模型：`minimal` 映射为 low，`xhigh`/`ultra` 映射为 max；GPT-OSS：`xhigh` 映射为 high |
| 推理输出 | **`delta.reasoning`**（非流式为 `message.reasoning`）。入参 assistant 的 `reasoning` 字段会映射为 Thinking（源码 `FromChatRequest`） |
| 图片 | 只支持 base64 data URL，**不支持远程 URL** |
| 工具调用 | **整段一次性下发**（arguments 完整），带 `id` 和 `index`；结束时单独发一个空 delta 加 `finish_reason` 的 chunk；如果发过工具调用，`stop` 会改写为 `tool_calls` |
| usage | `include_usage` 时，在 finish chunk 之后再发一个 `choices:[]` 的 usage chunk（还带有非标准的 `timings`）；`prompt_tokens_details.cached_tokens` 可能存在 |
| 错误 | `{error:{message,type,param,code}}`；流开始后再出错时以裸 JSON 写出（见 §5.6） |

### 10.7 LM Studio（`localhost:1234/v1`）[LMStudio]

| 项 | 规则 |
|---|---|
| 文档列出的参数 | `model`、`top_p`、`top_k`、`messages`、`temperature`、`max_tokens`、`stream`、`stop`、`presence_penalty`、`frequency_penalty`、`logit_bias`、`repeat_penalty`、`seed`；`tools` 见工具文档 |
| 思考 | 0.4.8（2026-03）起支持 `reasoning_effort`，usage 中返回 `reasoning_tokens` [LMStudio changelog，经搜索摘要确认] |
| 推理输出 | 0.3.23 起，gpt-oss 的推理放在 `choices.delta.reasoning`（非流式为 `message.reasoning`）。其它模型可能输出 `reasoning_content`，也可能把 `<think>` 混在 content 里（**未证实**，随版本和设置变化） |
| 工具调用 | 参数分片流式下发：首个片段带 `id`、`name` 和 `arguments:""`，后续片段的 id 和 name 为 null。**模型不支持原生工具调用、或者工具调用解析失败时，工具调用会落到 `content` 中** |
| tool_choice | 文档没有提到 |
| stream_options / usage | 文档没有说明（**未证实**） |

### 10.8 推理字段速查

| 厂商 | 流式推理字段 | 回传字段 | 必须回传吗 |
|---|---|---|---|
| OpenAI | 无 | 无 | — |
| DeepSeek | `reasoning_content` | `reasoning_content` | 带 tools 时**必须**（全部轮次） |
| Kimi | `reasoning_content` | `reasoning_content` | K3、K2.7 必须；K2.6 要设 `thinking.keep:"all"` |
| GLM | `reasoning_content` | `reasoning_content` | 交错思考加工具时必须；保留式思考要设 `clear_thinking:false` |
| DashScope | `reasoning_content` | `reasoning_content` | 只在 `preserve_thinking:true` 时生效 |
| OpenRouter | `reasoning` + `reasoning_details` | `reasoning_details`（推荐）或 `reasoning` | 有工具时必须（尤其是 Claude 和 Gemini 后端） |
| Ollama | `reasoning` | `reasoning` | 否 |
| LM Studio | `reasoning`（gpt-oss）或其它 | 未知 | 未知 |

---

## 11. 实现必须满足（检查清单）

**请求构建**
1. 必发 `stream:true`。只在流式时发 `stream_options`；官方端点发 `{"include_usage":true}`（OpenRouter 不需要）。
2. OpenAI 官方使用 `max_completion_tokens`，不要用 `max_tokens`（它与 o 系列推理模型不兼容）。兼容厂商按各自文档选择字段名：Kimi 用 `max_completion_tokens`，DeepSeek、GLM、Ollama、LM Studio 用 `max_tokens`。数值不能超过模型的输出上限，也不能超过「上下文窗口减去输入」。
3. `reasoning_effort` 只发模型支持的值。用户选择「关闭」时，若模型支持就发 `"none"`；GPT-6 Astra 不能发 `none`。不能用「省略字段」来表达关闭。
4. 推理模型的系统提示在官方端点上用 `developer` 角色，兼容厂商用 `system`。
5. function tool 必须是 `{type:"function", function:{name, description?, parameters?, strict?}}`。`name` 必须匹配 `^[a-zA-Z0-9_-]{1,64}$`，不合规时客户端先改名，并在回放时映射回原名。
6. 不要默认发 `strict:true`。如果发了，schema 必须满足 §4.2 的全部规则。
7. GPT-6 Astra 和 GPT-6.1 Sol 在 Chat 上不能带 tools，要改走 Responses。
8. 官方端点发 `store:false` 与 `include_obfuscation:false`。所有端点（Gemini 除外）发会话级 `prompt_cache_key`，见 §9。
9. 保持 tools 列表和 developer 前缀在整个会话内稳定。需要限制工具时用 `tool_choice` 或 `allowed_tools`，不要增删工具。

**消息回放**
10. 带 `tool_calls` 的 assistant 消息必须紧跟与之一一对应的 `tool` 消息。缺失的结果要补一条错误结果；没有对应调用的孤立 tool 消息必须丢弃或改写成文本；tool 组中间不能插入其它消息。
11. assistant 消息中，`tool_calls[].function.arguments` 要原样回放模型生成的字符串；有 tool_calls 时 `content` 可以为 `null`。
12. tool 消息的 content 只能是文本，图片放进紧随整组 tool 消息之后的 user 消息里。
13. `tool_call_id` 必须与对应 assistant 的 `tool_calls[].id` 完全一致；跨模型交接时做 id 规范化，并同步改写两端。
14. 按厂商回传推理内容（§10.8）：DeepSeek 和 Kimi 用 `reasoning_content`，OpenRouter 用 `reasoning_details`，并保证原样、顺序不变。不得把一个厂商的推理字段发给另一个厂商。
15. 出错或中止的 assistant 消息不回放；同时**要一起移除它对应的 tool 结果**，否则会留下孤立的 tool 消息。

**流解析**
16. SSE：忽略注释行（`:` 开头），多行 data 用 `\n` 拼接；遇到 `[DONE]`（以它开头即可）时停止。
17. 每个 data 先检查顶层 `error`，有就立即失败，即使 HTTP 状态是 200（OpenAI 和 OpenRouter 都如此）。
18. `choices` 可能为空（usage chunk），也可能缺失；不要直接取 `choices[0]`。按 `choice.index` 选候选（agent 只取 index 0）。
19. 分别累加 `delta.content`、`delta.refusal` 和推理字段（`reasoning_content`/`reasoning`/`reasoning_details`）。**refusal 不能丢弃**，要以文本或专门的错误形式展示给用户。
20. 工具调用按 `index` 累加，`id`、`name`、`arguments` 都按字符串拼接（与 SDK 一致）。name 和 id 可能晚于首个片段到达；后续片段的 `id` 可能为 `''`，也可能重复。如果同一个 index 上出现了**不同的非空 id**，应视为新的调用。整段下发（Ollama、GLM 非 tool_stream）也要能处理。
21. `finish_reason` 为 `length` 时停止原因必须是 Length，**不能执行可能被截断的工具调用**。`content_filter`、`sensitive` 要明确上报。扩展值要按 §5.5 分类处理，不要一律当作致命错误。
22. 读取所有出现在 chunk 上的 `usage`（顶层；兼容一些厂商放在 `choices[i].usage` 的情况），以**最后一个非 null 的值**为准。OpenRouter 的 usage chunk 会重复 finish_reason，按幂等方式处理。
23. 不含缓存的输入 = `prompt_tokens - cached_tokens - cache_write_tokens`，其中 cached 可以回退取 `prompt_cache_hit_tokens`（DeepSeek）或 `usage.cached_tokens`（Kimi）。`completion_tokens` 已经包含 reasoning，不要再加一遍。
24. 流在没有 `finish_reason` 和 `[DONE]` 的情况下结束时，视为截断。只有在尚未产出任何内容时才允许自动重试。
25. 忽略 `obfuscation`、`system_fingerprint` 以及未知字段。

**HTTP 与重试**
26. 只在拿到响应头、开始消费流之前重试：408、409、429（限流类）、≥500，以及连接错误和超时。遵守 `x-should-retry`。等待时长依次取 `retry-after-ms`、`retry-after`（秒或 HTTP 日期），并把负值截断为 0。服务端要求等待超过 120s 时放弃重试。
27. 429 要先看 `error.code`/`error.type`：`insufficient_quota`、`credit_balance_exhausted`、`*_spend_limit_exceeded`、`organization_usage_limit_exceeded` 都不重试。
28. 状态码为 2xx、但 `Content-Type` 不是 `text/event-stream` 时（例如代理返回了 JSON 错误），按 JSON 错误体解析，不要报成「流被截断」。
