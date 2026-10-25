# 输入框增强：@ 文件、/ 命令、Skills 与 Role

状态：设计已确认（2026-10-17）；C1–C11 全部实现（`src/TKCode.Desktop/Controls/Composer/`、`ViewModels/Completion/`、`TKCode.Agent.Coding/Workspace/WorkspaceFileIndex.cs`、`TKCode.Agent.Coding/Assets/`），实现与设计的差异见 §17
关联文档：[mcp.md](mcp.md)（§8 现有的 @ 资源 / / 提示词选择器）、[builtin-agent-tools.md](builtin-agent-tools.md)、[external-agent-runtimes.md](external-agent-runtimes.md)、[motion.md](motion.md)、[settings.md](../settings.md)、[composer-drafts.md](composer-drafts.md)（按会话保存草稿、↑↓ 历史输入）

## 1. 目标

- 输入 `@` 搜索并选择工作区里的文件或文件夹，选中后在输入框里显示为**内联 tag**。tag 和普通文字一样参与光标移动、选择和删除。
- 输入 `/` 选择**命令**（commands）、**Skills**、**Role**。原有的 MCP 提示词也并入同一个列表。
- commands / skills / roles 同时支持**全局**和**项目级**定义，项目级覆盖全局同名项。
- Role 的用法与 Cursor 的模式（Agent / Ask / Plan / 自定义）一致：属于会话状态，作用于之后的每一轮。
- 整个客户端保持 NativeAOT：发布时 0 个 IL/AOT 警告，并实际运行发布出来的二进制文件。

## 2. 已确认的决策（用户，2026-10-17）

| 维度 | 决策 |
|---|---|
| 内联 tag 的实现 | **自绘编辑器**：不用 TextBox 加覆盖层，也不用 U+FFFC 加内联 run。文档模型、排版、输入法、选择、撤销都由我们自己实现 |
| 代码位置 | 放在当前项目 `TKCode.Desktop`（`Controls/Composer/`），不另建工程 |
| 目录约定 | 全局目录 `~/.tkcode/{commands,skills,roles}`（受 `TKCODE_HOME` 控制）；项目目录 `<项目根>/.agents/{commands,skills,roles}`。**不**读取 `.claude/`、`.codex/` 这些兼容目录 |
| Role 语义 | 与 Cursor 的模式相同：内置 **Agent / Plan / ACP**（用户 2026-10-17 改定，去掉 Ask；ACP 参考 NextCoWork），另可自定义。按会话保存，在模式下拉框里切换，也可以用 Shift+Tab 循环切换 |
| @ 文件发送 | **只发路径**，模型需要时用 `read` 等工具自己读取 |
| Skills 触发 | 两种方式都支持：用户用 `/skill名` 显式指定；模型也能从系统提示的清单里自动发现，再用 `skill` 工具加载（渐进加载） |
| 外部运行时 | 把运行时自带的命令合并进 `/` 列表，原样透传；TKCode 定义的 command 和 skill 由 Host 展开后发送；Role 对外部运行时只注入指令（运行时有原生模式时映射过去，见 §11） |

## 3. 架构

```
TKCode.Desktop                                         TKCode.AgentHost
  ComposerView                                          AgentAssets（扫描 ~/.tkcode 与 .agents，监听变化）
   ├─ ComposerEditor（自绘控件）                         │   ├─ commands / skills / roles 目录
   │    ├─ ComposerDocument：文本片段 + tag 片段          │   └─ 解析 frontmatter（TKCode.Config，AOT 安全）
   │    ├─ 排版：TextLayout + ITextSource + TagRun        ├─ WorkspaceFileIndex（@ 文件搜索，按 cwd 缓存）
   │    └─ 输入法、选择、撤销、剪贴板、无障碍             ├─ 发送时展开：command 模板、skill 内容、role 指令
   └─ CompletionViewModel（统一的选择器）                └─ 内置会话：系统提示里的 skills 清单、skill 工具、role 对应的工具集
        ├─ FileProvider        @  → workspace/searchFiles
        ├─ McpResourceProvider @  → 现有 MCP 目录
        ├─ SlashProvider       /  → assets/list + 内置命令 + 运行时命令 + MCP 提示词
        └─ 选中 → 插入 tag / 切换 role / 执行内置命令
```

- **文件搜索和资产扫描都放在 Host**：UI 进程不扫描磁盘。大仓库只在 Host 里建一次索引，所有会话共用。
- **frontmatter 解析放在 `TKCode.Config`**：Host 和 Desktop 都可能用到，比如设置页预览。用一个只支持平面 key–value 和字符串列表的 YAML 子集解析器，不引入 YamlDotNet（它依赖反射，不适合 AOT）。

## 4. 目录与文件格式

```
~/.tkcode/                     <项目根>/.agents/
  commands/                     commands/
    review.md        → /review    review.md      ← 覆盖全局同名项
    git/commit.md    → /git:commit
  skills/                       skills/
    pdf/SKILL.md     → /pdf       pdf/SKILL.md
    pdf/scripts/...  （资源文件，模型按需读取）
  roles/                        roles/
    reviewer.md      → Role「reviewer」
```

- **项目根**：沿用 `SystemPromptBuilder` 的规则，从 cwd 向上找包含 `.git` 的目录，找不到就用 cwd。只读项目根下的 `.agents`，不逐级合并子目录。
- **命名**：文件名去掉 `.md` 就是名称；子目录用 `:` 连接，比如 `git:commit`。名称只能由 `[a-z0-9-_:.]` 组成，大小写不敏感。不合法的文件会被跳过，并在设置页列出原因。
- **优先级**：项目 > 全局 > 内置。同名时高优先级的项覆盖低优先级的项；选择器里只显示生效的那一项，标注来源，并提示它覆盖了全局的同名项。
- **命名空间冲突**：command 和 skill 共用 `/` 下的名字空间。同一层级里出现同名时 skill 优先（因为它还能被模型自动调用），被覆盖的 command 在设置页里给出警告。Role 不和它们冲突，见 §10.3。

### 4.1 Command：`commands/**/*.md`

一个 command 就是一个普通的 Markdown 文件（与 Cursor 的 `.cursor/commands/*.md` 相同）：文件名是命令名，正文就是要发给模型的提示词。

```markdown
Review the uncommitted changes. Point out bugs and risky edits with file:line references.
```

- **不需要 frontmatter**。选择器里的说明取正文第一行（去掉开头的 `#`）。如果写了 frontmatter，只认 `description` 一项，用来覆盖这条说明。
- **没有变量和参数语法**：用户在 command tag 后面输入的文字，原样附在正文之后，作为单独一段。
- 正文只是文本，不执行 `` !`cmd` ``，也不内联 `@file` 的内容；正文里的 `@path` 按普通文本发送。

### 4.2 Skill：`skills/<name>/SKILL.md`

遵循 Agent Skills 开放规范：

```markdown
---
name: pdf
description: Extract text and tables from PDF files. Use when the user mentions PDFs or forms.
---
# PDF
...（正文，可以引用同目录下的 scripts/、reference.md 等文件）
```

