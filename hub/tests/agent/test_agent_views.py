"""Bit opens workspace views (Addendum H): ui.mode out, workspace.mode back, and the ui_open_view tool."""

from __future__ import annotations

import asyncio
import time
from typing import Any

import pytest
from fastapi.testclient import TestClient

from agent import tools, workspace as views
from agent.bus import bus
from agent.events import Event
from agent.workspace import ACK_S, QUIET_ACK_S, Workspace, resolve


class Unity:
    """A Unity client as the workspace sees it: answers ui.mode the way Core's handler does (or stays silent)."""

    def __init__(self, ws: Workspace, *, at_desk: bool = True, answers: bool = True) -> None:
        self.ws, self.at_desk, self.answers = ws, at_desk, answers
        self.asked: list[str] = []
        self.shown = ""

    async def publish(self, type_: str, payload: Any) -> None:
        assert type_ == "ui.mode"
        self.asked.append(payload.mode)
        if not self.answers:
            return
        if self.at_desk:
            self.shown = payload.mode
        asyncio.get_running_loop().call_soon(self.ws.report, self.shown, None if self.at_desk else "not_at_desk")


@pytest.fixture
def one_socket(monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.setattr(type(bus), "count", property(lambda self: 1))


def test_resolve_takes_what_people_say() -> None:
    assert [resolve(v) for v in ("cad", "3D", "Modeling", "slack", "Messenger", "VS Code", "none", "")] == \
        ["cad", "cad", "cad", "messages", "messages", "code", "", ""]
    assert resolve("kitchen") is None


@pytest.mark.usefixtures("one_socket")
def test_open_waits_for_unity_to_confirm() -> None:
    async def run() -> None:
        ws = Workspace()
        unity = Unity(ws)
        ws.publish = unity.publish
        assert await ws.open("3d modeling") == {"ok": True, "view": "cad"}
        assert await ws.open("cad") == {"ok": True, "view": "cad", "already_open": True}  # no second ui.mode
        assert await ws.open("slack") == {"ok": True, "view": "messages"}
        assert await ws.open("none") == {"ok": True, "view": ""}
        assert unity.asked == ["cad", "messages", ""]
        assert "No view" in (await ws.open("kitchen"))["error"]

    asyncio.run(run())


@pytest.mark.usefixtures("one_socket")
def test_open_away_from_the_desk_says_why() -> None:
    async def run() -> None:
        ws = Workspace()
        ws.publish = Unity(ws, at_desk=False).publish
        out = await ws.open("cad")
        assert out["view"] == "" and "sit down" in out["error"]

    asyncio.run(run())


@pytest.mark.usefixtures("one_socket")
def test_an_old_unity_that_never_answers_costs_one_wait() -> None:
    async def run() -> None:
        ws = Workspace()
        ws.publish = Unity(ws, answers=False).publish
        loop = asyncio.get_running_loop()
        t0 = loop.time()
        assert "didn't open" in (await ws.open("cad"))["error"]
        first = loop.time() - t0
        t1 = loop.time()
        assert "didn't open" in (await ws.ensure("cad"))["error"]
        second = loop.time() - t1
        assert first >= ACK_S * 0.9 and second < ACK_S * 0.6 and second >= QUIET_ACK_S * 0.9
        ws.report("ml")  # a client spoke again: back to the full wait
        assert ws._unanswered is False and ws.mode == "ml"

    asyncio.run(run())


def test_no_client_or_no_hub_is_an_error(monkeypatch: pytest.MonkeyPatch) -> None:
    async def run() -> None:
        ws = Workspace()
        assert "isn't connected" in (await ws.open("cad"))["error"]  # no publish (no hub app)
        ws.publish = Unity(ws).publish
        monkeypatch.setattr(type(bus), "count", property(lambda self: 0))
        assert "isn't connected" in (await ws.open("cad"))["error"]  # no Unity socket

    asyncio.run(run())


def test_the_view_tool_is_registered_for_bit_only() -> None:
    assert tools.has("ui_open_view")
    tool = next(d for d in tools.definitions() if d["name"] == "ui_open_view")
    assert tool["parameters"]["properties"]["view"]["enum"] == ["cad", "code", "ml", "messages", "none"]
    assert "ui_open_view" not in [d["name"] for d in tools.definitions(lead=False, allow=("vscode_", "cad_"))]


def wait_for_mode(mode: str, timeout: float = 3.0) -> None:
    deadline = time.monotonic() + timeout
    while views.workspace.mode != mode:
        assert time.monotonic() < deadline, f"workspace never reported {mode!r}"
        time.sleep(0.01)


def test_ui_mode_round_trip_over_the_socket(client: TestClient, monkeypatch: pytest.MonkeyPatch) -> None:
    """The hub's ui.mode reaches the Unity socket; Unity's workspace.mode (with a reason) answers the tool call."""
    async def publish(type_: str, payload: Any) -> None:  # main.py uses the EventBus; this app only has the agent bus
        await bus.broadcast(Event(type=type_, payload=payload.model_dump()))

    monkeypatch.setattr(views.workspace, "publish", publish)
    with client.websocket_connect("/ws") as ws:
        ws.send_json({"type": "workspace.mode", "payload": {"pet_id": "bit", "mode": "code"}})
        wait_for_mode("code")
        pending = client.portal.start_task_soon(tools.call, "ui_open_view", {"view": "cad"}, "bit")
        while (m := ws.receive_json())["type"] != "ui.mode":
            pass
        assert m["payload"] == {"mode": "cad"}
        ws.send_json({"type": "workspace.mode", "payload": {"pet_id": "bit", "mode": "code", "reason": "not_at_desk"}})
        out = pending.result(timeout=5)
        assert out["view"] == "code" and "sit down" in out["error"]

        pending = client.portal.start_task_soon(tools.call, "ui_open_view", {"view": "cad"}, "bit")
        while ws.receive_json()["type"] != "ui.mode":
            pass
        ws.send_json({"type": "workspace.mode", "payload": {"pet_id": "bit", "mode": "cad"}})
        assert pending.result(timeout=5) == {"ok": True, "view": "cad"}
    assert views.workspace.mode is None  # the last Unity socket left
