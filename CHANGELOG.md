# Changelog

## 0.1.6

Adds projects: a project list in the sidebar, a dialog to create and edit projects, and projects with several folders that the agent can read and edit. Subagents get their own card in the transcript and a tab on the right that shows their full messages. The file explorer gains a context menu with rename, create and move to Trash.

### Added

- **Projects.** The sidebar lists your projects and replaces the "all projects" drop-down. Click a project to show only its sessions; with none selected, all sessions are shown. Create and edit projects in a dialog: name, device and source folders, with one folder marked as primary. Projects can be pinned, and removing one only removes it from TKCode, never your files or sessions.
  - The primary folder is the working directory of new sessions. TKCode looks for `AGENTS.md`, `.agents` and `.mcp.json` there, and git and project memory follow it. The other folders can be read, edited and searched, with the same approvals as the primary one, and are listed in the system prompt.
  - Changing a project's folders applies to all its sessions before their next turn. Changing the primary folder only affects new sessions.
  - The file explorer shows every folder, `@` file completion searches all of them, and the branch picker switches per folder. `/add-dir` adds a folder to the current session only.
- **Subagent cards and tabs.** A subagent shows as a small card with its name, model and a background marker. Clicking it opens a tab on the right with its complete messages, rendered like the main agent's, updating live while it runs. It also opens for finished subagents, after reopening a session, and after the Agent Host restarts. Works with the built-in agent, Claude Code and Codex.
- **File explorer actions.** A context menu on files, folders and the workspace root: open, reveal, new file, new folder, rename, show in Finder / File Explorer / file manager, open in terminal, copy full or relative path, and move to Trash or Recycle Bin (never a permanent delete). The toolbar can refresh, collapse all and show or hide hidden files.

### Fixed

- Subagents now follow the permission level of their parent session. Before, a subagent still asked for approval when the parent had full access.
- Tool details of external runtimes are saved with the session, so they are still there after reopening it.

**Full Changelog**: https://github.com/AtomUICommunity/TKCode/compare/v0.1.5...v0.1.6

## 0.1.5

The composer now keeps a separate draft for every session, including across restarts, and ↑ / ↓ bring back the messages you sent earlier in the session. The branch picker follows branch changes made outside the app and works in git worktrees and submodules.

### Added

- **Drafts per session.** Each session, and the empty new-session screen, has its own composer content: text, tags, images and a selected MCP prompt with its argument values. Switching to another session and back restores it. Before, the composer was shared, so text typed in one session showed up in all the others.
  - Drafts are saved to `~/.tkcode/drafts/` shortly after you stop typing and when the app quits, so they survive a restart. Drafts older than 3 days are deleted.
  - Deleting a session deletes its draft. Forking a session does not copy it.
  - If a send fails, the message goes back into the draft of the session it was sent from, even when you have switched to another session in the meantime.
  - The cursor position and undo history are not kept. An image whose file no longer exists is dropped together with its tag.
- **Recall sent messages with ↑ / ↓.** In the composer, ↑ brings back the messages sent earlier in the current session, newest first, with their tags and images, ready to edit. ↓ steps forward again, and past the newest message it restores the draft you were writing. Earlier history is loaded on demand when you reach the oldest message shown.
  - With text in the composer, the first ↑ only moves the cursor to the start, so a stray key press does not replace what you are writing. The next ↑ recalls a message. While you browse, a recalled message with several lines changes to the previous or next one only when the cursor is on its first or last line.
  - The keys keep their usual meaning while the completion list is open, with a selection, with Shift, Cmd or Ctrl held, and during input-method composition.
  - Editing a recalled message makes it your draft. Queued messages, MCP prompt cards and commands that only run in the interface (such as `/compact` and `/new`) are not part of the history.

### Fixed

