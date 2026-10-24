"""Tiny in-process broadcast bus: every connected /ws socket gets every event.

This is the only module-level mutable state in hub/agent (the ``bus`` singleton).
"""

from __future__ import annotations

import asyncio
import logging
import os
from collections.abc import Awaitable, Callable

from fastapi import WebSocket

from . import events as ev
from .events import Event, PetMode

log = logging.getLogger(__name__)

Emit = Callable[[Event], Awaitable[None]]
SEND_TIMEOUT_S = 5.0


class Bus:
    def __init__(self) -> None:
        self._sockets: dict[WebSocket, asyncio.Lock] = {}

    def add(self, ws: WebSocket) -> None:
        self._sockets[ws] = asyncio.Lock()

    def remove(self, ws: WebSocket) -> None:
        self._sockets.pop(ws, None)

    @property
    def count(self) -> int:
        return len(self._sockets)

    async def send(self, ws: WebSocket, event: Event) -> bool:
        """Send to one socket (serialized per socket). Returns False if it failed."""
        lock = self._sockets.get(ws)
        if lock is None:
            return False
        try:
            async with lock:
                await asyncio.wait_for(ws.send_text(event.to_json()), SEND_TIMEOUT_S)
            return True
        except Exception as exc:  # disconnected / slow client: drop it
            log.info("dropping socket after send failure: %s", exc)
            self.remove(ws)
            return False

    async def send_bytes(self, ws: WebSocket, data: bytes) -> bool:
        """Binary frame to one socket (speech audio is never broadcast), same per-socket lock."""
        lock = self._sockets.get(ws)
        if lock is None:
            return False
        try:
            async with lock:
                await asyncio.wait_for(ws.send_bytes(data), SEND_TIMEOUT_S)
            return True
        except Exception as exc:
            log.info("dropping socket after binary send failure: %s", exc)
            self.remove(ws)
            return False

    async def broadcast(self, event: Event) -> None:
        log.debug("bus -> %s %s", event.type, event.payload)
        for ws in list(self._sockets):
            await self.send(ws, event)


bus = Bus()


class Emitter:
    """Per-turn emit callable: adds companion pet.state, redacts paths, broadcasts.

    * Before each proposed event, sends ``pet.state`` if the mapped mode changed. Not for an empty
      ``stt.final`` (a dropped / cancelled / silent turn: nothing was heard, so the pet's state must
      not change), and never when ``companion=False`` (wake turns, agent/wake.py: the router sends
      ``pet.state listening`` itself once it knows the segment was for a gator).
    * Replaces the absolute repo path in any string payload field with "" so the
      raw path never reaches clients.
    * Optionally mirrors ``agent.answer_done`` as existing-contract ``pet.say``
      (``AGENT_EMIT_PET_SAY=1``).
    """

    def __init__(self, pet_id: str, redact: list[str] | None = None, target: Bus = bus, *,
                 companion: bool = True) -> None:
        self.pet_id = pet_id
        self._redact = [r.rstrip("/") + "/" for r in (redact or []) if r] + [
            r.rstrip("/") for r in (redact or []) if r
        ]
        self._bus = target
        self._companion = companion
        self._last_mode: PetMode | None = None
        self._emit_pet_say = os.getenv("AGENT_EMIT_PET_SAY", "0") == "1"

    def _scrub(self, event: Event) -> Event:
        if not self._redact:
            return event
        payload = dict(event.payload)
        for key, val in payload.items():
            if isinstance(val, str):
                for r in self._redact:
                    val = val.replace(r, "")
                payload[key] = val
        return Event(type=event.type, payload=payload)

    async def __call__(self, event: Event) -> None:
        mode = ev.PET_STATE_FOR_EVENT.get(event.type) if self._companion else None
        if event.type == "stt.final" and not str(event.payload.get("text") or "").strip():
            mode = None  # nothing heard: no state change for the pet
        if mode is not None and mode != self._last_mode:
            self._last_mode = mode
            await self._bus.broadcast(ev.pet_state(self.pet_id, mode))
        event = self._scrub(event)
        await self._bus.broadcast(event)
        if self._emit_pet_say and event.type == "agent.answer_done":
            await self._bus.broadcast(ev.pet_say(self.pet_id, event.payload.get("text", "")))
