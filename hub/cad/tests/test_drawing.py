"""The engineering drawing: every design (and the template) draws a PNG + SVG sheet, changed parts go orange, and over
REST it's made once per version, then cached, with /model.stl's 404/409 rules."""
import shutil
import struct
import time

import numpy as np
import pytest
from fastapi.testclient import TestClient
from PIL import Image

from cad import worker
from cad.worker import run_job
from main import create_app

from conftest import PARTS, ROCKET

SCRIPTS = sorted(PARTS.glob("*.py"))  # _template.py too: a copy of it must work as a new design


def png_size(data: bytes) -> tuple[int, int]:
    assert data[:8] == b"\x89PNG\r\n\x1a\n", "not a PNG"
    return struct.unpack(">II", data[16:24])


def orange_pixels(path) -> int:
    rgb = np.asarray(Image.open(path).convert("RGB")).astype(int)
    r, g, b = rgb[..., 0], rgb[..., 1], rgb[..., 2]
    return int(((r > 200) & (g > 80) & (g < 170) & (b < 80)).sum())


@pytest.mark.parametrize("script", SCRIPTS, ids=lambda p: p.stem)
def test_every_design_draws(script, tmp_path):
    built = run_job({"op": "build", "script": str(script), "params": {}, "out_dir": str(tmp_path)})
    assert built["ok"], built.get("traceback") or built["error"]
    t0 = time.perf_counter()
    r = run_job({"op": "drawing", "script": str(script), "params": built["params"], "out_dir": str(tmp_path),
                 "version": 1, "created_at": 1_800_000_000.0})
    elapsed = time.perf_counter() - t0
    assert r["ok"], r.get("traceback") or r["error"]
    png = (tmp_path / "drawing.png").read_bytes()
    assert png_size(png) == (2048, 1448) and 30_000 < len(png) < 3_000_000
    svg = (tmp_path / "drawing.svg").read_bytes()
    assert svg.startswith(b"<?xml") and b"<svg" in svg[:1000] and built["title"].encode() in svg
    assert orange_pixels(tmp_path / "drawing.png") == 0  # nothing changed in a first version
    print(f"{script.stem}: drawing {elapsed:.2f} s {r['timings_ms']}")


def test_changed_parts_are_orange(tmp_path):
    r = run_job({"op": "drawing", "script": str(ROCKET), "params": {"fin_count": 4}, "out_dir": str(tmp_path),
                 "version": 2, "highlight": ["FIN_4", "NOSE_CONE"]})
    assert r["ok"], r.get("traceback") or r["error"]
    assert orange_pixels(tmp_path / "drawing.png") > 2000


def test_a_drawing_reuses_the_build_it_follows():
    _, _, _, built = worker.build_part(ROCKET, {"fin_count": 4})
    assert worker.build_part(ROCKET, {"fin_count": 4}, reuse=True)[3] is built
    assert worker.build_part(ROCKET, {"fin_count": 4})[3] is not built  # only the drawing op reuses
    assert worker.build_part(ROCKET, {"fin_count": 5}, reuse=True)[3] is not built


@pytest.fixture(scope="module")
def hub(tmp_path_factory):
    """Hub on a copy of the rocket script (watcher off), so a test can edit the script without a rebuild."""
    script = tmp_path_factory.mktemp("parts") / "rocket.py"
    shutil.copy(ROCKET, script)
    with TestClient(create_app(data_dir=tmp_path_factory.mktemp("data"), cad_script=script, watch=False)) as c:
        yield c, script


def test_drawing_endpoint_makes_it_once_per_version(hub):
    client, _ = hub
    cad = client.app.state.cad
    jobs, call = [], cad._worker.call
    cad._worker.call = lambda job, timeout_s: (jobs.append(job["op"]), call(job, timeout_s))[1]
    try:
        assert client.post("/cad/params", json={"fin_count": 4}).json()["version"] == 2
        t0 = time.perf_counter()
        first = client.get("/cad/drawing.png")
        elapsed = time.perf_counter() - t0
        assert first.status_code == 200, first.text
        assert first.headers["content-type"] == "image/png" and first.headers["cache-control"] == "no-store"
        assert first.headers["x-forge-version"] == "2" and png_size(first.content) == (2048, 1448)

        again = client.get("/cad/drawing.png", params={"v": 2})
        assert again.content == first.content and "immutable" in again.headers["cache-control"]
        svg = client.get("/cad/drawing.svg", params={"v": 2})
        assert svg.status_code == 200 and svg.headers["content-type"].startswith("image/svg+xml")
        assert b"<svg" in svg.content[:1000]
        assert jobs.count("drawing") == 1  # the second PNG and the SVG came from the cache

        assert orange_pixels(cad.path(2, "drawing.png")) > 2000  # the fourth fin and the turned fins
        assert client.get("/cad/drawing.png", params={"v": 1}).status_code == 200
        assert orange_pixels(cad.path(1, "drawing.png")) == 0  # v1 came from nothing
        assert jobs.count("drawing") == 2
    finally:
        cad._worker.call = call
    print(f"first drawing over REST: {elapsed:.2f} s")


def test_drawing_404_and_409_like_stl(hub):
    client, script = hub
    assert client.get("/cad/drawing.png", params={"v": 999}).status_code == 404
    assert client.get("/cad/drawing.svg", params={"v": 999}).status_code == 404
    version = client.post("/cad/params", json={"fin_count": 5}).json()["version"]
    source = script.read_text(encoding="utf-8")
    script.write_text(source + "\n# edited after the build\n", encoding="utf-8")
    try:
        assert client.get("/cad/model.stl", params={"v": version}).status_code == 409
        assert client.get("/cad/drawing.png", params={"v": version}).status_code == 409
        assert client.get("/cad/drawing.svg", params={"v": version}).status_code == 409
        assert client.get("/cad/drawing.png", params={"v": 2}).status_code == 200  # made before the edit: cached
    finally:
        script.write_text(source, encoding="utf-8")
    assert client.get("/cad/drawing.png", params={"v": version}).status_code == 200
