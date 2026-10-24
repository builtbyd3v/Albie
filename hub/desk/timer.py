"""The focus timer: one at a time, owned by the hub.

Bit starts it ("set a focus timer for an hour"); Unity shows the pill and counts down on its own from remaining_s;
the hub rings it at the end (timer.state ringing, and Bit says so). Ringing lasts until it's stopped. Not saved to
disk: a hub restart forgets a running timer.
"""
from __future__ import annotations

import asyncio
import time
import uuid
from collections.abc import Awaitable, Callable

from events import TimerState

MAX_S = 10 * 3600

Publish = Callable[[TimerState], Awaitable[None]]


def human(seconds: float) -> str:
    """'1 hour 30 minutes', '25 minutes', '45 seconds': for Bit to say."""
    s = max(0, round(seconds))
    if s < 60:
        return f"{s} second{'' if s == 1 else 's'}"
    h, m = divmod(round(s / 60), 60)
    parts = []
    if h:
        parts.append(f"{h} hour{'' if h == 1 else 's'}")
    if m:
        parts.append(f"{m} minute{'' if m == 1 else 's'}")
    return " ".join(parts)


def ring_line(state: TimerState) -> str:
    """What Bit says when it rings."""
    if state.label.lower() == "focus":
        return f"Time's up! That's {human(state.duration_s)} of focus."
    return f"Time's up! Your {state.label} timer is done."


class FocusTimer:
    def __init__(self, publish: Publish, on_ring: Publish) -> None:
        self._publish = publish
        self._on_ring = on_ring
        self._state = TimerState()
        self._ends = 0.0  # time.monotonic() when it rings, while running
        self._task: asyncio.Task[None] | None = None

    def state(self) -> TimerState:
        s = self._state.model_copy()
        if s.status == "running":
            s.remaining_s = round(max(0.0, self._ends - time.monotonic()), 1)
        return s

    async def start(self, seconds: float, label: str = "") -> TimerState:
        """A new timer; replaces the one running (or ringing)."""
        seconds = _check(seconds)
        self._state = TimerState(timer_id=uuid.uuid4().hex[:8], label=label.strip() or "Focus", status="running",
                                 duration_s=seconds, remaining_s=seconds)
        self._arm(seconds)
        return await self._changed()

    async def pause(self) -> TimerState:
        if self._state.status != "running":
            raise ValueError(f"the timer isn't running ({self._state.status})")
        left = self.state().remaining_s
        self._disarm()
        self._state.status, self._state.remaining_s = "paused", left
        return await self._changed()

    async def resume(self) -> TimerState:
        if self._state.status != "paused":
            raise ValueError(f"the timer isn't paused ({self._state.status})")
        self._state.status = "running"
        self._arm(self._state.remaining_s)
        return await self._changed()

    async def add(self, seconds: float) -> TimerState:
        """More time; negative takes some off (never below 1 s). On a ringing timer it runs again for that long."""
        status = self._state.status
        if status == "idle":
            raise ValueError("no timer is set")
        if status == "ringing":
            if seconds <= 0:
                raise ValueError("the timer already rang; add a positive amount")
            self._state.status = "running"
            left = _check(seconds)
        else:
            left = _check(max(1.0, self.state().remaining_s + seconds))
        self._state.duration_s = round(max(left, self._state.duration_s + seconds), 1)
        if self._state.status == "running":
            self._arm(left)
        else:
            self._state.remaining_s = left
        return await self._changed()

    async def stop(self) -> TimerState:
        """Cancel it, or turn off the ring. Stopping no timer is fine."""
        self._disarm()
        self._state = TimerState()
        return await self._changed()

    def close(self) -> None:
        self._disarm()

    def _arm(self, seconds: float) -> None:
        self._disarm()
        self._ends = time.monotonic() + seconds
        self._task = asyncio.create_task(self._ring_later(self._state.timer_id, seconds))

    def _disarm(self) -> None:
        if self._task is not None:
            self._task.cancel()
            self._task = None

    async def _ring_later(self, timer_id: str, seconds: float) -> None:
        await asyncio.sleep(seconds)
        if self._state.timer_id != timer_id or self._state.status != "running":
            return
        self._task = None
        self._state.status, self._state.remaining_s = "ringing", 0.0
        await self._on_ring(await self._changed())

    async def _changed(self) -> TimerState:
        state = self.state()
        await self._publish(state)
        return state


def _check(seconds: float) -> float:
    seconds = float(seconds)
    if not 0 < seconds <= MAX_S:
        raise ValueError(f"a timer runs between 1 second and {MAX_S // 3600} hours")
    return round(seconds, 1)
