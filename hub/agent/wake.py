"""Say a gator's name and it listens: routing for Unity's always-on, VAD-gated speech (WakeListener).

Unity streams each speech segment it hears as a normal turn marked ``stt.start {wake: true}``. The transcript is
held back (no stt.*, no pet.state for room chatter) until we know who it is for; the gator's ``pet.state listening``
goes out only when a segment turns out to be a request for it:

    "Hey Albie, what's in ws.py?"   -> Albie answers (voice: Grok responds to the turn; else agent.ask)
    "Tester, run the hub tests"     -> crew.assign("tester", "run the hub tests") (hatches the tester if needed)
    "Hey Scout"                     -> wake.listen {pet_id: "scout"}: the next segment goes to the Scout, no name needed
    "...a bit later we can eat"     -> nobody: dropped (deleted from the voice conversation)
    "open the ML panel" + facing    -> judged (agent/addressed.py): Albie answers only if it was said to him
    "I don't know, bruh." + facing  -> dropped: talk to a person, not to Albie

Unnamed turns are judged, never answered on facing alone: at a shared desk people face Albie while talking to each
other. A turn is judged when the user faced Albie at speech onset (stt.start {facing: true}) or it is a gated turn
(begin(gated=True): a look-to-talk / follow-up reply, stt.start {require_name: true}); any other unnamed turn is
dropped without a judge call.

A name counts when it opens the sentence (after an optional "hey / ok / hi / yo"), or closes it after a comma
("thanks, Albie"), so "wait a bit" never wakes Albie. The lead's name is said "AL-bee"; at the start it may also
come back as two words ("Al bee, ..."). ``stt.start {wake: true, pet_id}`` after a wake.listen names
the target up front and needs no name.
"""

from __future__ import annotations

import asyncio
import logging
import re
from collections.abc import Awaitable, Callable
from dataclasses import dataclass

from . import addressed
from . import events as ev
from .crew import TYPES, crew

log = logging.getLogger(__name__)

GREETINGS = {"hey", "hi", "hello", "ok", "okay", "yo", "oi", "oh", "um", "uh", "so"}
# Common mis-hearings, per pet id ("bit" = the lead, spoken name Albie). Keep these unambiguous English-wise.
ALIASES: dict[str, set[str]] = {
    "bit": {"albie", "albee", "alby", "albi", "elbie", "abby"},  # "abby", "albi": live grok-transcribe for "Albie"
    "fixer": {"fixer", "fixa", "fixor"},
    "scribe": {"scribe", "scribes"},
    "shipper": {"shipper", "skipper"},
    "sentinel": {"sentinel", "centinel"},
}
# "Albie" transcribed as two words ("Al bee", "al be", "al b"): only where a name may open the sentence.
SPLIT_FIRST, SPLIT_SECOND = "al", {"bee", "be", "b"}
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
    if i + 1 < len(words) and words[i].group().lower() == SPLIT_FIRST and words[i + 1].group().lower() in SPLIT_SECOND:
        return lead_pet_id, _LEADING.sub("", text[words[i + 1].end():]).strip()
    if len(words) >= 2:  # vocative at the end: "..., Albie?"
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
        self.facing = False  # from stt.start {wake, facing}: user faced the lead at speech onset
        self.gated = False  # a look-to-talk / follow-up reply (stt.start {require_name}): judged like facing
        # Was an unnamed sentence said to the lead? The voice session sets one with his last line as context.
        self.judge: Callable[[str, bool], Awaitable[bool]] = lambda text, gated: addressed.judge(text, gated=gated)
        self.judged: bool | None = None  # last final's verdict (None = no judge call), for the turn: log line

    def begin(self, target: str | None, facing: bool = False, gated: bool = False) -> None:
        self.target = target or None
        self.facing, self.gated = facing, gated

    def _who(self, text: str, target: str | None) -> tuple[str | None, str]:
        pid, rest = parse(text, self.lead)
        if pid is None and target is not None:
            return target, text.strip()
        return pid, rest

    async def partial(self, text: str) -> None:
        pid, _ = self._who(text, self.target)
        if pid is not None:  # addressed: now the gator may show what it's hearing
            await self._emit_for(pid)(ev.stt_partial(pid, text))

    async def final(self, text: str, flags: tuple[str | None, bool, bool] | None = None) -> Decision:
        """flags: (target, facing, gated) of an earlier turn whose transcript arrived late (voice.py); None = this
        turn's, which are then reset."""
        target, facing, gated = flags if flags is not None else (self.target, self.facing, self.gated)
        if flags is None:
            self.target, self.facing, self.gated = None, False, False
        pid, rest = self._who(text, target)
        ask = pid is None and (facing or gated) and bool(text.strip())
        self.judged = None
        if ask:
            # ponytail: awaited on the voice reader (up to addressed.JUDGE_TIMEOUT_S); nothing else is due for this turn
            self.judged = await self.judge(text, gated)
            if self.judged:
                pid, rest = self.lead, text.strip()
        if pid is None:
            if text.strip():
                log.debug("wake: nobody addressed in %r", text)
            return Decision(None, text, False)
        log.info("wake: %r -> %s", text, pid)
        if pid == self.lead:
            if not rest:  # just "Hey Albie": open a hands-free turn on the Unity side
                await self._send_owner(ev.wake_listen(pid))
                return Decision(pid, rest, False)
            # Decided: this segment is for Albie. Only now does he look like he is listening (the stt.* events of a
            # wake turn carry no pet.state companion, see ws.py), and thinking follows right behind.
            await self._emit_for(pid)(ev.pet_state(pid, "listening"))
            await self._emit_for(pid)(ev.stt_final(pid, text, voice=True))
            return Decision(pid, rest, True)
        # A crew gator (hatched on demand).
        if not rest:
            self._spawn(self._summon(pid))
        else:
            await self._emit_for(pid)(ev.pet_state(pid, "listening"))
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
