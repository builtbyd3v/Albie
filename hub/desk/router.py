"""REST for the desk extras (proposed contract: README "Desk"): /timers, /notes, /movie, /ui/widgets.

Everything that changes something is a POST, like the rest of the hub (ForgeNet sends GET and POST).

Unity's buttons call these; Bit's tools go through the same DeskService, so both raise the same events.
"""
from __future__ import annotations

from fastapi import APIRouter, Depends, HTTPException, Request
from fastapi.responses import FileResponse
from pydantic import BaseModel, Field

from events import MovieInfo, MovieState, Note, NotesUpdated, TimersUpdated, TimerState, UiWidgets, WidgetState

from .movie import MovieMark
from .service import DeskService

router = APIRouter(tags=["desk"])


def desk(request: Request) -> DeskService:
    return request.app.state.desk


class TimerStart(BaseModel):
    minutes: float | None = Field(None, gt=0)
    seconds: float | None = Field(None, gt=0)  # instead of minutes (mocks, quick tests)
    label: str = ""


class TimerAdd(BaseModel):
    minutes: float


class NoteIn(BaseModel):
    text: str


class NotePatch(BaseModel):
    text: str | None = None
    done: bool | None = None


class MoviePlay(BaseModel):
    movie_id: str | None = None
    from_start: bool = False


class MovieSeek(BaseModel):
    position_s: float | None = Field(None, ge=0)  # to here, or
    delta_s: float | None = None                  # this far from where it is, or
    mark: str | None = None                       # a saved spot (id, label or its words); plays its movie


class MarkIn(BaseModel):
    label: str
    position_s: float | None = Field(None, ge=0)  # none = a few seconds before where the movie is now
    movie_id: str | None = None                   # none = the current movie


class MovieVolume(BaseModel):
    volume: float = Field(ge=0, le=1)


class MoviePosition(BaseModel):
    position_s: float = Field(ge=0)
    duration_s: float | None = Field(None, gt=0)
    ended: bool = False


class WidgetsIn(BaseModel):
    timer: WidgetState | None = None
    notes: WidgetState | None = None
    movie: WidgetState | None = None


async def _run(coro):
    try:
        return await coro
    except LookupError as e:
        raise HTTPException(404, str(e)) from None
    except ValueError as e:  # can't do that now: pause a timer that isn't running, an empty note, ...
        raise HTTPException(422, str(e)) from None


def _now(fn):
    """_run for the calls that don't publish anything (movie marks)."""
    try:
        return fn()
    except LookupError as e:
        raise HTTPException(404, str(e)) from None
    except ValueError as e:
        raise HTTPException(422, str(e)) from None


# ---------------------------------------------------------------------- timers
@router.get("/timers", response_model=TimersUpdated)
def timers(d: DeskService = Depends(desk)) -> TimersUpdated:
    return d.timers.updated()


@router.post("/timers", response_model=TimerState)
async def timer_start(req: TimerStart, d: DeskService = Depends(desk)) -> TimerState:
    """A new timer (up to 5); `label` names it ("tea"), default Focus."""
    if req.seconds is None and req.minutes is None:
        raise HTTPException(422, "give minutes or seconds")
    seconds = req.seconds if req.seconds is not None else req.minutes * 60
    return await _run(d.timers.start(seconds, req.label))


@router.post("/timers/stop_all", response_model=TimersUpdated)
async def timer_stop_all(d: DeskService = Depends(desk)) -> TimersUpdated:
    await _run(d.timers.stop_all())
    return d.timers.updated()


@router.post("/timers/{timer_id}/pause", response_model=TimerState)
async def timer_pause(timer_id: str, d: DeskService = Depends(desk)) -> TimerState:
    return await _run(d.timers.pause(timer_id))


@router.post("/timers/{timer_id}/resume", response_model=TimerState)
async def timer_resume(timer_id: str, d: DeskService = Depends(desk)) -> TimerState:
    return await _run(d.timers.resume(timer_id))


@router.post("/timers/{timer_id}/add", response_model=TimerState)
async def timer_add(timer_id: str, req: TimerAdd, d: DeskService = Depends(desk)) -> TimerState:
    return await _run(d.timers.add(timer_id, req.minutes * 60))


@router.post("/timers/{timer_id}/stop", response_model=TimerState)
async def timer_stop(timer_id: str, d: DeskService = Depends(desk)) -> TimerState:
    """Cancel it, or turn off its ring (the pill's Stop). Returns it as it was."""
    return await _run(d.timers.stop(timer_id))


# ---------------------------------------------------------------------- notes
@router.get("/notes", response_model=NotesUpdated)
def notes(d: DeskService = Depends(desk)) -> NotesUpdated:
    return d.notes.updated()