- `name` 必须与目录名一致，不一致时以目录名为准并给出警告。`description` 必填，它决定模型什么时候选用这个 skill，缺失的 skill 不会出现在模型的清单里，但仍可以手动用 `/` 调用。
- 其他 frontmatter 字段（`license`、`allowed-tools`、`metadata`…）首版只做透传和展示，不参与执行。

### 4.3 Role：`roles/*.md`

```markdown
---
name: Reviewer
description: Read-only code review mode
tools: readonly            # all | readonly | 工具名列表，例如 [read, grep, glob, web_fetch]
model: anthropic/claude-opus-5-5   # 可选：切换到这个 role 时同时切换模型
icon: eye                  # 可选：Lucide 图标名
---
You are reviewing code. Do not modify files; report findings with file:line references.
```

- 正文作为 role 的指令，追加到系统提示的 `role` 节。
- `tools` 省略时为 `all`。工具名列表里写到不存在的工具时忽略并警告；MCP 工具用 `mcp__server__tool` 的形式，也可以写 `mcp__server__*`。

## 5. 资产发现（Host：`AgentAssets`）

- 位置：`TKCode.Agent.Coding/Assets/`。内容包括 `AgentAssetCatalog`（按 cwd 缓存的不可变快照）、`CommandAsset`、`SkillAsset`、`RoleAsset`。
- **加载**：第一次用到某个 cwd 时扫描；之后用 `FileSystemWatcher` 监听全局目录和项目 `.agents`，复用 `SettingsWatcher` 的防抖。快照变化时向 UI 推送 `assets/changed {cwd}`。
- **限制**：只读 frontmatter 和描述，正文在用到时才读取，所以 skill 正文不会进入快照。单个文件超过 256 KB 时跳过；每个目录最多 500 项。
- **错误**：解析失败的文件放进快照的 `Problems` 列表，在设置页和选择器底部显示，不影响其他文件。

## 6. 自绘编辑器 `ComposerEditor`

替换 `ComposerView` 里的 `atom:TextArea#Input`。外观和尺寸行为（`MinLines` / `MaxLines` 自动增高、单行胶囊与两段布局切换、`SmoothSize`）保持不变。

### 6.1 文档模型（`ComposerDocument`，不依赖 UI，可以单元测试）

```csharp
public abstract record Segment;
public sealed record TextSegment(string Text) : Segment;
public sealed record TagSegment(TagKind Kind, string Value, string Label) : Segment;   // Kind: File / Folder / Command / Skill / McpResource / McpPrompt / Image
```

- **位置**：用一个整数偏移表示。文本里每个 UTF-16 码元占 1 个位置，**每个 tag 也只占 1 个位置**。光标和选区都用这种偏移，所以光标天然不可能落在 tag 内部。
- **编辑原语**：`Insert(pos, text)`、`InsertTag(pos, tag)`、`Delete(range)`、`Replace(range, …)`。相邻的文本片段自动合并。
- **删除**：退格或 Delete 碰到 tag 时整体删除。按单词删除（Alt/Ctrl+Backspace）时，tag 也算一个单词。
- **撤销 / 重做**：记录编辑原语的逆操作。连续输入按单词合并成一步，插入 tag 单独成一步，粘贴单独成一步。
- **纯文本投影**：`ToPlainText()` 把 tag 转成它的文本形式（文件为 `@path`，路径含空白时为 `@"path"`；命令为 `/name`）。供复制、计算宽度、草稿保存使用。
- **图片占位符**：现有的 `[图片 #N]` 改成 `Image` 类型的 tag，删除行为一致。`RemoveAttachment` 改为删除对应的 tag，不再做字符串替换。

### 6.2 排版与绘制

- 由 `ComposerTextSource : ITextSource` 生成 `TextLayout`：文本片段对应 `TextCharacters`；tag 对应 `TagRun : DrawableTextRun`。`TagRun` 的宽度等于图标宽度加文字宽度再加内边距，基线与正文对齐。
- **tag 外观**：圆角背景 `TKCode.AccentSoftBg`，文字 `TKCode.AccentText`，左侧是按类型显示的 Lucide 图标（文件、文件夹、命令、skill、MCP）。tag 宽度超过最大值时，文字中间截断，例如 `src/…/Foo.cs`。悬停 tag 时显示完整路径或描述。
- **换行**：tag 不拆开；单个 tag 比一整行还宽时独占一行并截断。
- **绘制顺序**：选区背景、文本与 tag（选中的 tag 加描边）、预编辑文字下划线、光标。光标闪烁复用 TextBox 的节奏，不聚焦时不重绘。
- **增量**：只在文档或宽度变化时重建 `TextLayout`；光标移动、闪烁不触发重排，延续 [streaming-cpu-findings] 里的结论。
- **占位文本**：文档为空且没有预编辑文字时绘制 `ComposerPlaceholder`。

### 6.3 输入

- **文本输入**：处理 `TextInput` 事件。用 `TextInputMethodClient` 支持输入法：
  - 预编辑文字作为临时片段绘制在光标处，加下划线；
  - `CursorRectangle` 给出光标位置，让候选框跟随；
  - `SurroundingText` 提供光标附近的文本，tag 以 U+FFFC 表示；
  - 提交时作为一次输入处理。
  中文、日文输入法和 macOS 的按住字母选重音字符都要测试到。
- **按键**：
  - 方向键，Home / End；Alt/Ctrl+←→ 按单词移动；Cmd+←→ 到行首、行尾；
  - 加 Shift 时扩展选区；↑↓ 按视觉行移动并保持列位置；
  - Cmd/Ctrl+A、C、X、V、Z，Shift+Cmd+Z；
  - Enter 发送，Shift+Enter 换行（沿用现有规则）。
  选择器打开时，↑↓、Enter、Tab、Esc 交给选择器处理。
- **鼠标**：单击放置光标，拖动选择，双击选中单词（双击 tag 选中这个 tag），三击选中一段。点击 tag 不打开文件，只放置光标（悬停时显示提示）。
- **剪贴板**：
  - 复制时写纯文本投影，另外写一份内部格式（片段的 JSON）。在 TKCode 内部复制粘贴时保留 tag。
  - 粘贴外部纯文本时一律当作文本，不自动转换 `@path`。
  - 图片粘贴沿用 `PasteAsync`。
- **拖放**：图片沿用现有逻辑。其他文件插入附件 tag（显示文件名，值为绝对路径），发送时把绝对路径包进提示词，不把文件内容放进会话；目录仍是文件夹 tag（工作区内为相对路径）。
- **无障碍**：提供一个 `AutomationPeer`，对外暴露纯文本投影，tag 用它的 `Label` 表示。

### 6.4 与 ViewModel 的绑定

- `ConversationViewModel.ComposerText`（string）改为 `ComposerDocument Composer`。保留只读的 `ComposerPlainText`，供 `CanSend`、`UpdateMultiline` 和 SmokeMode 使用。
- 草稿和发送失败后的恢复（`ComposerText = draft`）改为保存和恢复文档快照。
- `UpdateMultiline` 改用编辑器自己测量的单行宽度（`ComposerEditor.SingleLineWidth`），不再用 `MeasureWidth(text)`。

