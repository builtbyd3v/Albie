"""Desk extras: focus timers, notes, movie screen, widgets, sticky replay, tools and REST (no hub, no Unity)."""
from __future__ import annotations

import asyncio
import json
from pathlib import Path
from typing import Any

import pytest
from fastapi import FastAPI
from fastapi.testclient import TestClient

from desk import DeskService
from desk import tools as dtools
from desk.notes import NotesStore
from desk.router import router
from desk.timer import human, ring_line
from events import EventBus, TimerState


class Recorder(EventBus):
    def __init__(self) -> None:
        super().__init__()
        self.events: list[tuple[str, dict[str, Any]]] = []

    async def publish(self, type_: str, payload: Any) -> None:
        self.events.append((type_, payload.model_dump() if hasattr(payload, "model_dump") else payload))
        await super().publish(type_, payload)

    def types(self) -> list[str]:
        return [t for t, _ in self.events]

    def last(self, type_: str) -> dict[str, Any]:
        return next(p for t, p in reversed(self.events) if t == type_)


def make_desk(tmp_path: Path, movies: bool = True) -> tuple[DeskService, Recorder, list[str]]:
    if movies:
        media = tmp_path / "media"
        media.mkdir(parents=True)
        (media / "obsession.mp4").write_bytes(b"\x00" * 4096)
        (media / "obsession.json").write_text(json.dumps({"title": "Obsession", "year": 2025, "duration_s": 6600}))
        (media / "obsession.en.srt").write_text("1\n00:00:01,000 --> 00:00:02,000\nHi\n")
        (media / "spring.mp4").write_bytes(b"\x00" * 16)
        (media / "half.part.mp4").write_bytes(b"\x00" * 16)  # still encoding: not in the library
    spoken: list[str] = []

    async def speak(pet_id: str, text: str) -> bool:
        spoken.append(text)
        return True

    bus = Recorder()
    return DeskService(tmp_path, bus, speak), bus, spoken


# ---------------------------------------------------------------------- timers
def ts(label: str, duration: float) -> TimerState:
    return TimerState(timer_id="t1", label=label, status="ringing", duration_s=duration, remaining_s=0)


def test_human_and_ring_line() -> None:
    assert human(3600) == "1 hour"
    assert human(5400) == "1 hour 30 minutes"
    assert human(25 * 60) == "25 minutes"
    assert human(45) == "45 seconds"
    assert human(1) == "1 second"
    assert ring_line(ts("Focus", 3600)) == "Time's up! That's 1 hour of focus."
    assert ring_line(ts("tea", 240)) == "Time's up! Your tea timer is done."


def test_timer_runs_rings_and_stops(tmp_path: Path) -> None:
    desk, bus, spoken = make_desk(tmp_path)

    async def run() -> None:
        s = await desk.timers.start(0.2)
        assert (s.status, s.label, s.duration_s) == ("running", "Focus", 0.2)
        assert desk.widgets.timer == "open"
        await asyncio.sleep(0.35)
        assert desk.timers.find("Focus").status == "ringing"
        assert bus.last("timers.updated")["timers"][0]["status"] == "ringing"
        assert bus.last("pet.say")["text"].startswith("Time's up!") and spoken
        stopped = await desk.timers.stop(s.timer_id)
        assert stopped.status == "ringing" and len(desk.timers) == 0
        assert bus.last("timers.updated")["timers"] == [] and desk.widgets.timer == "closed"

    asyncio.run(run())


