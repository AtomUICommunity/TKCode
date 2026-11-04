#!/usr/bin/env python3
"""取出 CHANGELOG.md 中某个版本的段落，作为 GitHub Release 的正文（docs/design/auto-update.md §11）。

Release 的 body 会直接显示在客户端的更新说明里（§4），所以正文用仓库里手写的版本日志，
而不是从提交信息自动生成的说明。找不到该版本（或段落为空）时以非零码退出，调用方回退到 --generate-notes。

  python3 -I tools/release/changelog-notes.py 1.2.0 CHANGELOG.md
"""
from __future__ import annotations

import re
import sys


def extract(version: str, path: str) -> str | None:
    heading = re.compile(r"^##\s+" + re.escape(version) + r"\s*$")
    lines: list[str] = []
    found = False
    with open(path, encoding="utf-8") as handle:
        for line in handle:
            line = line.rstrip("\n")
            if not found:
                if heading.match(line):
                    found = True
                continue
            if line.startswith("## "):
                break
            lines.append(line)
    if not found:
        return None
    text = "\n".join(lines).strip("\n")
    return text or None


def main(argv: list[str]) -> int:
    if len(argv) != 3:
        sys.stderr.write("usage: changelog-notes.py <version> <changelog.md>\n")
        return 2
    notes = extract(argv[1], argv[2])
    if notes is None:
        sys.stderr.write(f"no changelog section for {argv[1]}\n")
        return 1
    sys.stdout.write(notes + "\n")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
