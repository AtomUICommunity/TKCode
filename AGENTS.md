# AGENTS.md

Guidance for coding agents and new contributors. The "what" lives in the code and docs; this file is the non-obvious rules. Verify against the code when in doubt.

## Stack

- Desktop AI agent client: Avalonia 12 + AtomUI 6 on .NET 11 RC2 (`global.json` pins 11.0.100-rc.2.26504.105, `latestFeature`, prereleases enabled). Single solution `TKCode.slnx` — add new projects to it.
- Runtime Async is enabled centrally with `runtime-async=on` in `Directory.Build.props`, including NativeAOT builds.
- Everything must stay NativeAOT-safe: `TKCode.Desktop`, `TKCode.AgentHost`, and `TKCode.Terminal.Host` set `PublishAot=true`; all libraries are `IsAotCompatible`.

## Setup (first build fails without it)

AtomUI 6.2.6's isolated build tools require a .NET 10 Runtime (also included in the .NET 10 SDK). Install it alongside the .NET 11 SDK; solution projects and published applications target .NET 11. Release CI provisions both SDKs and selects .NET 11 through `global.json`.

1. `git submodule update --init` — pulls `external/StreamMark`.
2. `tools/streammark/pack.sh` — packs `TKCode.StreamMark.*` (the nuget.org package IDs, which `TKCode.Arc` also depends on) into `packages/streammark` (the local NuGet feed in `nuget.config`). Needs a Rust toolchain once, for the RaTeX native lib. After bumping the submodule, keep `StreamMarkVersion` in `Directory.Packages.props` in sync.
3. Terminal work only: Zig 0.16.0 exactly — do not use a package-manager Zig. `native/tkcodepty/build.sh` auto-installs the pinned version via `tools/terminal/install-zig.sh`.

## Commands

| Purpose | Command |
|---|---|
| Build | `dotnet build TKCode.slnx` |
| Run app | `dotnet run --project src/TKCode.Desktop` |
| All tests | `dotnet test TKCode.slnx` |
| One project / one test | `dotnet test tests/<Project>` / `--filter "FullyQualifiedName~<Name>"` |
| AOT publish | `dotnet publish src/TKCode.Desktop -c Release -r <rid>` — the log must contain zero `warning IL####` (CI fails on any) |
| macOS app bundle | `tools/package_macos_app.sh osx-arm64` → `artifacts/TKCode.app` |
| Smoke-test a published build | `artifacts/publish/<rid>/TKCode --smoke --out=frame.png` |
| Regenerate README screenshots | `tools/readme-screenshots.sh` → `docs/images/` |
| Memory/CPU benchmark | `tools/mem-bench.sh [rid]` (opens real windows, ~25 s per scenario) |

Terminal native libraries (only for `TKCode.Terminal.*` / `native/` work):

- `native/tkcodepty/build.sh [rid]` builds the Unix PTY lib + spawn helper. `libghostty-vt` (pin, overlays, build script, bindings generator) now lives in the Arc repo and arrives with the `TKCode.Arc.Terminal` package.
- MSBuild picks the libs up via `native/TerminalNative.targets`; a build without them only warns — run the scripts if terminal tests fail to load natives.

## Layout

- `src/TKCode.Ai` → `TKCode.Agent` → `TKCode.Agent.Coding`: headless core (wire adapters, agent loop, coding session/tools). No UI references, ever.
- `src/TKCode.Rpc.Contracts`: hand-written JSON-RPC 2.0 over stdio (replaced StreamJsonRpc, which breaks under AOT). `src/TKCode.AgentHost`: session-host process. `src/TKCode.Config`: settings shared by Host and Desktop.
- `src/TKCode.Shell`: BCL-only shell detection and `agent.shell.profile` / `terminal.defaultProfile` resolution, shared by the agent `bash` tool and the pty host; platform access goes through `IShellSystem` so Windows rules are tested on any OS.
- `src/TKCode.Runtimes`: external agent runtimes (Claude Code, Codex, ACP). `src/TKCode.Mcp`: MCP client.
- `src/TKCode.Terminal.*`: Pty, Protocol, Host (the `tkcode-ptyhost` process). The libghostty-vt bindings/facade and the Avalonia `TerminalView` live in the `TKCode.Arc.Terminal` NuGet package (Arc repo, `src/TKCode.Arc.Terminal`; namespaces still `TKCode.Terminal.Core` / `TKCode.Terminal.Avalonia`). The native build (`tools/terminal/build-ghostty.sh`) and `gen-ghostty-bindings.sh` are in the Arc repo.
- `src/TKCode.Desktop`: the Avalonia app. `native/`: Zig/C sources and toolchain pins. `tools/`: build/gen/package scripts. `bench/`: memory harness. `docs/`: architecture and design docs. `external/StreamMark`: submodule, do not commit changes from this repo into it casually.

## Hard rules

