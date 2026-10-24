"""Bit opens and closes the workspace views (docs/PET_AGENT_CONTRACT.md, Addendum H).

Only Unity knows whether a view can open (the user has to be at the desk), so opening is a round trip:

    hub -> clients   ui.mode {mode}                      open that view ("" closes the open one)
    client -> hub    workspace.mode {mode, reason?}      what is open now; reason = why it isn't what was asked

``workspace.open(mode)`` publishes ``ui.mode`` and waits up to ``ACK_S`` for a ``workspace.mode`` that names the mode
asked for (or carries a reason), so Bit's tool result says what really happened. Unity already sends
``workspace.mode`` on every view change (ArrivalDirector), so a client without the reason field still confirms a
successful open; it only times out on a refusal. ws.py feeds every ``workspace.mode`` here through ``report``; main.py
sets ``publish`` to the EventBus. One state for the hub: the last view any Unity client reported.
"""

from __future__ import annotations

import asyncio
import logging
from collections.abc import Awaitable, Callable
from typing import Any

from pydantic import BaseModel

from events import UiMode

from .bus import bus

log = logging.getLogger(__name__)

ACK_S = 1.5        # Unity answers ui.mode in the same frame; this covers a slow Quest Wi-Fi round trip
QUIET_ACK_S = 0.4  # after a ui.mode went unanswered (a Unity build without the handler): don't stall every tool
VIEWS = ("cad", "code", "ml", "messages")
ALIASES = {
    "": "", "none": "", "close": "", "closed": "", "off": "",
    "cad": "cad", "3d": "cad", "modeling": "cad", "modelling": "cad", "design": "cad", "designs": "cad",
    "code": "code", "vscode": "code", "vs code": "code", "editor": "code", "screen": "code", "screens": "code",
    "ml": "ml", "training": "ml",
    "messages": "messages", "msg": "messages", "slack": "messages", "messenger": "messages", "inbox": "messages",
}
TITLES = {"cad": "CAD", "code": "CODE", "ml": "ML", "messages": "MSG", "": "nothing"}
REASONS = {
    "not_at_desk": "The user isn't at their desk, so the workspace can't open; ask them to sit down at it first.",
    "unknown_mode": "Unity has no view by that name.",
}

Publish = Callable[[str, BaseModel], Awaitable[None]]


def resolve(view: str) -> str | None:
    """A view id from what the model said ("3D", "Slack", "the 3D modeling view", "none"); None if it isn't one."""
    words = str(view).lower().replace("_", " ").replace("-", " ").split()
    phrase = " ".join(words)
    if phrase in ALIASES:
        return ALIASES[phrase]
    hits = {ALIASES[w] for w in words if ALIASES.get(w)}
    return hits.pop() if len(hits) == 1 else None


class Workspace:
    def __init__(self) -> None:
        self.mode: str | None = None  # None: no client has said yet
        self.reason: str | None = None
        self.publish: Publish | None = None
        self._waiters: list[tuple[str, asyncio.Future[tuple[str, str | None]]]] = []
        self._unanswered = False  # the last ui.mode got no answer: wait QUIET_ACK_S until a client reports again

    def report(self, mode: str, reason: str | None = None) -> None:
        """A client's workspace.mode: remember it and wake whoever waits for that view (or any reason)."""
        mode = mode.strip().lower()
        self.mode, self.reason, self._unanswered = mode, reason or None, False
        for wanted, fut in list(self._waiters):
            if not fut.done() and (mode == wanted or reason):
                fut.set_result((mode, reason or None))

    def forget(self) -> None:
        """The last Unity client went away: nobody shows anything."""
        self.mode, self.reason, self._unanswered = None, None, False

    async def open(self, view: str, timeout_s: float | None = None) -> dict[str, Any]:
        """Open a view ("" closes). Returns {ok, view, already_open?} or {error, view?}."""
        mode = resolve(view)
        if mode is None:
            return {"error": f"No view {view!r}; the views are {', '.join(VIEWS)} (or none to close)"}
        if self.mode == mode:
            return {"ok": True, "view": mode, "already_open": True}
        if self.publish is None or bus.count == 0:
            return {"error": "The headset app isn't connected, so I can't change what's on screen."}
        if timeout_s is None:
            timeout_s = QUIET_ACK_S if self._unanswered else ACK_S
        fut: asyncio.Future[tuple[str, str | None]] = asyncio.get_running_loop().create_future()
        entry = (mode, fut)
        self._waiters.append(entry)
        try:
            await self.publish("ui.mode", UiMode(mode=mode))
            try:
                shown, reason = await asyncio.wait_for(fut, timeout_s)
            except asyncio.TimeoutError:
                self._unanswered = True
                log.warning("ui.mode %s: no workspace.mode answer within %.1f s", mode or "(close)", timeout_s)
                what = f"The {TITLES[mode]} view didn't open" if mode else "The open view didn't close"
                return {"error": f"{what} (the headset app didn't confirm). The user may need to sit down at their desk."}
        finally:
            self._waiters.remove(entry)
        if shown == mode:
            return {"ok": True, "view": mode}
        return {"error": REASONS.get(reason or "", f"Unity didn't open it ({reason})."), "view": shown}

    async def ensure(self, view: str) -> dict[str, Any]:
        """For tools that act on a view: open it unless it's known to be open already. Never raises."""
        try:
            return await self.open(view)
        except Exception as e:  # noqa: BLE001 - showing the result is a nicety; the tool's own work still counts
            log.exception("opening %s failed", view)
            return {"error": str(e)}


workspace = Workspace()

# Bit's view tool (agent/tools.py registers it). CAD and messaging tools open their own view through `ensure`.
TOOLS: list[dict[str, Any]] = [
    {"name": "ui_open_view",
     "description": "Open one of the workspace views at the user's desk; one view shows at a time. cad = 3D modeling "
                    "(the CAD design on the desk, its drawing, panel and versions), code = VS Code, ml = machine "
                    "learning (training data and runs), messages = the Slack messenger; none closes the open view. "
                    "CAD and Slack tools open their view themselves, so call this only when the user asks for a view. "
                    "If it returns an error, tell the user (usually: sit down at the desk first).",
     "parameters": {"type": "object", "properties": {
         "view": {"type": "string", "enum": ["cad", "code", "ml", "messages", "none"]}},
         "required": ["view"]}},
]


async def call_tool(name: str, args: dict[str, Any]) -> dict[str, Any]:
    if name == "ui_open_view":
        return await workspace.open(str(args.get("view", "")))
    return {"error": f"Unknown view tool {name!r}"}
