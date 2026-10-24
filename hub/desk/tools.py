"""Bit's desk tools (voice model + Claude runner), registered through agent/tools.py like the CAD and Slack tools.

Prefixes timer_ / notes_ / movie_ map to the panels Bit walks to (agent/tools.py PANELS). Removing a note is never for
good: notes_remove with undo brings it back.
"""
from __future__ import annotations

from typing import Any

from events import MovieState, Note, TimerState

from .service import DeskService
from .timer import human

TOOLS: list[dict[str, Any]] = [
    {"name": "timer_start",
     "description": "Start the focus timer (one at a time: this replaces a running one). It shows as a pill above the "
                    "screens and rings when time is up. Then say in a few words what you set.",
     "parameters": {"type": "object", "properties": {
         "minutes": {"type": "number", "description": "length in minutes (0.5 = 30 seconds, 90 = an hour and a half)"},
         "label": {"type": "string", "description": "what it's for, only if the user said (e.g. 'tea'); default Focus"}},
         "required": ["minutes"]}},
    {"name": "timer_control",
     "description": "Change the focus timer. pause / resume; add: `minutes` more (negative takes time off; on a timer "
                    "that already rang it runs again for that long); stop: cancels it AND turns off the ring, so use "
                    "it for 'turn it off', 'stop the timer', 'I'm done'.",
     "parameters": {"type": "object", "properties": {
         "action": {"type": "string", "enum": ["pause", "resume", "add", "stop"]},
         "minutes": {"type": "number", "description": "add only"}},
         "required": ["action"]}},
    {"name": "timer_status",
     "description": "How much time is left on the focus timer, and whether it's running, paused or ringing.",
     "parameters": {"type": "object", "properties": {}}},
    {"name": "notes_add",
     "description": "Write something down in the user's notes, one note per call; the notes panel opens to show it. "
                    "Keep the user's words, minus filler.",
     "parameters": {"type": "object", "properties": {
         "text": {"type": "string", "description": "the note"}},
         "required": ["text"]}},
    {"name": "notes_list",
     "description": "The user's notes, newest first (id, text, done). Use it to read them back or to find one to change.",
     "parameters": {"type": "object", "properties": {}}},
    {"name": "notes_edit",
     "description": "Change one note: check it off (done=true), un-check it (done=false) or rewrite its text.",
     "parameters": {"type": "object", "properties": {
         "note": {"type": "string", "description": "the note's id, or a few words of its text"},
         "text": {"type": "string", "description": "new text (optional)"},
         "done": {"type": "boolean", "description": "checked off or not (optional)"}},
         "required": ["note"]}},
    {"name": "notes_remove",
     "description": "Remove one note. It can be brought back: undo=true restores the last note removed.",
     "parameters": {"type": "object", "properties": {
         "note": {"type": "string", "description": "the note's id, or a few words of its text"},
         "undo": {"type": "boolean", "description": "true = bring back the last removed note instead"}}}},
    {"name": "notes_view",
     "description": "Show or hide the notes panel: open ('pull up my notes'), minimized (it shrinks into a small dot "
                    "the user can move and tap to open again), closed.",
     "parameters": {"type": "object", "properties": {
         "state": {"type": "string", "enum": ["open", "minimized", "closed"]}},
         "required": ["state"]}},
    {"name": "movie_play",
     "description": "Put the movie on: a big screen opens above the desk and plays it, from where the user left off "
                    "unless from_start. Without a title it plays the current (or only) movie.",
     "parameters": {"type": "object", "properties": {
         "title": {"type": "string", "description": "which movie (optional)"},
         "from_start": {"type": "boolean", "description": "start over from the beginning"}}}},
    {"name": "movie_control",
     "description": "Control the movie: pause; resume; seek: to `seconds` from the start; skip: `seconds` forward "
                    "(negative goes back); volume: `volume` from 0 to 1; close: the screen goes away (the place is kept).",
     "parameters": {"type": "object", "properties": {
         "action": {"type": "string", "enum": ["pause", "resume", "seek", "skip", "volume", "close"]},
         "seconds": {"type": "number", "description": "seek / skip"},
         "volume": {"type": "number", "minimum": 0, "maximum": 1, "description": "volume"}},
         "required": ["action"]}},
    {"name": "movie_status",
     "description": "What's on the movie screen (title, playing or paused, how far in, volume) and the movies there are.",
     "parameters": {"type": "object", "properties": {}}},
]


