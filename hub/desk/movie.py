"""The movie screen: an .mp4 from .data/media on a big screen above the desk ("Bit, put the movie on").

Library: every <id>.mp4 in the media folder (gitignored with the rest of .data). Optional beside it: <id>.json
{title, year, duration_s} and <id>.en.srt subtitles. Encode new files as H.264 8-bit + stereo AAC with +faststart
(README "Desk"): Unity's VideoPlayer plays that in the Windows editor and on the Quest.

Unity streams GET /movie/files/<id>.mp4 (byte ranges, so it can seek) and keeps time; the hub keeps the state and
where you stopped in each movie (.data/desk/movie.json), so it picks up there next time.

Marks: spots saved under a name ("Bit, save minute 45 as Tony starts building the suit"), kept per movie in the same
file. "Skip to where Tony builds the suit" finds the mark by its words and jumps there; Unity only sees the seek.
"""
from __future__ import annotations

import json
import logging
import re
import time
from collections.abc import Awaitable, Callable
from pathlib import Path

from pydantic import BaseModel

from events import MovieInfo, MovieState

log = logging.getLogger("forge.desk")

FILE_NAME = re.compile(r"^[A-Za-z0-9][A-Za-z0-9._-]*\.(mp4|srt)$")
RESTART_NEAR_END_S = 120.0  # a movie stopped in its last two minutes starts over next time
MARK_NOW_LEAD_S = 5.0  # "save this spot": the user spoke (and Bit answered) after it began, so step back a little
STOP_WORDS = frozenset("a an and at back bit go in is it jump moment movie of on part point scene skip spot that the "
                       "this to when where with".split())

Publish = Callable[[MovieState], Awaitable[None]]


class MovieMark(BaseModel):
    """A spot in a movie saved under a name. Hub only (REST + Bit's tools); nothing on /ws."""
    mark_id: str
    label: str
    position_s: float


