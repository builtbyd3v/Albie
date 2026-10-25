"""The whole spoken flow through Bit's tool path (agent/tools.call, what Grok and the Claude runner call), on the real
hub app: CAD worker for real, Slack stubbed, Unity faked at the workspace boundary (it answers ui.mode like Core's
handler: opens the view at the desk, says why not elsewhere).

    "Open 3D modeling and open the quadcopter"  -> cad_open_part (opens the CAD view itself)
    "Can you expand that?"                       -> cad_view expand
    "Open up the rocket. Show me it flying."     -> cad_open_part, cad_demo
    "Make the shaft 10% larger"                  -> cad_get_params, cad_set_params
    "Draft a message to <teammate> with all the files for the supplier"  -> messages_send attach=all
    "Rephrase it"                                -> messages_edit_draft
    "Send it"                                    -> messages_confirm (one upload: text + 4 files, as the user)
"""
from __future__ import annotations

import asyncio
from pathlib import Path
from typing import Any

import pytest
from fastapi.testclient import TestClient

from agent import tools
from agent.bus import bus as agent_bus
from agent.workspace import workspace
from main import create_app
from tests.test_messaging import StubClient
from tests.test_messaging_files import FilesClient

ROCKET = Path(__file__).resolve().parents[1] / "cad" / "parts" / "rocket.py"


class FakeUnity:
    def __init__(self) -> None:
        self.at_desk = True
        self.shown = ""
        self.asked: list[str] = []

    async def publish(self, type_: str, payload: Any) -> None:
        self.asked.append(payload.mode)
        if self.at_desk:
            self.shown = payload.mode
        asyncio.get_running_loop().call_soon(workspace.report, self.shown, None if self.at_desk else "not_at_desk")


@pytest.fixture(scope="module")
def hub(tmp_path_factory):
    app = create_app(data_dir=tmp_path_factory.mktemp("data"), cad_script=ROCKET, watch=False)
    with TestClient(app) as c:
        yield c


@pytest.fixture
def world(hub, monkeypatch):
    """The hub with Slack on (stub clients, as the user) and a fake Unity at the desk with nothing open."""
    events: list[tuple[str, Any]] = []
    bus = hub.app.state.bus
    real = bus.publish

    async def record(type_, payload):
        events.append((type_, payload.model_dump() if hasattr(payload, "model_dump") else payload))
        await real(type_, payload)

    monkeypatch.setattr(bus, "publish", record)
    svc = hub.app.state.slack
    monkeypatch.setattr(svc, "enabled", True)
    user = FilesClient()
    monkeypatch.setattr(svc, "_client", StubClient("UBOT"))
    monkeypatch.setattr(svc, "_user_client", user)
    monkeypatch.setattr(svc, "user_id", "UME")
    monkeypatch.setattr(svc, "bot_user_id", "UBOT")
    monkeypatch.setattr(svc, "share_poll_s", 0.001)
    unity = FakeUnity()
    monkeypatch.setattr(workspace, "publish", unity.publish)
    monkeypatch.setattr(type(agent_bus), "count", property(lambda self: 1))
    workspace.report("")  # at the desk, no view open
    yield hub, events, unity, user
    workspace.forget()


def say(hub, name: str, **args) -> dict[str, Any]:
    out = hub.portal.call(tools.call, name, args, "bit")
    assert "error" not in out, f"{name} {args}: {out}"
    return out


def of(events, type_):
    return [p for t, p in events if t == type_]


