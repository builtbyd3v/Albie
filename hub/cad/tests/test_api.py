import shutil
import time

import pytest
from fastapi.testclient import TestClient

from cad.service import RegenError
from conftest import ROCKET
from main import create_app


@pytest.fixture(scope="module")
def client(tmp_path_factory):
    app = create_app(data_dir=tmp_path_factory.mktemp("data"), cad_script=ROCKET, watch=False)
    with TestClient(app) as c:
        yield c


@pytest.fixture(scope="module")
def scratch(tmp_path_factory):
    """Hub on a copy of the part script, with the file watcher on, so tests can break the script."""
    parts = tmp_path_factory.mktemp("parts")
    script = parts / "rocket.py"
    shutil.copy(ROCKET, script)
    app = create_app(data_dir=tmp_path_factory.mktemp("data"), cad_script=script, watch=True)
    with TestClient(app) as c:
        yield c, script


def test_edit_undo_flow(client):
    params = client.get("/cad/params").json()
    assert params["part"] == "rocket" and params["version"] == 1
    fin_count = next(p for p in params["params"] if p["name"] == "fin_count")
    assert fin_count == {**fin_count, "value": 3, "min": 3, "max": 6}

    with client.websocket_connect("/ws") as ws:
        sticky = ws.receive_json()  # latest model state is replayed on connect
        assert sticky["type"] == "cad.model_updated" and sticky["payload"]["version"] == 1

        t0 = time.perf_counter()
        r = client.post("/cad/params", json={"fin_count": 4, "body_length": 360})
        elapsed = time.perf_counter() - t0
        assert r.status_code == 200, r.text
        assert r.json()["version"] == 2 and r.json()["analysis"]["stability"] == "stable"
        assert elapsed < 2.0, f"regen took {elapsed:.2f} s (plan target 2 s)"

        event = ws.receive_json()
        assert event["type"] == "cad.model_updated"
        p = event["payload"]
        assert p["version"] == 2 and p["previous_version"] == 1 and p["source"] == "api"
        assert {"FIN_2", "FIN_4", "BODY_TUBE", "NOSE_CONE"} <= set(p["changed_ids"])
        assert not {"FIN_1", "MOUNT_TUBE"} & set(p["changed_ids"])  # fin_1 stays at 0 deg; untouched parts don't glow
        assert p["analysis"]["markers_gltf"]["cp"][1] > 0

        assert client.post("/cad/undo").json()["version"] == 1
        undo = ws.receive_json()["payload"]
        assert undo["source"] == "undo" and undo["removed_ids"] == ["FIN_4"]

    assert client.post("/cad/undo").status_code == 409  # v1 has no parent
    assert client.get("/cad/params").json()["version"] == 1


def test_files_and_caching(client):
    r = client.get("/cad/model.glb", params={"v": 1})
    assert r.status_code == 200 and r.content[:4] == b"glTF"
    assert r.headers["content-type"] == "model/gltf-binary" and "immutable" in r.headers["cache-control"]
    assert client.get("/cad/model.glb").headers["cache-control"] == "no-store"
    meta = client.get("/cad/metadata.json", params={"v": 1}).json()
    assert meta["project"]["rootObjectId"] == "ROCKET"
    assert client.get("/cad/analysis", params={"v": 1}).json()["motor"] == "Estes C6"
    assert client.get("/cad/model.glb", params={"v": 999}).status_code == 404


def test_param_validation(client):
    assert client.post("/cad/params", json={"wings": 2}).status_code == 422
    assert client.post("/cad/params", json={"fin_count": "lots"}).status_code == 422
    assert client.post("/cad/params", json={"nose_shape": "square"}).status_code == 422
    r = client.post("/cad/params", json={"fin_span": 500})
    assert r.status_code == 200 and r.json()["clamped"] == ["fin_span"]
    assert client.get("/cad/params").json()["params"][7]["value"] == 100  # fin_span max