def test_several_named_timers_soonest_first(tmp_path: Path) -> None:
    desk, bus, spoken = make_desk(tmp_path)

    async def run() -> None:
        focus = await desk.timers.start(3600)
        tea = await desk.timers.start(0.2, "tea")
        again = await desk.timers.start(600, "tea")
        assert (focus.label, tea.label, again.label) == ("Focus", "tea", "tea 2")
        assert [t.label for t in desk.timers.states()] == ["tea", "tea 2", "Focus"]  # by time left
        await asyncio.sleep(0.35)
        assert [t.status for t in desk.timers.states()] == ["ringing", "running", "running"]  # ringing first
        assert spoken == ["Time's up! Your tea timer is done."]
        assert [t.label for t in desk.timers.pick(None, "stop")] == ["tea"]  # "turn it off": the ringing one
        with pytest.raises(LookupError, match="which one"):
            desk.timers.pick(None, "pause")  # two running
        with pytest.raises(LookupError, match="2 timers match"):
            desk.timers.find("te")
        assert desk.timers.find("tea 2").timer_id == again.timer_id
        await desk.timers.stop("tea")
        assert [t.label for t in desk.timers.pick("all", "stop")] == ["tea 2", "Focus"]
        for _ in range(3):
            await desk.timers.start(60)
        with pytest.raises(ValueError, match="5 timers"):
            await desk.timers.start(60)
        await desk.timers.stop_all()
        assert len(desk.timers) == 0 and desk.widgets.timer == "closed"

    asyncio.run(run())


def test_timer_pause_resume_add(tmp_path: Path) -> None:
    desk, bus, _ = make_desk(tmp_path)

    async def run() -> None:
        with pytest.raises(LookupError):
            await desk.timers.pause("Focus")  # nothing set
        t = await desk.timers.start(60, "tea")
        paused = await desk.timers.pause("tea")
        assert paused.status == "paused" and 59 < paused.remaining_s <= 60
        with pytest.raises(ValueError):
            await desk.timers.pause("tea")
        await asyncio.sleep(0.05)
        assert desk.timers.find("tea").remaining_s == paused.remaining_s  # frozen
        added = await desk.timers.add(t.timer_id, 120)
        assert added.status == "paused" and added.remaining_s > 179 and added.duration_s == 180
        less = await desk.timers.add("tea", -1000)
        assert less.remaining_s == 1.0  # never below a second
        s = await desk.timers.resume("tea")
        assert s.status == "running"
        await desk.timers.stop_all()

    asyncio.run(run())


def test_timer_snooze_after_ring_and_minimized_pops_open(tmp_path: Path) -> None:
    desk, _, _ = make_desk(tmp_path)

    async def run() -> None:
        await desk.timers.start(0.1)
        await desk.set_widgets(timer="minimized")
        await asyncio.sleep(0.25)
        assert desk.timers.find("Focus").status == "ringing" and desk.widgets.timer == "open"
        s = await desk.timers.add("Focus", 300)
        assert s.status == "running" and 299 < s.remaining_s <= 300
        with pytest.raises(ValueError):
            await desk.timers.start(0)
        await desk.set_widgets(timer="closed")  # closing the stack stops them
        assert len(desk.timers) == 0
        with pytest.raises(ValueError):
            await desk.set_widgets(timer="open")  # nothing to show

    asyncio.run(run())


def test_stopping_cancels_the_ring(tmp_path: Path) -> None:
    desk, bus, spoken = make_desk(tmp_path)

    async def run() -> None:
        t = await desk.timers.start(0.1)
        await desk.timers.stop(t.timer_id)
        await asyncio.sleep(0.2)
        assert not spoken and len(desk.timers) == 0

    asyncio.run(run())


# ---------------------------------------------------------------------- notes
def test_notes_add_find_edit_remove_undo_persist(tmp_path: Path) -> None:
    desk, bus, _ = make_desk(tmp_path)

    async def run() -> None:
        a = await desk.add_note("Push the demo build by 3")
        b = await desk.add_note("  buy   more coffee ")
        assert b.text == "buy more coffee"
        assert [n.id for n in desk.notes.all()] == [b.id, a.id]  # newest first
        assert desk.widgets.notes == "open"  # adding shows the panel
        await desk.edit_note("demo", done=True)
        assert desk.notes.find(a.id).done
        await desk.remove_note("coffee")
        assert [n.text for n in desk.notes.all()] == ["Push the demo build by 3"]
        assert bus.last("notes.updated")["last_removed"]["text"] == "buy more coffee"
        await desk.undo_note()
        assert len(desk.notes) == 2 and desk.notes.updated().last_removed is None
        with pytest.raises(LookupError):
            await desk.undo_note()
        with pytest.raises(ValueError):
            await desk.add_note("   ")

    asyncio.run(run())
    again = NotesStore(tmp_path / "desk" / "notes.json")
    assert [n.text for n in again.all()] == ["buy more coffee", "Push the demo build by 3"]
    assert again.find("demo").done


