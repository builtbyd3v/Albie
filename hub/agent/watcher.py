"""Proactive Bit: when something breaks under the user, Bit speaks first. Once, never over anyone, never acting.

Callers hand events over directly (no bus listener, so no contract change):
vscode/router.py on POST /vscode/event, and cad/service.py on cad.regen_failed (wired in main.py).

Per event Bit moves to the panel (pet.state working + target_panel), says one line (pet.say bubble, and
voice when a Grok session is live) and makes the broken line glow in VS Code. It never edits code: Grok gets the
file, line and error as hidden context with the line, so a "yes" turns into its normal delegate(mode=code) fix.

Rules: one unprompted line per GAP_S; while waiting only the newest line per file (or terminal, CAD) is kept;
the same error is said once.
"""

from __future__ import annotations

import asyncio
import logging
import time
from collections.abc import Awaitable, Callable
from dataclasses import dataclass
from pathlib import Path
from typing import Any

from pydantic import BaseModel

from vscode import tools as vscode_tools

from . import events as ev
from .bus import bus
from .events import Event

log = logging.getLogger(__name__)

PET_ID = "bit"
GAP_S = 20.0
GLOW_MS = 8000
CTRL_C = 130
ERROR_MAX = 300  # chars of the error Grok gets as context


@dataclass(frozen=True)
class Line:
    slot: str  # what it is about (a file, the terminal); a new line for the same slot replaces the old one
    key: str | None  # same slot + key as last time = already said; None = always say
    text: str
    path: str | None = None
    line: int | None = None
    note: str | None = None
    panel: str = "screen-main"
    context: str | None = None  # what Grok reads (never says) with the line: what broke and how to fix it on "yes"


def fix_context(said: str, where: str, error: str) -> str:
    return (f"You just told the user: \"{said}\" It is about {where}: {error}. If they say yes, call delegate with "
            f"mode code and a task to fix exactly that, naming the file, the line and the error.")


def line_for(type_: str, p: dict[str, Any], cad_script: str | None) -> Line | None:
    """What Bit says about one event, or None to stay quiet."""
    if type_ == "cad.regen_failed":
        if p.get("source") != "script":
            return None  # pet and slider changes: the caller already sees the error
        n = p.get("error_line")
        text = f"Line {n} broke the rocket." if n else "The rocket script broke."
        text = f"{text} I kept the last good one. Want me to fix it?"
        key = f"{p.get('kept_version')}:{p['error']}"  # a good rebuild moves kept_version, so a rehearsal re-fires
        where = f"line {n} of {cad_script}" if n else str(cad_script)
        return Line("cad", key, text, cad_script, n, p["error"][:80],
                    context=fix_context(text, where, p["error"][:ERROR_MAX]))

    if type_ != "vscode.event":
        return None
    if p["kind"] == "command_finished":
        code = p.get("exit_code")
        if code in (None, 0, CTRL_C):
            return None
        text = f"That command crashed with exit code {code}. Want me to look at it?"
        return Line("terminal", None, text, context=fix_context(
            text, f"the terminal command {p.get('command') or 'the user ran'}", f"exit code {code}"))

    file = p.get("file") or ""
    if cad_script and Path(cad_script).as_posix().endswith("/" + file):
        return None  # a broken rocket.py also fails the regen, which points at the line itself
    name = Path(file).name
    count = p.get("count") or 0
    if count == 0:
        return Line(file, "fixed", f"Nice, {name} is clean again.")
    n = p.get("line")
    more = f", plus {count - 1} more" if count > 1 else ""
    text = f"Line {n} in {name} has an error{more}." if n else f"{name} has an error{more}."
    text = f"{text} Want me to look?"
    message = p.get("message") or ""
    where = f"line {n} of {file}" if n else file
    return Line(file, f"{n}:{message}", text, file, n, message[:80],
                context=fix_context(text, where, message[:ERROR_MAX] or "an error"))


class Watcher:
    def __init__(self, *, broadcast: Callable[[Event], Awaitable[None]] = bus.broadcast,
                 highlight: Callable[..., Any] = vscode_tools.highlight, gap_s: float = GAP_S) -> None:
        self.cad_script: str | None = None
        # ponytail: duck-typed, only Grok has say(); agent/ws.py calls add() and drop()
        self._voices: dict[Any, asyncio.Task[None] | None] = {}  # voice -> its queued say()
        self._broadcast, self._highlight, self._gap_s = broadcast, highlight, gap_s
        self._said: dict[str, str | None] = {}  # slot -> key last spoken
        self._pending: dict[str, Line] = {}  # slot -> newest unspoken line, oldest slot first
        self._next_ok = 0.0
        self._task: asyncio.Task[None] | None = None

    def add(self, voice: Any) -> None:
        self._voices[voice] = None

    def drop(self, voice: Any) -> None:
        """Socket closed: forget the voice and cancel its queued line, so say() can't reopen a paid session."""
        task = self._voices.pop(voice, None)
        if task is not None:
            task.cancel()

    def notify(self, type_: str, payload: BaseModel | dict[str, Any]) -> None:
        """Hand over an event (from a running event loop). Returns at once; Bit speaks in the background."""
        body = payload.model_dump() if isinstance(payload, BaseModel) else payload
        line = line_for(type_, body, self.cad_script)
        if line is None:
            return
        if line.key is not None and self._said.get(line.slot) == line.key:
            self._pending.pop(line.slot, None)  # back to what Bit last said: the queued change is stale
            return
        self._pending.pop(line.slot, None)  # re-insert at the end: newest line for this slot wins
        self._pending[line.slot] = line
        if self._task is None or self._task.done():
            self._task = asyncio.get_running_loop().create_task(self._drain())

    async def idle(self) -> None:
        """Wait until every queued line is out (tests)."""
        while self._task is not None and not self._task.done():
            await self._task

    async def _drain(self) -> None:
        while self._pending:
            wait = self._next_ok - time.monotonic()
            if wait > 0:
                await asyncio.sleep(wait)
                if not self._pending:
                    return
            line = self._pending.pop(next(iter(self._pending)))
            self._said[line.slot] = line.key
            self._next_ok = time.monotonic() + self._gap_s
            await self._speak(line)

    async def _speak(self, line: Line) -> None:
        if line.path and line.line:
            try:
                await asyncio.to_thread(self._highlight, line.path, line.line, None, line.note, GLOW_MS)
            except Exception as exc:  # noqa: BLE001 - VS Code closed: Bit still speaks
                log.info("no glow in VS Code: %s", exc)
        await self._broadcast(ev.pet_state(PET_ID, "working", target_panel=line.panel))
        await self._broadcast(ev.pet_say(PET_ID, line.text))
        for voice, queued in list(self._voices.items()):
            say = getattr(voice, "say", None)  # the fake voice has no say()
            if say is None:
                continue
            if queued is not None:
                queued.cancel()  # still waiting for silence: the newer line replaces it
            # say() waits until nobody is talking; the context goes in right before the line
            self._voices[voice] = asyncio.create_task(say(line.text, line.context))


watcher = Watcher()
