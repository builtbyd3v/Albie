"""Supervisor for screen-bridge/start.js: spawn, parse its lines into `screen.status`, restart, kill with the hub.

Lines we understand (start.js, signaling.py and index.html's status span):
    signaling on ws://0.0.0.0:8766        -> detail
    Sharing "<source>". ...               -> state sharing, source
    signaling: connected|disconnected     -> detail
    video: <connectionState>[ <codec>]    -> detail
    <n> fps, <w>x<h>                      -> fps (not published; GET /screens/status shows it)
    anything else                         -> logged as [screen-bridge] only
A child that exits on its own is restarted after 2 s, 5 s, 10 s; the fourth death is final (`state: error`).
Reaching `sharing` resets that counter, so a bridge that ran for an hour gets a fresh three tries.
"""
from __future__ import annotations

import asyncio
import contextlib
import logging
import os
import re
import shutil
import signal
import time
from collections.abc import Sequence
from pathlib import Path
from typing import Any

from events import SCREEN_STATUS, EventBus, ScreenStatus

log = logging.getLogger("forge.screens")

HUB_DIR = Path(__file__).resolve().parents[1]
DEFAULT_DIR = HUB_DIR.parent / "screen-bridge"
BACKOFF_S: tuple[float, ...] = (2.0, 5.0, 10.0)
KILL_GRACE_S = 3.0
DRAIN_S = 1.0  # after the child exits: how long to keep reading pipes its own children may still hold open
_SHARING = re.compile(r'^Sharing "(?P<source>.*?)"')
_STATUS = re.compile(r"^(signaling|video): \S")
_FPS = re.compile(r"^\d+ fps, \d+x\d+$")


def _truthy(value: str | None, default: bool) -> bool:
    if value is None or value.strip() == "":
        return default
    return value.strip().lower() not in ("0", "false", "no", "off")