## 7. 统一选择器（`CompletionViewModel`）

替换 `McpComposerViewModel` 里的选择部分。MCP 的“需要注意”提示和提示词参数表单留在 `McpComposerViewModel`。

### 7.1 触发规则

| 触发 | 条件 | 查询词 |
|---|---|---|
| `@` | 光标前是行首或空白，后面紧跟非空白字符 | `@` 到光标之间的文字（不含空白；用 `@"` 开头时可以含空格） |
| `/` | 位于**文档开头**，或光标前是行首或空白 | `/` 到光标之间的文字 |

- 查询期间的文字是**普通文本**，确认后整体替换成 tag。按 Esc 或光标离开查询范围时关闭选择器，文字保持原样。
- 选择器打开时 Tab 用于补全，与 Enter 一样确认当前项。选中的是文件夹时，Tab 改为补全到文件夹内部继续搜索（`@src/` → 列出 src 下的内容），Enter 插入文件夹 tag。

### 7.2 提供者

```csharp
interface ICompletionProvider
{
    char Trigger { get; }                         // '@' 或 '/'
    string Group { get; }                         // 用作分组标题
    ValueTask<IReadOnlyList<CompletionItem>> QueryAsync(string query, CompletionContext context, CancellationToken ct);
}
record CompletionItem(string Id, string Title, string? Subtitle, string Icon, string? Source /* 全局 / 项目 / 内置 / 运行时 / MCP 服务器名 */,
                      CompletionAction Action, double Score);
// CompletionAction：InsertTag(TagSegment) | SwitchRole(roleId) | RunBuiltin(commandId) | SelectMcpPrompt(McpPromptDto)
```

- `@`：`FileProvider`（文件和文件夹），`McpResourceProvider`。
- `/`：`BuiltinCommandProvider`、`CommandProvider`、`SkillProvider`、`RoleProvider`、`RuntimeCommandProvider`（外部运行时自带的命令）、`McpPromptProvider`。
- **查询**：每次输入后防抖 60 ms 再查询；新查询会取消旧的。各提供者并行查询，先返回的先显示，不等最慢的那个。

### 7.3 列表与排序

- 列表是**浮在输入框上方的弹出层**（`Popover`，与模型选择器同样的卡片外观），横向对齐到查询起点（`@` 或 `/`），不改变输入框的高度。每一行使用 `TKCode.RowButton`，最多显示 8 行，超出时滚动。
- 列表项不可获得焦点，键盘始终留在输入框里。点击弹出层以外的地方会关闭它，效果等同于按 Esc。
- 空状态时居中的输入框和停靠在底部的输入框共用同一个 ViewModel，只有获得焦点的那个会打开弹出层。
- 有查询词时按模糊匹配得分合并排序，不分组。没有查询词时按分组显示，顺序如下：
  - `@`：最近用过的文件，然后是当前打开的编辑器标签，然后是 MCP 资源；
  - `/`：Role，然后是命令，然后是 Skills，然后是运行时命令，然后是 MCP 提示词。
- 每一行显示：图标、标题（匹配的字符高亮）、副标题（描述或所在目录）、右侧来源标记（“项目”、“全局”，或服务器、运行时名称）。
- 没有结果时显示 `EmptyText`。文件索引还没建好时显示“正在索引…”，并先显示已有的部分结果。

## 8. @ 文件

### 8.1 索引（Host：`WorkspaceFileIndex`）

- **建立**：首次查询时为项目根建索引。在 git 仓库里用 `git ls-files -co --exclude-standard` 取得列表（包含未跟踪但没被忽略的文件），再推出目录集合；不是 git 仓库时遍历目录，跳过 `.gitignore` 里的项和常见的大目录（`node_modules`、`bin`、`obj`…）。
- **更新**：用 `FileSystemWatcher` 增量更新，防抖 200 ms；监听器溢出时重建整个索引。
- **规模**：最多索引 200 k 个条目。超过时只保留路径较浅的条目，并在列表底部提示“结果不完整”。
- 与 `GlobTool` / `GrepTool` 共用忽略规则的实现，避免两处逻辑各自漂移。

### 8.2 匹配

- **模糊匹配**：参考 fzf v2 的打分方式。匹配到文件名的字符加分；匹配位置在路径分隔符或驼峰边界上加分；连续匹配加分；路径越深分数越低。查询里含 `/` 时按整段路径匹配，否则优先匹配文件名。
- **最近使用加权**：本工作区里最近 @ 过、最近被打开或编辑过的文件。记录保存在 `state.json`，每个工作区最多 50 条。
- **性能**：返回前 50 项。在 200 k 条目上，单次查询的目标耗时低于 15 ms。

### 8.3 发送

- 文件 tag 序列化为 `@相对路径`（相对于 cwd；路径含空白时为 `@"相对路径"`）；文件夹在末尾加 `/`。**不读取文件内容**。多文件夹项目中其他文件夹里的文件序列化为绝对路径，补全列表里显示为「文件夹名/相对路径」（docs/design/projects.md §10.2）。
- 内置 Agent 的系统提示里加一句：用户消息中的 `@path` 是指向工作区文件的引用，需要时用 read、list 等工具读取。
- 外部运行时同样只发文本。Codex 的 `turn/start` 输入支持 `mention {name, path}` 条目，可以额外附上（只是提示信息，不包含文件内容）。

## 9. `/` 命令

### 9.1 内置命令（只在 UI 里执行，不发给模型）

| 命令 | 作用 |
|---|---|
| `/new` | 新建会话（等同于现有的快捷键） |
| `/model` | 打开模型选择器 |
| `/role` | 打开 Role 下拉框 |
| `/fork` | 从这里分叉会话（`session/fork`） |
| `/compact [指令]` | 压缩上下文（内置 Agent，docs/design/compaction.md）；后面的文字作为摘要重点。外部运行时用它们自己的 `/compact`（运行时命令） |

- 选中内置命令后立即执行，并清除输入框里的查询文字，不生成 tag。
- 内置命令和自定义 command 重名时内置命令优先，被遮住的自定义 command 在设置页里给出警告。
- 发给模型的内置命令 `/init`、`/commit` 见 §9.3。

### 9.2 自定义 command

- 选中后插入 command tag，tag 后面可以继续输入补充说明。
- **发送**：
  1. 转录里显示 command tag 加用户输入的文字。
  2. Host 读取 `.md` 正文，后面接上用户输入的文字，作为本轮实际的用户消息发给模型。展开后的全文存进 JSONL 的 `expanded` 字段，方便回放。
  3. 外部运行时同样发送展开后的文本，因为 TKCode 的 command 运行时并不认识。

### 9.3 内置提示词命令（发给模型）

