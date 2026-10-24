"""DeskService: the focus timer, notes and movie screen, which Bit adds on top of any mode, and which of them show.

One per hub (main.py). Every change goes out on /ws (timer.state, notes.updated, movie.state, ui.widgets), and all four
replay fresh to a client that connects later. The router (Unity's buttons) and the tools (Bit) both come through here.
"""
from __future__ import annotations

import logging
from collections.abc import Awaitable, Callable
from pathlib import Path
from typing import Any

from events import EventBus, MovieState, Note, TimerState, UiWidgets, WidgetState

from .movie import MovieService
from .notes import NotesStore
from .timer import FocusTimer, ring_line

log = logging.getLogger("forge.desk")

Speak = Callable[[str, str], Awaitable[bool]]


class DeskService:
    def __init__(self, data_dir: Path, bus: EventBus, speak: Speak, pet_id: str = "bit") -> None:
        self.bus, self.speak, self.pet_id = bus, speak, pet_id
        self.widgets = UiWidgets()
        self.timer = FocusTimer(self._timer_changed, self._timer_rang)
        self.notes = NotesStore(Path(data_dir) / "desk" / "notes.json")
        self.movie = MovieService(Path(data_dir) / "media", Path(data_dir) / "desk" / "movie.json", self._movie_changed)
        bus.sticky("ui.widgets", lambda: self.widgets)
        bus.sticky("timer.state", self.timer.state)
        bus.sticky("notes.updated", self.notes.updated)
        bus.sticky("movie.state", self.movie.state)

    def stop(self) -> None:
        self.timer.close()

    def health(self) -> dict[str, Any]:
        return {"timer": self.timer.state().status, "notes": len(self.notes), "movies": len(self.movie.library()),
                "movie": self.movie.state().status, "widgets": self.widgets.model_dump()}

    # ------------------------------------------------------------------ widgets
    async def show(self, timer: WidgetState | None = None, notes: WidgetState | None = None,
                   movie: WidgetState | None = None) -> UiWidgets:
        """Set which extras show; ui.widgets goes out only when something changed."""
        updates = {k: v for k, v in {"timer": timer, "notes": notes, "movie": movie}.items() if v is not None}
        nxt = UiWidgets(**{**self.widgets.model_dump(), **updates})
        if nxt != self.widgets:
            self.widgets = nxt
            await self.bus.publish("ui.widgets", nxt)
        return self.widgets

    async def set_widgets(self, timer: WidgetState | None = None, notes: WidgetState | None = None,
                          movie: WidgetState | None = None) -> UiWidgets:
        """Unity's buttons (minimize, expand, close) and Bit's notes_view. Closing the timer stops it; closing the
        movie closes it (its place is kept). The timer and the movie only open while they're on."""
        if timer in ("open", "minimized") and self.timer.state().status == "idle":
            raise ValueError("no timer is set")
        if movie in ("open", "minimized") and self.movie.state().status == "closed":
            raise ValueError("the movie isn't on")
        if timer == "closed" and self.timer.state().status != "idle":
            await self.timer.stop()
        if movie == "closed" and self.movie.state().status != "closed":
            await self.movie.close()
        return await self.show(timer=timer, notes=notes, movie=movie)

    # ------------------------------------------------------------------ notes
    async def add_note(self, text: str) -> Note:
        note = self.notes.add(text)
        await self._notes_changed(open_panel=True)
        return note

    async def edit_note(self, ref: str, text: str | None = None, done: bool | None = None) -> Note:
        note = self.notes.edit(ref, text, done)
        await self._notes_changed()
        return note

    async def remove_note(self, ref: str) -> Note:
        note = self.notes.remove(ref)
        await self._notes_changed()
        return note

    async def undo_note(self) -> Note:
        note = self.notes.undo()
        await self._notes_changed()
        return note

    async def _notes_changed(self, open_panel: bool = False) -> None:
        await self.bus.publish("notes.updated", self.notes.updated())
        if open_panel and self.widgets.notes == "closed":  # minimized stays a dot; its count goes up
            await self.show(notes="open")

    # ------------------------------------------------------------------ timer + movie events
    async def _timer_changed(self, state: TimerState) -> None:
        await self.bus.publish("timer.state", state)
        if state.status == "idle":
            await self.show(timer="closed")
        elif self.widgets.timer == "closed":
            await self.show(timer="open")

    async def _timer_rang(self, state: TimerState) -> None:
        if self.widgets.timer == "minimized":  # a dot that rings pops open so you see it
            await self.show(timer="open")
        line = ring_line(state)
        await self.bus.publish("pet.say", {"pet_id": self.pet_id, "text": line})
        try:
            await self.speak(self.pet_id, line)
        except Exception:  # noqa: BLE001 - the chime and the caption already went out
            log.exception("spoken timer line failed")

    async def _movie_changed(self, state: MovieState) -> None:
        await self.bus.publish("movie.state", state)
        if state.status == "closed":
            await self.show(movie="closed")
        elif self.widgets.movie == "closed":
            await self.show(movie="open")
