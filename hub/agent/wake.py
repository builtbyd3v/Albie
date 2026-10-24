"""Say a gator's name and it listens: routing for Unity's always-on, VAD-gated speech (WakeListener).

Unity streams each speech segment it hears as a normal turn marked ``stt.start {wake: true}``. The transcript is
held back (no stt.* for room chatter) until we know who it is for:

    "Hey Bit, what's in ws.py?"     -> Bit answers (voice: Grok responds to the turn; else agent.ask)
    "Tester, run the hub tests"     -> crew.assign("tester", "run the hub tests") (hatches the tester if needed)
    "Hey Scout"                     -> wake.listen {pet_id: "scout"}: the next segment goes to the Scout, no name needed
    "...a bit later we can eat"     -> nobody: dropped (deleted from the voice conversation)

A name counts when it opens the sentence (after an optional "hey / ok / hi / yo"), or closes it after a comma
("thanks, Bit"), so "wait a bit" never wakes Bit. ``stt.start {wake: true, pet_id}`` after a wake.listen names
the target up front and needs no name.
"""

from __future__ import annotations

import asyncio
import logging
import re
from collections.abc import Awaitable, Callable
from dataclasses import dataclass

from . import events as ev
from .crew import TYPES, crew

log = logging.getLogger(__name__)

GREETINGS = {"hey", "hi", "hello", "ok", "okay", "yo", "oi", "oh", "um", "uh", "so"}
# Common mis-hearings, per pet id ("bit" = the lead). Keep these unambiguous English-wise.
ALIASES: dict[str, set[str]] = {
    "bit": {"bit", "bitt", "bitty", "bits"},
    "fixer": {"fixer", "fixa", "fixor"},
    "scribe": {"scribe", "scribes"},
    "shipper": {"shipper", "skipper"},
    "sentinel": {"sentinel", "centinel"},
}
_WORD = re.compile(r"[A-Za-z']+")
_LEADING = re.compile(r"^[\s,.!?;:\-]+")


def _alias_map(lead_pet_id: str) -> dict[str, str]:
    out: dict[str, str] = {}
    for t in TYPES.values():
        for a in ALIASES.get(t.id, {t.id, t.name.lower()}):
            out[a] = t.id
    for a in ALIASES["bit"]:
        out[a] = lead_pet_id
    return out


def parse(text: str, lead_pet_id: str = "bit") -> tuple[str | None, str]:
    """(pet_id the sentence is addressed to or None, the rest of the sentence without the name)."""
    names = _alias_map(lead_pet_id)
    words = list(_WORD.finditer(text))
    i = 0
    while i < len(words) - 1 and i < 2 and words[i].group().lower() in GREETINGS:
        i += 1
    if i < len(words) and words[i].group().lower() in names:
        return names[words[i].group().lower()], _LEADING.sub("", text[words[i].end():]).strip()
    if len(words) >= 2:  # vocative at the end: "..., Bit?"
        last = words[-1]
        if last.group().lower() in names and text[:last.start()].rstrip().endswith(","):
            return names[last.group().lower()], text[:last.start()].rstrip().rstrip(",").strip()
    return None, text.strip()


@dataclass
class Decision:
    pet_id: str | None
    rest: str
    respond: bool  # the lead should answer this turn itself (voice: response.create; text: agent.ask)


Emit = Callable[[ev.Event], Awaitable[None]]


class WakeRouter:
    """Per socket. partial() decides whether to show a caption yet; final() decides who gets the turn."""

    def __init__(self, *, lead_pet_id: str, emit_for: Callable[[str], Emit], send_owner: Emit,
                 spawn: Callable[[Awaitable[object]], None]) -> None:
        self.lead = lead_pet_id
        self._emit_for = emit_for  # pet_id -> broadcast Emitter (pet.state companions)
        self._send_owner = send_owner  # owner socket only
        self._spawn = spawn  # fire-and-forget on the socket's task set
        self.target: str | None = None  # from stt.start {wake, pet_id}: addressed up front

    def begin(self, target: str | None) -> None:
        self.target = target or None

    def _who(self, text: str) -> tuple[str | None, str]:
        pid, rest = parse(text, self.lead)
        if pid is None and self.target is not None:
            return self.target, text.strip()
        return pid, rest

    async def partial(self, text: str) -> None:
        pid, _ = self._who(text)
        if pid is not None:  # addressed: now the gator may show what it's hearing
            await self._emit_for(pid)(ev.stt_partial(pid, text))

    async def final(self, text: str) -> Decision:
        pid, rest = self._who(text)
        self.target = None
        if pid is None:
            if text.strip():
                log.debug("wake: nobody addressed in %r", text)
            return Decision(None, text, False)
        log.info("wake: %r -> %s", text, pid)
        if pid == self.lead:
            if not rest:  # just "Hey Bit": open a hands-free turn on the Unity side
                await self._send_owner(ev.wake_listen(pid))
                return Decision(pid, rest, False)
            await self._emit_for(pid)(ev.stt_final(pid, text, voice=True))
            return Decision(pid, rest, True)
        # A crew gator (hatched on demand).
        if not rest:
            self._spawn(self._summon(pid))
        else:
            await self._emit_for(pid)(ev.stt_final(pid, rest))
            self._spawn(self._assign(pid, rest))
        return Decision(pid, rest, False)

    async def _summon(self, pid: str) -> None:
        if crew.get(pid) is None:
            out = await crew.spawn(pid)
            if "error" in out:
                await self._emit_for(pid)(ev.error(pid, str(out["error"])))
                return
            await asyncio.sleep(0.3)  # let the new gator's bridge attach before it's told to listen
        await self._send_owner(ev.wake_listen(pid))

    async def _assign(self, pid: str, task: str) -> None:
        out = await crew.assign(pid, task)
        if "error" in out:
            await self._emit_for(pid)(ev.error(pid, str(out["error"])))
