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


@pytest.mark.parametrize("text, said_to_albie", [
    ("So, what do you want me to look at?", True),
    ("Want to you to open the email", True),
    ("Oh, how?", True),
    ("Can we get a reset button on CAD?", True),
    ("No, it's the speech detect, it's like bugging out right now. It's not working.", False),
    ("It's definitely not picking up 48.", False),
    ("I wanna throw you in a fire pit.", False),
])
def test_guess(text: str, said_to_albie: bool) -> None:
    assert addressed.guess(text) is said_to_albie


@pytest.mark.parametrize("text, last, said_to_albie", [
    ("Yeah.", "Want me to open it?", True),    # the answer to his question
    ("Yeah.", "Here are some fun facts.", False),  # live 18:31:35: filler answered
    ("Damn.", None, False),                     # live 18:33:41
    ("Whoa.", None, False),                     # live 18:42:38
    ("He's not into that, bro.", None, False),  # live 18:31:05
    ("Why is this funny to me, bro? I feel like a", None, False),  # live 18:33:33
])
def test_rules_with_albies_last_line(text: str, last: str | None, said_to_albie: bool) -> None:
    assert addressed.rules(text, last) is said_to_albie


@pytest.mark.parametrize("text", ["Okay yeah, just list like 20.", "Yeah, go ahead with another.",
                                  "You're low-key a little funny guy, aren't you?"])
def test_a_follow_up_reply_counts_when_the_model_is_slow_or_absent(text: str, monkeypatch: pytest.MonkeyPatch) -> None:
    """Live 18:34: real follow-ups to "give me a hundred random fun facts" were dropped after a judge timeout."""
    monkeypatch.setattr(addressed, "GATED_TIMEOUT_S", 0.01)

    async def slow(*_: object) -> bool:
        await asyncio.sleep(1)
        return False

    monkeypatch.setattr(addressed, "default_ask", lambda: None)
    assert asyncio.run(addressed.judge(text, "Here are some fun facts.", 2.0, gated=True)) is True
    assert asyncio.run(addressed.judge(text, "Here are some fun facts.", 2.0, gated=True, ask=slow)) is True


def test_a_follow_up_the_model_says_is_side_talk_is_dropped() -> None:
    """Live 18:48: 'are you fixing like speech to text at all? I think I want to look into the whistle' (to a teammate)."""
    async def no(*_: object) -> bool:
        return False

    text = "What are you, are you fixing like speech to text at all? I think I want to look into the whistle."
    assert asyncio.run(addressed.judge(text, "Sure, what next?", 3.0, gated=True, ask=no)) is False


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