- The branch picker under the composer follows branch changes made outside the app. A branch created, switched or deleted in the terminal or by the agent now shows up without switching projects. The list is also reloaded each time the picker opens.
- The branch picker shows the branch in linked git worktrees, in submodules and in checkouts made with `--separate-git-dir`, where `.git` is a file. Before, it showed none. In a worktree it lists the branches of the main repository, and it refreshes when you switch branches in the worktree or create one in the main repository.

**Full Changelog**: https://github.com/AtomUICommunity/TKCode/compare/v0.1.4...v0.1.5

## 0.1.4

Adds a Plan mode that ends in a plan you approve, project memory for the built-in agent, a context usage card that shows the prompt cache hit rate, automatic session titles with a separate fast model, configurable shells, interface zoom, and tray, Dock and jump-list menus with single-instance launch. Prompt caching now covers every Anthropic Messages endpoint and most Chat Completions endpoints. The app opens without a loading page and now builds on .NET 11 RC2, AtomUI 6.2.6 and Avalonia 12.1.4.

### Added

- **Plan mode with plan approval.** In Plan mode the agent researches, writes its plan to a plan file (`~/.tkcode/plans/<session>.md`) and calls `exit_plan_mode`. An approval panel replaces the composer with these choices: approve and run automatically, approve and review each step, approve and hand off to ACP, or clear the context and run from the plan alone (built-in agent only). "Keep planning" sends your feedback back to the agent. You can edit the plan in the panel or open it in an external editor before approving. After approval the session continues in the same turn, and the plan stays in the transcript as a card that shows its outcome.
  - In Agent mode the model can call `enter_plan_mode` to propose planning first. You confirm in the question panel.
  - Plan mode is read-only. `write` and `edit` work only on the plan file, and subagents are limited to `explore` and `plan`. Commands that might change the workspace ask for approval in every permission mode, including Full access.
  - Claude Code's `ExitPlanMode` opens the same approval panel. When the CLI enters or leaves plan mode by itself, TKCode's mode follows it. Codex and ACP are not connected yet.
