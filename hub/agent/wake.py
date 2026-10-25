"""Say a gator's name and it listens: routing for Unity's always-on, VAD-gated speech (WakeListener).

Unity streams each speech segment it hears as a normal turn marked ``stt.start {wake: true}``. The transcript is
held back (no stt.*, no pet.state for room chatter) until we know who it is for; the gator's ``pet.state listening``
goes out only when a segment turns out to be a request for it:

    "Hey Albie, what's in ws.py?"   -> Albie answers (voice: Grok responds to the turn; else agent.ask)
    "Tester, run the hub tests"     -> crew.assign("tester", "run the hub tests") (hatches the tester if needed)
    "Hey Scout"                     -> wake.listen {pet_id: "scout"}: the next segment goes to the Scout, no name needed
    "...a bit later we can eat"     -> nobody: dropped (deleted from the voice conversation)
    "open the ML panel" + facing    -> Albie answers: looking at him counts as his name
    "I don't know, bruh." + facing  -> dropped: agent/addressed.py word rules (talk to a person, fragments, filler)

Name only (Dev, 2026-10-24): every hands-free sentence says "Albie"; an unnamed turn is dropped. With
VOICE_FACING=1 a turn whose speaker faced Albie at onset (stt.start {facing: true}) counts as named, minus the
agent/addressed.py word rules. Buttons (Space, A, the whistle) never need the name.

A name counts when it opens the sentence (after an optional "hey / ok / hi / yo"), or closes it after a comma
("thanks, Albie"), so "wait a bit" never wakes Albie. The lead's name is said "AL-bee"; at the start it may also
come back as two words ("Al bee, ..."). ``stt.start {wake: true, pet_id}`` after a wake.listen names
the target up front and needs no name.
"""

from __future__ import annotations

import asyncio
import logging
import os
import re
from collections.abc import Awaitable, Callable
from dataclasses import dataclass

from . import addressed
from . import events as ev
from .crew import TYPES, crew

log = logging.getLogger(__name__)

# Dev, 2026-10-24: name only. At the desk Albie sits in front of the VS Code screen, so looking at the screen read as
# looking at him (live 19:29-19:33: angle 5-17 degrees either way) and side talk was answered. VOICE_FACING=1 brings
# "looking at him counts as his name" back.
FACING_COUNTS = os.getenv("VOICE_FACING", "0") == "1"

GREETINGS = {"hey", "hi", "hello", "ok", "okay", "yo", "oi", "oh", "um", "uh", "so", "yes", "yeah", "yep", "alright",
             "well", "and", "please"}  # "Yes, Albie. Find..." (live 19:54) was dropped with only the first set
# Common mis-hearings, per pet id ("bit" = the lead, spoken name Albie). Keep these unambiguous English-wise.
# A name only counts at the start of a sentence (after a greeting) or after a comma, so "Scott" here means a
# sentence addressed to someone called Scott goes to the Scout; drop an alias if that bites in your room.
ALIASES: dict[str, set[str]] = {
    "bit": {"albie", "albee", "alby", "albi", "elbie", "abby"},  # "abby", "albi": live grok-transcribe for "Albie"
    "scout": {"scout", "scouts", "scott", "scoot", "skout"},
    "builder": {"builder", "builders", "bilder"},
    "fixer": {"fixer", "fixers", "fixa", "fixor", "fisher"},
    "tester": {"tester", "testers", "testa", "tessa"},
    "reviewer": {"reviewer", "reviewers", "reviewa"},
    "scribe": {"scribe", "scribes", "scribed", "scribble"},
    "shipper": {"shipper", "shippers", "skipper", "chipper"},
    "designer": {"designer", "designers", "designa"},
    "sentinel": {"sentinel", "sentinels", "centinel", "sentinal"},
}
# "Albie" transcribed as two words ("Al bee", "al be", "al b"): only where a name may open the sentence.
SPLIT_FIRST, SPLIT_SECOND = "al", {"bee", "be", "b"}
# "-er" names the transcriber splits in two ("Fix her, run the tests"). Folded back into one word only where a
# name can stand: opening the sentence (after a greeting) and set off by punctuation, or closing it after a comma,
# so "Albie, test her code" keeps its words.
JOINED: dict[str, str] = {"build her": "builder", "fix her": "fixer", "test her": "tester",
                          "review her": "reviewer", "ship her": "shipper", "design her": "designer"}
