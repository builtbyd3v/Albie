"""Every design in the library (and the template) builds, exports metadata cad-vision accepts and reports sane
metrics, with each preset too. Plus each design's own checks that its numbers describe the drawn geometry."""
import json
import math

import pytest
from build123d import Axis, CenterOf

from cad.analysis import analyze, mass_properties
from cad.export import read_glb_json
from cad.worker import build_part, load_part, run_job

from conftest import PARTS
from test_scene import check_scene

SCRIPTS = sorted(PARTS.glob("*.py"))  # _template.py too: a copy of it must work as a new design
QUAD, GEARBOX = PARTS / "quadcopter.py", PARTS / "gearbox.py"


def metrics(script, params):
    module, values, _, result = build_part(script, params)
    return {m["id"]: m for m in analyze(result, mass_properties(result), module.analyze)["metrics"]}


@pytest.mark.parametrize("script", SCRIPTS, ids=lambda p: p.stem)
def test_design_builds_exports_and_reports_metrics(script, tmp_path):
    module = load_part(script)
    for i, params in enumerate([{}, *getattr(module, "PRESETS", {}).values()]):
        out = tmp_path / f"v{i}"
        r = run_job({"op": "build", "script": str(script), "params": params, "out_dir": str(out)})
        assert r["ok"], r.get("traceback") or r["error"]
        assert set(params) <= {p["name"] for p in r["schema"]}, f"preset {params} names an unknown parameter"
        assert r["part"] == script.stem and r["title"]
        meta = json.loads((out / "metadata.json").read_text())  # export_part already ran cad-vision's mapping rules
        parts = [o for o in meta["objects"] if o["type"] == "part"]
        assert len(parts) >= 2, "multi-part, so taking it apart is interesting"
        assert all(o["mass"] and o["mass"] > 0 for o in parts), "every part needs a mass model"
        found = {m["id"]: m for m in r["analysis"]["metrics"]}
        assert {"mass", "size", "parts"} <= set(found)
        for m in found.values():
            assert m["status"] in ("ok", "warn", "bad") and m["display"] and math.isfinite(m["value"]), m
            if m["min"] is not None:  # a gauge: bands ascend and end at max
                bounds = [b["to"] for b in m["bands"]]
                assert m["min"] < m["max"] and bounds == sorted(bounds) and bounds[-1] == m["max"], m
        gltf = read_glb_json(out / "model.glb")
        extents = [abs(x) for a in gltf["accessors"] if len(a.get("max", [])) == 3 for x in a["max"] + a["min"]]
        # metres, and small enough for the Quest's Wi-Fi (the humanoid's detailed meshes make ~4.8 MB, the rest < 1 MB)
        assert max(extents) < 1.0 and r["glb_bytes"] < 6_000_000
        check_scene(r["analysis"], meta, r["schema"])  # the UI data: markers ... verdict, parameter rows (test_scene)


def test_quadcopter_clearance_is_the_gap_between_the_built_props():
    for params in ({}, {"arm_count": 6}, {"prop_diameter": 7.0}):
        _, values, _, result = build_part(QUAD, params)
        motors = {s.label: s.center(CenterOf.BOUNDING_BOX) for s in result.assembly.leaves if s.label.startswith("motor_")}
        a, b = motors["motor_1"], motors["motor_2"]
        gap = math.dist((a.X, a.Y), (b.X, b.Y)) - values["prop_diameter"] * 25.4
        assert metrics(QUAD, params)["clearance"]["value"] == pytest.approx(gap, abs=0.5)
    assert metrics(QUAD, {})["clearance"]["status"] == "ok"
    assert metrics(QUAD, {"prop_diameter": 7.0})["clearance"]["note"] == "props hit"
    assert metrics(QUAD, {"prop_diameter": 7.0, "arm_length": 160})["clearance"]["status"] == "ok"


def test_gearbox_teeth_mesh_without_colliding():
    _, values, _, result = build_part(GEARBOX, {})
    parts = {s.label: s for s in result.assembly.leaves}
    pinion, gear = parts["pinion"], parts["gear"]
    m, z1, z2 = values["module"], values["pinion_teeth"], values["gear_teeth"]
    centres = {name: parts[name].center(CenterOf.MASS) for name in ("pinion", "gear")}  # on the axis: teeth repeat
    axis = {name: (c.X, c.Z) for name, c in centres.items()}
    for name, teeth in (("pinion", z1), ("gear", z2)):  # tip circles say the module means what it should
        cx, cz = axis[name]
        tip = max(math.hypot(v.X - cx, v.Z - cz) for v in parts[name].vertices())
        assert tip == pytest.approx(m * (teeth + 2) / 2, abs=0.05)
    assert axis["gear"][0] - axis["pinion"][0] == pytest.approx(m * (z1 + z2) / 2, abs=0.3)
    overlap = pinion & gear  # None when the solids don't touch
    assert overlap is None or overlap.volume < 0.5  # mm^3: the teeth interleave without colliding...
    gx, gz = axis["gear"]
    half_tooth = gear.rotate(Axis((gx, 0, gz), (0, 1, 0)), 180 / z2)
    assert (pinion & half_tooth).volume > 1.0  # ...which they would, turned half a tooth (so the check can fail)


def test_gearbox_metrics():
    base = metrics(GEARBOX, {})
    assert base["ratio"]["value"] == pytest.approx(3.0) and base["speed"]["value"] == pytest.approx(1000)
    assert base["rim"]["status"] == "ok" and base["pinion"]["status"] == "ok"
    assert metrics(GEARBOX, {"pinion_teeth": 10, "shaft_diameter": 12})["rim"]["status"] == "bad"
    assert metrics(GEARBOX, {"pinion_teeth": 12})["pinion"]["status"] == "warn"  # undercut
