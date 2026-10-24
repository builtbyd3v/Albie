"""Was this said to Albie? The check for a turn that doesn't name him.

Dev's rule (2026-10-24, after a day of side talk answered): Albie answers only when you say his name ("Albie, ...",
"Hey Albie ..."), press a button, or look at him while you talk. Looking at him counts as saying his name, with
no model call; the word rules below only drop what is never a request even then:

* empty, or a fragment cut off mid-sentence ("...the no messages thing or", "Can I");
* talk to a person ("dude", "bruh", "guys");
* filler alone ("Yeah.", "Whoa."), unless Albie just asked a question.

A gated turn (stt.start {require_name}: Unity's follow-up window while you look elsewhere) needs the name: an
unnamed one is never for him.
"""

from __future__ import annotations

import re

_WORD = re.compile(r"[A-Za-z0-9']+")  # digits too: "list like 20" ends on 20, not on a dangling "like"
PEOPLE = {"dude", "bruh", "bro", "guys", "man", "mate", "buddy", "fam"}  # said to a person, not the gator
DANGLING = {"or", "and", "so", "but", "like", "because", "um", "uh", "the", "a", "to", "if", "i"}  # cut off mid-sentence
FILLER = {"oh", "so", "um", "uh", "okay", "ok", "hey", "well", "alright", "and"}
NOISE = FILLER | {"yeah", "yes", "yep", "no", "nope", "damn", "wow", "lol", "hmm", "huh", "right", "sure", "cool",
                  "nice", "mhm", "ah", "oops", "what", "whoa", "woah", "dang", "haha", "omg"}  # alone: nothing to answer


def _words(text: str) -> list[str]:
    return [w.lower() for w in _WORD.findall(text)]


def rules(text: str, last_reply: str | None = None) -> bool | None:
    """False for what is never for Albie; None when nothing rules it out."""
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


async def judge(text: str, last_reply: str | None = None, *, gated: bool = False) -> bool:
    """An unnamed turn: for Albie if you looked at him and the rules don't rule it out. Gated turns need his name."""
    if gated:
        return False
    return rules(text, last_reply) is not False
