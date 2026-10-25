#!/usr/bin/env bash
# 从 StreamMark 源码打包 NuGet 包到 packages/streammark/（TKCode 的本地包源，见 nuget.config）。
# 默认使用子模块 external/StreamMark，版本取 Directory.Packages.props 的 StreamMarkVersion。
# 本地同时开发 StreamMark 时可以指定源码目录和新版本：
#   tools/streammark/pack.sh ../StreamMark 0.1.0-alpha.16   （之后把 StreamMarkVersion 改成同一版本）
# RaTeX 原生库只构建本机 RID（需要 Rust 工具链）。
set -euo pipefail
root="$(cd "$(dirname "$0")/../.." && pwd)"
src="$(cd "${1:-$root/external/StreamMark}" && pwd)"
version="${2:-$(sed -n 's:.*<StreamMarkVersion>\(.*\)</StreamMarkVersion>.*:\1:p' "$root/Directory.Packages.props")}"
out="$root/packages/streammark"
[[ -f "$src/native/ratex/Cargo.toml" ]] || git -C "$src" submodule update --init --depth 1 native/ratex
"$src/native/build.sh"
for project in Core Avalonia RaTeX Highlighting.TextMate Mermaid Theme.AtomUI; do
  # 包 ID 与 nuget.org 一致（TKCode.StreamMark.*）：TKCode.Arc 依赖同名包，程序集名也相同，不能再用另一套 ID
  dotnet pack "$src/src/StreamMark.$project" -c Release -o "$out" -p:Version="$version" --nologo -v quiet
done
ls "$out" | grep -F "$version"