- **Project memory** (`autoMemory`, on by default). The built-in agent keeps notes in `~/.tkcode/projects/<project>/memory/`, and all worktrees of a repository share them. The `MEMORY.md` index (first 200 lines or 25 KB) is loaded when a session starts and again after compaction. The agent reads and writes memory files without approval prompts. Subagents and external runtimes are not affected.
- **`@import` in AGENTS.md.** Context files can include other files with `@path`: relative, `~/` or absolute paths, up to 4 levels deep, ignored inside code. Project files can only import files inside the project. The global `~/.tkcode/AGENTS.md` can import from anywhere.
- **Context usage card.** Hovering the context ring under the composer shows tokens used against the budget, a segmented bar, and a breakdown into system prompt, system tools, MCP tools, skills, messages and other. It also shows the session's average prompt cache hit rate and warns when auto-compaction is close. External runtimes show only the total and the hit rate.
- **Automatic session titles.** After the first message, a separate request generates a short title and saves it with the session. A manual rename always wins. If the request fails or takes more than 30 seconds, the first line of the message stays as the title. External runtimes with no fast model configured use a temporary read-only session of the same CLI.
- **Fast model** (`fastModel`, Settings → General → Model configuration). Subagents and session titles use it. By default it follows the session model. An explicit `model` passed to the `agent` tool still takes precedence. Only built-in API models can be selected.
- **Prompt cache lifetime** (`promptCacheTtl`: `5m` or `1h`, Settings → General). It applies to every Anthropic Messages endpoint and to Claude models over Chat Completions. Running sessions pick it up on their next request.
- **Interface zoom** (`uiScale`, 0.75–2). Text, icons and spacing scale together, popups included. Settings → General offers six steps from 80% to 150%. ⌘+ / ⌘− / ⌘0 (Ctrl on Windows and Linux) and the command palette step through the same levels. Tooltips and confirmation dialogs do not scale yet.
- **Shell selection** (`agent.shell.profile`, `terminal.defaultProfile`). Settings → General lists the shells detected on the machine, separately for the agent's `bash` tool and for new terminals, plus Auto and a field for a custom path or program name. A shell that is not available falls back to Auto and is marked in settings. A new agent shell applies from the next command and updates the shell named in the system prompt. A new terminal shell applies only to terminals opened afterwards.
- **Tray, Dock and jump-list menus.** The tray menu gains New session and a Projects submenu with recent projects. On macOS the Dock menu offers New session and recent projects. On Windows the taskbar jump list offers New session, Quit TKCode and recent projects.
- **Single instance.** Launching TKCode again brings up the running window, even one hidden in the tray, instead of starting a second copy. The running instance receives `--new-session`, `--open-project <path>` and `--quit` from the command line. `--new-instance` skips the check.
- **File attachments.** Dropping, pasting or picking a file that is not an image adds an attachment tag. The file is not uploaded: the agent gets its absolute path and reads it with its own tools. The attach picker accepts any file and has Images and Documents filters.
- **Clickable file paths in replies.** These open in the workbench preview:
  - Markdown links to local files, including `path:line` suffixes and Windows drive paths.
  - Inline code that names an existing file.
  - Code blocks whose fence names a file (for example ```` ```cs src/App.cs ````), through an "Open preview" button.
- **NextCoWork provider** (`nextcowork`, key from `COWORK_API_KEY`). It uses the Responses API by default and the Messages API for Claude models. Once a key is set, its model list comes from `/v1/models`, minus image-generation models.
- **Crash logs.** Unhandled exceptions in the desktop app and the Agent Host are written to `~/.tkcode/logs/crash-*.log`. The desktop app also writes a daily `desktop-YYYYMMDD.log` next to the host log.

### Changed

- **No loading page at startup.** The main window appears right away instead of after the loading page, and does not wait for the Agent Host. The quips from the old loading page now appear on the new-session welcome screen.
- **Full access is now remembered.** Each project remembers its permission mode for new sessions, Full access included. Reopened sessions and sessions continued in a new session keep their mode. Before, Full access always reset to Ask. The confirmation dialog now says this.
- **The primary model starts every new conversation.** The `model` setting, now labelled "Primary model", is the starting model for every new conversation, ahead of the model you last picked. Changing it does not switch existing sessions.
- **New default shells on Windows.**
  - The agent's `bash` tool now tries the latest stable PowerShell 7 first, then Git Bash, then Windows PowerShell. Before, Git Bash came first.
  - New terminals open PowerShell 7, then Windows PowerShell, then cmd. Before, they opened cmd.
  - Output from PowerShell commands the agent runs is forced to UTF-8, which avoids garbled text on non-English Windows.
- **Prompt caching.**
  - Anthropic Messages requests now carry one cache breakpoint on the system prompt and one on the last message. This replaces the top-level automatic `cache_control`, which is still sent to Kimi. Breakpoints go to every Messages endpoint, not only api.anthropic.com. Requests also carry a per-session `metadata.user_id`, so relays keep a session on one upstream account.
  - Chat Completions requests carry a per-session `prompt_cache_key` on every endpoint except Gemini's OpenAI-compatible one. Claude models (through OpenRouter and relays) and Qwen on DashScope get `cache_control` breakpoints. xAI and OpenRouter also get their session-affinity headers.
  - Compaction summary requests now read the cached conversation prefix instead of paying full price for it again.
  - If an endpoint rejects these fields, set `compat.supportsCacheControl: false` or `compat.supportsPromptCacheKey: false` for that provider.
- The session list shows 20 sessions. A Show more button adds 20 at a time, replacing loading on scroll.
- User messages longer than 8 lines are collapsed, with Show more / Show less.
- The per-turn changes card lists the first five files and folds the rest behind "Show N more".
- Request errors appear in a card that wraps, can be selected and copied, and scrolls when long, for example a JSON error body.
- The send and stop buttons are now solid accent circles that fade when disabled.
- Text and thinking blocks are marked finished as soon as the block ends, not when the whole message or tool run ends.
- Removing a provider now asks for confirmation, and every provider has a remove button.
  - A built-in provider detected from an environment variable is hidden with `"hidden": true`. The variable itself is not changed.
  - An unsaved draft is discarded without asking.
- StreamMark is updated to `0.1.0-alpha.23`, built against Avalonia 12.1.4. Fallback fonts that only have a synthetic italic face render upright instead of loading a synthesized copy of the font.
- TKCode now runs on .NET 11 RC2 with Runtime Async, AtomUI 6.2.6 and Avalonia 12.1.4. On x64, .NET 11 requires a CPU with x86-64-v2 support.

### Fixed

- A tool error in a Claude Code session no longer crashes the app. The crash happened because Claude Code reports some tool results as a plain string.
- Selected text in replies is readable in the light theme. It used to be black on black.
- The copy and fullscreen buttons on tables in replies are no longer covered by the header text. They now sit in a bar above the table ([#7](https://github.com/AtomUICommunity/TKCode/issues/7)).
- `originalImages`, `defaultRole` and `skills` in `settings.json` no longer produce "Unknown field" warnings.
- The context ring no longer flickers during external-runtime turns. Those runtimes report zero usage on intermediate messages, and that zero used to replace the last real value.
- Plan mode could run shell commands that change the workspace without asking when the permission mode was Auto-approve or Full access. Plan mode is now read-only (see Added).
- Claude Code's `ExitPlanMode` went to the generic approval card as raw JSON, or was allowed without asking, and could leave TKCode in Plan mode after the CLI had left it. It now goes through the plan approval panel.
- Dropping files that are not images no longer also attaches the drag preview icon as an image.
- Subagents that run on a different model now use the closest thinking level that model supports, instead of inheriting one it does not.
- Terminal: a zero-width character such as U+200B added to the previous cell no longer leaves a stale cell on screen. This is fixed with a libghostty-vt overlay patch.

### Build

- Building requires the .NET 11 RC2 SDK (`11.0.100-rc.2.26504.105` or a later feature band; `global.json` allows prereleases). It also requires a .NET 10 Runtime, because AtomUI 6.2.6's build tools need it. All projects target `net11.0` and enable Runtime Async, NativeAOT builds included.
- AtomUI 6.2.6 changes how the app starts. AtomUI is configured once in `Program.UseTKCodeAtomUI()`, and every `AppBuilder` that starts `App` must call it: desktop, headless tests and smoke mode. See `docs/architecture.md` §7.1.
- Desktop and the memory harness set `UseOptimizedPublish=false`. Without it, .NET 11's optimized publish can succeed but leave out the compiled XAML. Check published builds with `--smoke`.
- The new `TKCode.Shell` library holds the shell detection shared by the agent and the pty host. It has its own test project, which the terminal workflow runs on all three platforms.
- Local builds are single-instance too. To run one next to an installed TKCode, pass `--new-instance` or use a different `TKCODE_HOME`. `--crash-test` throws an unhandled exception so you can check crash logging.
- `--startup-bench` renders the first frame of a published build headlessly, and `tools/startup-bench.py` measures startup with it.
- `tools/streammark/pack.sh` packs the packages under their local `StreamMark.*` IDs. On nuget.org the packages are named `TKCode.StreamMark.*`.
- In a build without the terminal native library, a terminal tab now says the library is missing and points to `tools/terminal/build-ghostty.sh`.
- Release CI installs the .NET 10 SDK alongside .NET 11. On every platform it runs the Agent Host `--self-test` and a headless Desktop `--smoke` on the published build.

**Full Changelog**: https://github.com/AtomUICommunity/TKCode/compare/v0.1.3...v0.1.4

## 0.1.3

Updates StreamMark so Mermaid diagrams follow the mermaid.js theme and stay readable in the AtomUI themes.

### Changed

- StreamMark is updated to `0.1.0-alpha.21`. Mermaid diagrams reproduce the mermaid.js dark and default themes, composite translucent theme colors onto the real backdrop (no more white-on-white in the dark theme or black-on-black in the light theme), and fit tall diagrams by width instead of capping them at 900px.
- The Mermaid theme is set explicitly to mermaid.js (`MermaidTheme.MermaidJs`).

**Full Changelog**: https://github.com/AtomUICommunity/TKCode/compare/v0.1.2...v0.1.3

## 0.1.2

Renames the project from TCode to TKCode and reduces work while thinking blocks stream.

### Changed

- **Project renamed from TCode to TKCode.** This covers the solution, projects and namespaces, the pty and ghostty native libraries (`libtkcodepty`, `tkcode-pty-spawn`, `tkcode_ghostty.h`), the macOS bundle id (`com.tkcode.desktop`), and the GitHub repository (`AtomUICommunity/TKCode`).
- **Data directory and environment variables are renamed.** The data directory is now `~/.tkcode` and every `TCODE_*` variable is now `TKCODE_*` (for example `TKCODE_HOME`). Existing data in `~/.tcode` is not migrated automatically; move it with `mv ~/.tcode ~/.tkcode`.
- Streaming thinking blocks update their duration and token statistics at most every 500 ms, and count characters incrementally instead of rescanning the whole text on every refresh.
- StreamMark is updated to `0.1.0-alpha.19` (position- and history-independent fallback fonts; fixes Windows CI font handling).

**Full Changelog**: https://github.com/AtomUICommunity/TKCode/compare/v0.1.1...v0.1.2

## 0.1.1

Largest update since 0.1.0. Adds conversation compaction, an interactive question panel for the agent, file change tracking with revert, and a number of desktop UI and release-pipeline changes.

### Added

- **Conversation compaction.** When a session approaches the model's context limit, TKCode summarizes the history and continues the session from the summary. Compaction runs proactively before a request and reactively when the provider reports a context overflow. The summary replaces the earlier history as a whole; the rest of the session continues unchanged.
- **Agent questions (`ask_user`).** The agent can ask the user a structured question. The question appears in a panel that replaces the composer while it is pending; sending is blocked until the question is answered. External runtimes' own question tools (Claude Code, Codex, ACP) use the same panel.
- **Change tracking and diff view.** A new Changes tab lists files the agent modified, with a per-turn changes view and a diff view for each file.
- **Revert file changes.** Files changed by the agent can be reverted from the change views.
- **Built-in prompt commands.** `/commit` and `/init` are available as built-in slash commands.
- **Session file reading.** A dedicated session file reader is added to the coding session store.
- **Resizable explorer panel.** The explorer and workbench panels can be resized with a splitter.

### Changed

- Image attachments are reworked: attachment storage and handling in the session store were rewritten.
- Background task notifications are emitted when the set of running background tasks changes.
- Idle sessions can release their in-memory resources in the Agent Host.
- Tool calls are rendered as step views with a dedicated task orb, replacing the old task list.
- Terminal tabs use the updated terminal theme and the refreshed tab view.
- Settings schema and localization strings (English and Simplified Chinese) were updated for the new features.
- Shared line-diff logic moved from the coding tools into the core `TKCode.Agent` library so both the agent and the UI can use it.

### Removed

- The macOS installer package (`component.plist` and `postinstall`). macOS builds ship as the `.app` bundle and the dmg/zip artifacts.
- The standalone task list view, replaced by the step views and task orb.

### Release process

- Release versions come from the git tag only. Tagging `v0.1.1` builds `0.1.1`; the local build stays `0.0.0-dev`.
- The release CI creates a draft release for stable tags. Tags containing `-` are published as pre-releases.

**Full Changelog**: https://github.com/AtomUICommunity/TKCode/compare/v0.1.0...v0.1.1