def test_the_whole_script(world):
    hub, events, unity, user = world
    cad = hub.app.state.cad

    # "Hey Bit, open 3D modeling and open the quadcopter."
    out = say(hub, "cad_open_part", part="quadcopter")
    assert out["part"] == "quadcopter" and unity.asked == ["cad"] and workspace.mode == "cad"
    assert of(events, "cad.model_updated")[-1]["part"] == "quadcopter"

    # "Can you expand that?"
    assert say(hub, "cad_view", expand=True)["shown"] == ["expand"]
    assert of(events, "cad.action")[-1] | {"seq": 0} == {"action": "expand", "on": True, "tab": None, "part_id": None,
                                                        "part": "quadcopter", "version": cad.head, "seq": 0}

    # "Open up the rocket. Can you show me it flying real quick?"
    say(hub, "cad_open_part", part="the rocket")
    flying = say(hub, "cad_demo")
    assert flying["demo"] == "Fly" and flying["flight"]["apogee_m"] > 100
    demo = of(events, "cad.action")[-1]
    assert (demo["action"], demo["on"], demo["part"], demo["version"]) == ("demo", True, "rocket", cad.head)
    assert unity.asked == ["cad"]  # the CAD view stayed open: no more ui.mode

    # "Let's make the shaft 10% larger."
    body = next(p for p in say(hub, "cad_get_params")["params"] if p["name"] == "body_length")
    longer = say(hub, "cad_set_params", changes={"body_length": round(body["value"] * 1.1)},
                 request="Make the shaft 10% larger")
    assert longer["params"]["body_length"] == round(body["value"] * 1.1) and "BODY_TUBE" in longer["changed_ids"]

    # "Draft a message to Jane with all of the files so we can get that sent over to the supplier."
    drafted = say(hub, "messages_send", channel="Jane", attach=["all"],
                  text="Hey Jane, here's the latest rocket (CAD, STL, drawing and parts list) for the supplier.")
    v = cad.head
    assert drafted["files"] == [f"rocket_v{v}.step", f"rocket_v{v}.stl", f"rocket_v{v}_drawing.pdf",
                                f"rocket_v{v}_parts.csv"]
    assert unity.asked == ["cad", "messages"] and workspace.mode == "messages"  # the messenger popped up
    card = of(events, "notify.draft")[-1]
    assert card["channel_name"] == "Jane Doe" and len(card["attachments"]) == 4
    assert all("path" not in a for a in card["attachments"])
    assert user.uploads == [] and user.posted == []  # nothing sent yet

    # "Can you actually rephrase this?"
    edited = say(hub, "messages_edit_draft",
                 text="Hi Jane, attached are the final rocket files. Could you send them to the supplier today?")
    assert edited["draft_id"] == drafted["draft_id"] and edited["files"] == drafted["files"]

    # "Okay, send it."
    sent = say(hub, "messages_confirm", send=True)
    assert sent["status"] == "sent" and sent["files"] == drafted["files"]
    [upload] = user.uploads
    assert upload["channel"] == "DU2" and upload["initial_comment"].startswith("Hi Jane, attached")
    assert [f["filename"] for f in upload["file_uploads"]] == drafted["files"]
    assert all(Path(f["file"]).is_file() for f in upload["file_uploads"])
    assert of(events, "notify.sent")[-1]["draft_id"] == drafted["draft_id"]


def test_away_from_the_desk_bit_still_works_and_says_so(world):
    hub, events, unity, _ = world
    unity.at_desk = False
    out = hub.portal.call(tools.call, "cad_open_part", {"part": "gearbox"}, "bit")
    assert out["part"] == "gearbox" and "sit down" in out["view_error"]
    assert "sit down" in hub.portal.call(tools.call, "cad_view", {"expand": True}, "bit")["error"]
    assert "sit down" in hub.portal.call(tools.call, "ui_open_view", {"view": "messages"}, "bit")["error"]
    say(hub, "cad_open_part", part="rocket")


def test_every_tool_has_a_description_bit_can_use():
    names = {d["name"] for d in tools.definitions()}
    assert {"ui_open_view", "cad_open_part", "cad_view", "cad_demo", "cad_list_versions", "cad_restore_version",
            "cad_export_files", "messages_send", "messages_edit_draft", "messages_confirm"} <= names
    for d in tools.definitions():
        assert len(d["description"]) >= 30 and d["parameters"]["type"] == "object"