`/init`、`/commit` 需要模型参与，因此是“提示词型”内置命令：
- 选中后和自定义 command 一样插入 command tag，用户可以在后面补充说明（如 `/commit 只提交 src/ 下的改动`）。
- 发送时 Host（`ExpandCommands`，`BuiltinPromptCommands.Render`）用内置正文替代 `.md` 文件，正文在用户文字之前发给模型。转录显示 tag 加用户文字，展开全文存进 JSONL 的 `Content`。
- 正文是 `src/TKCode.Agent.Coding/Assets/BuiltinPrompts/*.md` 嵌入资源，改动要经过本节的审阅。占位符 `{{model_name}}`、`{{model_id}}` 在展开时替换为当前会话的模型，每次发送都取当前模型。
- 与同名自定义 command 重名时内置优先；Desktop 的 `BuiltinCommandProvider.Names` 把自定义同名项隐藏。
- 外部运行时同样发送展开后的文本。

**`/init`**：让模型阅读项目后新建或更新根目录的 `AGENTS.md`。已有 `AGENTS.md` / `AGENTS.override.md` 时在原文上补充，不整体覆盖、不删除用户写的内容；只写代码里读不出来的信息（构建、测试、运行、目录职责、约定、坑），不超过约 100 行；完成后说明哪些是推断。

**`/commit`**：模型并行运行 `git status`、`git diff`（含已暂存）与 `git log -n 10`，参考近期提交的风格；没有改动就说明并停止；只暂存本次相关的文件，不暂存 `.env` 等疑似密钥的文件；不 amend、不 `--no-verify`、不 push；提交信息末尾加一行 `Co-Authored-By: {{model_name}} ({{model_id}}) <token@routin.ai>`，用 HEREDOC 传入。

完整原文见 `Assets/BuiltinPrompts/init.md` 与 `commit.md`。提交邮箱固定为 `token@routin.ai`。

## 10. Skills

### 10.1 自动发现（只对内置 Agent）

- 系统提示新增 `skills` 节，列出生效的 skill，每项一行：名称、描述、`SKILL.md` 的路径。最多 100 项，超过时截断并说明。该节放在 `SystemPromptTracker` 的可变部分，skill 列表变化时按“补丁节”的方式更新，与 MCP 工具的更新机制相同（mcp.md §5.4），不影响前缀缓存。
- 新增 `skill` 工具：参数为 `{name}`，返回 `SKILL.md` 的正文，以及 skill 目录的绝对路径，方便模型继续读取同目录下的资源。它是只读工具，不需要审批。目录里的脚本由模型通过 shell 工具执行，按现有规则审批。

### 10.2 显式调用

- 用户输入 `/skill名` 时插入 skill tag，可以放在消息的任何位置。
- **发送**：Host 把 skill 正文作为本轮的附加上下文（`<skill name="…" path="…">…</skill>`）放在用户文本之前；转录里只显示 tag。外部运行时也用同样的内联方式。如果 Codex 运行时支持 `skill {name, path}` 输入项，优先用它。

### 10.3 Role（类似 Cursor 的模式）

- **内置三个 role**：

  | Role | 工具 | 指令 |
  |---|---|---|
  | Agent | 全部 | 无（默认）；从其他模式切回时见 §18.4 |
  | Plan | 只读；write / edit 只放行计划文件，agent 只能派只读子 Agent，去掉 MCP 工具；另有 `exit_plan_mode`（[plan-mode.md](plan-mode.md) §8） | 调查、澄清后写计划文件并请求批准，见 [plan-mode.md](plan-mode.md) §12.1 |
  | ACP | 只有阅读、搜索、skill、任务清单与子 Agent 相关工具 | 主 Agent 设计方案、拆分任务，交给子 Agent 执行并整合结果，见 §18.3 |

- **自定义 role**：见 §4.3。`tools` 为工具名列表时，在 `BuiltinTools.Create` 之后按名字过滤（新增 `ToolSet.Custom` 加 `AllowedTools`）。
- **会话状态**：`SessionOptionsDto` 新增 `Role` 字段，随会话保存并恢复。切换 role 时：
  - 用 `SetOptionsAsync` 通知 Host；
  - 在下一轮之前替换系统提示的 `role` 节和工具定义，与 MCP 补丁节使用同一套机制；
  - role 指定了 `model` 时，同时切换模型（`SetModelAsync`）。
- **UI**：在权限选择器旁边放一个模式下拉框，显示图标加名称。在输入框里按 Shift+Tab 依次切换 Agent → Plan → ACP → 自定义 role。在 `/` 列表里选中某个 role 等同于切换，不生成 tag。
- **子 Agent**：首版不变，`explore` / `plan` / `general-purpose` 仍是写死的。之后可以让自定义 role 也能作为 `agent` 工具的 `agentType`。

## 11. 外部运行时

| 运行时 | 自带命令来源 | 模式映射 |
|---|---|---|
| Claude Agent | `initialize` 应答的 `commands`（含描述与参数提示），比 `system/init` 的 `slash_commands` 信息更全 | Plan → 原生 `plan` 权限模式（启动参数 `--permission-mode` 或在线 `set_permission_mode`），优先于权限档位；ACP 与自定义模式只发说明；Plan 不发说明，由 CLI 原生的 plan 提示词负责，`ExitPlanMode` 进入 TKCode 的计划审批面板（[plan-mode.md](plan-mode.md) §12.8、§15）。支持子 Agent，可以用 ACP |
| Codex | 规格中没有列出命令或 skills 的方法，不提供 | Plan → 只读沙箱（线程级 `sandbox: "read-only"`，轮次级 `sandboxPolicy: {type: "readOnly"}`，sticky）；自定义模式只发说明。不支持子 Agent，不提供 ACP |
| ACP Agent | `available_commands_update`（整体替换；可能在 `session/new` 响应之前到达，走现有的暂存回放） | 只发说明；不提供 ACP。用 Agent 自己的模式列表（`modes` / `category:"mode"`）替换 TKCode 模式列表，留到后续版本 |

- **说明怎么发**：模式变化后，下一条用户消息发给运行时之前，在最前面加一个 `<system-reminder>` 文本块（§18.8）。只发一次，不进入转录。这一轮出错或被中止时，留到下一轮再发。消息以 `/命令` 开头时也留到下一轮，因为运行时只认文字开头的斜杠命令（ACP 规格 §6.2）。新会话一开始就是 Agent 时不发。
- **ACP 依赖子 Agent**：`RuntimeCapabilities.SupportsSubagents` 经由 `ModelDto.SupportsSubagents` 告诉界面，当前模型的运行时不支持时，模式列表里不出现 ACP。Host 也会拒绝在这类会话上切到 ACP，保持原来的模式。
- **自带命令**：Host 以会话事件 `commandsChanged` 推送，界面保存在 `SessionViewModel.RuntimeCommands`。`/` 列表里归为运行时分组，右侧显示运行时名称。选中后插入 `RuntimeCommand` tag，发送时原样是 `/名称` 文字，Host 不展开。运行时进程启动（第一轮）之后才有列表。
- TKCode 自己的 command、skill 在外部运行时上照常由 Host 展开后发送，因为运行时并不认识它们。

## 12. 消息序列化与转录

