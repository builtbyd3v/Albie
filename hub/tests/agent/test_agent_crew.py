"""Bit's crew (agent/crew.py): roster from Unity, parallel member turns, Bit's crew tools, crew runner options."""

from __future__ import annotations

import asyncio
import subprocess
from pathlib import Path
from typing import Any

import pytest
from fastapi.testclient import TestClient

from agent import crew as crew_mod
from agent import runner, tools
from agent.crew import TYPES, Crew, crew, find_type, spoken
from agent.events import Event


@pytest.fixture(autouse=True)
def fresh_crew() -> None:
    """The crew is module state shared by every socket: start each test empty."""
    crew.members.clear()
    crew.project_id = None
    crew._roster_seen = False
    crew._changed = asyncio.Event()
    crew._listeners.clear()


def _collect(ws: Any, pet_id: str, until: str, limit: int = 80) -> list[tuple[str, dict[str, Any]]]:
    out: list[tuple[str, dict[str, Any]]] = []
    for _ in range(limit):
        msg = ws.receive_json()
        if msg["payload"].get("pet_id") == pet_id:
            out.append((msg["type"], msg["payload"]))
            if msg["type"] == until:
                return out
    raise AssertionError(f"never saw {until} for {pet_id}: {out}")


def _hello(ws: Any) -> None:
    ws.send_json({"type": "hello", "payload": {"client": "unity", "pet_id": "bit", "project_id": "demo"}})


# ------------------------------------------------------------------ catalog / roster
def test_types_and_lookup() -> None:
    assert 5 <= len(TYPES) <= 10
    assert find_type("Tester") is TYPES["tester"] and find_type("the reviewer") is TYPES["reviewer"]
    assert find_type("wizard") is None
    assert all(t.read_only for t in (TYPES["scout"], TYPES["reviewer"], TYPES["sentinel"]))
    assert not TYPES["builder"].read_only


def test_roster_replaces_membership() -> None:
    c = Crew()
    c.set_roster([{"pet_id": "tester", "agent_type": "tester"}, {"pet_id": "x", "agent_type": "nope"}])
    assert list(c.members) == ["tester"]
    assert c.get("Tester") is c.members["tester"]
    c.set_roster([{"pet_id": "scout", "agent_type": "scout"}])
    assert list(c.members) == ["scout"]
    assert c.roster() == [{"agent": "Scout", "type": "scout", "role": TYPES["scout"].role, "busy": False}]


def test_spoken_trims_long_results() -> None:
    long = "First sentence is here. " + "word " * 80
    assert spoken(long) == "First sentence is here."
    assert spoken("short") == "short"


# ------------------------------------------------------------------ tools
def test_crew_tools_are_lead_only() -> None:
    lead = {d["name"] for d in tools.definitions()}
    member = {d["name"] for d in tools.definitions(lead=False)}
    assert {"spawn_agent", "assign_task", "dismiss_agent", "crew_status"} <= lead
    assert not member & {"spawn_agent", "assign_task", "dismiss_agent", "crew_status"}
    assert "mcp__forge__assign_task" not in tools.mcp_names(lead=False)


def test_crew_tool_does_not_walk_bit(monkeypatch: pytest.MonkeyPatch) -> None:
    sent: list[Event] = []

    async def broadcast(e: Event) -> None:
        sent.append(e)

    monkeypatch.setattr(tools.bus, "broadcast", broadcast)
    out = asyncio.run(tools.call("crew_status", {}, "bit"))
    assert out["crew"] == [] and "tester" in out["available_types"]
    assert sent == []


def test_spawn_waits_for_unity(monkeypatch: pytest.MonkeyPatch) -> None:
    sent: list[Event] = []

    async def broadcast(e: Event) -> None:
        sent.append(e)
        if e.type == "crew.spawn":  # Unity hatches it and reports the new roster
            asyncio.get_running_loop().call_soon(
                crew.set_roster, [{"pet_id": e.payload["agent_type"], "agent_type": e.payload["agent_type"]}])

    monkeypatch.setattr(crew_mod.bus, "broadcast", broadcast)
    monkeypatch.setattr(type(crew_mod.bus), "count", property(lambda self: 1))

    async def go() -> list[dict[str, Any]]:
        crew._changed = asyncio.Event()
        return [await crew.spawn("Tester"), await crew.spawn("tester"), await crew.spawn("wizard")]

    hatched, again, bad = asyncio.run(go())
    assert hatched == {"status": "hatched", "agent": "Tester", "role": TYPES["tester"].role}
    assert again == {"status": "already_here", "agent": "Tester"}
    assert "error" in bad
    assert [(e.type, e.payload) for e in sent] == [("crew.spawn", {"agent_type": "tester"})]


