"""Was this said to Albie? The check for hands-free turns that don't name him.

Facing Albie or replying in his follow-up window used to count as saying his name, so at a shared desk he
answered teammates talking to each other ("Hey, listen to me, dude.", "I don't know, bruh."). Now such a turn
is judged on its transcript first (agent/wake.py WakeRouter.final):

1. ``rules``: clear cases with no model call. Empty, mid-sentence fragments ("...it wasn't supposed to show
   like the no messages thing or") and talk to a person ("dude", "bruh", "guys") are never for Albie; filler
   alone ("Yeah.", "Damn.") only counts as the answer to a question Albie just asked.
2. A gated turn (a reply in Albie's follow-up window) that passes the rules is for him unless Claude says
   otherwise within GATED_TIMEOUT_S: it came right after he spoke, and Unity drops a hands-free turn with no
   stt.final within 6 s, so a slow or failed model call means "answer".
3. A facing turn (wake turn, user looked at Albie) is the doubtful one: Claude (``ADDRESSED_MODEL``, default
   Haiku) reads Albie's last line and the sentence and says yes or no within JUDGE_TIMEOUT_S. Optional: needs
   the ``anthropic`` package and ANTHROPIC_API_KEY; ``warm()`` loads it before the first turn.
4. ``guess`` when there is no model, it fails or it is too slow: a sentence that opens like a request.

ADDRESSED_JUDGE=rules skips the model (rules + guess only).
"""

from __future__ import annotations

import asyncio
import importlib.util
import logging
import os
import re
from collections.abc import Awaitable, Callable
from typing import Any

log = logging.getLogger(__name__)

JUDGE_TIMEOUT_S = 2.5  # warm Haiku answers in ~0.7 s from the venue Wi-Fi; a cold connection took ~1.5-3 s
GATED_TIMEOUT_S = 1.2  # a follow-up reply must reach Unity within its 6 s stt.final limit: past this, answer it
_WORD = re.compile(r"[A-Za-z0-9']+")  # digits too: "list like 20" ends on 20, not on a dangling "like"
PEOPLE = {"dude", "bruh", "bro", "guys", "man", "mate", "buddy", "fam"}  # said to a person, not the gator
DANGLING = {"or", "and", "so", "but", "like", "because", "um", "uh", "the", "a", "to", "if", "i"}  # cut off mid-sentence
FILLER = {"oh", "so", "um", "uh", "okay", "ok", "hey", "well", "alright", "and"}
NOISE = FILLER | {"yeah", "yes", "yep", "no", "nope", "damn", "wow", "lol", "hmm", "huh", "right", "sure", "cool",
                  "nice", "mhm", "ah", "oops", "what", "whoa", "woah", "dang", "haha", "omg"}  # alone: nothing to answer
REQUEST_START = {
    "can", "could", "would", "will", "please", "open", "close", "show", "tell", "what", "what's", "how", "why",
    "where", "when", "who", "which", "is", "are", "does", "go", "run", "stop", "make", "change", "set", "give",
    "switch", "help", "explain", "find", "read", "check", "want", "let's", "start", "hide", "zoom", "undo",
}
SYSTEM = (
    "Albie is a voice assistant, a small cartoon alligator on a desk in a shared room. People at the desk also "
    "talk to each other and think aloud. A microphone heard the sentence below while someone was looking at "
    "Albie or just after Albie spoke. Decide whether it was said TO Albie: a request, a question or a reply "
    "meant for him. Talk to another person, comments about Albie, and thinking aloud are not. "
    "Answer with exactly one word: yes or no."
)

Ask = Callable[[str, str | None, float | None], Awaitable[bool]]


def _words(text: str) -> list[str]:
    return [w.lower() for w in _WORD.findall(text)]


def rules(text: str, last_reply: str | None = None) -> bool | None:
    """False for what is never for Albie; None when it takes judgment."""
    words = _words(text)
    if not words:
        return False
    if text.strip().startswith(("...", "…")) or words[-1] in DANGLING:
        return False  # a fragment of something longer
    if words[0] in PEOPLE or words[-1] in PEOPLE:
        return False
    if set(words) <= NOISE:
        return bool(last_reply and last_reply.rstrip().endswith("?"))  # "Yeah." answers "Want me to open it?"
    return None


def guess(text: str) -> bool:
    """No model: a sentence that opens like a request ("can you...", "open...", "what...")."""
    first = next((w for w in _words(text) if w not in FILLER), "")
    return first in REQUEST_START


def prompt(text: str, last_reply: str | None, age_s: float | None) -> str:
    said = f'Albie\'s last line ({age_s:.0f} s ago): "{last_reply}"\n' if last_reply and age_s is not None else ""
    return f'{said}Heard: "{text}"'


_client: Any = None


def warm() -> None:
    """Load the SDK and make the client now, so the first judged turn doesn't pay for it (~2 s)."""
    global _client
    if _client is None and default_ask() is not None:
        import anthropic

        _client = anthropic.AsyncAnthropic(timeout=JUDGE_TIMEOUT_S, max_retries=0)


async def ask_claude(text: str, last_reply: str | None, age_s: float | None) -> bool:
    global _client
    import anthropic  # optional dependency: only imported when installed (see default_ask)

    if _client is None:
        _client = anthropic.AsyncAnthropic(timeout=JUDGE_TIMEOUT_S, max_retries=0)
    response = await _client.messages.create(
        model=os.environ.get("ADDRESSED_MODEL", "claude-haiku-5-5"),
        max_tokens=5,
        system=SYSTEM,
        thinking={"type": "disabled"},  # one word, as fast as possible
        output_config={"effort": "low"},
        messages=[{"role": "user", "content": prompt(text, last_reply, age_s)}],
    )
    if response.stop_reason == "refusal":
        raise RuntimeError("refused")
    answer = next((b.text for b in response.content if b.type == "text"), "")
    return answer.strip().lower().startswith("y")


def default_ask() -> Ask | None:
    """Claude when the package and a key are there (and ADDRESSED_JUDGE isn't "rules"), else None."""
    if os.environ.get("ADDRESSED_JUDGE", "").lower() == "rules" or not os.environ.get("ANTHROPIC_API_KEY"):
        return None
    return ask_claude if importlib.util.find_spec("anthropic") else None


async def judge(text: str, last_reply: str | None = None, age_s: float | None = None, *, gated: bool = False,
                ask: Ask | None = None) -> bool:
    """True if the sentence was said to Albie. gated: a reply in his follow-up window (no model call)."""
    clear = rules(text, last_reply)
    if clear is not None:
        return clear
    ask = ask or default_ask()
    if gated:
        if ask is None:
            return True
        try:
            return await asyncio.wait_for(ask(text, last_reply, age_s), GATED_TIMEOUT_S)
        except Exception as exc:  # noqa: BLE001 - too slow for Unity's limit: a reply right after Albie spoke counts
            log.info("addressed: judge failed on a reply (%s), answering", type(exc).__name__)
            return True
    if ask is None:
        return guess(text)
    try:
        return await asyncio.wait_for(ask(text, last_reply, age_s), JUDGE_TIMEOUT_S)
    except Exception as exc:  # noqa: BLE001 - timeout, network, refusal: fall back to the word rules
        log.info("addressed: judge failed (%s), guessing", type(exc).__name__)
        return guess(text)