- `PromptRequest` 新增 `Segments: IReadOnlyList<PromptSegmentDto>`：`{kind: text|file|folder|command|skill|mcpResource|mcpPrompt|image, value, label}`。`Text` 保留为纯文本投影，兼容旧逻辑。Host 只信任 `Segments`，不从文本里反向解析 `@` 或 `/`。
- 现有的 `McpResources` / `McpPrompt` / `Images` 字段改为从 `Segments` 推导；保留一个协议版本的兼容期，然后删除。
- **JSONL**：用户消息新增 `segments` 字段，内容与上面相同；展开后的 command / skill 文本单独记在 `expanded` 字段。
- **转录**：用户气泡里的 tag 与输入框里的外观一致，沿用 `ComposerEditor` 的 `TagRun` 绘制，但只读。`McpMentions.Collapse` 的作用由 `segments` 取代，旧会话没有 `segments` 时继续用它。
- “编辑并重发”时，用 `segments` 还原出完整的输入框文档。

## 13. RPC 变化（协议版本 +1）

| 方法 / 通知 | 说明 |
|---|---|
| `workspace/searchFiles {cwd, query, limit}` → `{items[{path, isDir, score, matches[]}], complete, indexing}` | §8 |
| `assets/list {cwd}` → `{commands[], skills[], roles[], problems[]}` | 只含元数据 |
| `assets/changed {cwd}`（通知） | UI 重新拉取 |
| `session/commands {sessionId, commands[]}`（会话事件） | 外部运行时自带的命令 |
| `PromptRequest.Segments` | §12 |
| `SessionOptionsDto.Role` | §10.3 |

## 14. 设置

- 设置页新增"命令、Skills 与模式"分区（`AssetsSettingsViewModel`），内容由 `assets/list` 提供，收到 `assets/changed` 时自动刷新：
  - **默认模式**（`settings.json` 的 `defaultRole`）：可选 Agent / Plan / ACP 和当前项目的自定义模式。选回 Agent 时从 settings.json 删除该字段。新会话从默认模式开始；没有改过模式时，空状态沿用当前模式。
  - **目录**：全局（`~/.tkcode`）和项目（`<项目根>/.agents`）各自的路径。
  - **命令、Skills、自定义模式三个列表**：每一项显示名称、描述、来源标记，模式还会显示工具和模型。可以在编辑器中打开，也可以在文件夹中显示。
  - **新建**：分为"新建（全局）"和"新建（项目）"，按 §18.9 的模板写入一个不重名的文件（`new-command.md`、`skills/new-skill/SKILL.md`、`new-mode.md`，重名时加序号），然后用系统默认程序打开。没有项目时，"新建（项目）"不可用。
  - **需要注意**：列出被覆盖、被遮住的项和解析问题，点一下就打开对应的文件。
- `skills.autoDiscover` 只在 settings.json 里设置，见 C8。

## 15. 实施阶段

| 阶段 | 内容 | 验证 |
|---|---|---|
| C1 | `ComposerDocument` 模型、编辑原语、撤销 / 重做、纯文本投影 | 单元测试 |
| C2 | `ComposerEditor` 排版与绘制（文本加 `TagRun`）、光标、选择、鼠标操作 | headless 快照：空、单行、多行、tag 换行、选区跨 tag、长 tag 截断，浅色和深色主题各一套 |
| C3 | 键盘、输入法（`TextInputMethodClient`）、剪贴板、拖放、无障碍；替换 `TextArea`，接入 `UpdateMultiline` / `Morph` / 图片占位 | headless 文本输入与预编辑测试；手工测试中文输入法；AOT 发布后实际运行 |
| C4 | `CompletionViewModel` 和提供者框架；把 MCP 选择器迁移过来 | 现有 MCP 测试继续通过 |
| C5 | `WorkspaceFileIndex` 加 `workspace/searchFiles`；@ 文件 tag 与只发路径；最近使用记录 | 索引与模糊匹配的单元测试，以及 200 k 条目的性能测试 |
| C6 | `AgentAssets`（frontmatter 解析、扫描、监听、覆盖规则）、`assets/list` | 单元测试：全局 / 项目覆盖、命名空间冲突、错误文件 |
| C7 | 自定义 command 展开与内置命令；JSONL 的 `segments` / `expanded` 字段；转录里的 tag | 用 FakeWireApi 跑端到端测试 |
| C8 | Skills：系统提示的 skills 节、`skill` 工具、显式调用 | FakeWireApi 测试 |
| C9 | Role：内置三种加自定义、`SessionOptionsDto.Role`、工具集过滤、模式下拉框、Shift+Tab | 快照测试，以及工具集断言 |
| C10 | 外部运行时：自带命令与 role 映射 | 用协议夹具测试（不跑真实的模型轮次） |
| C11 | 设置页分区和文档（settings.md、architecture.md） | 快照 |

每个阶段结束时都要做一次 AOT 发布：0 个 IL 警告，并启动二进制文件做冒烟测试。

## 16. 风险

- **输入法**是自绘编辑器里风险最大的部分。macOS 的标记文本和 Windows 的 TSF 在 Avalonia 里行为不同，预编辑期间光标和候选框的位置容易出错。缓解办法：在 C3 中尽早单独做一个输入法原型，并在三个平台上手工验证。
- **在大段文本上的性能**：文档长度不设上限。粘贴几万字时需要按段落缓存 `TextLayout`，只重排改动的那一段。
- **快捷键差异**：各平台的单词移动和行首行尾快捷键不同，统一走 Avalonia 的 `PlatformHotkeyConfiguration`，不写死按键。
- **`.agents` 内容不受信**：项目里的 command、skill、role 都只是文本，不执行代码，所以首版不像 MCP 那样要求用户信任项目。但 skill 可能诱导模型执行目录里的脚本，这些执行仍然经过 shell 审批。
- **command 与 skill 重名**：按 §4 的规则处理，并在设置页给出提示，避免用户困惑。

## 17. 实现状态（2026-10-17）

C1–C11 已完成，`ComposerView` 已改用 `ComposerEditor`，`@` 和 `/` 走统一补全，`@` 可以搜索工作区文件。

| 文件 | 内容 |
|---|---|
| `ComposerDocument.cs` | 文档模型、编辑原语、撤销合并、单词边界、纯文本 / 字符投影 |
| `ComposerLayout.cs` | 逐行排版（直接用 `TextFormatter`）、命中测试、选区与 tag 矩形 |
| `TagRun.cs` | `TagRun`（图标、圆角底色、中间截断）与 `ComposerTextSource` |
| `ComposerEditor.cs` | 控件：测量与自动增高、内部滚动、绘制、键盘、鼠标、剪贴板 |
| `ComposerEditor.Ime.cs` | 预编辑文字、`TextInputMethodClient`、无障碍 peer |
| `ComposerClipboard.cs` | 剪贴板内部格式（源生成 JSON） |

与设计的差异：

