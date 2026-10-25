"""The movie screen: an .mp4 from .data/media on a big screen above the desk ("Bit, put the movie on").

Library: every <id>.mp4 in the media folder (gitignored with the rest of .data). Optional beside it: <id>.json
{title, year, duration_s} and <id>.en.srt subtitles. Encode new files as H.264 8-bit + stereo AAC with +faststart
(README "Desk"): Unity's VideoPlayer plays that in the Windows editor and on the Quest.

Unity streams GET /movie/files/<id>.mp4 (byte ranges, so it can seek) and keeps time; the hub keeps the state and
where you stopped in each movie (.data/desk/movie.json), so it picks up there next time.
"""
from __future__ import annotations

import json
import logging
import re
import time
from collections.abc import Awaitable, Callable
from pathlib import Path

from events import MovieInfo, MovieState

log = logging.getLogger("forge.desk")

FILE_NAME = re.compile(r"^[A-Za-z0-9][A-Za-z0-9._-]*\.(mp4|srt)$")
RESTART_NEAR_END_S = 120.0  # a movie stopped in its last two minutes starts over next time

Publish = Callable[[MovieState], Awaitable[None]]


class MovieService:
    def __init__(self, media_dir: Path, state_path: Path, publish: Publish) -> None:
        self.media_dir = Path(media_dir)
        self.state_path = Path(state_path)
        self._publish = publish
        self._state = MovieState()
        self._at = time.monotonic()  # when _state.position_s was true
        self._places: dict[str, float] = {}  # movie_id -> where it was stopped
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

    # ------------------------------------------------------------------ internals
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
        except FileNotFoundError:
            pass
        except (OSError, ValueError, TypeError, AttributeError) as e:
            log.warning("movie state unreadable (%s); starting fresh", e)

    def _save(self) -> None:
        self._remember()
        self.state_path.parent.mkdir(parents=True, exist_ok=True)
        tmp = self.state_path.with_suffix(".tmp")
        tmp.write_text(json.dumps({"places": self._places, "volume": self._state.volume}), encoding="utf-8")
        tmp.replace(self.state_path)


def _sidecar(path: Path) -> dict:
    try:
        data = json.loads(path.read_text(encoding="utf-8"))
        return data if isinstance(data, dict) else {}
    except FileNotFoundError:
        return {}
    except (OSError, ValueError) as e:
        log.warning("%s unreadable (%s)", path.name, e)
        return {}
