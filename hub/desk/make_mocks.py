"""Write desk mocks for Unity's hub-off sandbox, built from the hub's own models so they match what it sends.

    python -m desk.make_mocks [out_dir]     (from hub/; default hub/.data/mocks/desk)

Resources/Mocks/ holds the REST answers (ForgeNet.Get reads Resources/Mocks/<path>.json): timer.json (GET /timer),
notes.json (GET /notes), movie.json (GET /movie), movie/library.json, ui/widgets.json. desk/sequence.json is a
ForgeMockPlayer list that walks through the story: Bit sets a timer, notes come in, the notes shrink to a dot, the
movie comes on, the timer rings and is turned off. Copy both folders into unity/Assets/_Forge/Mocks/.
The movie has no file in mock mode: point the player at a local clip there.
"""
from __future__ import annotations

import json
import sys
from pathlib import Path
from typing import Any

from pydantic import BaseModel

from events import MovieInfo, MovieState, Note, NotesUpdated, TimerState, UiWidgets

HUB = Path(__file__).resolve().parents[1]
T0 = 1793030400.0  # a fixed "now" so the files don't change between runs

MOVIE = MovieInfo(movie_id="obsession", title="Obsession", year=2025, duration_s=6519.8,
                  url="/movie/files/obsession.mp4", subtitles_url="/movie/files/obsession.en.srt")
NOTES = [Note(id="n3a91c0", text="Ask Adrian about the gator's jump timing", created_at=T0 - 60, updated_at=T0 - 60),
         Note(id="n1f2e77", text="Push the demo build by 3", done=True, created_at=T0 - 1800, updated_at=T0 - 300),
         Note(id="n08bd42", text="Re-record the fallback ML run", created_at=T0 - 7200, updated_at=T0 - 7200)]
TIMER = TimerState(timer_id="t4c2", label="Focus", status="running", duration_s=3600, remaining_s=3600)


def sequence() -> list[dict[str, Any]]:
    """[{at, type, payload}] for ForgeMockPlayer. The timer is 25 s long here so it rings during the sandbox."""
    run = TIMER.model_copy(update={"duration_s": 25, "remaining_s": 25})
    steps: list[tuple[float, str, BaseModel | dict[str, Any]]] = [
        (0.5, "timer.state", run),
        (0.5, "ui.widgets", UiWidgets(timer="open")),
        (2.0, "notes.updated", NotesUpdated(notes=NOTES[1:])),
        (2.0, "ui.widgets", UiWidgets(timer="open", notes="open")),
        (5.0, "notes.updated", NotesUpdated(notes=NOTES)),  # "Bit, add that to my notes"
        (9.0, "ui.widgets", UiWidgets(timer="open", notes="minimized")),
        (11.0, "movie.state", MovieState(status="playing", movie=MOVIE, position_s=1200, seek_rev=1)),
        (11.0, "ui.widgets", UiWidgets(timer="open", notes="minimized", movie="open")),
        (16.0, "movie.state", MovieState(status="playing", movie=MOVIE, position_s=1205, volume=0.4, seek_rev=1)),
        (25.5, "timer.state", run.model_copy(update={"status": "ringing", "remaining_s": 0})),
        (25.5, "pet.say", {"pet_id": "bit", "text": "Time's up! That's 25 seconds of focus."}),
        (31.0, "timer.state", TimerState()),
        (31.0, "ui.widgets", UiWidgets(notes="minimized", movie="open")),
    ]
    return [{"at": at, "type": t, "payload": p.model_dump() if isinstance(p, BaseModel) else p} for at, t, p in steps]


def files() -> dict[str, Any]:
    return {
        "Resources/Mocks/timer.json": TIMER.model_dump(),
        "Resources/Mocks/notes.json": NotesUpdated(notes=NOTES).model_dump(),
        "Resources/Mocks/movie.json": MovieState(status="paused", movie=MOVIE, position_s=1200).model_dump(),
        "Resources/Mocks/movie/library.json": [MOVIE.model_dump()],
        "Resources/Mocks/ui/widgets.json": UiWidgets(timer="open", notes="open").model_dump(),
        "desk/sequence.json": sequence(),
    }


def main(out: Path) -> None:
    for rel, body in files().items():
        path = out / rel
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(json.dumps(body, indent=2) + "\n", encoding="utf-8")
        print(path.relative_to(out))


if __name__ == "__main__":
    main(Path(sys.argv[1]) if len(sys.argv) > 1 else HUB / ".data" / "mocks" / "desk")
