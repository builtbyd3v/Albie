"""'Albie, stop' while he works: the task ends, then "Undo, continue, or leave it?" (agent/ws.py, agent/voice.py)."""

from __future__ import annotations

import asyncio
from pathlib import Path
from typing import Any

import pytest

from agent import runner as runner_mod
from agent import voice as voice_mod
from agent import ws as ws_mod
from agent.events import Event


@pytest.mark.parametrize("text", ["Albie, shut up.", "Albie, be quiet.", "Albie, cancel.", "Never mind.", "Shut up, Albie."])
def test_more_ways_to_say_stop(text: str) -> None:
    """Live 22:36: 'Albie, shut up' was answered."""
    assert voice_mod.is_stop(text) or voice_mod.has_named_stop(text)


@pytest.mark.parametrize("text, choice", [
    ("Undo it.", "undo"), ("Yeah, revert that.", "undo"), ("Continue.", "continue"), ("Keep going.", "continue"),
    ("Go on.", "continue"), ("Pick up where you left off.", "continue"), ("Leave it.", "leave"), ("No.", "leave"),
    ("What time is it?", None),
])
def test_the_answer_to_the_stop_question(text: str, choice: str | None) -> None:
    assert voice_mod.stop_choice(text) == choice


class FakeVoice:
    def __init__(self) -> None:
        self.choice_until = 0.0
        self.last_change: tuple[str, float] | None = None
        self.said: list[str] = []

    async def say(self, text: str) -> None:
        self.said.append(text)


def _session(voice: FakeVoice) -> ws_mod.Session:
    s = ws_mod.Session(ws=None)  # type: ignore[arg-type]
    s.voice = voice  # type: ignore[assignment]
    return s


def test_stopping_a_code_task_asks_and_undo_restores_its_files(tmp_path: Path, monkeypatch: pytest.MonkeyPatch) -> None:
    sent: list[Event] = []

    async def broadcast(event: Event) -> None:
        sent.append(event)

    monkeypatch.setattr(ws_mod.bus, "broadcast", broadcast)
    edited, created = tmp_path / "index.html", tmp_path / "start.js"
    edited.write_text("<button>Launch</button>")

    async def run() -> None:
        voice = FakeVoice()
        s = _session(voice)

        async def task() -> str:
            runner_mod.TASK_EDITS["bit"] = {str(edited): "<button>Launch</button>", str(created): None}
            edited.write_text("<button class=center>Start</button>")  # the runner got this far
            created.write_text("animate()")
            await asyncio.sleep(10)
            return ""

        s.turn, s.turn_mode, s.turn_text = asyncio.create_task(task()), "code", "Center the start button"
        await asyncio.sleep(0.01)
        await ws_mod._stop_and_ask(s, voice)
        await asyncio.sleep(0.01)
        assert s.turn.done()
        card = [e for e in sent if e.type == "pet.say"][-1].payload
        assert card["text"] == "Stopped. Want me to undo what I changed, pick up where I left off, or leave it?"
        assert [a["action_id"] for a in card["card"]["actions"]] == ["stop:undo", "stop:continue", "stop:leave"]
        assert voice.choice_until > 0
        await ws_mod._on_card_action(s, ws_mod.PetCardAction(pet_id="bit", action_id="stop:undo"))
        assert edited.read_text() == "<button>Launch</button>" and not created.exists()
        assert voice.choice_until == 0.0 and s.stopped is None

    asyncio.run(run())


def test_continue_restarts_the_same_task(monkeypatch: pytest.MonkeyPatch) -> None:
    asked: list[Any] = []

    async def on_ask(s: Any, ask: Any) -> None:
        asked.append(ask)

    async def broadcast(event: Event) -> None:
        pass

    monkeypatch.setattr(ws_mod, "_on_ask", on_ask)
    monkeypatch.setattr(ws_mod.bus, "broadcast", broadcast)

    async def run() -> None:
        voice = FakeVoice()
        s = _session(voice)
        s.stopped = {"task": "Center the start button", "edits": {}, "cad": False}
        await ws_mod._stop_choice(s, "continue")
        await asyncio.sleep(0.01)
        assert asked and asked[0].mode == "code" and asked[0].text.startswith("Center the start button")
        assert "continue from there" in asked[0].text

    asyncio.run(run())


def test_stopping_while_he_only_talks_asks_nothing(monkeypatch: pytest.MonkeyPatch) -> None:
    sent: list[Event] = []

    async def broadcast(event: Event) -> None:
        sent.append(event)

    monkeypatch.setattr(ws_mod.bus, "broadcast", broadcast)

    async def run() -> None:
        voice = FakeVoice()
        await ws_mod._stop_and_ask(_session(voice), voice)
        assert sent == [] and voice.choice_until == 0.0

    asyncio.run(run())


def test_a_bare_undo_in_the_window_is_answered_by_the_hub_not_grok() -> None:
    from .test_agent_voice import MockGrok, Sink, _session as grok_session, _settle

    async def run() -> None:
        grok, sink = MockGrok(), Sink()
        s = grok_session(grok, sink)
        chosen: list[str] = []

        async def on_choice(c: str) -> None:
            chosen.append(c)

        s.on_choice = on_choice
        s.choice_until = 10**12
        await s.begin_turn()
        await s.feed(b"\x10\x00" * 9600)
        await s.commit_turn()
        grok.push(type="input_audio_buffer.committed")
        grok.push(type="conversation.item.input_audio_transcription.completed", transcript="Undo it.")
        grok.push(type="response.created")
        await _settle()
        assert chosen == ["undo"] and {"type": "response.cancel"} in grok.sent
        await s.close()

    asyncio.run(run())


def test_stopping_his_training_run_offers_undo_and_train_again(monkeypatch: pytest.MonkeyPatch) -> None:
    sent: list[Event] = []
    calls: list[tuple[str, dict]] = []

    async def broadcast(event: Event) -> None:
        sent.append(event)

    async def call(name: str, args: dict, pet_id: str) -> dict:
        calls.append((name, args))
        return {"ok": True}

    async def stop_training() -> bool:
        return True  # a run Albie started was training

    monkeypatch.setattr(ws_mod.bus, "broadcast", broadcast)
    monkeypatch.setattr(ws_mod.tools, "call", call)
    monkeypatch.setitem(ws_mod.task_stoppers, "ml", stop_training)

    async def run() -> None:
        voice = FakeVoice()
        voice.last_ml = ("ml_apply", {})  # type: ignore[attr-defined]
        s = _session(voice)
        await ws_mod._stop_and_ask(s, voice)
        card = [e for e in sent if e.type == "pet.say"][-1].payload
        assert card["text"] == "Stopped the training. Want me to undo the change, start it again, or leave it?"
        await ws_mod._stop_choice(s, "undo")
        assert calls == [("ml_undo", {})]
        s.stopped = {"task": None, "edits": {}, "cad": False, "ml": ("ml_train", {"epochs": 5})}
        await ws_mod._stop_choice(s, "continue")
        assert calls[-1] == ("ml_train", {"epochs": 5})

    asyncio.run(run())
