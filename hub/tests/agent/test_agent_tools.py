"""Bit's fast tools: one registry (agent/tools.py) shared by Grok (voice.py) and the Claude runner."""

from __future__ import annotations

import asyncio
import json
from typing import Any

import pytest

from agent import tools
from agent.events import Event


@pytest.fixture()
def fake_tools(monkeypatch: pytest.MonkeyPatch) -> list[tuple[str, dict[str, Any]]]:
    """Registry with two stand-in tools; records calls and pet.state broadcasts."""
    calls: list[tuple[str, dict[str, Any]]] = []

    async def call(name: str, args: dict[str, Any]) -> dict[str, Any]:
        calls.append((name, args))
        if name == "cad_boom":
            raise RuntimeError("worker died")
        return {"ok": True}

    defs = [{"name": n, "description": n, "parameters": {"type": "object", "properties": {}}}
            for n in ("cad_set_params", "cad_boom")]
    monkeypatch.setattr(tools, "_tools", {})
    tools.register(defs, call)
    return calls


@pytest.fixture()
def broadcasts(monkeypatch: pytest.MonkeyPatch) -> list[Event]:
    sent: list[Event] = []

    async def broadcast(e: Event) -> None:
        sent.append(e)

    monkeypatch.setattr(tools.bus, "broadcast", broadcast)
    return sent


def test_call_routes_and_walks_bit_to_the_panel(fake_tools, broadcasts) -> None:
    out = asyncio.run(tools.call("cad_set_params", {"changes": {"fin_count": 4}}, "bit"))
    assert out == {"ok": True}
    assert fake_tools == [("cad_set_params", {"changes": {"fin_count": 4}})]
    assert [(e.type, e.payload) for e in broadcasts] == \
        [("pet.state", {"pet_id": "bit", "mode": "working", "target_panel": "cad-viewer"})]


def test_unknown_tool_and_crash_come_back_as_errors(fake_tools, broadcasts) -> None:
    assert "unknown tool" in asyncio.run(tools.call("launch_rocket", {}, "bit"))["error"]
    assert asyncio.run(tools.call("cad_boom", {}, "bit")) == {"error": "worker died"}


def test_vscode_tools_are_registered_by_default() -> None:
    names = {d["name"] for d in tools.definitions()}
    assert {"vscode_get_context", "vscode_highlight", "vscode_open_file"} <= names


def test_grok_and_mcp_shapes(fake_tools, broadcasts) -> None:
    assert tools.grok_definitions()[0] == {"type": "function", "name": "cad_set_params", "description": "cad_set_params",
                                           "parameters": {"type": "object", "properties": {}}}
    assert tools.mcp_names() == ["mcp__forge__cad_set_params", "mcp__forge__cad_boom"]


def test_allow_filters_by_name_or_prefix(fake_tools, broadcasts) -> None:
    tools.register([{"name": "vscode_open_file", "description": "", "parameters": {"type": "object", "properties": {}}}],
                   lambda n, a: None)
    tools.register([{"name": "spawn_agent", "description": "", "parameters": {"type": "object", "properties": {}}}],
                   lambda n, a: None, lead_only=True)
    assert tools.mcp_names(lead=False, allow=["vscode_"]) == ["mcp__forge__vscode_open_file"]
    assert tools.mcp_names(lead=False, allow=["vscode_", "cad_boom"]) == ["mcp__forge__cad_boom", "mcp__forge__vscode_open_file"]
    assert tools.mcp_names(lead=True, allow=["spawn_agent"]) == ["mcp__forge__spawn_agent"]
    assert tools.mcp_names(lead=False, allow=["spawn_agent"]) == []  # lead-only wins over allow
    assert tools.mcp_names(lead=False, allow=[]) == []
    assert [d["name"] for d in tools.definitions(allow=("cad_",))] == ["cad_set_params", "cad_boom"]
    assert [t.name for t in tools._mcp_tools("tester", lead=False, allow=["vscode_"])] == ["vscode_open_file"]
    assert tools.is_lead_only("spawn_agent") and not tools.is_lead_only("cad_boom")


def test_mcp_server_runs_a_tool(fake_tools, broadcasts) -> None:
    server = tools.mcp_server("bit")
    assert server["type"] == "sdk" and server["name"] == "forge"
    handler = next(t for t in tools._mcp_tools("bit") if t.name == "cad_set_params").handler
    out = asyncio.run(handler({"changes": {}}))
    assert json.loads(out["content"][0]["text"]) == {"ok": True}