@router.post("/notes", response_model=Note)
async def notes_add(req: NoteIn, d: DeskService = Depends(desk)) -> Note:
    return await _run(d.add_note(req.text))


@router.post("/notes/undo", response_model=NotesUpdated)
async def notes_undo(d: DeskService = Depends(desk)) -> NotesUpdated:
    await _run(d.undo_note())
    return d.notes.updated()


@router.post("/notes/{note_id}/edit", response_model=Note)
async def notes_edit(note_id: str, req: NotePatch, d: DeskService = Depends(desk)) -> Note:
    return await _run(d.edit_note(note_id, req.text, req.done))


@router.post("/notes/{note_id}/remove", response_model=NotesUpdated)
async def notes_remove(note_id: str, d: DeskService = Depends(desk)) -> NotesUpdated:
    """Removes it, kept for Undo."""
    await _run(d.remove_note(note_id))
    return d.notes.updated()


# ---------------------------------------------------------------------- movie
@router.get("/movie", response_model=MovieState)
def movie_state(d: DeskService = Depends(desk)) -> MovieState:
    return d.movie.state()


@router.get("/movie/library", response_model=list[MovieInfo])
def movie_library(d: DeskService = Depends(desk)) -> list[MovieInfo]:
    return d.movie.library()


@router.get("/movie/files/{name}")
def movie_file(name: str, d: DeskService = Depends(desk)) -> FileResponse:
    """The .mp4 (byte ranges, so the player can seek) or its .srt."""
    try:
        path = d.movie.file(name)
    except LookupError as e:
        raise HTTPException(404, str(e)) from None
    media_type = "video/mp4" if path.suffix == ".mp4" else "text/plain; charset=utf-8"
    return FileResponse(path, media_type=media_type)


@router.post("/movie/play", response_model=MovieState)
async def movie_play(req: MoviePlay, d: DeskService = Depends(desk)) -> MovieState:
    return await _run(d.movie.play(req.movie_id, req.from_start))


@router.post("/movie/pause", response_model=MovieState)
async def movie_pause(d: DeskService = Depends(desk)) -> MovieState:
    return await _run(d.movie.pause())


@router.post("/movie/resume", response_model=MovieState)
async def movie_resume(d: DeskService = Depends(desk)) -> MovieState:
    return await _run(d.movie.resume())


@router.post("/movie/seek", response_model=MovieState)
async def movie_seek(req: MovieSeek, d: DeskService = Depends(desk)) -> MovieState:
    if req.position_s is not None:
        return await _run(d.movie.seek(req.position_s))
    if req.delta_s is not None:
        return await _run(d.movie.skip(req.delta_s))
    if req.mark:
        return (await _run(d.movie.goto_mark(req.mark)))[1]
    raise HTTPException(422, "give position_s, delta_s or mark")


@router.get("/movie/marks", response_model=list[MovieMark])
def movie_marks(movie_id: str | None = None, d: DeskService = Depends(desk)) -> list[MovieMark]:
    """A movie's saved spots by time (no movie_id = the current movie)."""
    return _now(lambda: d.movie.marks(movie_id)[1])


@router.post("/movie/marks", response_model=MovieMark)
def movie_mark_add(req: MarkIn, d: DeskService = Depends(desk)) -> MovieMark:
    return _now(lambda: d.movie.add_mark(req.label, req.position_s, req.movie_id)[1])


@router.post("/movie/marks/{mark_id}/remove", response_model=MovieMark)
def movie_mark_remove(mark_id: str, d: DeskService = Depends(desk)) -> MovieMark:
    return _now(lambda: d.movie.remove_mark(mark_id))


@router.post("/movie/volume", response_model=MovieState)
async def movie_volume(req: MovieVolume, d: DeskService = Depends(desk)) -> MovieState:
    return await _run(d.movie.set_volume(req.volume))


@router.post("/movie/close", response_model=MovieState)
async def movie_close(d: DeskService = Depends(desk)) -> MovieState:
    return await _run(d.movie.close())


@router.post("/movie/position", response_model=MovieState)
async def movie_position(req: MoviePosition, d: DeskService = Depends(desk)) -> MovieState:
    """Unity's player reports where it is (every few seconds, on pause, at the end)."""
    return await _run(d.movie.report(req.position_s, req.duration_s, req.ended))


# ---------------------------------------------------------------------- widgets
@router.get("/ui/widgets", response_model=UiWidgets)
def widgets(d: DeskService = Depends(desk)) -> UiWidgets:
    return d.widgets


@router.post("/ui/widgets", response_model=UiWidgets)
async def widgets_set(req: WidgetsIn, d: DeskService = Depends(desk)) -> UiWidgets:
    """Minimize, expand, close. Closing the timers stops them; closing the movie closes it."""
    return await _run(d.set_widgets(req.timer, req.notes, req.movie))