class MovieService:
    def __init__(self, media_dir: Path, state_path: Path, publish: Publish) -> None:
        self.media_dir = Path(media_dir)
        self.state_path = Path(state_path)
        self._publish = publish
        self._state = MovieState()
        self._at = time.monotonic()  # when _state.position_s was true
        self._places: dict[str, float] = {}  # movie_id -> where it was stopped
        self._marks: dict[str, list[MovieMark]] = {}  # movie_id -> its marks, by time
        self._load()

    # ------------------------------------------------------------------ library
    def library(self) -> list[MovieInfo]:
        if not self.media_dir.is_dir():
            return []
        movies = []
        for mp4 in sorted(self.media_dir.glob("*.mp4")):
            if not FILE_NAME.match(mp4.name) or "." in mp4.stem:  # skips half-written <id>.part.mp4
                continue
            meta = _sidecar(mp4.with_suffix(".json"))
            srt = mp4.with_name(f"{mp4.stem}.en.srt")
            movies.append(MovieInfo(
                movie_id=mp4.stem,
                title=str(meta.get("title") or mp4.stem.replace("_", " ").replace("-", " ").title()),
                year=meta.get("year"),
                duration_s=meta.get("duration_s"),
                url=f"/movie/files/{mp4.name}",
                subtitles_url=f"/movie/files/{srt.name}" if srt.is_file() else None,
            ))
        return movies

    def find(self, query: str | None = None) -> MovieInfo:
        """By id or title (or part of it); none = the current movie, else the first."""
        movies = self.library()
        if not movies:
            raise LookupError(f"no movies yet: put an .mp4 in {self.media_dir}")
        q = (query or "").strip().lower()
        if not q:
            current = self._state.movie
            return next((m for m in movies if current and m.movie_id == current.movie_id), movies[0])
        exact = [m for m in movies if q in (m.movie_id.lower(), m.title.lower())]
        hits = exact or [m for m in movies if q in m.title.lower() or q in m.movie_id.lower()]
        if not hits:
            raise LookupError(f"no movie called {query!r}; there's " + ", ".join(m.title for m in movies))
        return hits[0]

    def file(self, name: str) -> Path:
        """A file Unity may fetch: an .mp4 or .srt directly in the media folder."""
        path = self.media_dir / name
        if not FILE_NAME.match(name) or not path.is_file():
            raise LookupError(f"no media file {name!r}")
        return path

    # ------------------------------------------------------------------ state
    def state(self) -> MovieState:
        s = self._state.model_copy(deep=True)
        s.position_s = round(self._position(), 1)
        return s

    async def play(self, query: str | None = None, from_start: bool = False) -> MovieState:
        """Open the screen and play: where you left off, unless from_start. Already on: just plays."""
        movie = self.find(query)
        current = self._state.movie
        if from_start or current is None or current.movie_id != movie.movie_id or self._state.status == "closed":
            self._remember()
            start = 0.0 if from_start else self._places.get(movie.movie_id, 0.0)
            if movie.duration_s and start > movie.duration_s - RESTART_NEAR_END_S:
                start = 0.0
            self._state.movie = movie
            self._jump(start)
        else:
            self._settle()
        self._state.status = "playing"
        return await self._changed()

    async def pause(self) -> MovieState:
        self._need_on()
        self._settle()
        self._state.status = "paused"
        return await self._changed()

    async def resume(self) -> MovieState:
        if self._state.status == "closed":
            return await self.play()
        self._settle()
        self._state.status = "playing"
        return await self._changed()

    async def seek(self, position_s: float) -> MovieState:
        self._need_on()
        self._jump(position_s)
        return await self._changed()

    async def skip(self, delta_s: float) -> MovieState:
        self._need_on()
        self._jump(self._position() + delta_s)
        return await self._changed()

    async def set_volume(self, volume: float) -> MovieState:
        self._state.volume = round(min(1.0, max(0.0, float(volume))), 2)
        return await self._changed()

    async def close(self) -> MovieState:
        """The screen goes away; the place is kept for next time."""
        self._settle()
        self._state.status = "closed"
        return await self._changed()

    async def report(self, position_s: float, duration_s: float | None = None, ended: bool = False) -> MovieState:
        """Unity's player says where it is (every few seconds, on pause, at the end). No event for a plain report:
        Unity is the clock, so echoing it back would only make the player jump."""
        movie = self._state.movie
        if movie is None or self._state.status == "closed":
            return self.state()
        if duration_s and not movie.duration_s:
            movie.duration_s = round(duration_s, 1)
        self._state.position_s, self._at = max(0.0, position_s), time.monotonic()
        if ended and self._state.status == "playing":
            self._state.status = "paused"
            return await self._changed()
        self._save()
        return self.state()

    # ------------------------------------------------------------------ marks
    def marks(self, query: str | None = None) -> tuple[MovieInfo, list[MovieMark]]:
        """A movie's marks by time (none = the current movie)."""
        movie = self.find(query)
        return movie, list(self._marks.get(movie.movie_id, []))

    def add_mark(self, label: str, position_s: float | None = None,
                 query: str | None = None) -> tuple[MovieInfo, MovieMark]:
        """Save a spot at position_s, or (none) a few seconds before where the movie is now. A label that's there
        already moves."""
        label = " ".join(str(label).split())
        if not label:
            raise ValueError("say what happens there")
        movie = self.find(query)
        if position_s is None:
            if not self._is_on(movie.movie_id):
                raise ValueError(f"{movie.title} isn't on: say when in it")
            position_s = self._position() - MARK_NOW_LEAD_S
        duration = movie.duration_s or (self._state.movie.duration_s if self._is_on(movie.movie_id) else None)
        pos = max(0.0, float(position_s))
        if duration:
            pos = min(pos, duration)
        marks = self._marks.setdefault(movie.movie_id, [])
        mark = next((m for m in marks if m.label.lower() == label.lower()), None)
        if mark is None:
            mark = MovieMark(mark_id=self._next_mark_id(), label=label, position_s=round(pos, 1))
            marks.append(mark)
        else:
            mark.position_s = round(pos, 1)
        marks.sort(key=lambda m: m.position_s)
        self._save()
        return movie, mark

    def remove_mark(self, ref: str) -> MovieMark:
        movie_id, mark = self.find_mark(ref)
        self._marks[movie_id].remove(mark)
        if not self._marks[movie_id]:
            del self._marks[movie_id]
        self._save()
        return mark

    def find_mark(self, ref: str) -> tuple[str, MovieMark]:
        """By id, label, or the words of it ("where Tony builds the suit"). Ties go to the movie that's on."""
        q = " ".join(str(ref).split()).lower()
        if not self._marks:
            raise LookupError("no saved spots yet")
        on = self._state.movie.movie_id if self._state.movie and self._state.status != "closed" else None
        ordered = sorted(self._marks.items(), key=lambda kv: kv[0] != on)
        for movie_id, marks in ordered:
            for m in marks:
                if q in (m.mark_id, m.label.lower()):
                    return movie_id, m
        words = _words(q)
        best: tuple[str, MovieMark] | None = None
        best_score = (0, 0.0)
        for movie_id, marks in ordered:
            for m in marks:
                label = _words(m.label)
                shared = len(words & label)
                score = (shared, shared / max(1, len(label)))
                if shared and score > best_score:
                    best, best_score = (movie_id, m), score
        if best is None:
            saved = ", ".join(f"{m.label} ({clock(m.position_s)})" for _, ms in ordered for m in ms)
            raise LookupError(f"no saved spot like {ref!r}; saved: {saved}")
        return best

    async def goto_mark(self, ref: str) -> tuple[MovieMark, MovieState]:
        """Jump to a mark and play. A mark in another movie (or with the screen closed) puts that movie on."""
        movie_id, mark = self.find_mark(ref)
        if not self._is_on(movie_id):
            movie = self.find(movie_id)
            self._remember()
            self._state.movie = movie
        self._jump(mark.position_s)
        self._state.status = "playing"
        return mark, await self._changed()

    # ------------------------------------------------------------------ internals
    def _is_on(self, movie_id: str) -> bool:
        movie = self._state.movie
        return movie is not None and movie.movie_id == movie_id and self._state.status != "closed"

    def _next_mark_id(self) -> str:
        ids = [int(m.mark_id[1:]) for ms in self._marks.values() for m in ms if m.mark_id[1:].isdigit()]
        return f"m{max(ids, default=0) + 1}"

    def _position(self) -> float:
        pos = self._state.position_s
        if self._state.status == "playing":
            pos += time.monotonic() - self._at
        movie = self._state.movie
        return min(pos, movie.duration_s) if movie and movie.duration_s else pos

    def _settle(self) -> None:
        """Freeze the estimated position before the status changes."""
        self._state.position_s, self._at = self._position(), time.monotonic()

    def _jump(self, position_s: float) -> None:
        movie = self._state.movie
        pos = max(0.0, float(position_s))
        if movie and movie.duration_s:
            pos = min(pos, movie.duration_s)
        self._state.position_s, self._at = pos, time.monotonic()
        self._state.seek_rev += 1

    def _remember(self) -> None:
        if self._state.movie is not None:
            self._places[self._state.movie.movie_id] = round(self._position(), 1)

    def _need_on(self) -> None:
        if self._state.movie is None or self._state.status == "closed":
            raise ValueError("the movie isn't on")

    async def _changed(self) -> MovieState:
        self._save()
        state = self.state()
        await self._publish(state)
        return state

    def _load(self) -> None:
        try:
            data = json.loads(self.state_path.read_text(encoding="utf-8"))
            self._places = {str(k): float(v) for k, v in data.get("places", {}).items()}
            self._state.volume = float(data.get("volume", self._state.volume))
            self._marks = {str(k): [MovieMark.model_validate(m) for m in v] for k, v in data.get("marks", {}).items()}
        except FileNotFoundError:
            pass
        except (OSError, ValueError, TypeError, AttributeError) as e:
            log.warning("movie state unreadable (%s); starting fresh", e)

    def _save(self) -> None:
        self._remember()
        self.state_path.parent.mkdir(parents=True, exist_ok=True)
        tmp = self.state_path.with_suffix(".tmp")
        marks = {k: [m.model_dump() for m in v] for k, v in self._marks.items()}
        tmp.write_text(json.dumps({"places": self._places, "volume": self._state.volume, "marks": marks}),
                       encoding="utf-8")
        tmp.replace(self.state_path)


def clock(seconds: float) -> str:
    """'1:02:30' / '45:00': a spot in a movie."""
    s = max(0, round(seconds))
    h, rest = divmod(s, 3600)
    m, s = divmod(rest, 60)
    return f"{h}:{m:02d}:{s:02d}" if h else f"{m}:{s:02d}"


def _words(text: str) -> set[str]:
    """The words that tell marks apart, roughly stemmed ("builds", "building" -> "build")."""
    out = set()
    for w in re.findall(r"[a-z0-9]+", text.lower()):
        if len(w) < 2 or w in STOP_WORDS:
            continue
        for suffix in ("ing", "ed", "es", "s", "e"):
            if w.endswith(suffix) and len(w) - len(suffix) >= 3:
                w = w[: -len(suffix)]
                break
        out.add(w)
    return out


def _sidecar(path: Path) -> dict:
    try:
        data = json.loads(path.read_text(encoding="utf-8"))
        return data if isinstance(data, dict) else {}
    except FileNotFoundError:
        return {}
    except (OSError, ValueError) as e:
        log.warning("%s unreadable (%s)", path.name, e)
        return {}
