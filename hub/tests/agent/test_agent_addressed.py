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
def test_looking_at_albie_counts_as_his_name(text: str) -> None:
    assert asyncio.run(addressed.judge(text)) is True


@pytest.mark.parametrize("text", ["Okay yeah, just list like 20.", "It would be cool if you could control the screen."])
def test_a_gated_turn_needs_his_name(text: str) -> None:
    """Dev's rule: every sentence says "Albie" unless you look at him."""
    assert asyncio.run(addressed.judge(text, "Here are some fun facts.", gated=True)) is False
