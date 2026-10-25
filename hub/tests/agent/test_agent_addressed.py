"""Was this said to Albie? (agent/addressed.py). Sentences are from the live Quest logs (2026-10-24)."""

from __future__ import annotations

import asyncio

import pytest

from agent import addressed


@pytest.mark.parametrize("text", [
    "", "   ",
    "... bought in, and it was, it wasn't supposed to show like the no messages thing or",
    "Hey, listen to me, dude.",
    "I don't know, bruh.",
    "He's not into that, bro.",
    "Why is this funny to me, bro? I feel like a",
    "Logic right now with the agent is bugging. It wasn't working so",
    "Can I",
])
def test_rules_drop_what_is_never_for_albie(text: str) -> None:
    assert addressed.rules(text) is False


@pytest.mark.parametrize("text, last, said_to_albie", [
    ("Yeah.", "Want me to open it?", True),      # the answer to his question
    ("Yeah.", "Here are some fun facts.", False),  # live 18:31:35
    ("Damn.", None, False),
    ("Whoa.", None, False),                       # live 18:42:38
])
def test_filler_alone_only_answers_his_question(text: str, last: str | None, said_to_albie: bool) -> None:
    assert addressed.rules(text, last) is said_to_albie


@pytest.mark.parametrize("text", ["Open the ML panel.", "Okay yeah, just list like 20.", "Can we get a reset button on CAD?"])
def test_looking_at_albie_counts_as_his_name_only_with_voice_facing(text: str) -> None:
    assert asyncio.run(addressed.judge(text, facing=True)) is True
    assert asyncio.run(addressed.judge(text)) is False  # name only (default)


@pytest.mark.parametrize("text, last, age, said_to_albie", [
    ("Yes please.", "Want me to look at it?", 3.0, True),
    ("Sure, go ahead.", "Should I open train.py?", 5.0, True),
    ("No thanks.", "Want the traceback?", 2.0, True),
    ("Yes, fix it.", "Line 9 broke the rocket. I kept the last good one. Want me to fix it?", 6.0, True),
    ("Yes please.", "I opened it.", 3.0, False),                 # he didn't ask
    ("Yes please.", "Want me to look at it?", 30.0, False),      # too late
    ("Yes, open train.py.", "Want me to look at it?", 3.0, False),  # more than an answer: say "Albie"
    ("It would be cool if you could control the screen.", "Anything else?", 2.0, False),
])
def test_a_bare_answer_to_his_question_needs_no_name(text: str, last: str, age: float, said_to_albie: bool) -> None:
    assert asyncio.run(addressed.judge(text, last, age)) is said_to_albie


@pytest.mark.parametrize("text, last, age, held", [
    ("Can you open the, um", None, None, "partial"),  # mid-thought: the next segment finishes it
    ("Albie, make the arms longer and", None, None, "partial"),
    ("Um, Albie, close all of this, clear the notes, um", None, None, None),  # finished, then a sound
    ("Yeah, what, yeah, um.", None, None, "filler"),
    ("Nah.", "Want me to open it?", 3.0, None),  # answers his question
    ("Nah.", "Want me to open it?", 30.0, "filler"),
    ("Open up Iron Man now.", None, None, None),
    ("How can I get", None, None, "partial"),  # live 02:05: no end mark, cut off
    ("Is the PyTorch", None, None, "partial"),
    ("I think...", None, None, "partial"),
])
def test_free_speech_holds_half_thoughts(text: str, last: str | None, age: float | None, held: str | None) -> None:
    assert addressed.unfinished(text, last, age) == held