class ScreenBridgeService:
    """One child process group (npm start in screen-bridge/) owned by the hub's lifespan."""

    def __init__(self, bus: EventBus, *, directory: Path | None = None, enabled: bool | None = None,
                 command: Sequence[str] | None = None, backoff: Sequence[float] = BACKOFF_S,
                 kill_grace_s: float = KILL_GRACE_S) -> None:
        self.bus = bus
        self.dir = Path(directory or os.environ.get("SCREEN_BRIDGE_DIR") or DEFAULT_DIR).expanduser()
        self.backoff = tuple(backoff)
        self.kill_grace_s = kill_grace_s
        self.command: list[str] | None = list(command) if command else None
        self.enabled = _truthy(os.environ.get("SCREEN_BRIDGE"), True) if enabled is None else enabled
        self.state: str = "stopped"
        self.source: str | None = None
        self.detail: str | None = None
        self.fps: str | None = None
        self.restarts = 0
        self.pid: int | None = None
        self._proc: asyncio.subprocess.Process | None = None
        self._task: asyncio.Task[None] | None = None
        self._stopping = False
        self._started_at = 0.0
        bus.make_sticky(SCREEN_STATUS)

    # ------------------------------------------------------------------ lifecycle
    def _resolve_command(self) -> list[str] | None:
        """npm start (runs the VS Code extension install first) when npm is on PATH, else node start.js."""
        if self.command:
            return self.command
        if shutil.which("node") is None:
            log.warning("screen bridge disabled: `node` is not on PATH (install Node or set SCREEN_BRIDGE=0)")
            return None
        if not (self.dir / "start.js").is_file():
            log.warning("screen bridge disabled: %s has no start.js (set SCREEN_BRIDGE_DIR)", self.dir)
            return None
        if shutil.which("npm") is not None:
            return ["npm", "start"]
        return ["node", "start.js"]

    async def start(self) -> None:
        if not self.enabled:
            log.info("screen bridge off (SCREEN_BRIDGE=0)")
            return
        cmd = self._resolve_command()
        if cmd is None:
            self.enabled = False
            return
        self.command = cmd
        self._stopping = False
        self._task = asyncio.create_task(self._supervise(), name="screen-bridge")

    async def stop(self) -> None:
        self._stopping = True
        await self._kill()  # before cancelling the supervisor: its cleanup forgets the process handle
        task, self._task = self._task, None
        if task is not None:
            task.cancel()
            with contextlib.suppress(asyncio.CancelledError):
                await task
        if self.enabled and self.state != "stopped":
            await self._set("stopped", detail="hub shutting down")

    def health(self) -> dict[str, Any]:
        return {"enabled": self.enabled, "state": self.state, "source": self.source}

    def status(self) -> dict[str, Any]:
        return {**self.health(), "detail": self.detail, "fps": self.fps, "pid": self.pid, "restarts": self.restarts,
                "dir": str(self.dir), "command": self.command}

    # ------------------------------------------------------------------ supervision
    async def _supervise(self) -> None:
        deaths = 0
        while not self._stopping:
            try:
                code = await self._run_once()
            except asyncio.CancelledError:
                raise
            except Exception as exc:  # noqa: BLE001 - spawn failure (bad cwd, npm missing): report, maybe retry
                log.error("[screen-bridge] failed to start: %s", exc)
                code = None
                self.detail = str(exc)
            if self._stopping:
                return
            if self.state == "sharing":
                deaths = 0  # it worked for a while: fresh retries
            if deaths >= len(self.backoff):
                await self._set("error", detail=f"screen bridge died {deaths + 1} times (last exit {code}); giving up. "
                                                "Fix it and restart the hub, or run `npm start` in screen-bridge/.")
                return
            delay = self.backoff[deaths]
            deaths += 1
            self.restarts += 1
            log.warning("[screen-bridge] exited (%s); restarting in %.0fs (%d/%d)", code, delay, deaths, len(self.backoff))
            await self._set("starting", detail=f"exited ({code}); restarting in {delay:.0f}s")
            await asyncio.sleep(delay)

    async def _run_once(self) -> int | None:
        assert self.command is not None
        self.source = self.fps = None
        proc = await asyncio.create_subprocess_exec(
            *self.command, cwd=str(self.dir), env=os.environ.copy(),
            stdin=asyncio.subprocess.DEVNULL, stdout=asyncio.subprocess.PIPE, stderr=asyncio.subprocess.PIPE,
            start_new_session=True,  # its own process group: killpg takes Chrome and both python servers with it
        )
        self._proc, self.pid, self._started_at = proc, proc.pid, time.monotonic()
        log.info("[screen-bridge] started pid %d: %s (in %s)", proc.pid, " ".join(self.command), self.dir)
        await self._set("starting", detail="starting", publish=True)
        pumps = asyncio.gather(self._pump(proc.stdout, logging.INFO), self._pump(proc.stderr, logging.WARNING))
        try:
            code = await _exited(proc)
            # Chrome / signaling.py inherit the pipes: if they outlive start.js there is no EOF, so don't wait for one.
            with contextlib.suppress(asyncio.TimeoutError, asyncio.CancelledError):
                await asyncio.wait_for(pumps, DRAIN_S)
            if not self._stopping:  # a crashed start.js leaves its servers on :8766/:8765; the restart needs them gone
                _signal_group(proc.pid, signal.SIGTERM)
            return code
        finally:
            pumps.cancel()
            if self._proc is proc:
                self._proc = None
            self.pid = None

    async def _pump(self, stream: asyncio.StreamReader | None, level: int) -> None:
        if stream is None:
            return
        while True:
            raw = await stream.readline()
            if not raw:
                return
            line = raw.decode("utf-8", "replace").rstrip()
            if not line:
                continue
            log.log(logging.DEBUG if _FPS.match(line) else level, "[screen-bridge] %s", line)
            await self._parse(line)

    async def _parse(self, line: str) -> None:
        m = _SHARING.match(line)
        if m:
            await self._set("sharing", source=m.group("source"), detail=line.split(". ", 1)[0])
        elif _STATUS.match(line):
            await self._set(self.state, detail=line)
        elif line.startswith("signaling on"):
            await self._set("starting", detail=line)
        elif _FPS.match(line):
            self.fps = line

    async def _set(self, state: str, *, source: str | None = None, detail: str | None = None,
                   publish: bool | None = None) -> None:
        """Update and publish screen.status when something visible changed (or `publish` forces it)."""
        changed = state != self.state or (source is not None and source != self.source) or detail != self.detail
        self.state = state
        if source is not None:
            self.source = source
        self.detail = detail
        if changed or publish:
            await self.bus.publish(SCREEN_STATUS, ScreenStatus(state=state, source=self.source, detail=self.detail))

    # ------------------------------------------------------------------ shutdown
    async def _kill(self) -> None:
        proc = self._proc
        if proc is None or proc.returncode is not None:
            return
        pgid = proc.pid  # start_new_session=True: the child leads its own group
        _signal_group(pgid, signal.SIGINT)  # start.js closes Chrome and its children on SIGINT
        try:
            await asyncio.wait_for(_exited(proc), self.kill_grace_s)
        except asyncio.TimeoutError:
            log.warning("[screen-bridge] still alive %.0fs after SIGINT; killing the process group", self.kill_grace_s)
            _signal_group(pgid, signal.SIGKILL)
            with contextlib.suppress(asyncio.TimeoutError):
                await asyncio.wait_for(_exited(proc), 2.0)
        self._proc = None


async def _exited(proc: asyncio.subprocess.Process) -> int | None:
    """Return the exit code as soon as the child is gone. Not `proc.wait()`: on Python 3.11 that also waits for
    EOF on the pipes, which never comes while Chrome or signaling.py (inherited fds) outlive start.js."""
    while proc.returncode is None:
        await asyncio.sleep(0.05)
    return proc.returncode


def _signal_group(pgid: int, sig: signal.Signals) -> None:
    try:
        os.killpg(pgid, sig)
    except ProcessLookupError:
        pass
    except OSError as exc:
        log.warning("killpg(%d, %s) failed: %s", pgid, sig.name, exc)
