"""Bit driving the CAD view (Addendum H): designs by name, the view opened first, cad.action for Unity's buttons,
versions, and the supplier files (STEP, STL, drawing PDF, parts CSV) that Slack drafts attach."""
import csv
import io

import pytest
from fastapi.testclient import TestClient

from cad.package import KINDS
from cad.tools import attachments, call_tool, find_part
from conftest import ROCKET
from main import create_app


@pytest.fixture(scope="module")
def client(tmp_path_factory):
    app = create_app(data_dir=tmp_path_factory.mktemp("data"), cad_script=ROCKET, watch=False)
    with TestClient(app) as c:
        yield c


class Bus:
    """What the CAD tools publish themselves (cad.view, cad.action); CadService keeps publishing on the hub's bus."""

    def __init__(self):
        self.events = []

    async def publish(self, type_, payload):
        self.events.append((type_, payload.model_dump()))

    def of(self, type_):
        return [p for t, p in self.events if t == type_]


class Show:
    """agent/workspace.py ensure: records what was asked, and what the design was at that moment."""

    def __init__(self, cad, error=None):
        self.cad, self.error, self.asked = cad, error, []

    async def __call__(self, view):
        self.asked.append((view, self.cad.active))
        return {"error": self.error} if self.error else {"ok": True, "view": view}


def tool(client, name, bus=None, show=None, **args):
    return client.portal.call(call_tool, name, args, client.app.state.cad, bus or Bus(), show)


def open_part(client, part):
    assert "error" not in tool(client, "cad_open_part", part=part)


def test_designs_by_what_people_call_them(client):
    cad = client.app.state.cad
    said = {"quad": "quadcopter", "the drone": "quadcopter", "Quad copter": "quadcopter", "Gear box": "gearbox",
            "gears": "gearbox", "the model rocket": "rocket", "Rocket": "rocket", "gearbox": "gearbox"}
    assert {k: find_part(cad, k) for k in said} == said
    for nonsense in ("submarine", ""):
        with pytest.raises(LookupError):
            find_part(cad, nonsense)
    assert "error" in tool(client, "cad_open_part", part="submarine")


def test_open_part_opens_the_design_then_the_view(client):
    cad = client.app.state.cad
    show = Show(cad)
    out = tool(client, "cad_open_part", show=show, part="drone")
    assert out["part"] == "quadcopter" and "view_error" not in out
    assert show.asked == [("cad", "quadcopter")]  # the view opens on the new design, not the old one
    away = Show(cad, error="The user isn't at their desk")
    out = tool(client, "cad_open_part", show=away, part="rocket")
    assert out["part"] == "rocket" and out["view_error"] == "The user isn't at their desk"  # the design still opened
    assert cad.active == "rocket"


def test_changes_show_the_view_first(client):
    cad = client.app.state.cad
    show = Show(cad)
    before = cad.head
    out = tool(client, "cad_set_params", show=show, changes={"body_length": 330}, request="Make the shaft 10% longer")
    assert out["version"] > before and show.asked == [("cad", "rocket")]
    assert cad.current.label == "Make the shaft 10% longer"
    assert tool(client, "cad_undo", show=show)["version"] == before
    assert len(show.asked) == 2


def test_view_presses_unitys_buttons(client):
    cad = client.app.state.cad
    bus = Bus()
    out = tool(client, "cad_view", bus=bus, show=Show(cad), expand=True, markers=True, tab="analysis")
    assert out == {"ok": True, "shown": ["expand", "markers", "tab"]}
    actions = bus.of("cad.action")
    assert [(a["action"], a["on"], a["tab"]) for a in actions] == [("expand", True, None), ("markers", True, None),
                                                                   ("tab", None, "analysis")]
    assert all(a["part"] == "rocket" and a["version"] == cad.head for a in actions)
    assert actions[0]["seq"] < actions[1]["seq"] < actions[2]["seq"]
    assert bus.of("cad.view") == []  # explode / focus weren't asked for: the sticky view is left alone

    bus = Bus()
    assert tool(client, "cad_view", bus=bus, show=Show(cad), explode=True, focus_id="fin_1")["ok"]
    assert bus.of("cad.view") == [{"explode": True, "focus_id": "FIN_1"}] and bus.of("cad.action") == []
    assert tool(client, "cad_view", bus=bus, show=Show(cad), focus_id="")["ok"]
    assert bus.of("cad.view")[-1] == {"explode": None, "focus_id": None}
    assert tool(client, "cad_view", bus=bus, show=Show(cad), explode=False)["shown"] == ["explode"]
    assert bus.of("cad.view")[-1] == {"explode": False, "focus_id": None}
    assert [a["action"] for a in bus.of("cad.action")] == ["reassemble"]  # pulled-off parts go back too

    bus = Bus()
    assert "error" in tool(client, "cad_view", bus=bus, show=Show(cad), focus_id="WING")
    assert "error" in tool(client, "cad_view", bus=bus, show=Show(cad), tab="settings")
    assert "error" in tool(client, "cad_view", bus=bus, show=Show(cad))
    assert "error" in tool(client, "cad_view", bus=bus, show=Show(cad, error="not at the desk"), expand=True)
    assert bus.events == []  # nothing pressed when it can't be seen


