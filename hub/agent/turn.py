"""Shared turn plumbing used by both the real and the fake runner."""

from __future__ import annotations

import asyncio
import re
from collections.abc import Awaitable, Callable
from typing import Literal

from .events import AskMode

ConcreteMode = Literal["qa", "code"]

# Leading imperative verbs that mean "change the code".
_CODE_VERBS = (
    "fix|add|change|make|refactor|implement|rename|delete|remove|update|create|write|move|"
    "replace|edit|bump|clean"
)
_CODE_RE = re.compile(
    rf"^\s*(?:(?:hey|ok|okay|so|please|can you|could you|would you|go|now)[\s,]+)*(?:{_CODE_VERBS})\b",
    re.IGNORECASE,
)


def classify_mode(text: str, mode: AskMode) -> ConcreteMode:
    """'auto' -> 'code' if the request starts with an imperative edit verb, else 'qa'."""
    if mode != "auto":
        return mode
    return "code" if _CODE_RE.search(text) else "qa"


class TurnControl:
    """Cancellation handle shared by ws.py (agent.cancel) and the runner.

    The runner registers an async ``interrupter`` (e.g. ClaudeSDKClient.interrupt);
    ``cancel()`` calls it, and ws.py hard-cancels the asyncio task if the runner
    does not finish within a grace period.
    """

    def __init__(self) -> None:
        self.cancelled = asyncio.Event()
        self._interrupter: Callable[[], Awaitable[None]] | None = None

    def set_interrupter(self, fn: Callable[[], Awaitable[None]] | None) -> None:
        self._interrupter = fn

    async def cancel(self) -> None:
        self.cancelled.set()
        if self._interrupter is not None:
            await self._interrupter()