- **排版**：没有用 `TextLayout`，而是自己逐行调用 `TextFormatter.FormatLine`（`ComposerLayout`）。原因是 `TextLayout` 拿到 `ITextSource` 时不知道文本长度，文档以换行结尾时不会补出光标所在的空行。空行的高度按排一个空格得出，因为空文本源排出的行只有字号那么高。
- **字号与行高**：输入框固定为 14 / 20，与原来 AtomUI `TextArea` 的高度一致；紧凑形态展开成多行时仍用 22。
- **右键菜单**：新增剪切、复制、粘贴、全选。
- **拖入文件**：非图片文件插入为附件 tag。文件内容不进会话，发给 Agent 的是「[Attached file: 绝对路径]」再加一句读取说明。目录在工作区内仍是文件夹 tag，工作区外是绝对路径文字。
- **图片占位符**：`[图片 #N]` 改为 `Image` tag，删除附件时一并删除对应的 tag；`ImagePrompt.RemovePlaceholder` 已删除。
- **`ComposerText` 仍保留**：它是文档的纯文本投影，供 `CanSend` 和图片展开使用，到 C7 时再替换掉。
- **C4 补全**：
  - `CompletionViewModel` 的输入只有文档和当前获得焦点的输入框（`ICompletionHost`）。两个 `ComposerView`（空状态时居中的那个、停靠在底部的那个）共用同一个 ViewModel，由获得焦点的那个提供光标。
  - 新的查询立即询问提供者，之后的输入再防抖 60 ms。
  - 按 Esc 关闭后，同一个查询（起点和触发字符都相同）不会再自动打开。
  - 模糊匹配 `FuzzyMatcher` 放在 `TKCode.Config`，Host 的文件索引（C5）也用它。
- **C5 文件索引**：
  - 列表来源与 glob 工具相同：有 rg 时用 `rg --files`，否则用 `FileWalker`。设计里写的 `git ls-files` 没有采用，以免同一套忽略规则有两种实现。
  - 文件变化不做增量更新，只把索引标记为过期；下一次搜索时在后台重建，期间继续返回旧结果并标记 `indexing`。
  - 索引根目录是 cwd，因此返回的路径都相对于 cwd。
  - 打分分块并行，每块用堆保留前 limit 名，只为最终返回的条目计算命中位置。17 万条合成路径的平均搜索耗时：Release 约 13 ms，Debug 约 18 ms。
  - 协议版本升到 15，新增 `workspace/searchFiles`。
  - 最近 @ 过的文件记在 `state.json` 的 `recentMentions` 里（按工作区，每个最多 50 条）。
- **C6 资产**：
  - `Frontmatter`（放在 TKCode.Config）只解析 YAML 的一个子集：标量、加引号的字符串、行尾注释、流式列表和块列表、`|` 与 `>` 多行文本；嵌套映射只记下键名。
  - `AgentAssetLoader` 负责扫描与合并，`AgentAssetStore` 按项目根缓存结果。全局目录监听整个 `~/.tkcode`，但只响应 commands、skills、roles 三个目录下的变化，会话记录的写入不会触发。项目监听根目录下 `.agents` 的创建和删除，以及 `.agents` 内部的变化，300 ms 防抖。
  - 内置命令与自定义 command 的重名由 Desktop 判断（C7），Host 不知道有哪些内置命令。
  - RPC 新增 `assets/list` 和 `assets/changed`（同一协议版本 15 内）。冒烟模式新增 `assets.list` 检查。
- **补全列表改为弹出层**（用户反馈）：最初放在输入框框内的上方，会把输入框撑高；现在改为浮在上方的 `Popover`。AtomUI 的 Popup 不使用 `PlacementRect`，所以改用 `HorizontalOffset` 对齐到查询起点。
- **不是由编辑器自己发起的文档修改**（设置文本、恢复草稿、补全插入、删除图片 tag）：编辑器把光标移到变化范围的末尾。之前光标会停在原处，导致设置文本后检测不到查询。
- **C7 command 与消息格式**：
  - 内置命令首版只有 `/new`、`/model`、`/fork`。`/compact` 已在内置 Agent 实现上下文压缩后加入；`/role` 已保留名字，在 C9 实现。
  - 与内置命令同名的自定义 command 不出现在列表里。
  - command tag 可以放在消息的任何位置，可以有多个。Desktop 发送的 `Text` 不含 command tag；Host 读出各个 command 的正文，按出现顺序放在用户文字之前，用空行分隔。找不到的 command 直接报错，输入框内容恢复。
  - `UserMessage` 新增 `Display`（显示用的片段），写入 JSONL 的 `display` 字段，`Content` 是展开后实际发给模型的版本。设计里写的 `segments` / `expanded` 两个字段合并成了这一个，因为展开后的文字已经在 `Content` 里。
  - `PromptRequest.Segments`、`MessageStarted.Segments`、`HistoryMessageDto.Segments` 携带显示用的片段。只有带 tag 的消息才会有。协议版本升到 16。
  - 转录里带 tag 的用户消息改用只读的 `ComposerEditor`（新增 `IsReadOnly`）显示，外观与输入框一致，可以选择复制。会话标题用 `/review 文字` 这样的形式，不用展开后的正文。
  - `McpResources` / `McpPrompt` / `Images` 字段暂时保留，没有改为从 `Segments` 推导。
  - 排队中的消息也带着片段；编辑排队消息时只保留其中的 command tag，其余 tag 变成纯文字。
- **C8 skills**：
  - 自动发现做成一个 `IDynamicToolSource`（`SkillToolSource`），和 MCP 一样在每批用户输入前刷新。有可发现的 skill（带描述）时才提供 `skill` 工具和 `skills` 系统提示节，变化时走系统提示补丁。会话只有一个动态来源的位置，所以新增了 `CompositeToolSource`，把 MCP 和 skills 合并起来，其中一个出错不影响另一个。
  - `skill` 工具返回 `<skill name="…" directory="…">正文</skill>`，权限类别为 `None`，不需要审批。没有描述的 skill 不出现在清单里，但仍可以按名字加载。
  - 显式调用：skill tag 发送时，Host 把同样格式的 `<skill>` 块放在 command 正文和用户文字之前；skill 的名字（`/pdf`）留在用户文字里，句子读起来更自然。同一个 skill 出现多次只附一次。
  - `settings.json` 新增 `skills.autoDiscover`（默认 true）。
  - 转录里的工具调用新增 `Skill` 类别：图标为 sparkles，动词为“使用 skill”，主参数为 skill 名（Claude 运行时的 `Skill` 工具同样适用）。