def test_notes_find_is_strict_and_minimized_stays_a_dot(tmp_path: Path) -> None:
    desk, _, _ = make_desk(tmp_path)

    async def run() -> None:
        await desk.add_note("call mom")
        await desk.add_note("call the dentist")
        with pytest.raises(LookupError, match="2 notes match"):
            desk.notes.find("call")
        with pytest.raises(LookupError, match="no note"):
            desk.notes.find("groceries")
        await desk.set_widgets(notes="minimized")
        await desk.add_note("third")
        assert desk.widgets.notes == "minimized"

    asyncio.run(run())


# ---------------------------------------------------------------------- movie
def test_movie_library(tmp_path: Path) -> None:
    desk, _, _ = make_desk(tmp_path)
    movies = {m.movie_id: m for m in desk.movie.library()}
    assert set(movies) == {"obsession", "spring"}
    o = movies["obsession"]
    assert (o.title, o.year, o.duration_s) == ("Obsession", 2025, 6600)
    assert o.url == "/movie/files/obsession.mp4" and o.subtitles_url == "/movie/files/obsession.en.srt"
    assert movies["spring"].title == "Spring" and movies["spring"].subtitles_url is None
    assert desk.movie.find("obses").movie_id == "obsession"
    with pytest.raises(LookupError, match="there's"):
        desk.movie.find("titanic")
    for bad in ("../notes.json", "obsession.json", "..\\x.mp4", "nope.mp4"):
        with pytest.raises(LookupError):
            desk.movie.file(bad)


def test_movie_play_pause_seek_close_resumes_where_you_left_off(tmp_path: Path) -> None:
    desk, bus, _ = make_desk(tmp_path)

    async def run() -> None:
        s = await desk.movie.play("obsession")
        assert (s.status, s.position_s, s.seek_rev) == ("playing", 0, 1)
        assert desk.widgets.movie == "open"
        await desk.movie.report(1200.0, duration_s=6600)
        await asyncio.sleep(0.1)
        assert desk.movie.state().position_s > 1200  # estimated while playing
        p = await desk.movie.pause()
        assert p.status == "paused" and p.seek_rev == 1  # pausing doesn't seek
        s = await desk.movie.skip(-30)
        assert s.seek_rev == 2 and abs(s.position_s - (p.position_s - 30)) < 0.2
        s = await desk.movie.seek(99999)
        assert s.position_s == 6600  # clamped to the end
        await desk.movie.seek(1500)
        await desk.movie.set_volume(1.7)
        assert desk.movie.state().volume == 1.0
        c = await desk.movie.close()
        assert c.status == "closed" and desk.widgets.movie == "closed"
        with pytest.raises(ValueError):
            await desk.movie.pause()
        s = await desk.movie.resume()  # = play: back where it was
        assert s.status == "playing" and abs(s.position_s - 1500) < 0.2 and s.seek_rev == 5
        s = await desk.movie.play(from_start=True)
        assert s.position_s == 0 and s.seek_rev == 6

    asyncio.run(run())


def test_movie_place_and_volume_survive_a_restart_and_the_end_starts_over(tmp_path: Path) -> None:
    desk, _, _ = make_desk(tmp_path)

    async def run() -> None:
        await desk.movie.play()
        await desk.movie.set_volume(0.3)
        await desk.movie.seek(3000)
        await desk.movie.close()

    asyncio.run(run())
    again = DeskService(tmp_path, Recorder(), desk.speak)

    async def later() -> None:
        s = await again.movie.play("obsession")
        assert abs(s.position_s - 3000) < 0.2 and s.volume == 0.3
        await again.movie.report(6590, ended=True)
        assert again.movie.state().status == "paused"
        await again.movie.close()
        s = await again.movie.play()
        assert s.position_s == 0  # finished: starts over

    asyncio.run(later())