def test_spawn_without_headset_is_an_error(monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.setattr(type(crew_mod.bus), "count", property(lambda self: 0))
    assert "no headset" in asyncio.run(crew.spawn("scout"))["error"]


# ------------------------------------------------------------------ over the socket (FAKE_AGENT)
def test_crew_members_work_in_parallel_with_bit(client: TestClient) -> None:
    with client.websocket_connect("/ws") as ws:
        _hello(ws)
        ws.send_json({"type": "crew.roster", "payload": {"members": [
            {"pet_id": "tester", "agent_type": "tester"}, {"pet_id": "scout", "agent_type": "scout"}]}})
        ws.send_json({"type": "agent.ask", "payload": {"pet_id": "tester", "text": "add a test for greet"}})
        ws.send_json({"type": "agent.ask", "payload": {"pet_id": "scout", "text": "fix where greet lives"}})
        ws.send_json({"type": "agent.ask", "payload": {"pet_id": "bit", "text": "what does greet do?", "mode": "qa"}})
        want = {("tester", "agent.task_complete"),  # tester defaults to code
                ("scout", "agent.answer_done"),  # read-only: "fix ..." is still answered as qa
                ("bit", "agent.answer_done")}  # Bit's own turn isn't cancelled by the crew's
        msgs: list[dict[str, Any]] = []
        while want and len(msgs) < 80:
            msgs.append(ws.receive_json())
            want.discard((msgs[-1]["payload"].get("pet_id"), msgs[-1]["type"]))
            assert msgs[-1]["type"] != "agent.error", msgs[-1]
    assert not want


def test_ask_to_unknown_pet_is_bits(client: TestClient) -> None:
    """A pet_id that isn't on the crew keeps the old behaviour (the socket's turn)."""
    with client.websocket_connect("/ws") as ws:
        _hello(ws)
        ws.send_json({"type": "agent.ask", "payload": {"pet_id": "ghost", "text": "where is greet?", "mode": "qa"}})
        evts = _collect(ws, "ghost", "agent.answer_done")
    assert evts[-1][0] == "agent.answer_done"


def test_finished_work_reaches_listeners(client: TestClient) -> None:
    heard: list[tuple[str, str]] = []

    async def listener(member: Any, result: str) -> None:
        heard.append((member.pet_id, result))

    crew.add_listener(listener)
    with client.websocket_connect("/ws") as ws:
        _hello(ws)
        ws.send_json({"type": "crew.roster", "payload": {"members": [{"pet_id": "scout", "agent_type": "scout"}]}})
        ws.send_json({"type": "agent.ask", "payload": {"pet_id": "scout", "text": "where is greet?"}})
        _collect(ws, "scout", "agent.answer_done")
        ws.send_json({"type": "agent.ask", "payload": {"pet_id": "scout", "text": "and again?"}})
        _collect(ws, "scout", "agent.answer_done")
    assert [p for p, _ in heard] == ["scout", "scout"]


def test_busy_member_refuses_a_second_task(client: TestClient, monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.setenv("FAKE_AGENT_DELAY", "0.3")
    with client.websocket_connect("/ws") as ws:
        _hello(ws)
        ws.send_json({"type": "crew.roster", "payload": {"members": [{"pet_id": "fixer", "agent_type": "fixer"}]}})
        ws.send_json({"type": "agent.ask", "payload": {"pet_id": "fixer", "text": "fix the greeting"}})
        ws.send_json({"type": "agent.ask", "payload": {"pet_id": "fixer", "text": "fix something else"}})
        evts = _collect(ws, "fixer", "agent.error")
    assert "still busy" in evts[-1][1]["message"]


# ------------------------------------------------------------------ runner options / worktree
def _project(tmp_path: Path):
    from agent.config import ProjectConfig

    return ProjectConfig(id="demo", path=tmp_path)


def test_crew_options_follow_the_kit(tmp_path: Path) -> None:
    reviewer = runner._options(_project(tmp_path), "qa", "reviewer", TYPES["reviewer"])
    assert "Bash" in reviewer.tools and "Bash(git diff:*)" in reviewer.allowed_tools
    assert "Edit" not in reviewer.tools and "You are Reviewer" in reviewer.system_prompt
    assert "Bash" not in reviewer.allowed_tools  # only the git read commands, not every shell command
    assert "mcp__forge__spawn_agent" not in reviewer.allowed_tools

    scribe = runner._options(_project(tmp_path), "code", "scribe", TYPES["scribe"], cwd=tmp_path / "wt")
    assert scribe.tools == runner.DOCS_TOOLS + ["Bash"] and scribe.cwd == str(tmp_path / "wt")
    assert "Bash(git commit:*)" in scribe.allowed_tools and "Bash" not in scribe.allowed_tools
    assert "Bash(git worktree:*)" in scribe.disallowed_tools

    bit = runner._options(_project(tmp_path), "qa", "bit")
    assert "mcp__forge__assign_task" in bit.allowed_tools and "crew of helper gators" in bit.system_prompt


def test_crew_runners_get_only_their_kit_and_vscode_tools(tmp_path: Path, monkeypatch: pytest.MonkeyPatch) -> None:
    """Allowlist: never cad_* (except the Designer), never messages_*, never the crew tools; Bit keeps everything."""
    monkeypatch.setattr(tools, "_tools", dict(tools._tools))  # registry copy: vscode_* and the crew tools are in it

    async def noop(name: str, args: dict[str, Any]) -> dict[str, Any]:
        return {}

    schema = {"type": "object", "properties": {}}
    tools.register([{"name": n, "description": n, "parameters": schema}
                    for n in ("cad_get_params", "cad_set_params", "messages_send", "messages_read_latest")], noop)

    def forge(opts) -> set[str]:
        return {t.removeprefix("mcp__forge__") for t in opts.allowed_tools if t.startswith("mcp__forge__")}

    reviewer = forge(runner._options(_project(tmp_path), "qa", "reviewer", TYPES["reviewer"]))
    assert reviewer and all(n.startswith("vscode_") for n in reviewer)
    builder = forge(runner._options(_project(tmp_path), "code", "builder", TYPES["builder"]))
    assert builder == reviewer
    designer = forge(runner._options(_project(tmp_path), "qa", "designer", TYPES["designer"]))
    assert {"cad_get_params", "cad_set_params"} <= designer and all(n.startswith(("vscode_", "cad_")) for n in designer)
    assert {n for n in designer if not n.startswith("cad_")} == reviewer  # other suites may register more cad_* tools
    bit = forge(runner._options(_project(tmp_path), "qa", "bit"))
    assert {"cad_set_params", "messages_send", "spawn_agent", "assign_task", "vscode_highlight"} <= bit
    assert runner.crew_forge_allow(TYPES["tester"]) == ("vscode_",) and runner.crew_forge_allow(TYPES["designer"]) == ("vscode_", "cad_")
    # the in-process MCP server offers the same filtered set
    server_tools = {t.name for t in tools._mcp_tools("builder", lead=False, allow=runner.crew_forge_allow(TYPES["builder"]))}
    assert server_tools == builder


def test_create_task_worktree(tmp_path: Path, monkeypatch: pytest.MonkeyPatch) -> None:
    repo = tmp_path / "repo"
    repo.mkdir()
    for args in (["init", "-q"], ["-c", "user.email=t@t", "-c", "user.name=t", "commit", "-q", "--allow-empty", "-m", "x"]):
        subprocess.run(["git", "-C", str(repo), *args], check=True)
    monkeypatch.delenv("AGENT_WORKTREE_DIR", raising=False)
    path, branch, _ = runner.create_task_worktree(repo, "Add a test for greet", "abcdef12", "tester")
    assert branch == "pet/tester/add-a-test-for-greet-abcdef"
    assert path == tmp_path / "repo-crew" / "tester-abcdef" and (path / ".git").exists()
    head = subprocess.run(["git", "-C", str(repo), "branch", "--show-current"], capture_output=True, text=True)
    assert head.stdout.strip() in ("master", "main")  # the main checkout never switched
    assert runner.create_task_worktree(tmp_path / "not-a-repo-" , "x", "1", "tester") is None


def test_sending_a_crew_gator_home_never_cancels_bit(client: TestClient, monkeypatch: pytest.MonkeyPatch) -> None:
    """Unity's Dismiss: agent.cancel {pet_id: crew} then a roster without it. Bit's own turn keeps going."""
    monkeypatch.setenv("FAKE_AGENT_DELAY", "0.15")
    with client.websocket_connect("/ws") as ws:
        _hello(ws)
        ws.send_json({"type": "crew.roster", "payload": {"members": [{"pet_id": "fixer", "agent_type": "fixer"}]}})
        ws.send_json({"type": "agent.ask", "payload": {"pet_id": "fixer", "text": "fix the greeting"}})
        ws.send_json({"type": "agent.ask", "payload": {"pet_id": "bit", "text": "what does greet do?", "mode": "qa"}})
        ws.send_json({"type": "agent.cancel", "payload": {"pet_id": "fixer"}})
        ws.send_json({"type": "crew.roster", "payload": {"members": []}})
        seen: list[tuple[str | None, str]] = []
        while ("bit", "agent.answer_done") not in seen and len(seen) < 80:
            m = ws.receive_json()
            seen.append((m["payload"].get("pet_id"), m["type"]))
    assert ("fixer", "agent.task_complete") not in seen
    assert ("fixer", "pet.state") in seen  # cancelled -> idle
    assert crew.members == {}