def test_view_and_versions(client):
    assert client.post("/cad/view", json={"explode": True, "focus_id": "FIN_1"}).json() == {"ok": True}
    assert client.post("/cad/view", json={"focus_id": "WING"}).status_code == 404
    latest = client.get("/cad/latest").json()
    assert latest["cad.view"] == {"explode": True, "focus_id": "FIN_1", "focus_ids": []}
    assert latest["cad.model_updated"]["version"] == client.get("/cad/params").json()["version"]
    versions = client.get("/cad/versions").json()
    assert sum(v["head"] for v in versions) == 1 and versions[0]["parent"] is None


def test_stl(client):
    r = client.get("/cad/model.stl")
    assert r.status_code == 200 and len(r.content) > 10_000


def test_flight(client):
    first = client.get("/cad/flight", params={"v": 1})
    assert first.status_code == 200, first.text
    f = first.json()
    assert 100 < f["apogee_m"] < 400 and 3 < f["time_to_apogee_s"] < 10
    assert abs(f["trajectory_m"][0][1]) < 1.0  # starts at the pad
    assert f["trajectory_m"][-1][1] == pytest.approx(f["apogee_m"], abs=0.5)
    assert f["warning"] is None  # baseline is stable
    assert client.get("/cad/flight", params={"v": 1}).json() == f  # cached per version


def test_pet_tools(client):
    from cad.tools import TOOLS, call_tool

    cad, bus = client.app.state.cad, client.app.state.bus

    def call(name, **args):
        return client.portal.call(call_tool, name, args, cad, bus)

    assert {t["name"] for t in TOOLS} >= {"cad_get_params", "cad_set_params", "cad_undo", "cad_simulate_flight"}
    before = call("cad_get_params")["version"]
    done = call("cad_set_params", changes={"nose_shape": "cone"})
    assert done["version"] > before and done["changed_ids"] == ["NOSE_CONE"]
    assert "error" in call("cad_set_params", changes={"wings": 2})
    assert "error" in call("cad_view", focus_id="WING")
    assert call("cad_undo")["version"] == before
    assert "trajectory_m" not in call("cad_simulate_flight")
    assert "def build" in call("cad_get_context")["script"]


def wait_for_version(client, version, timeout=30.0):
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        if client.get("/health").json()["cad_version"] == version:
            return
        time.sleep(0.2)
    raise AssertionError(f"hub never reached v{version}")


def test_script_save_regenerates_and_errors_keep_last_good(scratch):
    client, script = scratch
    source = script.read_text(encoding="utf-8")
    assert client.post("/cad/params", json={"fin_count": 5}).json()["version"] == 2

    def fin_mass():
        return next(b["mass_g"] for b in client.get("/cad/analysis").json()["breakdown"] if b.get("id") == "FIN_1")

    thin = fin_mass()
    script.write_text(source.replace("FIN_THICKNESS = 3.2", "FIN_THICKNESS = 6.4"), encoding="utf-8")
    wait_for_version(client, 3)  # the save regenerated it, keeping the current params
    assert fin_mass() == pytest.approx(2 * thin, rel=0.01)
    assert client.get("/cad/params").json()["params"][4]["value"] == 5

    cad = client.app.state.cad
    script.write_text(source.replace("def build(p) -> BuildResult:", "def build(p) -> BuildResult:\n    1 / 0"),
                      encoding="utf-8")
    with pytest.raises(RegenError) as failed:
        client.portal.call(cad.rebuild_from_script)
    assert failed.value.failure.kind == "build" and "ZeroDivisionError" in failed.value.failure.error
    assert failed.value.failure.error_line is not None
    assert cad.head == 3  # last good model stays up

    cad.timeout_s = 3.0
    script.write_text(source.replace("def build(p) -> BuildResult:", "def build(p) -> BuildResult:\n    while 1: pass"),
                      encoding="utf-8")
    with pytest.raises(RegenError) as hung:
        client.portal.call(cad.rebuild_from_script)
    assert hung.value.failure.kind == "timeout" and cad.head == 3

    cad.timeout_s = 30.0  # respawned worker is cold the first time
    script.write_text(source, encoding="utf-8")
    client.portal.call(cad.rebuild_from_script)
    assert client.get("/cad/params").json()["params"][4]["value"] == 5  # head params carried over