def test_movie_marks_save_find_jump_and_survive_a_restart(tmp_path: Path) -> None:
    desk, bus, _ = make_desk(tmp_path)

    async def run() -> None:
        with pytest.raises(ValueError):
            desk.movie.add_mark("the cave", query="obsession")  # not on, no time given
        _, suit = desk.movie.add_mark("Tony Stark starts building Iron Man", 2700, "obsession")
        desk.movie.add_mark("The cave escape", 1500, "obsession")
        desk.movie.add_mark("Opening", 10, "spring")
        assert [m.label for m in desk.movie.marks("obsession")[1]] == ["The cave escape", "Tony Stark starts building Iron Man"]
        assert desk.movie.find_mark("the point where Tony builds the suit")[1] == suit
        with pytest.raises(LookupError, match="saved: "):
            desk.movie.find_mark("the dance number")

        mark, s = await desk.movie.goto_mark("where Tony starts building Iron Man")  # screen closed: puts it on
        assert mark == suit and s.status == "playing" and s.movie.movie_id == "obsession"
        assert s.position_s == 2700 and bus.last("movie.state")["seek_rev"] == s.seek_rev
        await desk.movie.pause()
        mark, s = await desk.movie.goto_mark("opening")  # another movie's spot: switches and plays
        assert s.movie.movie_id == "spring" and s.position_s == 10 and s.status == "playing"

        await desk.movie.play("obsession")
        await desk.movie.seek(3600)
        _, now = desk.movie.add_mark("Suit test flight")  # where it is now, a few seconds back
        assert 3590 < now.position_s < 3600
        _, moved = desk.movie.add_mark("the cave escape", 1550)  # same label: moves it
        assert moved.position_s == 1550 and len(desk.movie.marks()[1]) == 3
        assert desk.movie.remove_mark("flight").label == "Suit test flight"

    asyncio.run(run())
    again = DeskService(tmp_path, Recorder(), desk.speak)
    assert [(m.label, m.position_s) for m in again.movie.marks("obsession")[1]] == [
        ("The cave escape", 1550), ("Tony Stark starts building Iron Man", 2700)]
    assert again.movie.find_mark("opening")[0] == "spring"


# ---------------------------------------------------------------------- sticky replay
class FakeSocket:
    def __init__(self) -> None:
        self.sent: list[dict[str, Any]] = []

    async def send_json(self, message: dict[str, Any]) -> None:
        self.sent.append(message)


def test_late_client_gets_fresh_state(tmp_path: Path) -> None:
    desk, bus, _ = make_desk(tmp_path)

    async def run() -> None:
        await desk.timers.start(60)
        await asyncio.sleep(0.2)
        ws = FakeSocket()
        await bus.attach(ws)  # type: ignore[arg-type]
        by_type = {m["type"]: m["payload"] for m in ws.sent}
        assert {"ui.widgets", "timers.updated", "notes.updated", "movie.state"} <= set(by_type)
        assert by_type["timers.updated"]["timers"][0]["remaining_s"] < 59.9  # computed now, not the start payload
        assert by_type["ui.widgets"]["timer"] == "open"
        assert bus.latest("timers.")["timers.updated"]["timers"][0]["status"] == "running"
        await desk.timers.stop_all()

    asyncio.run(run())


