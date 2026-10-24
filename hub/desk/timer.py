"""Focus timers: a few at a time, each with a name, owned by the hub.

Bit starts one ("set a 25 minute timer for the standup"); Unity shows them as pills above the screens (minimized: one
dot showing the one that ends soonest) and counts each down on its own from remaining_s; the hub rings each at its
end (timers.updated with it ringing, and Bit says so). A timer rings until it's stopped. Not saved to disk: a hub
restart forgets them.
"""
from __future__ import annotations

import asyncio
import time
import uuid
from collections.abc import Awaitable, Callable

from events import TimersUpdated, TimerState

MAX_S = 10 * 3600
MAX_TIMERS = 5

Publish = Callable[[TimersUpdated], Awaitable[None]]
Ring = Callable[[TimerState], Awaitable[None]]


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
    """What Bit says when a timer rings."""
    if state.label.lower() == "focus":
        return f"Time's up! That's {human(state.duration_s)} of focus."
    return f"Time's up! Your {state.label} timer is done."


class _Timer:
    def __init__(self, label: str, seconds: float) -> None:
        self.state = TimerState(timer_id="t" + uuid.uuid4().hex[:6], label=label, status="running",
                                duration_s=seconds, remaining_s=seconds)
        self.ends = 0.0  # time.monotonic() when it rings, while running
        self.task: asyncio.Task[None] | None = None

    def remaining(self) -> float:
        if self.state.status == "running":
            return max(0.0, self.ends - time.monotonic())
        return self.state.remaining_s

    def snapshot(self) -> TimerState:
        s = self.state.model_copy()
        s.remaining_s = round(self.remaining(), 1)
        return s

    def disarm(self) -> None:
        if self.task is not None:
            self.task.cancel()
            self.task = None


