"""FAKE_AGENT=1: scripted events with realistic delays, no model calls.

Same signature as ``runner.run_turn`` so ws.py / router.py don't care which one runs.
Ask text "error" -> agent.error.
"""

from __future__ import annotations

import asyncio
import logging
import os

from . import events as ev
from .bus import Emit
from .config import ProjectConfig
from .events import AskMode
from .turn import TurnControl, classify_mode

log = logging.getLogger(__name__)

# Tests set FAKE_AGENT_DELAY=0 to run instantly.
def _delay() -> float:
    return float(os.getenv("FAKE_AGENT_DELAY", "0.6"))


async def _sleep(control: TurnControl | None, factor: float = 1.0) -> bool:
    """Sleep; return True if the turn was cancelled meanwhile."""
    if control is None:
        await asyncio.sleep(_delay() * factor)
        return False
    try:
        await asyncio.wait_for(control.cancelled.wait(), _delay() * factor)
        return True
    except asyncio.TimeoutError:
        return False


async def run_turn(
    project: ProjectConfig,
    text: str,
    mode: AskMode,
    emit: Emit,
    *,
    pet_id: str,
    control: TurnControl | None = None,
    agent: object | None = None,  # crew member's AgentType (ignored: same script for everyone)
) -> str:
    concrete = classify_mode(text, mode)
    log.info("fake turn pet=%s project=%s mode=%s", pet_id, project.id, concrete)
    await emit(ev.thinking(pet_id))
    if await _sleep(control):
        return ""

    if text.strip().lower() in {"error", "mock.error"}:
        msg = "Oops, my brain glitched (fake error)."
        await emit(ev.error(pet_id, msg))
        return msg

    if concrete == "code":
        task_id = "fake-task"
        await emit(ev.task_started(pet_id, task_id, f"Working on: {text[:60]}"))
        for i, step in enumerate(
            ["Reading hub/agent/ws.py", "Editing hub/agent/ws.py", "Running tests"], start=1
        ):
            if await _sleep(control):
                return ""
            await emit(ev.task_progress(pet_id, task_id, step, pct=round(i / 3, 2)))
        if await _sleep(control):
            return ""
        summary = "Done! I made the change on a new branch."
        await emit(ev.task_complete(pet_id, task_id, summary, branch="pet/fake-123"))
        return summary

    answer = "The WebSocket handler lives in hub/agent/ws.py. It routes hello, ask and cancel."
    words = answer.split(" ")
    cuts = [len(words) // 3, 2 * len(words) // 3, len(words)]
    for cut in cuts:
        if await _sleep(control, 0.5):
            return ""
        await emit(ev.partial_answer(pet_id, " ".join(words[:cut])))
    await emit(ev.answer_done(pet_id, answer))
    return answer