# ---------------------------------------------------------------------- tools
def test_tools(tmp_path: Path) -> None:
    desk, _, _ = make_desk(tmp_path)

    async def run() -> None:
        out = await dtools.call_tool("timer_start", {"minutes": 60}, desk)
        assert out["status"] == "running" and out["length"] == "1 hour" and out["name"] == "Focus"
        await dtools.call_tool("timer_start", {"minutes": 4, "label": "tea"}, desk)
        status = await dtools.call_tool("timer_status", {}, desk)
        assert [t["name"] for t in status["timers"]] == ["tea", "Focus"]
        assert (await dtools.call_tool("timer_control", {"action": "add"}, desk))["error"] == "minutes is missing"
        assert "which one" in (await dtools.call_tool("timer_control", {"action": "pause"}, desk))["error"]
        assert (await dtools.call_tool("timer_control", {"action": "pause", "timer": "tea"}, desk))["status"] == "paused"
        assert (await dtools.call_tool("timer_control", {"action": "stop", "timer": "tea"}, desk))["stopped"] == ["tea"]
        assert (await dtools.call_tool("timer_control", {"action": "stop"}, desk))["stopped"] == ["Focus"]  # the only one
        assert (await dtools.call_tool("timer_status", {}, desk))["timers"] == []

        out = await dtools.call_tool("notes_add", {"text": "ship the timer"}, desk)
        assert out["count"] == 1
        assert (await dtools.call_tool("notes_edit", {"note": "timer", "done": True}, desk))["edited"]["done"]
        assert (await dtools.call_tool("notes_view", {"state": "minimized"}, desk))["panel"] == "minimized"
        assert "removed" in await dtools.call_tool("notes_remove", {"note": "ship"}, desk)
        assert (await dtools.call_tool("notes_remove", {"undo": True}, desk))["restored"]["text"] == "ship the timer"
        assert (await dtools.call_tool("notes_list", {}, desk))["notes"][0]["text"] == "ship the timer"
        assert "error" in await dtools.call_tool("notes_view", {"state": "huge"}, desk)

        out = await dtools.call_tool("movie_play", {"title": "obsession"}, desk)
        assert out["status"] == "playing" and out["title"] == "Obsession" and "left" in out
        assert (await dtools.call_tool("movie_control", {"action": "volume", "volume": 0.5}, desk))["volume"] == "50%"
        assert (await dtools.call_tool("movie_control", {"action": "volume", "volume": 20}, desk))["volume"] == "20%"
        assert (await dtools.call_tool("movie_control", {"action": "volume", "by": 0.15}, desk))["volume"] == "35%"
        assert (await dtools.call_tool("movie_control", {"action": "volume", "by": -1}, desk))["volume"] == "0%"
        out = await dtools.call_tool("movie_marks", {"action": "save", "label": "Tony builds the suit", "seconds": 2700}, desk)
        assert out == {"saved": {"label": "Tony builds the suit", "at": "45:00"}, "movie": "Obsession"}
        await dtools.call_tool("movie_marks", {"action": "save", "label": "Finale", "seconds": 6000}, desk)
        assert (await dtools.call_tool("movie_status", {}, desk))["marks"][1] == {"label": "Finale", "at": "1:40:00"}
        out = await dtools.call_tool("movie_control", {"action": "seek", "mark": "where he builds the suit"}, desk)
        assert out["mark"] == "Tony builds the suit" and out["at"] == "45 minutes"
        assert "error" in await dtools.call_tool("movie_control", {"action": "seek", "mark": "the dance"}, desk)
        assert (await dtools.call_tool("movie_marks", {"action": "remove", "label": "finale"}, desk))["removed"]["label"] == "Finale"
        assert len((await dtools.call_tool("movie_marks", {"action": "list"}, desk))["marks"]) == 1
        assert (await dtools.call_tool("movie_control", {"action": "close"}, desk))["status"] == "closed"
        assert set((await dtools.call_tool("movie_status", {}, desk))["library"]) == {"Obsession", "Spring"}
        assert "error" in await dtools.call_tool("movie_play", {"title": "titanic"}, desk)
        assert "error" in await dtools.call_tool("desk_nope", {}, desk)

    asyncio.run(run())
    assert {t["name"] for t in dtools.TOOLS} == {
        "timer_start", "timer_control", "timer_status", "notes_add", "notes_list", "notes_edit", "notes_remove",
        "notes_view", "movie_play", "movie_control", "movie_marks", "movie_status"}


# ---------------------------------------------------------------------- REST
@pytest.fixture()
def client(tmp_path: Path):
    desk, _, _ = make_desk(tmp_path)
    app = FastAPI()
    app.state.desk = desk
    app.include_router(router)
    with TestClient(app) as c:
        yield c