- **C9 模式**：
  - 内置模式按用户要求改为 Agent / Plan / ACP。ACP 不需要新工具：现有的 `agent` 工具已经支持在一次回复中并行派出子 Agent、后台运行，以及用 `send_message` 追问。
  - 模式在 Host 中是 `SessionRole`（工具过滤加上 `mode` 系统提示节），放在 `ToolContext.Role` 上，在每批用户输入前与 MCP、skills 一起生效。`AgentSession.ContextUpdates` 合并成了一条路径，因为不管有没有动态工具来源，都要应用模式。
  - 从其他模式切回 Agent 的判断依据是"当前生效的系统提示里有没有 `mode` 节"（`SystemPromptTracker.HasSection`），因此重新打开的会话也能正确处理。
  - `SessionOptionsDto.Role` 写进会话记录（`OptionsChangeEntry.Role`），重新打开时恢复；自定义 role 已被删除时回到 Agent。协议版本升到 17。
  - 自定义 role 指定了 `model` 时，由 Desktop 用 `ModelPickerViewModel.SelectByRef` 切换模型，与手动选择走同一条路径，包括运行时切换的限制。模型不可用时给出提示，模式照常切换。
  - Desktop：`RolePickerView` 放在权限选择器右边，Agent 时芯片显示为灰色，其他模式显示为强调色。Shift+Tab 循环切换；`/` 列表最前面是模式（不列出当前模式），选中即切换；`/role` 打开下拉框。空状态沿用当前模式，新会话以它开始。
  - `rules` 节新增 `@path` 的说明（§8.3、§18.7）。
  - 外部运行时的模式映射留到 C10，目前只对内置 Agent 生效。`defaultRole` 设置留到 C11。
- **C10 外部运行时**：见 §11。
  - 运行时契约新增 `TurnOptions.Mode`（`RuntimeMode`）、`IRuntimeHost.OnCommands`（默认实现为空，旧的测试宿主不受影响）、`RuntimeCapabilities.SupportsSubagents`。
  - `IExternalAgentLoop` 新增 `SetMode`、`SupportsSubagents`、`CommandsChanged`。`AgentSession.Role` 的 setter 负责组装说明，用 `SessionRole.ExternalReminder` 生成。
  - 协议版本升到 18：新增 `commandsChanged` 会话事件和 `ModelDto.SupportsSubagents`。
  - 没有做的：用 ACP Agent 自己的模式列表替换 TKCode 模式（需要新的界面和协议对接），以及 Codex 自带命令（规格中没有对应的方法）。
- **C11 设置页**：见 §14。打开文件统一走 `ShellOpen`，从 `SettingsViewModel` 中提出来复用。解析问题的说明来自 Host，目前只有英文。
- **MCP 资源改为内联 tag**：输入框上方的资源胶囊已经去掉，资源以 `McpResource` tag（值为 `服务器:URI`）留在输入框里。发送时从 tag 取出引用；转录中已经出现在正文里的资源块不再重复显示。MCP 提示词选中后仍是输入框上方的参数卡片。

验证：

- `ComposerDocumentTests`（12 个）、`ComposerEditorTests`（7 个，包括浅色和深色快照、预编辑快照、输入法与剪贴板）、`CompletionTests`（触发检测、确认、Esc、慢提供者、模糊匹配打分）、`FileMentionTests`（经由真实 Host：浅层列表、模糊搜索、Tab 进入文件夹、只发路径、最近使用）、`WorkspaceFileIndexTests`（.gitignore、两种列表来源、文件变化、20 万条规模）、`AgentAssetTests`（frontmatter、覆盖与遮住、各类问题、文件过大、监听与通知）、`SlashCommandTests`（经由真实 Host：列表与遮住、command 展开、转录 tag、会话记录与重新打开、/model、/new、找不到的 command；skill 的显式调用、系统提示清单与 `skill` 工具调用）、`RoleModeTests`（经由真实 Host：芯片、Shift+Tab、`/acp`、自定义 role 连带切换模型、重新打开后恢复）、`SystemPromptTests`（模式的工具过滤与补丁、切回 Agent、`@path` 规则）；C10 的测试只用协议夹具与假运行时，不启动真实 CLI：`ExternalAgentTests`（说明只发一次、不进转录、等 / 命令、出错保留）、`AcpFixtureTests`（夹具 A 的命令列表、夹具 F 中早到的命令更新）、`ClaudeFixtureTests`（initialize 的 commands、plan 权限映射）、`CodexFixtureTests`（只读沙箱）、`SessionRuntimeSwitchTests`（无子 Agent 时拒绝 ACP）、`JsonRpcConnectionTests`（commandsChanged 的源生成序列化）；`AssetsSettingsTests`（列表与来源、被覆盖与问题、三种模板新建后能被解析并出现在列表中、默认模式的写入与清除、新会话的模式）；MCP 端到端测试已改为走补全。Desktop 82 个、Agent 91 个、AgentHost 34 个、Runtimes 42 个、Ai 43 个、Mcp 46 个测试全部通过。
- 冒烟模式新增 `files.search`（在 AOT 发布的 Host 上调用 `workspace/searchFiles`）和 `command.expanded`（command 与 skill 的展开、显示片段的会话记录序列化）和 `role.plan`（Host 解析并应用模式）。
- AOT 发布（osx-arm64）0 个 IL/AOT 警告，冒烟模式新增了 `composer.*` 检查并全部通过。
- **尚未验证**：在真实输入法（macOS 拼音、Windows TSF、Linux IBus/Fcitx）下手工测试。无头测试只覆盖到 `TextInputMethodClient` 这一层。


## 18. 提示词设计（已确认并实现，2026-10-17）

这一节集中设计本功能涉及的所有提示词。代码中的文本与这里一致：`SessionRole`（模式）、`SkillToolSource` / `SkillTool`（skills）、`SystemPromptBuilder.GeneralRules`（@ 引用）。修改提示词时两处一起改。

### 18.1 原则

1. **结构固定**。每个模式的提示词都按同一个顺序写：这个模式是什么、按什么顺序工作、硬性限制、最后交付什么。工作流程用编号列表，限制单独放在最后一段。参考 NextCoWork 的 mode 提示词。
2. **限制与工具一致**。提示词里说"不能写文件"，那么工具列表里就确实没有 write / edit。提示词负责解释原因和应该怎么做，真正的约束靠过滤工具来保证，不靠模型自觉。
3. **位置在后，用来覆盖**。模式节（节名 `mode`）放在 `project_context` 和 skills 之后、`cwd` 之前。AGENTS.md 里如果写着"直接改"之类的话，会被模式节覆盖。NextCoWork 也是这样放的。
4. **只引用存在的工具**。提示词里提到的工具名必须是该模式下实际可用的，例如 `agent`、`task_create`、`send_message`。内置 Agent 用 `ask_user` 向用户提问（[ask-user.md](ask-user.md) §5）；外部运行时的模式说明不提工具名（见 §18.8）。
5. **切换有交代**。模式变化时，下一批用户输入前会追加一条系统提示补丁（`Updated/Removed system prompt section "mode"`）。从 Plan 或 ACP 切回 Agent 时，补丁不是"删除"这一节，而是换成一段简短的 Agent 说明，明确告诉模型现在可以动手，以及之前定下的计划怎么处理。新会话一开始就是 Agent 模式时不加这一节，保持基础提示词不变。
6. **英文书写**，与现有系统提示一致。

### 18.2 Plan

已由 [plan-mode.md](plan-mode.md) §12.1 取代（2026-10-19）：Plan 把计划写进计划文件，用 `exit_plan_mode` 请求批准，批准后同一轮继续执行。没有宿主（没有计划审批服务）的会话仍使用原来的版本（`SessionRole.PlanWithoutApproval`）：以一段 Markdown 回复给出计划，由用户手动切换模式。

### 18.3 ACP（协调模式）

