from __future__ import annotations

import asyncio
from typing import Any

from agent.events import Event
from agent.watcher import Watcher, line_for

SCRIPT = "/repo/hub/cad/parts/rocket.py"


def test_regen_failed_from_script_points_at_the_line() -> None:
    line = line_for("cad.regen_failed", {"source": "script", "kind": "build", "error": "fin_count must be > 0",
                                         "error_line": 42}, SCRIPT)
    assert line is not None
    assert line.text == "Line 42 broke the rocket. I kept the last good one."
    assert (line.path, line.line, line.note) == (SCRIPT, 42, "fin_count must be > 0")


def test_regen_failed_from_pet_or_ui_stays_quiet() -> None:
    # The pet sees its own tool error; only edits to the script surprise the user.
    assert line_for("cad.regen_failed", {"source": "pet", "kind": "params", "error": "x"}, SCRIPT) is None


def test_saved_file_errors_and_fixed() -> None:
    broke = line_for("vscode.event", {"kind": "errors", "file": "src/train.py", "line": 7,
                                      "message": "undefined name 'lr'", "count": 3}, SCRIPT)
    assert broke is not None
    assert broke.text == "Line 7 in train.py has an error, plus 2 more. Want me to look?"
    assert (broke.path, broke.line) == ("src/train.py", 7)
    fixed = line_for("vscode.event", {"kind": "errors", "file": "src/train.py", "count": 0}, SCRIPT)
    assert fixed is not None and fixed.text == "Nice, train.py is clean again." and fixed.line is None


def test_errors_in_the_cad_script_are_left_to_regen_failed() -> None:
    assert line_for("vscode.event", {"kind": "errors", "file": "hub/cad/parts/rocket.py", "line": 3,
                                     "message": "bad", "count": 1}, SCRIPT) is None


def test_command_crash_speaks_but_success_and_ctrl_c_do_not() -> None:
    crash = line_for("vscode.event", {"kind": "command_finished", "command": "python train.py", "exit_code": 1}, None)
    assert crash is not None and crash.text == "That command crashed with exit code 1. Want me to look at it?"
    assert line_for("vscode.event", {"kind": "command_finished", "command": "x", "exit_code": 0}, None) is None
    assert line_for("vscode.event", {"kind": "command_finished", "command": "x", "exit_code": 130}, None) is None
    assert line_for("vscode.event", {"kind": "command_finished", "command": "x"}, None) is None


class FakeVoice:
    def __init__(self) -> None:
        self.said: list[str] = []

    async def say(self, text: str) -> None:
        self.said.append(text)


def _watcher(gap_s: float) -> tuple[Watcher, list[Event], list[tuple[Any, ...]]]:
    sent: list[Event] = []
    glows: list[tuple[Any, ...]] = []

    async def broadcast(event: Event) -> None:
        sent.append(event)

    w = Watcher(broadcast=broadcast, highlight=lambda *a: glows.append(a), gap_s=gap_s)
    w.cad_script = SCRIPT
    return w, sent, glows


def _says(sent: list[Event]) -> list[str]:
    return [e.payload["text"] for e in sent if e.type == "pet.say"]


ERR = {"kind": "errors", "file": "a.py", "line": 2, "message": "boom", "count": 1}


def test_bit_moves_speaks_and_glows() -> None:
    async def run() -> None:
        w, sent, glows = _watcher(gap_s=0)
        voice = FakeVoice()
        w.add(voice)
        w.notify("vscode.event", ERR)
        await w.idle()
        assert [e.type for e in sent] == ["pet.state", "pet.say"]
        assert sent[0].payload == {"pet_id": "bit", "mode": "working", "target_panel": "screen-main"}
        assert glows == [("a.py", 2, None, "boom", 8000)]
        assert voice.said == ["Line 2 in a.py has an error. Want me to look?"]

    asyncio.run(run())


def test_same_error_twice_is_said_once() -> None:
    async def run() -> None:
        w, sent, _ = _watcher(gap_s=0)
        w.notify("vscode.event", ERR)
        await w.idle()
        w.notify("vscode.event", ERR)
        await w.idle()
        assert len(_says(sent)) == 1

    asyncio.run(run())


def test_budget_keeps_only_the_newest_queued_line() -> None:
    async def run() -> None:
        w, sent, _ = _watcher(gap_s=0.2)
        w.notify("vscode.event", ERR)
        await asyncio.sleep(0.01)  # first line goes out now and starts the gap
        w.notify("vscode.event", {**ERR, "file": "b.py"})
        w.notify("vscode.event", {"kind": "errors", "file": "b.py", "count": 0})  # fixed before the gap ended
        await asyncio.sleep(0.05)
        assert _says(sent) == ["Line 2 in a.py has an error. Want me to look?"]
        await w.idle()
        assert _says(sent) == ["Line 2 in a.py has an error. Want me to look?", "Nice, b.py is clean again."]

    asyncio.run(run())


def test_vscode_off_does_not_stop_the_bubble() -> None:
    async def run() -> None:
        sent: list[Event] = []

        async def broadcast(event: Event) -> None:
            sent.append(event)

        def highlight(*_: Any) -> None:
            raise ConnectionRefusedError

        w = Watcher(broadcast=broadcast, highlight=highlight, gap_s=0)
        w.notify("vscode.event", ERR)
        await w.idle()
        assert len(_says(sent)) == 1

    asyncio.run(run())


def test_errors_without_a_line_number() -> None:
    line = line_for("vscode.event", {"kind": "errors", "file": "a.py", "count": 1, "message": "x"}, None)
    assert line is not None and line.text == "a.py has an error. Want me to look?" and line.line is None


def test_a_crash_does_not_push_out_a_queued_cad_line_and_dropped_lines_are_not_marked_said() -> None:
    async def run() -> None:
        w, sent, _ = _watcher(gap_s=0.1)
        w.notify("vscode.event", ERR)
        await asyncio.sleep(0.01)
        regen = {"source": "script", "kind": "build", "error": "bad", "error_line": 9, "kept_version": 1}
        w.notify("cad.regen_failed", regen)
        w.notify("vscode.event", {"kind": "command_finished", "command": "x", "exit_code": 1})
        w.notify("vscode.event", {**ERR, "file": "b.py"})
        w.notify("vscode.event", {"kind": "errors", "file": "b.py", "count": 0})  # replaces b.py's queued error
        await w.idle()
        assert _says(sent)[1:] == ["Line 9 broke the rocket. I kept the last good one.",
                                   "That command crashed with exit code 1. Want me to look at it?",
                                   "Nice, b.py is clean again."]
        w.notify("vscode.event", {**ERR, "file": "b.py"})  # never said before, so it is said now
        await w.idle()
        assert _says(sent)[-1] == "Line 2 in b.py has an error. Want me to look?"

    asyncio.run(run())


class SlowVoice(FakeVoice):
    async def say(self, text: str) -> None:
        await asyncio.sleep(10)  # Grok still talking
        self.said.append(text)


def test_closed_socket_cancels_queued_speech() -> None:
    async def run() -> None:
        w, _, _ = _watcher(gap_s=0)
        voice = SlowVoice()
        w.add(voice)
        w.notify("vscode.event", ERR)
        await w.idle()
        w.drop(voice)
        await asyncio.sleep(0.01)
        assert voice.said == [] and asyncio.all_tasks() == {asyncio.current_task()}

    asyncio.run(run())
