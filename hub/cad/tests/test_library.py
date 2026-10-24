"""The design library over REST and Bit's tools: list, switch, presets, per-design history, new files live,
and a restart that comes back to the same design. Tests run in order on one hub."""
import shutil

import pytest
from fastapi.testclient import TestClient

from main import create_app

from conftest import PARTS


@pytest.fixture(scope="module")
def lib(tmp_path_factory):
    """Hub on a copy of the parts folder (watcher on), so tests can add designs to it."""
    parts = tmp_path_factory.mktemp("parts")
    for name in ("rocket.py", "quadcopter.py", "gearbox.py", "_template.py"):
        shutil.copy(PARTS / name, parts / name)
    data = tmp_path_factory.mktemp("data")
    with TestClient(create_app(data_dir=data, cad_script=parts / "rocket.py", watch=True)) as client:
        yield client, parts, data


def next_of(ws, type_):
    while True:
        message = ws.receive_json()
        if message["type"] == type_:
            return message["payload"]


def test_switch_between_designs(lib):
    client, _, _ = lib
    listing = client.get("/cad/parts").json()
    assert listing["active"] == "rocket"
    assert [p["part"] for p in listing["parts"]] == ["gearbox", "quadcopter", "rocket"]  # _template isn't a design
    rocket, quad = (next(p for p in listing["parts"] if p["part"] == name) for name in ("rocket", "quadcopter"))
    assert rocket["title"] == "Model rocket" and rocket["features"] == ["flight"] and rocket["version"] == 1
    assert {"name": "Four fins", "params": {"fin_count": 4}} in rocket["presets"]
    assert quad["version"] is None and quad["title"] == "Quadcopter" and quad["presets"]  # read from the source

    with client.websocket_connect("/ws") as ws:
        assert ws.receive_json()["payload"]["part"] == "rocket"  # sticky state names the design
        opened = client.post("/cad/part", json={"part": "quadcopter"})
        assert opened.status_code == 200 and opened.json()["part"] == "quadcopter" and opened.json()["version"] == 2
        assert next_of(ws, "cad.view") == {"explode": False, "focus_id": None}  # part IDs differ per design
        event = next_of(ws, "cad.model_updated")
        assert event["part"] == "quadcopter" and event["title"] == "Quadcopter" and event["previous_version"] is None
        assert {"clearance", "twr", "mass", "size", "parts"} <= {m["id"] for m in event["analysis"]["metrics"]}

    params = client.get("/cad/params").json()
    assert params["part"] == "quadcopter" and params["features"] == [] and params["presets"][0]["name"] == "7-inch props"
    assert client.get("/cad/flight").status_code == 409  # drones don't get the rocket's flight sim
    hit = client.post("/cad/preset", json={"name": "7-INCH props"})  # case doesn't matter
    assert hit.status_code == 200 and hit.json()["version"] == 3
    assert next(m for m in hit.json()["analysis"]["metrics"] if m["id"] == "clearance")["status"] == "bad"
    assert client.post("/cad/preset", json={"name": "wings"}).status_code == 404
    assert client.post("/cad/undo").json()["version"] == 2  # undo stays in the drone's own history

    back = client.post("/cad/part", json={"part": "rocket"}).json()
    assert back["part"] == "rocket" and back["version"] == 1  # resumes where it was, no rebuild
    assert client.get("/cad/params", params={"v": 3}).json()["part"] == "quadcopter"  # v numbers are library-wide
    assert [v["part"] for v in client.get("/cad/versions").json()] == ["rocket"]
    assert client.post("/cad/part", json={"part": "submarine"}).status_code == 404


def test_new_design_file_shows_up_live_and_a_broken_one_stays_closed(lib):
    client, parts, _ = lib
    shutil.copy(parts / "_template.py", parts / "lamp_box.py")
    box = next(p for p in client.get("/cad/parts").json()["parts"] if p["part"] == "lamp_box")
    assert box["title"] == "Storage box" and box["version"] is None
    opened = client.post("/cad/part", json={"part": "lamp_box"})
    assert opened.status_code == 200, opened.text

    (parts / "broken.py").write_text("PARAMS = [\n", encoding="utf-8")
    broken = next(p for p in client.get("/cad/parts").json()["parts"] if p["part"] == "broken")
    assert "SyntaxError" in broken["error"]
    assert client.post("/cad/part", json={"part": "broken"}).status_code == 500
    assert client.get("/cad/params").json()["part"] == "lamp_box"  # still on the last design that built
    (parts / "broken.py").unlink()


def test_pet_tools_for_the_library(lib):
    from cad.tools import call_tool

    client, _, _ = lib
    cad, bus = client.app.state.cad, client.app.state.bus

    def call(tool, **args):
        return client.portal.call(call_tool, tool, args, cad, bus)

    assert {"rocket", "quadcopter", "gearbox"} <= {p["part"] for p in call("cad_list_parts")["parts"]}
    opened = call("cad_open_part", part="gearbox")
    assert opened["part"] == "gearbox" and "PINION" in opened["part_ids"] and "High reduction" in opened["presets"]
    done = call("cad_apply_preset", name="High reduction")
    assert done["params"]["gear_teeth"] == 72 and "GEAR" in done["changed_ids"]
    assert "error" in call("cad_open_part", part="submarine")
    assert "error" in call("cad_simulate_flight")  # gearboxes don't fly


def test_a_restart_reopens_the_same_design(lib):
    client, parts, data = lib
    head = client.get("/cad/params").json()
    with TestClient(create_app(data_dir=data, cad_script=parts / "rocket.py", watch=False)) as again:
        resumed = again.get("/cad/params").json()
        assert (resumed["part"], resumed["version"]) == (head["part"], head["version"]) == ("gearbox", head["version"])
