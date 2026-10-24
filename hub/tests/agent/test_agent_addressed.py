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
    "Logic right now with the agent is bugging. It wasn't working so",
    "Can I",
])
def test_rules_drop_what_is_never_for_albie(text: str) -> None:
    assert addressed.rules(text) is False


@pytest.mark.parametrize("text", ["Open the ML panel.", "Did it say the PR was good?", "I wanna throw you in a fire pit."])
def test_rules_leave_the_rest_to_judgment(text: str) -> None:
    assert addressed.rules(text) is None


@pytest.mark.parametrize("text, last, said_to_albie", [
    ("So, what do you want me to look at?", None, True),
    ("Want to you to open the email", None, True),
    ("Oh, how?", None, True),
    ("Yes please", "Want me to open it?", True),       # a short reply to his question
    ("Yes please", "I opened it.", False),               # nothing was asked
    ("No, it's the speech detect, it's like bugging out right now. It's not working.", "Want me to look?", False),
    ("It's definitely not picking up 48.", None, False),
    ("I wanna throw you in a fire pit.", None, False),
])
def test_guess(text: str, last: str | None, said_to_albie: bool) -> None:
    assert addressed.guess(text, last) is said_to_albie


def test_judge_skips_the_model_for_clear_cases() -> None:
    asked: list[str] = []

    async def ask(text: str, last: str | None, age: float | None) -> bool:
        asked.append(text)
        return True

    assert asyncio.run(addressed.judge("I don't know, bruh.", ask=ask)) is False
    assert asked == []
    assert asyncio.run(addressed.judge("Did it say the PR was good?", "Hi!", 3.0, ask=ask)) is True
    assert asked == ["Did it say the PR was good?"]


def test_judge_falls_back_to_the_guess_when_the_model_is_slow_or_fails(monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.setattr(addressed, "JUDGE_TIMEOUT_S", 0.01)

    async def slow(text: str, last: str | None, age: float | None) -> bool:
        await asyncio.sleep(1)
        return False

    async def broken(text: str, last: str | None, age: float | None) -> bool:
        raise ConnectionError

    assert asyncio.run(addressed.judge("Open the ML panel.", ask=slow)) is True
    assert asyncio.run(addressed.judge("It's not working.", ask=broken)) is False


def test_no_model_without_a_key_or_with_rules_only(monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.delenv("ANTHROPIC_API_KEY", raising=False)
    assert addressed.default_ask() is None
    monkeypatch.setenv("ANTHROPIC_API_KEY", "sk-test")
    monkeypatch.setenv("ADDRESSED_JUDGE", "rules")
    assert addressed.default_ask() is None


def test_prompt_gives_albies_last_line() -> None:
    assert addressed.prompt("Yes", "Want me to look?", 4.2) == 'Albie\'s last line (4 s ago): "Want me to look?"\nHeard: "Yes"'
    assert addressed.prompt("Open it", None, None) == 'Heard: "Open it"'