class TimerBoard:
    def __init__(self, publish: Publish, on_ring: Ring) -> None:
        self._publish = publish
        self._on_ring = on_ring
        self._timers: list[_Timer] = []  # in the order they were set

    # ------------------------------------------------------------------ reading
    def states(self) -> list[TimerState]:
        """Ringing first, then the rest by time left (running before paused at equal time)."""
        order = {"ringing": 0, "running": 1, "paused": 2}
        snaps = [t.snapshot() for t in self._timers]
        return sorted(snaps, key=lambda s: (s.status != "ringing", s.remaining_s, order[s.status]))

    def updated(self) -> TimersUpdated:
        return TimersUpdated(timers=self.states())

    def __len__(self) -> int:
        return len(self._timers)

    def find(self, ref: str) -> TimerState:
        return self._find(ref).snapshot()

    def pick(self, ref: str | None, action: str) -> list[TimerState]:
        """The timers an action means. `ref`: an id, a name (or part of one) or "all". Without one: the only timer
        it can apply to; stop means whichever are ringing ("turn it off"). Raises LookupError when it's unclear."""
        if ref and ref.strip().lower() == "all":
            return self.states()
        if ref and ref.strip():
            return [self._find(ref).snapshot()]
        fits = {"pause": ("running",), "resume": ("paused",), "add": ("running", "paused", "ringing"),
                "stop": ("ringing",)}.get(action, ("running", "paused", "ringing"))
        hits = [t for t in self._timers if t.state.status in fits]
        if action == "stop" and hits:
            return [t.snapshot() for t in hits]
        if action == "stop":
            hits = list(self._timers)
        if len(hits) == 1:
            return [hits[0].snapshot()]
        if not self._timers:
            raise LookupError("no timer is set")
        if not hits:
            raise LookupError(f"no timer to {action}")
        raise LookupError("which one? " + ", ".join(f"{t.state.label} ({human(t.remaining())} left)" for t in hits))

    # ------------------------------------------------------------------ changing
    async def start(self, seconds: float, label: str = "") -> TimerState:
        seconds = _check(seconds)
        if len(self._timers) >= MAX_TIMERS:
            raise ValueError(f"{MAX_TIMERS} timers are running already; stop one first")
        timer = _Timer(self._name(label), seconds)
        self._timers.append(timer)
        self._arm(timer, seconds)
        await self._changed()
        return timer.snapshot()

    async def pause(self, ref: str) -> TimerState:
        timer = self._find(ref)
        if timer.state.status != "running":
            raise ValueError(f"the {timer.state.label} timer isn't running ({timer.state.status})")
        left = timer.remaining()
        timer.disarm()
        timer.state.status, timer.state.remaining_s = "paused", round(left, 1)
        await self._changed()
        return timer.snapshot()

    async def resume(self, ref: str) -> TimerState:
        timer = self._find(ref)
        if timer.state.status != "paused":
            raise ValueError(f"the {timer.state.label} timer isn't paused ({timer.state.status})")
        timer.state.status = "running"
        self._arm(timer, timer.state.remaining_s)
        await self._changed()
        return timer.snapshot()

    async def add(self, ref: str, seconds: float) -> TimerState:
        """More time; negative takes some off (never below 1 s). On a ringing timer it runs again for that long."""
        timer = self._find(ref)
        if timer.state.status == "ringing":
            if seconds <= 0:
                raise ValueError("that timer already rang; add a positive amount")
            timer.state.status = "running"
            left = _check(seconds)
        else:
            left = _check(max(1.0, timer.remaining() + seconds))
        timer.state.duration_s = round(max(left, timer.state.duration_s + seconds), 1)
        if timer.state.status == "running":
            self._arm(timer, left)
        else:
            timer.state.remaining_s = left
        await self._changed()
        return timer.snapshot()

    async def stop(self, ref: str) -> TimerState:
        """Cancel it, or turn off its ring. It's gone from the list afterwards."""
        timer = self._find(ref)
        snap = timer.snapshot()
        timer.disarm()
        self._timers.remove(timer)
        await self._changed()
        return snap

    async def stop_all(self) -> list[TimerState]:
        snaps = [t.snapshot() for t in self._timers]
        for t in self._timers:
            t.disarm()
        self._timers.clear()
        await self._changed()
        return snaps

    def close(self) -> None:
        for t in self._timers:
            t.disarm()

    # ------------------------------------------------------------------ internals
    def _find(self, ref: str) -> _Timer:
        ref = (ref or "").strip()
        for t in self._timers:
            if t.state.timer_id == ref:
                return t
        words = ref.lower()
        exact = [t for t in self._timers if t.state.label.lower() == words]
        hits = exact or [t for t in self._timers if words and words in t.state.label.lower()]
        if len(hits) == 1:
            return hits[0]
        if not self._timers:
            raise LookupError("no timer is set")
        if not hits:
            raise LookupError(f"no timer called {ref!r}; there's " + ", ".join(t.state.label for t in self._timers))
        raise LookupError(f"{len(hits)} timers match {ref!r}: " + ", ".join(t.state.label for t in hits))

    def _name(self, label: str) -> str:
        """The label as given ("Focus" if none), made unique: a second "tea" is "tea 2"."""
        base = " ".join(label.split())[:40] or "Focus"
        taken = {t.state.label.lower() for t in self._timers}
        name, n = base, 2
        while name.lower() in taken:
            name, n = f"{base} {n}", n + 1
        return name

    def _arm(self, timer: _Timer, seconds: float) -> None:
        timer.disarm()
        timer.ends = time.monotonic() + seconds
        timer.task = asyncio.create_task(self._ring_later(timer, seconds))

    async def _ring_later(self, timer: _Timer, seconds: float) -> None:
        await asyncio.sleep(seconds)
        if timer not in self._timers or timer.state.status != "running":
            return
        timer.task = None
        timer.state.status, timer.state.remaining_s = "ringing", 0.0
        await self._changed()
        await self._on_ring(timer.snapshot())

    async def _changed(self) -> None:
        await self._publish(self.updated())


def _check(seconds: float) -> float:
    seconds = float(seconds)
    if not 0 < seconds <= MAX_S:
        raise ValueError(f"a timer runs between 1 second and {MAX_S // 3600} hours")
    return round(seconds, 1)