def test_rest_timers_and_widgets(client: TestClient) -> None:
    assert client.get("/timers").json() == {"timers": []}
    assert client.post("/timers", json={}).status_code == 422
    s = client.post("/timers", json={"minutes": 25, "label": "standup"}).json()
    assert s["status"] == "running" and s["duration_s"] == 1500 and s["label"] == "standup"
    tid = s["timer_id"]
    assert client.post(f"/timers/{tid}/pause").json()["status"] == "paused"
    assert client.post(f"/timers/{tid}/pause").status_code == 422
    assert client.post(f"/timers/{tid}/add", json={"minutes": 5}).json()["duration_s"] == 1800
    assert client.post("/timers/nope/stop").status_code == 404
    client.post("/timers", json={"seconds": 90})
    assert [t["label"] for t in client.get("/timers").json()["timers"]] == ["Focus", "standup"]
    assert client.post("/ui/widgets", json={"timer": "minimized"}).json()["timer"] == "minimized"
    assert client.post("/ui/widgets", json={"notes": "sideways"}).status_code == 422
    assert client.post(f"/timers/{tid}/stop").json()["label"] == "standup"
    assert client.post("/timers/stop_all").json() == {"timers": []}
    assert client.get("/ui/widgets").json() == {"timer": "closed", "notes": "closed", "movie": "closed"}


def test_rest_notes(client: TestClient) -> None:
    n = client.post("/notes", json={"text": "remember the chime"}).json()
    assert client.post(f"/notes/{n['id']}/edit", json={"done": True}).json()["done"] is True
    assert client.post("/notes", json={"text": ""}).status_code == 422
    assert client.post(f"/notes/{n['id']}/remove").json()["notes"] == []
    assert client.post("/notes/nope/remove").status_code == 404
    assert client.post("/notes/undo").json()["notes"][0]["text"] == "remember the chime"
    assert client.get("/notes").json()["last_removed"] is None


def test_rest_movie_and_byte_ranges(client: TestClient) -> None:
    assert [m["movie_id"] for m in client.get("/movie/library").json()] == ["obsession", "spring"]
    s = client.post("/movie/play", json={"movie_id": "obsession"}).json()
    assert s["status"] == "playing" and s["movie"]["url"] == "/movie/files/obsession.mp4"
    assert client.post("/movie/seek", json={}).status_code == 422
    assert client.post("/movie/seek", json={"position_s": 60}).json()["seek_rev"] == 2
    assert client.post("/movie/volume", json={"volume": 2}).status_code == 422
    assert client.post("/movie/position", json={"position_s": 61.5}).json()["position_s"] >= 61.5
    part = client.get("/movie/files/obsession.mp4", headers={"Range": "bytes=0-99"})
    assert part.status_code == 206 and len(part.content) == 100 and part.headers["content-type"] == "video/mp4"
    assert "--> 00:00:02,000" in client.get("/movie/files/obsession.en.srt").text
    assert client.get("/movie/files/obsession.json").status_code == 404
    assert client.post("/movie/close").json()["status"] == "closed"
    assert client.post("/movie/pause").status_code == 422

    assert client.post("/movie/marks", json={"label": "Suit up"}).status_code == 422  # closed: needs a time
    m = client.post("/movie/marks", json={"label": "Suit up", "position_s": 2700, "movie_id": "obsession"}).json()
    assert m["label"] == "Suit up" and m["position_s"] == 2700
    assert [x["mark_id"] for x in client.get("/movie/marks", params={"movie_id": "obsession"}).json()] == [m["mark_id"]]
    s = client.post("/movie/seek", json={"mark": "suit"}).json()
    assert s["status"] == "playing" and s["position_s"] == 2700
    assert client.post("/movie/seek", json={"mark": "nothing like it"}).status_code == 404
    assert client.post(f"/movie/marks/{m['mark_id']}/remove").json()["label"] == "Suit up"
    assert client.get("/movie/marks").json() == []
