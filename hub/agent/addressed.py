"""Was this said to Albie? The check for a turn that doesn't name him.

Dev's rule (2026-10-24, after a day of side talk answered): Albie answers only when you say his name ("Albie, ...",
"Hey Albie ..."), press a button, or look at him while you talk. Looking at him counts as saying his name, with
no model call; the word rules below only drop what is never a request even then:

* empty, or a fragment cut off mid-sentence ("...the no messages thing or", "Can I");
* talk to a person ("dude", "bruh", "guys");
* filler alone ("Yeah.", "Whoa."), unless Albie just asked a question.

Name only (default): an unnamed turn is never for him, except a bare answer ("yes", "sure", "no thanks") within
REPLY_S of a question he asked, so "Want me to look?" can be answered without his name.
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


REPLY_S = 10.0  # after Albie asks a question, a bare "yes" / "sure" / "no" this soon is the answer, no name needed
ANSWER = {"yes", "yeah", "yep", "yup", "sure", "ok", "okay", "no", "nope", "nah", "please", "do", "it", "go", "ahead",
          "thanks", "thank", "you", "sounds", "good", "great", "alright", "right", "not", "now", "fix", "look"}
ANSWER_CORE = {"yes", "yeah", "yep", "yup", "sure", "ok", "okay", "no", "nope", "nah", "go", "do", "fix"}


def is_answer(text: str) -> bool:
    """Only answer words: "yes please", "sure, go ahead", "no thanks", "do it"."""
    words = set(_words(text))
    return bool(words) and words <= ANSWER and bool(words & ANSWER_CORE)


async def judge(text: str, last_reply: str | None = None, age_s: float | None = None, *, facing: bool = False) -> bool:
    """An unnamed turn. facing (VOICE_FACING=1 only): for Albie unless the rules rule it out. Otherwise only a bare
    answer within REPLY_S of a question he asked ("Want me to look?" "Yes please.")."""
    if facing:
        return rules(text, last_reply) is not False
    asked = bool(last_reply and last_reply.rstrip().endswith("?"))
    return asked and age_s is not None and age_s <= REPLY_S and is_answer(text)