_JOINED_ALT = "|".join(re.escape(k) for k in JOINED)
_JOINED_OPEN = re.compile(r"^(\W*(?:" + "|".join(GREETINGS) + r")\s+){0,2}(" + _JOINED_ALT + r")(?=\s*(?:[,.!?;:]|$))", re.I)
_JOINED_CLOSE = re.compile(r"(?<=,)(\s*)(" + _JOINED_ALT + r")(\W*)$", re.I)


def _fold(text: str) -> str:
    text = _JOINED_OPEN.sub(lambda m: (m.group(1) or "") + JOINED[m.group(2).lower()], text, count=1)
    return _JOINED_CLOSE.sub(lambda m: m.group(1) + JOINED[m.group(2).lower()] + m.group(3), text, count=1)
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


_SENTENCE = re.compile(r"\S.*?(?:[.?!]+(?=\s|$)|$)")  # a dot inside "ws.py" doesn't end a sentence


def parse(text: str, lead_pet_id: str = "bit") -> tuple[str | None, str]:
    """(pet_id the turn is addressed to or None, the turn without the name). The name may open any sentence of the
    turn ("Switch to code. Albie, switch to code.", live 19:55); what came before it stays in the rest unless it is
    only greetings ("Okay. Albie" is just his name)."""
    for m in _SENTENCE.finditer(text):
        pid, rest = _parse_one(m.group(), lead_pet_id)
        if pid is not None:
            before = text[:m.start()].strip()
            if all(w.group().lower() in GREETINGS for w in _WORD.finditer(before)):
                before = ""
            after = text[m.end():].strip()
            return pid, " ".join(p for p in (before, rest, after) if p)
    return None, text.strip()


def _parse_one(text: str, lead_pet_id: str) -> tuple[str | None, str]:
    """One sentence: (pet_id, the sentence without the name), or (None, sentence)."""
    names = _alias_map(lead_pet_id)
    text = _fold(text)
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
        self.gated = False  # a look-to-talk / follow-up reply (stt.start {require_name}): needs the name
        # Was an unnamed sentence said to the lead? The voice session sets one with his last line as context.
        self.judge: Callable[[str, bool], Awaitable[bool]] = lambda text, facing: addressed.judge(text, facing=facing)
        self.judged: bool | None = None  # last final's verdict (None = no judge call), for the turn: log line

    def begin(self, target: str | None, facing: bool = False, gated: bool = False) -> None:
        self.target = target or None
        self.facing, self.gated = facing, gated

    def crew_target(self, text: str) -> str | None:
        """The crew gator a non-wake turn (push-to-talk, hands-free, Bit's follow-up window) is really for, if any.
        Stricter than a wake turn: the name must be greeted ("Hey Tester ...") or set off ("Tester, ..."), so a
        question to Bit that merely starts with a type name ("builder pattern?") stays Bit's."""
        pid, _ = parse(text, self.lead)
        if pid is None or pid == self.lead:
            return None
        names = _alias_map(self.lead)
        text = _fold(text)
        words = list(_WORD.finditer(text))
        first = next((w for w in words[:3] if names.get(w.group().lower()) == pid), None)
        if first is None or first is not words[0]:
            return pid  # greeted, or the "..., Tester" vocative (parse already required the comma)
        after = text[first.end():].lstrip()
        return pid if not after or after[0] in ",.!?;:" else None

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
        ask = pid is None and bool(text.strip())  # cheap: the judge only says yes to a look or a bare answer
        self.judged = None
        if ask:
            self.judged = await self.judge(text, facing and FACING_COUNTS)
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