def test_demo_per_design(client):
    cad = client.app.state.cad
    bus = Bus()
    out = tool(client, "cad_demo", bus=bus, show=Show(cad))
    assert out["ok"] and out["running"] and out["demo"] == "Fly"
    assert 100 < out["flight"]["apogee_m"] < 400 and "samples" not in out["flight"]
    assert bus.of("cad.action")[-1]["action"] == "demo" and bus.of("cad.action")[-1]["on"] is True
    assert tool(client, "cad_demo", bus=bus, show=Show(cad), run=False)["running"] is False
    assert bus.of("cad.action")[-1]["on"] is False

    away = tool(client, "cad_demo", bus=Bus(), show=Show(cad, error="not at the desk"))
    assert away["error"] == "not at the desk" and away["flight"]["apogee_m"] > 100  # Bit can still say the numbers

    for part in ("quadcopter", "gearbox"):
        open_part(client, part)
        out = tool(client, "cad_demo", bus=Bus(), show=Show(cad))
        assert out["ok"] and out["demo"] in ("Hover", "Run") and "flight" not in out and out["readouts"]
    open_part(client, "rocket")


def test_blocked_demo_says_why(client, monkeypatch):
    cad = client.app.state.cad
    monkeypatch.setitem(cad.current.analysis, "demo", {"kind": "hover", "label": "Hover", "blocked": "props collide"})
    bus = Bus()
    assert tool(client, "cad_demo", bus=bus, show=Show(cad)) == {"error": "The demo can't run: props collide"}
    assert bus.events == []


def test_versions_and_going_back(client):
    cad = client.app.state.cad
    first = tool(client, "cad_set_params", changes={"fin_count": 4}, request="Four fins")["version"]
    tool(client, "cad_set_params", changes={"fin_count": 5})
    listed = tool(client, "cad_list_versions")
    assert listed["part"] == "rocket" and listed["versions"][-1]["current"]
    assert any(v["version"] == first and v["label"] == "Four fins" for v in listed["versions"])
    assert all(v["primary"] for v in listed["versions"])  # the static margin, in words

    was = cad.head
    back = tool(client, "cad_restore_version", version=first)
    assert back["version"] > was and back["params"] == cad.get(first).params
    assert cad.current.label == f"Back to v{first}" and cad.current.source == "pet"
    assert tool(client, "cad_restore_version", version=cad.head)["already_current"] is True
    assert "error" in tool(client, "cad_restore_version", version=99999)
    assert tool(client, "cad_undo")["version"] == was  # going back is a change like any other: undo returns


def test_supplier_files(client):
    cad = client.app.state.cad
    out = tool(client, "cad_export_files")
    assert out["part"] == "rocket" and out["version"] == cad.head
    assert [f["kind"] for f in out["files"]] == list(KINDS)
    names = {f["kind"]: f["name"] for f in out["files"]}
    v = cad.head
    assert names == {"step": f"rocket_v{v}.step", "stl": f"rocket_v{v}.stl", "drawing": f"rocket_v{v}_drawing.pdf",
                     "parts": f"rocket_v{v}_parts.csv"}
    assert all("path" not in f for f in out["files"])  # paths stay on the hub

    files = client.portal.call(attachments, cad, ["all"])
    by_kind = {a.kind: a for a in files}
    assert open(by_kind["step"].path, "rb").read(12) == b"ISO-10303-21"
    assert open(by_kind["drawing"].path, "rb").read(4) == b"%PDF"
    assert by_kind["stl"].bytes > 10_000
    rows = list(csv.reader(io.StringIO(open(by_kind["parts"].path, encoding="utf-8").read())))
    assert rows[0] == ["Item", "Part", "Material", "Qty", "Mass (g)"] and rows[-1][1] == "Total"
    assert any(r[1] == "fin" and int(r[3]) == cad.current.params["fin_count"] for r in rows[1:])
    assert "path" not in by_kind["step"].model_dump() and by_kind["step"].source == f"cad rocket v{v}"
    assert [a.kind for a in client.portal.call(attachments, cad, ["drawing", "step"])] == ["step", "drawing"]
    with pytest.raises(LookupError):
        client.portal.call(attachments, cad, ["zip"])

    listed = client.get("/cad/files").json()
    assert listed["version"] == v and [f["name"] for f in listed["files"]] == [a.name for a in files]
    got = client.get(f"/cad/files/{names['step']}")
    assert got.status_code == 200 and got.content == open(by_kind["step"].path, "rb").read()
    assert client.get("/cad/files/nope.zip").status_code == 404


def test_every_design_packages(client):
    for part in ("quadcopter", "gearbox"):
        open_part(client, part)
        files = tool(client, "cad_export_files")["files"]
        assert [f["kind"] for f in files] == list(KINDS) and all(f["bytes"] > 0 for f in files)
    open_part(client, "rocket")