async def call_tool(name: str, args: dict[str, Any], desk: DeskService) -> dict[str, Any]:
    """Run one tool. Errors come back as {"error": ...} so the model can explain them."""
    try:
        if name == "timer_start":
            return _timer(await desk.timer.start(_num(args, "minutes") * 60, str(args.get("label") or "")))
        if name == "timer_control":
            action = args.get("action")
            if action == "pause":
                return _timer(await desk.timer.pause())
            if action == "resume":
                return _timer(await desk.timer.resume())
            if action == "add":
                return _timer(await desk.timer.add(_num(args, "minutes") * 60))
            if action == "stop":
                return {**_timer(await desk.timer.stop()), "stopped": True}
            return {"error": f"unknown timer action {action!r}"}
        if name == "timer_status":
            return _timer(desk.timer.state())

        if name == "notes_add":
            note = await desk.add_note(str(args.get("text") or ""))
            return {"added": _note(note), "count": len(desk.notes)}
        if name == "notes_list":
            return {"notes": [_note(n) for n in desk.notes.all()], "panel": desk.widgets.notes}
        if name == "notes_edit":
            done = args.get("done")
            note = await desk.edit_note(str(args.get("note") or ""), args.get("text"),
                                        None if done is None else bool(done))
            return {"edited": _note(note)}
        if name == "notes_remove":
            if args.get("undo"):
                return {"restored": _note(await desk.undo_note())}
            note = await desk.remove_note(str(args.get("note") or ""))
            return {"removed": _note(note), "undo": "notes_remove with undo=true brings it back"}
        if name == "notes_view":
            return {"panel": (await desk.set_widgets(notes=args.get("state"))).notes, "count": len(desk.notes)}

        if name == "movie_play":
            return _movie(await desk.movie.play(args.get("title"), bool(args.get("from_start"))))
        if name == "movie_control":
            action = args.get("action")
            if action == "pause":
                return _movie(await desk.movie.pause())
            if action == "resume":
                return _movie(await desk.movie.resume())
            if action == "seek":
                return _movie(await desk.movie.seek(_num(args, "seconds")))
            if action == "skip":
                return _movie(await desk.movie.skip(_num(args, "seconds")))
            if action == "volume":
                return _movie(await desk.movie.set_volume(_num(args, "volume")))
            if action == "close":
                return _movie(await desk.movie.close())
            return {"error": f"unknown movie action {action!r}"}
        if name == "movie_status":
            return {**_movie(desk.movie.state()), "library": [m.title for m in desk.movie.library()]}
    except (LookupError, ValueError, TypeError) as e:
        return {"error": str(e)}
    return {"error": f"Unknown desk tool {name!r}"}


def _num(args: dict[str, Any], key: str) -> float:
    value = args.get(key)
    if value is None:
        raise ValueError(f"{key} is missing")
    return float(value)


def _timer(s: TimerState) -> dict[str, Any]:
    if s.status == "idle":
        return {"status": "idle", "note": "no timer is set"}
    return {"status": s.status, "label": s.label, "left": human(s.remaining_s), "remaining_s": s.remaining_s,
            "length": human(s.duration_s)}


def _note(n: Note) -> dict[str, Any]:
    return {"id": n.id, "text": n.text, "done": n.done}


def _movie(s: MovieState) -> dict[str, Any]:
    if s.movie is None or s.status == "closed":
        return {"status": "closed", "title": s.movie.title if s.movie else None}
    out: dict[str, Any] = {"status": s.status, "title": s.movie.title, "at": human(s.position_s),
                           "volume": s.volume}
    if s.movie.duration_s:
        out["left"] = human(s.movie.duration_s - s.position_s)
    return out
