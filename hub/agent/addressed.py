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

import asyncio
import logging
import os
import re

log = logging.getLogger(__name__)

_WORD = re.compile(r"[A-Za-z0-9']+")  # digits too: "list like 20" ends on 20, not on a dangling "like"
PEOPLE = {"dude", "bruh", "bro", "guys", "man", "mate", "buddy", "fam"}  # said to a person, not the gator
DANGLING = {"or", "and", "so", "but", "like", "because", "um", "uh", "the", "a", "to", "if", "i", "then"}  # cut off mid-sentence
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


TRAILING = {"um", "uh", "er", "erm", "hmm"}
COMPLETE = {"louder", "quieter", "softer", "pause", "resume", "undo", "redo", "play", "continue", "reset", "launch",
            "fly", "explode", "again", "mute", "unmute", "stop", "yes", "no", "sure", "thanks", "faster", "slower"}  # "...clear the notes, um": a finished request with a trailing sound


def unfinished(text: str, last_reply: str | None = None, age_s: float | None = None, *,
               complete: bool = True) -> str | None:
    """Free speech (nobody else in the room): a turn Albie should not answer yet, because people pause mid-thought.
    "partial": it trails off ("can you open the, um") and the next segment finishes it; grok-transcribe ends a finished
    sentence with . ? or ! and leaves a cut-off one bare ("Go to the", "How can I get": every bare turn on 2026-10-24
    was a fragment; complete=False for a live partial, which has no end mark yet). "filler": only "yeah" / "um",
    unless it answers the question Albie just asked. None: answer it."""
    words = _words(text)
    if not words:
        return None
    asked = bool(last_reply and last_reply.rstrip().endswith("?")) and age_s is not None and age_s <= REPLY_S
    if set(words) <= NOISE | TRAILING | {"nah"}:
        return None if asked else "filler"
    if words[-1] in TRAILING:  # "...clear the notes, um" (live 01:39, no end mark): judged by the word before it
        while words and words[-1] in TRAILING:
            words.pop()
        if len(words) <= 3:
            return "partial"  # a short opener then a sound: "And then, um", "Can you, uh" (lab 05:32)
    elif len(words) <= 2 and (words[-1] in COMPLETE or (words[0] in COMPLETE and words[-1] in {"it", "that"})):
        return None  # "Louder" / "Undo" come back bare too: a whole command, not a half-thought (lab 07:55: 6 s wait)
    else:
        end = text.rstrip(" \"')")
        if end.endswith(("...", "…")) or (complete and not end.endswith((".", "?", "!"))):
            return "partial"
    return "partial" if words and words[-1] in DANGLING else None


async def judge(text: str, last_reply: str | None = None, age_s: float | None = None, *, facing: bool = False) -> bool:
    """An unnamed turn. facing (VOICE_FACING=1 only): for Albie unless the rules rule it out. Otherwise only a bare
    answer within REPLY_S of a question he asked ("Want me to look?" "Yes please.")."""
    if facing:
        return rules(text, last_reply) is not False
    asked = bool(last_reply and last_reply.rstrip().endswith("?"))
    return asked and age_s is not None and age_s <= REPLY_S and is_answer(text)


FILM_MODEL = os.getenv("VOICE_FILM_JUDGE_MODEL", "claude-haiku-5-5")
FILM_PROMPT = ("A person sits at a desk in VR with an assistant called Albie. A movie is playing on a big screen next to "
               "them, and the microphone hears the movie too. Albie's last line was: {last!r}. The microphone heard: "
               "{text!r}. Reply with one word: user (the person talking to Albie: a request, a question, or an answer "
               "to him) or film (movie dialogue, or anything not said to Albie).")
_film_client = None


async def said_to_albie(text: str, last_reply: str | None = None) -> bool:
    """The movie is on (agent/wake.py name_needed): the person, or the film's dialogue? Claude Haiku decides in about a
    second. Any error counts as the person: an answered film line beats a deaf Albie (lab 2026-10-25: a word list
    alone dropped "Is it still stable?", "Fix it." and "Yes, apply it." while the movie was on)."""
    global _film_client
    try:
        import anthropic

        if _film_client is None:
            _film_client = anthropic.AsyncAnthropic()
        verdict = ""
        for _ in range(2):  # lab 07:36: one empty answer let a film line through; ask once more
            msg = await asyncio.wait_for(_film_client.messages.create(
                model=FILM_MODEL, max_tokens=16,
                messages=[{"role": "user", "content": FILM_PROMPT.format(last=last_reply or "", text=text)}]), 2.0)
            verdict = "".join(getattr(b, "text", "") for b in msg.content).strip().lower()
            if "film" in verdict or "user" in verdict:
                break
        log.info("film judge: %r -> %s", text[:80], verdict or f"(no answer, {msg.stop_reason}: counted as film)")
        # No answer twice: film. Desk requests and the rest of a held half-thought never get here (voice.py addressed).
        return "user" in verdict
    except Exception as exc:  # noqa: BLE001 - fail open
        log.warning("film judge failed (%s): counting it as the user", exc)
        return True
