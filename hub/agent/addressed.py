"""Was this said to Albie? The check for hands-free turns that don't name him.

Facing Albie or replying in his follow-up window used to count as saying his name, so at a shared desk he
answered teammates talking to each other ("Hey, listen to me, dude.", "I don't know, bruh."). Now such a turn
is judged on its transcript first (agent/wake.py WakeRouter.final):

1. ``rules``: clear cases with no model call. Empty, mid-sentence fragments ("...it wasn't supposed to show
   like the no messages thing or") and talk to a person ("dude", "bruh", "guys") are never for Albie.
2. Claude (``ADDRESSED_MODEL``, default Haiku) reads Albie's last line and the sentence and says yes or no,
   within JUDGE_TIMEOUT_S. Optional: needs the ``anthropic`` package and ANTHROPIC_API_KEY.
3. ``guess`` when there is no model, it fails or it is too slow: a short reply to a question Albie just asked,
   or a sentence that opens like a request ("can you...", "open...", "what...").

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

JUDGE_TIMEOUT_S = 1.5
SHORT_REPLY_WORDS = 4  # "yes please", "the left one": a reply to Albie's question needs no request shape
_WORD = re.compile(r"[A-Za-z']+")
PEOPLE = {"dude", "bruh", "bro", "guys", "man", "mate", "buddy", "fam"}  # said to a person, not the gator
DANGLING = {"or", "and", "so", "but", "like", "because", "um", "uh", "the", "a", "to", "if", "i"}  # cut off mid-sentence
FILLER = {"oh", "so", "um", "uh", "okay", "ok", "hey", "well", "alright", "and"}
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


def rules(text: str) -> bool | None:
    """False for what is never for Albie; None when it takes judgment."""
    words = _words(text)
    if not words:
        return False
    if text.strip().startswith(("...", "…")) or words[-1] in DANGLING:
        return False  # a fragment of something longer
    if words[0] in PEOPLE or words[-1] in PEOPLE:
        return False
    return None


def guess(text: str, last_reply: str | None) -> bool:
    """No model: a short answer to Albie's question, or a sentence that opens like a request."""
    words = _words(text)
    if last_reply and last_reply.rstrip().endswith("?") and len(words) <= SHORT_REPLY_WORDS:
        return True
    first = next((w for w in words if w not in FILLER), "")
    return first in REQUEST_START


def prompt(text: str, last_reply: str | None, age_s: float | None) -> str:
    said = f'Albie\'s last line ({age_s:.0f} s ago): "{last_reply}"\n' if last_reply and age_s is not None else ""
    return f'{said}Heard: "{text}"'


_client: Any = None


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


async def judge(text: str, last_reply: str | None = None, age_s: float | None = None, *,
                ask: Ask | None = None) -> bool:
    """True if the sentence was said to Albie."""
    clear = rules(text)
    if clear is not None:
        return clear
    ask = ask or default_ask()
    if ask is None:
        return guess(text, last_reply)
    try:
        return await asyncio.wait_for(ask(text, last_reply, age_s), JUDGE_TIMEOUT_S)
    except Exception as exc:  # noqa: BLE001 - timeout, network, refusal: fall back to the word rules
        log.info("addressed: judge failed (%s), guessing", type(exc).__name__)
        return guess(text, last_reply)