```
You are the coordinating agent in ACP mode. You own the architecture, the task breakdown, delegation to subagents, integration of their results and the final report. You do not implement changes yourself.

Work in this order:
1. Investigate enough of the workspace to define the architecture and clear task boundaries.
2. If a choice would materially change scope or behavior and the code does not settle it, ask the user with ask_user before dispatching any work.
3. Record the plan with task_create: one task per unit of delegated work, with the files it owns. Keep the tasks updated with task_update as work progresses.
4. Dispatch each task to a general-purpose subagent with the agent tool. A subagent cannot see this conversation, so its prompt must contain:
   - the goal and how it fits the overall design;
   - the relevant facts you found, with file paths;
   - the files it owns, and the files it must not touch;
   - constraints and conventions to follow;
   - how to verify its work, and what to report back (changes made, verification run and its result, open problems).
5. Run independent tasks in parallel, by issuing several agent calls in one response, only when their files do not overlap. Run dependent or overlapping tasks one after another.
6. Review every report against the plan. Resolve gaps or conflicts with further delegated tasks; send_message reaches a named subagent again.
7. Before finishing, delegate verification (tests, build, or a review of the combined changes) to a subagent.
8. Finish with a report: what was done and by which task, the verification evidence, and anything left unresolved.

Constraints: you cannot write or edit files, run commands or use MCP tools in this mode; you may read and search to plan and to check results. Never state that something was changed or verified unless a subagent reported it.
```

工具：read、ls、glob、grep、web_fetch、web_search、skill、task_create / task_get / task_list / task_update / task_stop、agent、send_message、list_agents、ask_user。子 Agent 仍使用现有的 general-purpose 提示（`SubagentTypes.PromptOf`），它的每一次工具调用照常走审批。

### 18.4 切回 Agent

已由 [plan-mode.md](plan-mode.md) §12.5 取代（2026-10-19）：计划文件存在时，说明里带上它的路径。

### 18.5 自定义 role

正文原样作为模式节的内容。frontmatter 里的 `tools` 决定工具过滤，但提示词里不会自动补充限制说明，由作者自己写清楚。设置页的"新建 role"模板会按 18.1 的结构给出示例。

### 18.6 Skills

系统提示的 `skills` 节：

```
Skills are packaged instructions for specific kinds of tasks, written by the user or for this project. Each entry below is a skill name followed by when to use it.

- Before starting a task that clearly matches a skill's description, call the skill tool with its name and follow the instructions it returns. Do not load skills speculatively.
- Paths in a skill are relative to the skill directory that the tool returns. Scripts there run with the bash tool and need the usual approvals.
- If the user's request conflicts with a skill's instructions, follow the user.
- When a message already contains a <skill> block, that skill is loaded; do not call the tool for it again.

Available skills:
- <name>: <description>
```

`skill` 工具的描述：

```
Load a skill's instructions. Returns the content of its SKILL.md and the absolute path of its directory. Call it before starting a task that matches a skill listed in the skills section, or when the user names a skill.
```

用户显式调用 skill 时附加的内容，以及工具返回的内容，使用同一个格式：`<skill name="pdf" directory="/abs/path">…SKILL.md 正文…</skill>`。

### 18.7 `@` 引用与 command

- 在 `rules` 节加一条通用规则（这一条是 §8.3 原本就计划要加的）：
  `In user messages, @path refers to a file or directory in the workspace: a relative path is relative to the primary working directory, an absolute path is used as is. Read it with your tools when you need its contents; the message only carries the path.`
  （多文件夹项目引入后的版本，见 docs/design/projects.md §7.2。）
- command 的 `.md` 正文是用户自己写的提示词，原样放在用户文字之前，不加任何包装。

### 18.8 外部运行时的模式说明

外部运行时无法过滤工具，工具名也和 TKCode 不同（Claude 是 Task / TodoWrite，Codex 没有任务清单工具），所以 Plan 和 ACP 各有一份不提 TKCode 工具名、措辞改为"不要做"的版本。只读由运行时原生手段保证（§11）。模式变化后的第一条用户消息前面加：

```
<system-reminder>
This conversation is now in {Plan | ACP | Agent | 自定义名} mode. Follow these instructions until the mode changes again:

{正文}
</system-reminder>
```

Plan 的正文：与 §18.2 相同，只有第 1 步改为 "Investigate the workspace with read-only actions."，最后一段改为：

```
Constraints: do not modify the workspace in this mode: no file edits and no commands that change anything. Never claim that something was changed. When the user approves the plan, they will switch to Agent or ACP mode to carry it out.
```

ACP 的正文：与 §18.3 相同，但做以下替换：
- 开头改为 "You are the coordinating agent."；
- 第 3 步改为 "Record the plan in your task list …"；
- 第 4 步改为 "… with your subagent tool."；
- 第 5 步改为 "… by launching several subagents in one response …"；
- 第 6 步去掉 send_message 的说明；
- 最后一段改为：

```
Constraints: do not write or edit files or run commands yourself, even though those tools may be available to you; you may read and search to plan and to check results. Never state that something was changed or verified unless a subagent reported it.
```

切回 Agent 的正文：

```
You can read, edit and write files and run commands again to carry out the user's task. If a plan was agreed earlier in this conversation, follow it unless the user says otherwise, and say where you deviate from it.
```

自定义模式的正文是 role 文件的正文；正文为空时不发。

### 18.9 新建模板

模板是给用户的起点，不是直接发给模型的提示词。但它决定了用户写出来的提示词长什么样，所以按 §18.1 的结构给出骨架，每一处占位都说明要写什么。代码在 `AssetTemplates` 中。

command（`commands/new-command.md`）：

```
---
description: One line shown in the / list. Say what the command does.
---
Write the prompt here, as you would type it to the agent: the goal, the files or areas involved, and what to report back.
Text typed after the command in the composer is added below this prompt.
```

skill（`skills/new-skill/SKILL.md`）。description 是模型决定是否加载这个 skill 的唯一依据，所以模板提醒写清适用的任务、文件类型和关键词：

```
---
name: new-skill
description: What this skill does and when to use it. The model decides whether to load the skill from this sentence alone, so name the tasks, file types and keywords it applies to.
---
# new-skill

## When to use
- The situations this skill is for, and the ones it is not for.

## Steps
1. The first thing to do.
2. The next thing to do.

## Notes
- Conventions, pitfalls and checks to run before finishing.
- Paths in this file are relative to this skill's directory; put scripts and references next to SKILL.md.
```

模式（`roles/new-mode.md`）。正文按 §18.1 的结构写，限制要与 `tools` 一致：

```
---
name: new-mode
description: One line shown in the mode picker.
tools: all          # all | readonly | [read, grep, glob, ...]
# model: provider/model   # switch to this model when the mode is chosen
# icon: eye               # a Lucide icon name
---
You are in new-mode mode. Say in one or two sentences what this mode is for.

Work in this order:
1. The first step.
2. The next step.

Constraints: what the agent must not do in this mode. Keep this consistent with `tools` above.

Finish with: what the final reply should contain.
```