- **AOT:** JSON only via source-generated `JsonSerializerContext` contexts; build request bodies with `JsonNode` (no generic `JsonArray.Add<T>`). No reflection, `Activator`, dynamic proxies, or runtime codegen. Vet every new NuGet package for AOT compatibility and confirm a zero-warning publish.
- **AtomUI** comes from NuGet only; never copy or modify its source; theme via public tokens/theme XML. Startup is the 6.2.6 contract (`docs/architecture.md` §7.1): `Application.UseAtomUI` and `WithDefaultTheme` are gone. Call `Program.UseTKCodeAtomUI()` (`AppBuilder.UseAtomUI`) on every `AppBuilder` that starts `App` — desktop, headless tests, smoke, and the startup bench. The XAML previewer calls `BuildAvaloniaApp` without `Main`, so `App.Services` may be null. The configuration callback runs before platform init and may only declare packages and settings; do not touch `FontManager`, `Application.Current`, `TopLevel`, bitmaps, or controls there — committed work goes in `AddInitializer`. `App` is `partial`; the handwritten `RegisterServices` must call `base.RegisterServices()` then `ApplicationStartup.Attach(this)` (`ATOMUIGEN039` if `Attach` is missing). Avalonia stays at least 12.1.4. On macOS, `WithAtomUIDefaultOptions` prefers Metal, then OpenGL, then Software; headless screenshot tests do not call it. Do not reference generated `ControlPackageGroup_*` names.
- **Central package versions** in `Directory.Packages.props` only; no `Version` attributes on PackageReferences.
- **Localization:** the single source of truth is `src/TKCode.Desktop/Localization/strings.tsv` (key, en-US, zh-CN). Regenerate with `python3 -I tools/gen_strings.py`; never hand-edit `TKCodeLangResourceKind.g.cs` or the `.xlf` files. `{loc:}` looks up `Application.Resources`; `Strings.Get` does not. AtomUI 6.2.6 inserts `LanguageResourceProvider` before XAML load, and assigning `<Application.Resources>` replaces that dictionary — `App.Initialize` merges the compiled palette back. Do not load `Palette.axaml` with a runtime `ResourceInclude` (AOT-unsafe), and do not put keys directly on `Application.Resources`.
- **UI zoom:** `settings.json` `uiScale` scales the whole UI through `ZoomHost` (`Controls/ZoomHost.cs`), which wraps the main window content. Popups are separate windows, so every `motion:Popover` must wrap its content in `<controls:ZoomHost>` (`UiZoomTests` fails otherwise). Code that talks to native window geometry (macOS traffic-light inset, min size, title-bar height hint) must convert through `UiZoom.Scale`.
- **Generated assets:** provider icons (`tools/gen_provider_icons.py` → `Icons/ProviderIconPaths.g.cs`) and the model catalog (`tools/gen_catalog.py` → `TKCode.AgentHost/Catalog/catalog.json`) are generated — edit the inputs, rerun the scripts.

## Protocol constraints (see docs/architecture.md, docs/protocols/)

- Conversation history is append-only; the system prompt and tool set freeze at session start; changes are expressed by appending `SystemMessage`s. Compaction (summary + new turn) is the only history replacement and happens only between model requests.
- Streams never throw: every failure ends as an `ErrorEvent` (`StopReason` `Error`/`Aborted`), keeping partial content.
- Implement adapters strictly per the specs in `docs/protocols/`; replay provider-opaque data (thinking signatures, reasoning items) verbatim.
- Host's stdout is reserved for JSON-RPC frames — Host logs go to stderr.
- Adding an RPC method touches four places: the interface, `RpcMethods`, `RpcJsonContext`, and the proxy + dispatcher (docs/architecture.md §5).

## Tests

- `tests/TKCode.Desktop.Tests` and `TKCode.Terminal.Avalonia.Tests` render real components headlessly (Avalonia.Headless + Skia) and write PNGs to `snapshots/` (or `$TKCODE_SNAPSHOT_DIR`). Do not drive the real desktop or synthesize OS input.
- Useful env vars: `TKCODE_HOME` (redirects `~/.tkcode`), `TKCODE_AGENTHOST_DIR`, `TKCODE_TEST_HOST_EXE` (point RPC e2e tests at an AOT-published Host), `TKCODE_PTYHOST_DIR` (AOT pty host), `TKCODE_DIFF_SEEDS` / `TKCODE_FUZZ_MINUTES` / `TKCODE_PTY_STRESS` (long nightly sweeps, skipped unless set).

## CI

- `release.yml` (on `v*` tags): builds StreamMark native libs and packs NuGet → builds terminal native libs → per-platform AOT publish with the zero-warning gate → Host self-tests and headless Desktop smoke tests → signed manifest → draft GitHub Release.
- `terminal.yml` (on terminal/native paths): native libs for all RIDs → tests on 3 platforms → esctest2 → AOT `tkcode-ptyhost` + `--self-test`.

## Pitfalls

- Restore/build fails on `TKCode.StreamMark.*` → you skipped the setup steps above.
- Keep `UseOptimizedPublish=false` on Desktop and MemoryHarness: .NET 11's optimized NativeAOT publish prepares resources before Avalonia adds them during reference resolution. A publish can otherwise succeed with missing compiled XAML; verify the published Desktop with `--smoke`.
- The Desktop build auto-copies the Agent Host into `agenthost/` and the pty host into `ptyhost/` in its output; publishing AOTs both into the same subfolders. Run the app binary, not the hosts directly.
- Desktop is single-instance (`instance.lock` in `~/.tkcode` + a named pipe): a second launch just forwards to the running one, which may be hidden in the tray. Pass `--new-instance` (or a different `TKCODE_HOME`) when you need a local build next to an installed one. Tray / Dock / jump-list menus: `docs/design/system-menus.md`.
- Local builds stay version `0.0.0-dev` (no update checks); release CI overrides `-p:Version=<tag without v>`.
- Linux AOT binaries link the builder's glibc; the native floor is glibc 2.28 (CI verifies).
- Some scripts mentioned in `docs/terminal/12-build-and-ci.md` (`build-native.sh`, `verify-native.sh`) do not exist yet — trust the files in `tools/`, not the doc.
