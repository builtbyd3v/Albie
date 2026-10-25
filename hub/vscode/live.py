"""Albie's code edits, live in VS Code: the file opens as an edit starts, and the changed lines glow once it lands.

agent/runner.py calls `opening` for each Edit / MultiEdit / Write tool use and `edited` when its result comes back.
Best effort: VS Code closed or the extension off never slows or breaks the agent's turn.
"""
from __future__ import annotations

import asyncio
import logging
from collections.abc import Coroutine
from pathlib import Path
from typing import Any

from vscode import tools

log = logging.getLogger("forge.vscode.live")

EDIT_TOOLS = frozenset({"Edit", "MultiEdit", "Write"})
NOTE = "Albie edited this"
GLOW_MS = 6000
WRITE_LINES = 40  # a new file glows from the top, at most this many lines


def _new_text(name: str, inp: dict[str, Any]) -> str:
    if name == "Write":
        return str(inp.get("content") or "")
    if name == "MultiEdit":
        edits = inp.get("edits") or [{}]
        return str(edits[0].get("new_string") or "")
    return str(inp.get("new_string") or "")


def _line_of(text: str, snippet: str) -> int | None:
    i = text.find(snippet) if snippet else -1
    return text.count("\n", 0, i) + 1 if i >= 0 else None


def changed_lines(name: str, inp: dict[str, Any], text: str) -> tuple[int, int] | None:
    """1-based (start, end) of what the edit wrote into `text` (the file after it), or None (deleted, not found)."""
    if name == "Write":
        lines = text.count("\n") + (0 if text.endswith("\n") else 1)
        return (1, max(1, min(lines, WRITE_LINES)))
    new = _new_text(name, inp)
    start = _line_of(text, new)
    if start is None:
        return None
    return (start, start + new.rstrip("\n").count("\n"))


def opening(name: str, inp: dict[str, Any]) -> Coroutine[Any, Any, None]:
    """Open the file at the code about to change (the old text's line; a new file at line 1). The line is read now,
    when the tool use arrives: the runner awaits the open in the background, and by then the edit may have landed."""
    path = inp.get("file_path")
    line = 1
    if path and name != "Write":
        old = str(inp.get("old_string") or (inp.get("edits") or [{}])[0].get("old_string") or "")
        try:
            line = _line_of(Path(path).read_text(), old) or 1
        except OSError:
            pass
    return _open(str(path), line) if path else asyncio.sleep(0)


async def _open(path: str, line: int) -> None:
    try:
        await asyncio.to_thread(tools.open_file, path, line)
    except (OSError, RuntimeError, ValueError) as e:  # VS Code closed or the extension off
        log.debug("open %s in VS Code: %s", path, e)


async def edited(name: str, inp: dict[str, Any]) -> None:
    """Glow the lines the edit wrote, with Albie's note."""
    path = inp.get("file_path")
    if not path:
        return
    try:
        span = changed_lines(name, inp, Path(path).read_text())
        if span:
            await asyncio.to_thread(tools.highlight, str(path), span[0], span[1], NOTE, GLOW_MS)
    except (OSError, RuntimeError, ValueError) as e:
        log.debug("glow %s in VS Code: %s", path, e)
