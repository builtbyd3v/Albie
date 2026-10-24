"""End-to-end fake-mode flows over the real /ws/agent handler."""

from __future__ import annotations

from typing import Any

import pytest
from fastapi.testclient import TestClient


def _collect(ws: Any, until: set[str], limit: int = 50) -> list[tuple[str, dict[str, Any]]]:
    out: list[tuple[str, dict[str, Any]]] = []
    for _ in range(limit):
        msg = ws.receive_json()
        out.append((msg["type"], msg["payload"]))
        if msg["type"] in until:
            return out
    raise AssertionError(f"never saw {until}: {out}")


def _types(evts: list[tuple[str, dict[str, Any]]]) -> list[str]:
    """Event types, with pet.state flattened to 'pet.state:<mode>'."""
    return [f"pet.state:{p['mode']}" if t == "pet.state" else t for t, p in evts]


def _hello(ws: Any) -> None:
    ws.send_json({"type": "hello", "payload": {"client": "unity", "pet_id": "bit", "project_id": "demo"}})


def test_qa_flow(client: TestClient) -> None:
    with client.websocket_connect("/ws/agent") as ws:
        _hello(ws)
        ws.send_json({"type": "agent.ask", "payload": {"pet_id": "bit", "project_id": "demo",
                                                        "text": "where is the ws handler?", "mode": "qa"}})
        evts = _collect(ws, {"agent.answer_done", "agent.error"})
    assert _types(evts) == [
        "pet.state:thinking", "agent.thinking",
        "pet.state:speaking", "agent.partial_answer", "agent.partial_answer", "agent.partial_answer",
        "agent.answer_done",
    ]
    partials = [p["text"] for t, p in evts if t == "agent.partial_answer"]
    assert all(len(a) < len(b) for a, b in zip(partials, partials[1:])), "partials are cumulative"
    assert evts[-1][1]["text"] == partials[-1]
    assert all(p["pet_id"] == "bit" for _, p in evts)


def test_code_flow(client: TestClient) -> None:
    with client.websocket_connect("/ws/agent") as ws:
        _hello(ws)
        ws.send_json({"type": "agent.ask", "payload": {"text": "fix the greeting", "mode": "code"}})
        evts = _collect(ws, {"agent.task_complete", "agent.error"})
    assert _types(evts) == [
        "pet.state:thinking", "agent.thinking",
        "pet.state:working", "agent.task_started",
        "agent.task_progress", "agent.task_progress", "agent.task_progress",
        "pet.state:idle", "agent.task_complete",
    ]
    started = evts[3][1]
    done = evts[-1][1]
    assert started["task_id"] == done["task_id"]
    assert done["branch"] == "pet/fake-123"
    assert done["summary"]


def test_auto_mode_picks_code_for_imperatives(client: TestClient) -> None:
    with client.websocket_connect("/ws/agent") as ws:
        _hello(ws)
        ws.send_json({"type": "agent.ask", "payload": {"text": "Please rename greet to hello", "mode": "auto"}})
        evts = _collect(ws, {"agent.task_complete", "agent.answer_done", "agent.error"})
    assert evts[-1][0] == "agent.task_complete"


def test_error_flow(client: TestClient) -> None:
    with client.websocket_connect("/ws/agent") as ws:
        _hello(ws)
        ws.send_json({"type": "agent.ask", "payload": {"text": "error", "mode": "qa"}})
        evts = _collect(ws, {"agent.error", "agent.answer_done"})
    assert _types(evts)[-2:] == ["pet.state:idle", "agent.error"]
    assert evts[-1][1]["message"]


def test_cancel(client: TestClient, monkeypatch) -> None:
    monkeypatch.setenv("FAKE_AGENT_DELAY", "0.5")
    with client.websocket_connect("/ws/agent") as ws:
        _hello(ws)
        ws.send_json({"type": "agent.ask", "payload": {"text": "explain everything", "mode": "qa"}})
        assert _collect(ws, {"agent.thinking"})[-1][0] == "agent.thinking"
        ws.send_json({"type": "agent.cancel", "payload": {"pet_id": "bit"}})
        evts = _collect(ws, {"pet.state"})
    assert evts[-1] == ("pet.state", {"pet_id": "bit", "mode": "idle"})


def test_stt_none_provider(client: TestClient) -> None:
    with client.websocket_connect("/ws/agent") as ws:
        _hello(ws)
        ws.send_json({"type": "stt.start", "payload": {"pet_id": "bit", "sample_rate": 16000, "encoding": "pcm16"}})
        ws.send_bytes(b"\x00\x00" * 1600)
        ws.send_json({"type": "stt.stop", "payload": {"pet_id": "bit"}})
        evts = _collect(ws, {"stt.final"})
    assert evts[-1] == ("stt.final", {"pet_id": "bit", "text": ""})


def test_bad_frame_and_unknown_project(client: TestClient) -> None:
    with client.websocket_connect("/ws/agent") as ws:
        ws.send_text("not json")
        assert _collect(ws, {"agent.error"})[-1][0] == "agent.error"
        ws.send_json({"type": "agent.ask", "payload": {"text": "hi", "project_id": "nope"}})
        assert _collect(ws, {"agent.error"})[-1][0] == "agent.error"


def test_client_handlers_extend_the_socket(client: TestClient, monkeypatch: pytest.MonkeyPatch) -> None:
    """Other modules own their client -> hub messages (main.py registers messages.focus for hub/messaging)."""
    import agent.ws as ws_mod
    from pydantic import BaseModel

    got: list[tuple[str, dict[str, Any]]] = []

    class Payload(BaseModel):
        channel: str

    async def handler(session: ws_mod.Session, payload: dict[str, Any]) -> None:
        Payload.model_validate(payload)
        got.append((session.pet_id, payload))

    monkeypatch.setitem(ws_mod.client_handlers, "messages.focus", handler)
    with client.websocket_connect("/ws/agent") as ws:
        _hello(ws)
        ws.send_json({"type": "messages.focus", "payload": {"channel": "C1", "ts": "1.0"}})
        ws.send_json({"type": "messages.focus", "payload": {"ts": "1.0"}})  # invalid -> agent.error, socket survives
        err = _collect(ws, {"agent.error"})[-1][1]
    assert got == [("bit", {"channel": "C1", "ts": "1.0"})]
    assert err["message"] == "Bad messages.focus message."


def test_post_agent_text(client: TestClient) -> None:
    r = client.post("/agent/text", json={"pet_id": "bit", "text": "what does hello.py do?"})
    assert r.status_code == 200
    assert "hub/agent/ws.py" in r.json()["reply"]


def test_projects_and_health_hide_paths(client: TestClient) -> None:
    projects = client.get("/agent/projects").json()
    assert projects == [{"id": "demo", "default_mode": "qa", "available": True, "git": False}]
    assert client.get("/health").json()["ok"] is True
