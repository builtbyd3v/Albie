"""workspace.mode (Addendum F): the session remembers Unity's view, Grok gets a one-line note, crew tools gate on CODE."""

from __future__ import annotations

import asyncio
import json
from typing import Any

import pytest
from fastapi.testclient import TestClient

from agent import tools
from agent.events import CLIENT_MESSAGES, Event, WorkspaceMode, parse_payload
from agent.voice import CREW_OFF_CODE_SCREEN, GrokVoiceSession, view_note
from tests.agent.test_agent_voice import MockGrok, Sink, _session, _settle


def test_payload_model() -> None:
    assert CLIENT_MESSAGES["workspace.mode"] is WorkspaceMode
    assert parse_payload(Event(type="workspace.mode", payload={"pet_id": "bit", "mode": "cad"})).mode == "cad"
    assert WorkspaceMode.model_validate({}).mode == ""  # closed


def test_view_note_is_one_line() -> None:
    for mode in ("code", "cad", "ml", "messages", "", "whistle"):
        line = view_note(mode)
        assert "\n" not in line and line.endswith(".")
    assert "CODE" in view_note("code") and "CAD" in view_note("cad") and "WHISTLE" in view_note("whistle")


def test_note_is_a_system_item_and_buffers_while_idle() -> None:
    async def run() -> None:
        grok, sink = MockGrok(), Sink()
        s = _session(grok, sink)
        await s.note("The user switched to the CAD view.")  # socket not open yet: kept, not connected for it
        assert grok.sent == []
        await s.start()
        assert grok.types() == ["session.update", "conversation.item.create"]
        item = grok.sent[1]["item"]
        assert item == {"type": "message", "role": "system",
                        "content": [{"type": "input_text", "text": "The user switched to the CAD view."}]}
        await s.note("The user switched to the CODE view.")  # live: straight through, still no response.create
        assert grok.types()[-1] == "conversation.item.create" and "response.create" not in grok.types()
        assert sink.log == []  # nothing reaches Unity
        await s.close()

    asyncio.run(run())


def test_crew_tools_answer_an_error_off_the_code_screen(monkeypatch: pytest.MonkeyPatch) -> None:
    called: list[str] = []

    async def call(name: str, args: dict[str, Any], pet_id: str) -> dict[str, Any]:
        called.append(name)
        return {"status": "hatched"}

    monkeypatch.setattr(tools, "call", call)
    monkeypatch.setattr(tools, "has", lambda name: True)
    monkeypatch.setattr(tools, "is_lead_only", lambda name: name == "spawn_agent")

    async def one(mode: str | None, name: str) -> dict[str, Any]:
        grok, sink = MockGrok(), Sink()
        s = _session(grok, sink)
        s.workspace_mode = mode
        await s.begin_turn()
        grok.push(type="response.created")
        grok.push(type="response.function_call_arguments.done", name=name, call_id="c", arguments="{}")
        grok.push(type="response.done")
        await _settle()
        out = [m for m in grok.sent if (m.get("item") or {}).get("type") == "function_call_output"]
        await s.close()
        return json.loads(out[0]["item"]["output"])

    async def run() -> None:
        assert await one("cad", "spawn_agent") == CREW_OFF_CODE_SCREEN
        assert await one("", "spawn_agent") == CREW_OFF_CODE_SCREEN
        assert await one("code", "spawn_agent") == {"status": "hatched"}
        assert await one(None, "spawn_agent") == {"status": "hatched"}  # a client that never sends workspace.mode
        assert await one("cad", "vscode_highlight") == {"status": "hatched"}  # only the crew tools are gated

    asyncio.run(run())
    assert called == ["spawn_agent", "spawn_agent", "vscode_highlight"]


class StubVoice:
    """What ws.py needs from a voice session, recording notes."""

    provider, sample_rate = "stub", 24000

    def __init__(self) -> None:
        self.notes: list[str] = []
        self.workspace_mode: str | None = None
        self.wake_router = None

    async def start(self) -> None: ...
    async def close(self) -> None: ...
    def is_echo(self, text: str) -> bool: return False
    async def note(self, text: str) -> None: self.notes.append(text)


def test_mode_over_ws_notes_the_voice_once_per_change(client: TestClient, monkeypatch: pytest.MonkeyPatch) -> None:
    import agent.ws as ws_mod

    stub = StubVoice()
    monkeypatch.setattr(ws_mod, "make_voice", lambda **kw: stub)
    with client.websocket_connect("/ws/agent") as ws:
        ws.send_json({"type": "workspace.mode", "payload": {"pet_id": "bit", "mode": "cad"}})  # before voice opens
        ws.send_json({"type": "hello", "payload": {"client": "unity", "pet_id": "bit", "project_id": "demo"}})
        assert ws.receive_json()["type"] == "voice.ready"
        ws.send_json({"type": "workspace.mode", "payload": {"pet_id": "bit", "mode": "cad"}})  # same: no note
        ws.send_json({"type": "workspace.mode", "payload": {"pet_id": "bit", "mode": "CODE"}})
        ws.send_json({"type": "workspace.mode", "payload": {"pet_id": "bit", "mode": ""}})
        ws.send_json({"type": "agent.ask", "payload": {"text": "error", "mode": "qa"}})  # round trip
        while ws.receive_json()["type"] != "agent.error":
            pass
    assert stub.workspace_mode == ""
    assert stub.notes == [view_note("code"), view_note("")]
