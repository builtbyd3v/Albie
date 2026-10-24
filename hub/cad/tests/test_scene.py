"""Round 4 data for the CAD UI (CAD_PLAN section 5): parameter groups and help, markers, dimensions, the exploded
view, the mass breakdown, the demo (launch / hover / spin) and the rocket's flight to landing, for every design.

Positions are checked against the GLB itself: each part's box in glTF scene space comes from the GLB's node
transforms and accessor bounds, i.e. the frame Unity loads (before glTFast's X flip)."""
import json
import math
import shutil

import numpy as np
import pytest
from fastapi.testclient import TestClient

from cad import scene
from cad.analysis import mass_properties
from cad.export import read_glb_json
from cad.part_api import DEFAULT_GROUP, Param, param_groups
from cad.worker import build_part, load_part, run_job
from events import CadAnalysis, CadModelUpdated
from main import create_app

from conftest import PARTS, ROCKET

DESIGNS = ["rocket", "quadcopter", "gearbox", "_template"]   # _template: a copy of it must work as a new design


@pytest.fixture(scope="module")
def built(tmp_path_factory):
    """Each design at its defaults, built and exported once: {name: (worker result, metadata, GLB part boxes)}."""
    out = {}
    for name in DESIGNS:
        folder = tmp_path_factory.mktemp(name.strip("_"))
        r = run_job({"op": "build", "script": str(PARTS / f"{name}.py"), "params": {}, "out_dir": str(folder)})
        assert r["ok"], r.get("traceback") or r["error"]
        meta = json.loads((folder / "metadata.json").read_text(encoding="utf-8"))
        out[name] = r, meta, glb_boxes(folder / "model.glb")
    return out


def glb_boxes(path) -> dict[str, tuple[np.ndarray, np.ndarray]]:
    """Object ID -> (min, max) of that node's meshes in glTF scene space (metres, Y up), from node transforms."""
    gltf = read_glb_json(path)

    def local(node) -> np.ndarray:
        if "matrix" in node:
            return np.array(node["matrix"], dtype=float).reshape(4, 4).T
        x, y, z, w = node.get("rotation", (0.0, 0.0, 0.0, 1.0))
        rot = np.array([[1 - 2 * (y * y + z * z), 2 * (x * y - z * w), 2 * (x * z + y * w)],
                        [2 * (x * y + z * w), 1 - 2 * (x * x + z * z), 2 * (y * z - x * w)],
                        [2 * (x * z - y * w), 2 * (y * z + x * w), 1 - 2 * (x * x + y * y)]])
        m = np.eye(4)
        m[:3, :3] = rot * np.array(node.get("scale", (1.0, 1.0, 1.0)))
        m[:3, 3] = node.get("translation", (0.0, 0.0, 0.0))
        return m

    boxes: dict[str, tuple[np.ndarray, np.ndarray]] = {}

    def visit(index: int, parent: np.ndarray) -> None:
        node = gltf["nodes"][index]
        world = parent @ local(node)
        if "mesh" in node:
            corners = []
            for prim in gltf["meshes"][node["mesh"]]["primitives"]:
                acc = gltf["accessors"][prim["attributes"]["POSITION"]]
                lo, hi = acc["min"], acc["max"]
                corners += [(world @ np.array([(lo, hi)[i & 1][0], (lo, hi)[i >> 1 & 1][1], (lo, hi)[i >> 2 & 1][2],
                                               1.0]))[:3] for i in range(8)]
            pts = np.array(corners)
            boxes[node["name"].upper()] = pts.min(axis=0), pts.max(axis=0)
        for child in node.get("children", []):
            visit(child, world)

    for root in gltf["scenes"][0]["nodes"]:
        visit(root, np.eye(4))
    return boxes


def leaf_ids(meta) -> list[str]:
    return [o["id"] for o in meta["objects"] if o["type"] == "part"]


def unit(v) -> bool:
    return abs(math.sqrt(sum(x * x for x in v)) - 1.0) < 1e-3


def check_scene(analysis: dict, meta: dict) -> None:
    """What every design's analysis must satisfy (also run on every preset by test_designs)."""
    parts = leaf_ids(meta)
    explode = analysis["explode"]
    assert explode["axis"] in ("stack", "radial", "shaft", "auto")
    assert [p["id"] for p in explode["parts"]] == parts, "one entry per leaf part, in assembly order"
    for p in explode["parts"]:
        assert unit(p["dir"]) and p["dist_mm"] >= 0 and isinstance(p["order"], int) and p["order"] >= 0, p
    assert any(p["dist_mm"] > 0 for p in explode["parts"])

    markers = analysis["markers"]
    assert markers[0]["id"] == "cg" and markers[0]["pos"] == analysis["cg_gltf"]
    assert len({m["id"] for m in markers}) == len(markers)
    assert all(len(m["pos"]) == 3 and m["label"] and m["kind"] for m in markers)

    dims = analysis["dimensions"]
    assert 2 <= len(dims) <= 4 and len({d["id"] for d in dims}) == len(dims)
    for d in dims:
        span = np.subtract(d["to"], d["from"]) * 1000
        assert d["value_mm"] == pytest.approx(np.linalg.norm(span), abs=0.15) and d["value_mm"] > 0, d
        assert unit(d["offset_dir"]) and abs(np.dot(span / np.linalg.norm(span), d["offset_dir"])) < 1e-3, d
        assert d["offset_mm"] > 0

    rows = analysis["mass_breakdown"]
    assert sum(r["mass_g"] for r in rows) == pytest.approx(analysis["mass_g"], abs=0.1 * len(rows))
    assert sum(r["share"] for r in rows) == pytest.approx(1.0, abs=1e-3)
    in_rows = [p for r in rows for p in r["part_ids"]]
    assert sorted(in_rows) == sorted(parts), "each part in exactly one row"

    demo = analysis["demo"]
    assert demo["kind"] in ("launch", "hover", "spin", "none")
    for s in demo["spin_parts"]:
        assert s["id"] in parts and unit(s["dir"]) and s["spin"] == ("ccw" if s["rpm"] > 0 else "cw"), s
        assert s["motor_id"] is None or s["motor_id"] in parts

    # The WS event carries all of it (events.py mirrors the shapes; nothing is dropped or renamed).
    event = CadModelUpdated(version=1, params={}, analysis=analysis).model_dump()["analysis"]
    for key in ("markers", "dimensions", "explode", "mass_breakdown", "demo"):
        assert json.dumps(event[key], sort_keys=True) == json.dumps(analysis[key], sort_keys=True), key
    assert set(CadAnalysis.model_validate(analysis).model_dump()) >= set(analysis)


# Parameters

@pytest.mark.parametrize("name", DESIGNS)
def test_every_param_has_a_group_step_label_and_help(name, built):
    schema = built[name][0]["schema"]
    for p in schema:
        assert p["group"] and p["label"] and isinstance(p["help"], str), p
        assert p["help"], f"{p['name']}: the library designs and the template explain every parameter"
        if p["options"]:
            assert p["step"] is None
        else:
            assert 0 < p["step"] <= (p["max"] - p["min"]) / 2, p
    groups = param_groups(schema)
    assert groups == list(dict.fromkeys(p["group"] for p in schema))
    assert {"rocket": ["Body", "Nose", "Fins"], "quadcopter": ["Frame", "Props", "Power"],
            "gearbox": ["Gears", "Shafts"], "_template": ["Size", "Walls"]}[name] == groups


def test_param_fallbacks_for_a_new_design():
    plain = Param("depth", 40.0, 10, 160, "mm")
    assert plain.schema(40.0)["group"] == DEFAULT_GROUP and plain.schema(40.0)["help"] == ""
    assert plain.schema(40.0)["step"] == 2 and Param("x", 0.5, 0.0, 1.0).fine_step() == 0.01
    assert Param("n", 3, 1, 9).fine_step() == 1 and Param("loose", 1.0).fine_step() is None
    assert Param("shape", "a", options=("a", "b")).schema("a")["step"] is None
    assert Param("set", 2.0, 0, 10, step=0.25).schema(2.0)["step"] == 0.25   # a part's own step wins
    assert param_groups([{"name": "a"}, {"name": "b", "group": "Body"}]) == [DEFAULT_GROUP, "Body"]


# Explode

@pytest.mark.parametrize("name", DESIGNS)
def test_every_design_passes_the_shared_checks(name, built):
    r, meta, _ = built[name]
    check_scene(r["analysis"], meta)


def test_rocket_explodes_along_its_stack(built):
    moves = {p["id"]: p for p in built["rocket"][0]["analysis"]["explode"]["parts"]}
    assert built["rocket"][0]["analysis"]["explode"]["axis"] == "stack"
    assert moves["NOSE_CONE"]["dir"] == [0.0, 1.0, 0.0] and moves["NOSE_CONE"]["dist_mm"] > 50
    assert moves["BODY_TUBE"]["dist_mm"] == 0
    for part in ("MOUNT_TUBE", "CENTERING_RING_FWD", "CENTERING_RING_AFT"):
        assert moves[part]["dir"] == [0.0, -1.0, 0.0]
    # exploded centres stack: aft ring below the mount tube below the forward ring below the tail (y = 0)
    boxes = built["rocket"][2]

    def exploded_y(part):
        lo, hi = boxes[part]
        return (lo[1] + hi[1]) / 2 + moves[part]["dir"][1] * moves[part]["dist_mm"] / 1000
    half_tube = (boxes["MOUNT_TUBE"][1][1] - boxes["MOUNT_TUBE"][0][1]) / 2
    assert exploded_y("CENTERING_RING_AFT") < exploded_y("MOUNT_TUBE") - half_tube
    assert exploded_y("MOUNT_TUBE") + half_tube < exploded_y("CENTERING_RING_FWD") < 0
    for fin in (p for p in moves if p.startswith("FIN_")):
        lo, hi = boxes[fin]
        radial = np.array([(lo[0] + hi[0]) / 2, 0.0, (lo[2] + hi[2]) / 2])
        assert np.dot(moves[fin]["dir"], radial / np.linalg.norm(radial)) > 0.8 and moves[fin]["dir"][1] < 0, fin


def test_quadcopter_explodes_radially(built):
    explode = built["quadcopter"][0]["analysis"]["explode"]
    moves, boxes = {p["id"]: p for p in explode["parts"]}, built["quadcopter"][2]
    assert explode["axis"] == "radial" and moves["BOTTOM_PLATE"]["dist_mm"] == 0
    for part in ("TOP_PLATE", "BATTERY", "FLIGHT_CONTROLLER", "STANDOFF_1"):
        assert moves[part]["dir"] == [0.0, 1.0, 0.0]
    assert moves["BATTERY"]["dist_mm"] > moves["TOP_PLATE"]["dist_mm"] > moves["STANDOFF_1"]["dist_mm"]
    for i in range(1, 5):
        lo, hi = boxes[f"PROP_{i}"]
        out = np.array([(lo[0] + hi[0]) / 2, 0.0, (lo[2] + hi[2]) / 2])
        out /= np.linalg.norm(out)
        arm, motor, prop = (moves[f"{k}_{i}"]["dir"] for k in ("ARM", "MOTOR", "PROP"))
        assert arm[1] == 0 and np.dot(arm, out) > 0.99            # arms slide straight out
        assert np.dot(motor, out) > 0.9 and 0 < motor[1] < prop[1]  # props rise above their motors
        assert moves[f"PROP_{i}"]["order"] == 0 and moves[f"ARM_{i}"]["order"] == 3


def test_gearbox_explodes_along_its_shafts(built):
    explode = built["gearbox"][0]["analysis"]["explode"]
    moves = {p["id"]: p for p in explode["parts"]}
    assert explode["axis"] == "shaft" and moves["HOUSING_BOTTOM"]["dist_mm"] == 0
    assert moves["HOUSING_TOP"]["dir"] == [0.0, 1.0, 0.0] and moves["HOUSING_TOP"]["order"] == 0
    # the shafts run along glTF Z (part Y): the pinion slides to the front (+Z), the gear to the back
    assert moves["PINION"]["dir"][2] > 0 > moves["GEAR"]["dir"][2] and moves["PINION"]["dir"][0] == 0
    assert moves["BEARING_IN_FRONT"]["dir"][2] > moves["PINION"]["dir"][2] > 0
    assert moves["BEARING_OUT_BACK"]["dir"][2] < moves["GEAR"]["dir"][2] < 0
    lift = {p: moves[p]["dir"][1] * moves[p]["dist_mm"] for p in moves if p != "HOUSING_BOTTOM"}
    assert lift["HOUSING_TOP"] > lift["GEAR"] == pytest.approx(lift["INPUT_SHAFT"], abs=0.2)  # one lift for the train


@pytest.mark.parametrize("name", DESIGNS)
def test_auto_explode_works_for_any_design(name):
    """The fallback a part script gets without its own explode: every part covered, sensible distances."""
    _, _, _, result = build_part(PARTS / f"{name}.py", {})
    masses = mass_properties(result)
    plan = scene.auto_explode(result, masses)
    labels = [s.label for s in scene.leaves(result.assembly)]
    out = scene.explode(plan, labels)
    size = result.assembly.bounding_box().size
    longest = max(size.X, size.Y, size.Z)
    assert out["axis"] == "auto" and [p["id"] for p in out["parts"]] == [label.upper() for label in labels]
    moving = [p for p in out["parts"] if p["dist_mm"] > 0]
    assert moving and all(unit(p["dir"]) for p in out["parts"])
    assert all(0.12 * longest - 0.1 <= p["dist_mm"] <= 0.6 * longest + 0.1 for p in moving)
    if name == "_template":   # the lid lifts off, the tray drops a little
        parts = {p["id"]: p for p in out["parts"]}
        assert parts["LID"]["dir"][1] > 0.99 and parts["TRAY"]["dir"][1] < -0.99


def test_explode_rejects_a_part_that_does_not_exist():
    from cad.part_api import Explode, ExplodeMove
    with pytest.raises(ValueError, match="isn't a part"):
        scene.explode(Explode("stack", [ExplodeMove("wing", (0, 0, 1), 10)]), ["body"])


# Markers and dimensions, against the GLB

def centre(box) -> np.ndarray:
    return (box[0] + box[1]) / 2


def test_markers_sit_where_the_glb_puts_them(built):
    rocket = built["rocket"][0]["analysis"]
    marks = {m["id"]: m["pos"] for m in rocket["markers"]}
    assert marks["cp"] == rocket["markers_gltf"]["cp"] and marks["cg"] == pytest.approx(rocket["markers_gltf"]["cg"])
    assert marks["cg"][1] == pytest.approx((rocket["length_mm"] - rocket["cg_mm"]) / 1000, abs=1e-4)  # from the tail

    quad, boxes = built["quadcopter"][0]["analysis"], built["quadcopter"][2]
    thrust = next(m for m in quad["markers"] if m["id"] == "thrust")
    props = np.array([centre(boxes[f"PROP_{i}"]) for i in range(1, 5)])
    assert thrust["kind"] == "thrust" and thrust["pos"] == pytest.approx(props.mean(axis=0), abs=2e-4)

    gear, boxes = built["gearbox"][0]["analysis"], built["gearbox"][2]
    marks = {m["id"]: m["pos"] for m in gear["markers"]}
    for key, shaft, end in (("input", "INPUT_SHAFT", 1), ("output", "OUTPUT_SHAFT", 0)):
        lo, hi = boxes[shaft]
        # the shaft's end sticking out: the front (+Z in glTF, part -Y) for the input, the back for the output
        assert marks[key] == pytest.approx([centre(boxes[shaft])[0], centre(boxes[shaft])[1], (lo, hi)[end][2]],
                                           abs=2e-4)


def test_dimensions_match_the_model(built):
    r, _, boxes = built["rocket"]
    dims = {d["id"]: d for d in r["analysis"]["dimensions"]}
    assert set(dims) == {"length", "diameter", "fin_span"}
    assert dims["length"]["value_mm"] == r["analysis"]["length_mm"]
    assert dims["diameter"]["value_mm"] == r["params"]["body_diameter"]
    assert dims["fin_span"]["value_mm"] == r["params"]["fin_span"]
    assert dims["diameter"]["offset_dir"] == [0.0, 0.0, 1.0]   # toward the front (part -Y), facing the viewer

    q = built["quadcopter"][0]
    dims = {d["id"]: d for d in q["analysis"]["dimensions"]}
    assert set(dims) == {"wheelbase", "width", "height"}
    assert dims["wheelbase"]["value_mm"] == pytest.approx(2 * q["params"]["arm_length"], abs=0.2)
    assert dims["width"]["value_mm"] == q["analysis"]["size_mm"][0]
    assert dims["height"]["value_mm"] == q["analysis"]["size_mm"][2]

    g, _, boxes = built["gearbox"]
    dims = {d["id"]: d for d in g["analysis"]["dimensions"]}
    p = g["params"]
    assert dims["centre_distance"]["value_mm"] == pytest.approx(p["module"] * (p["pinion_teeth"] + p["gear_teeth"]) / 2)
    assert dims["length"]["value_mm"] == pytest.approx(1000 * (boxes["HOUSING_BOTTOM"][1][0]
                                                               - boxes["HOUSING_BOTTOM"][0][0]), abs=0.15)
    assert dims["height"]["value_mm"] == pytest.approx(1000 * boxes["HOUSING_TOP"][1][1], abs=0.15)

    t = built["_template"][0]["analysis"]     # the generic default: overall width, depth, height
    dims = {d["id"]: d["value_mm"] for d in t["dimensions"]}
    assert dims == {"width": t["size_mm"][0], "depth": t["size_mm"][1], "height": t["size_mm"][2]}


# Mass breakdown

def test_mass_breakdown(built):
    rocket = {r["id"]: r for r in built["rocket"][0]["analysis"]["mass_breakdown"]}
    assert {"NOSE_CONE", "BODY_TUBE", "MOTOR_MOUNT", "FIN_SET"} <= set(rocket)   # top-level assemblies by default
    assert rocket["FIN_SET"]["part_ids"] == ["FIN_1", "FIN_2", "FIN_3"]
    points = [r for r in rocket.values() if not r["part_ids"]]                   # motor + recovery, not drawn
    assert sorted(r["mass_g"] for r in points) == [12.0, 24.1]
    gear = [r["label"] for r in built["gearbox"][0]["analysis"]["mass_breakdown"]]
    assert gear == ["Housing", "Gears", "Shafts", "Bearings"]                    # the part's own mass_groups


# Demos

def test_rocket_demo_is_a_launch(built):
    demo = built["rocket"][0]["analysis"]["demo"]
    assert demo["kind"] == "launch" and demo["label"] == "Fly" and demo["source"] == "/cad/flight"
    assert demo["axis"] == {"origin": [0.0, 0.0, 0.0], "dir": [0.0, 1.0, 0.0]}   # from the tail, nose up
    assert demo["values"]["ejection_delay_s"] == 5.0 and not demo["spin_parts"]


def test_quadcopter_demo_hovers_on_alternating_props(built):
    r, _, boxes = built["quadcopter"]
    demo, metrics = r["analysis"]["demo"], {m["id"]: m for m in r["analysis"]["metrics"]}
    v = demo["values"]
    assert demo["kind"] == "hover" and demo["label"] == "Hover"
    assert v["thrust_to_weight"] == pytest.approx(metrics["twr"]["value"], rel=1e-3)
    assert v["hover_throttle_pct"] == pytest.approx(100 / math.sqrt(v["thrust_to_weight"]), rel=1e-3)
    assert v["thrust_margin_pct"] == pytest.approx(100 * (1 - 1 / v["thrust_to_weight"]), rel=1e-3)
    assert v["hover_rpm"] == pytest.approx(v["max_rpm"] * v["hover_throttle_pct"] / 100, rel=1e-3)
    assert 3000 < v["hover_rpm"] < v["max_rpm"] < 60000
    spins = demo["spin_parts"]
    assert [s["id"] for s in spins] == [f"PROP_{i}" for i in range(1, 5)]
    assert [s["motor_id"] for s in spins] == [f"MOTOR_{i}" for i in range(1, 5)]
    assert [s["spin"] for s in spins] == ["ccw", "cw", "ccw", "cw"] and sum(s["rpm"] for s in spins) == 0
    for s in spins:
        assert s["dir"] == [0.0, 1.0, 0.0] and abs(s["rpm"]) == pytest.approx(v["hover_rpm"], abs=0.1)
        for part in (s["id"], s["motor_id"]):   # the axis runs up the middle of the prop and its motor
            c = centre(boxes[part])
            assert (c[0], c[2]) == pytest.approx((s["origin"][0], s["origin"][2]), abs=2e-4), part


def test_gearbox_demo_spins_the_train(built):
    r, _, boxes = built["gearbox"]
    demo, metrics = r["analysis"]["demo"], {m["id"]: m for m in r["analysis"]["metrics"]}
    v, p = demo["values"], r["params"]
    assert demo["kind"] == "spin" and demo["label"] == "Run"
    assert v["ratio"] == pytest.approx(metrics["ratio"]["value"]) and v["output_rpm"] == pytest.approx(
        metrics["speed"]["value"]) and v["output_torque_nm"] == pytest.approx(metrics["torque"]["value"], abs=1e-3)
    spins = {s["id"]: s for s in demo["spin_parts"]}
    assert set(spins) == {"INPUT_SHAFT", "PINION", "GEAR", "OUTPUT_SHAFT"}
    rel = -p["pinion_teeth"] / p["gear_teeth"]
    for part, expected in (("PINION", 1.0), ("INPUT_SHAFT", 1.0), ("GEAR", rel), ("OUTPUT_SHAFT", rel)):
        s = spins[part]
        assert s["rel_speed"] == pytest.approx(expected, abs=1e-4)
        assert s["rpm"] == pytest.approx(v["input_rpm"] * expected, abs=0.1)
        assert s["dir"] == [0.0, 0.0, 1.0]                       # along the shafts, toward the front
        c = centre(boxes[part if part.endswith("SHAFT") else ("INPUT_SHAFT" if part == "PINION" else "OUTPUT_SHAFT")])
        assert (s["origin"][0], s["origin"][1]) == pytest.approx((c[0], c[1]), abs=2e-4)
    assert spins["PINION"]["spin"] == "ccw" and spins["GEAR"]["spin"] == "cw"    # meshing gears turn opposite ways
    assert spins["PINION"]["origin"][0] - spins["GEAR"]["origin"][0] == pytest.approx(
        -p["module"] * (p["pinion_teeth"] + p["gear_teeth"]) / 2000)


def test_template_has_no_demo(built):
    demo = built["_template"][0]["analysis"]["demo"]
    assert demo["kind"] == "none" and demo["spin_parts"] == [] and demo["axis"] is None


# The flight, pad to landing

def flight_for(params, tmp_path):
    r = run_job({"op": "flight", "script": str(ROCKET), "params": params, "out_dir": str(tmp_path)})
    assert r["ok"], r.get("traceback") or r["error"]
    return r["flight"]


def check_flight(f):
    samples, events = f["samples"], {e["id"]: e for e in f["events"]}
    ts = [s["t"] for s in samples]
    assert ts[0] == 0 and all(b > a for a, b in zip(ts, ts[1:])) and len(samples) <= 400
    dt = f["sample_dt_s"]
    assert round(dt / 0.05, 6).is_integer() and all(b - a == pytest.approx(dt, abs=2e-3) for a, b in zip(ts, ts[1:-1]))
    assert 0 < ts[-1] - ts[-2] <= dt + 2e-3 and ts[-1] == pytest.approx(f["landing_time_s"], abs=0.01)
    assert all(s["alt_m"] >= 0 for s in samples) and samples[-1]["alt_m"] == 0
    assert max(s["alt_m"] for s in samples) == pytest.approx(f["apogee_m"], abs=0.5)
    assert [e["id"] for e in f["events"]] == ["liftoff", "rail_exit", "burnout", "apogee", "deploy", "landing"]
    assert [e["t"] for e in f["events"]] == sorted(e["t"] for e in f["events"])
    assert events["apogee"]["alt_m"] == pytest.approx(f["apogee_m"], abs=0.1)
    assert events["apogee"]["t"] == pytest.approx(f["time_to_apogee_s"], abs=0.01)
    assert events["rail_exit"]["speed_mps"] == pytest.approx(f["rail_exit_speed_mps"], abs=0.1)
    assert events["deploy"]["t"] == pytest.approx(max(f["burnout_time_s"] + 5.0, f["time_to_apogee_s"]), abs=0.02)
    assert events["landing"]["alt_m"] == 0 and 2.5 < f["descent_rate_mps"] < 8
    assert samples[-1]["vz_mps"] == pytest.approx(-f["descent_rate_mps"], abs=0.1)
    assert f["landing_time_s"] > f["time_to_apogee_s"] + 10 and f["drift_m"] >= 0
    peak = min(samples, key=lambda s: abs(s["t"] - f["time_to_apogee_s"]))
    assert abs(peak["vz_mps"]) < 2.0                                   # level at the top
    fastest = max(s["speed_mps"] for s in samples)                     # the grid can miss the peak at burnout
    assert 0.85 * f["max_speed_mps"] < fastest <= f["max_speed_mps"] + 0.1


def test_flight_samples_and_events_from_pad_to_landing(tmp_path):
    f = flight_for({}, tmp_path)
    check_flight(f)
    assert f["warning"] is None and len(f["trajectory_m"]) == 48       # the existing fields are still there
    assert 4 < f["descent_rate_mps"] < 5 and 40 < f["landing_time_s"] < 60   # a ~70 g rocket on a 12" chute


def test_flight_when_the_charge_fires_before_apogee(tmp_path):
    """A light rocket still climbs when the C6-5's charge fires: the chute is drawn opening at apogee, and said so."""
    f = flight_for({"body_diameter": 28, "body_length": 250, "nose_length": 120, "fin_span": 40, "fin_root": 50,
                    "fin_tip": 20}, tmp_path)
    check_flight(f)
    assert f["time_to_apogee_s"] > f["burnout_time_s"] + 5.0
    assert f["deploy_time_s"] == f["time_to_apogee_s"] and "before apogee" in f["assumptions"][-1]


# Over REST, the WebSocket and Bit's tools

@pytest.fixture(scope="module")
def hub(tmp_path_factory):
    parts = tmp_path_factory.mktemp("parts")
    for name in ("rocket.py", "quadcopter.py"):
        shutil.copy(PARTS / name, parts / name)
    with TestClient(create_app(data_dir=tmp_path_factory.mktemp("data"), cad_script=parts / "rocket.py",
                               watch=False)) as client:
        yield client


def test_api_serves_the_new_fields(hub):
    params = hub.get("/cad/params").json()
    assert params["groups"] == ["Body", "Nose", "Fins"]
    assert all(p["group"] and p["help"] for p in params["params"])
    with hub.websocket_connect("/ws") as ws:
        event = ws.receive_json()["payload"]                    # the sticky cad.model_updated
    analysis = hub.get("/cad/analysis").json()
    for key in ("markers", "dimensions", "explode", "mass_breakdown", "demo"):
        assert event["analysis"][key] == analysis[key], key
    assert analysis["demo"]["kind"] == "launch"

    flight = hub.get("/cad/flight").json()
    check_flight(flight)

    from cad.tools import call_tool
    cad, bus = hub.app.state.cad, hub.app.state.bus

    def call(tool, **args):
        return hub.portal.call(call_tool, tool, args, cad, bus)

    brief = call("cad_get_params")["analysis"]   # Bit gets the numbers, not the coordinates
    assert "explode" not in brief and "markers" not in brief and "spin_parts" not in brief["demo"]
    assert brief["dimensions"][0] == {"id": "length", "label": "Length", "value_mm": 390.0}
    assert brief["mass_breakdown"] == analysis["mass_breakdown"]
    told = call("cad_simulate_flight")
    assert "samples" not in told and "trajectory_m" not in told and told["events"] == flight["events"]

    opened = hub.post("/cad/part", json={"part": "quadcopter"}).json()
    assert opened["analysis"]["demo"]["kind"] == "hover"
    assert hub.get("/cad/params").json()["groups"] == ["Frame", "Props", "Power"]


def test_a_hexacopter_spins_six_props_in_pairs(tmp_path):
    """The part count follows the parameters (test_designs runs check_scene on every preset of every design)."""
    hexa = load_part(PARTS / "quadcopter.py").PRESETS["Hexacopter"]
    r = run_job({"op": "build", "script": str(PARTS / "quadcopter.py"), "params": hexa, "out_dir": str(tmp_path)})
    assert r["ok"], r.get("traceback") or r["error"]
    spins = r["analysis"]["demo"]["spin_parts"]
    assert [s["spin"] for s in spins] == ["ccw", "cw"] * 3 and sum(s["rpm"] for s in spins) == 0
    moves = {p["id"]: p for p in r["analysis"]["explode"]["parts"]}
    assert all(moves[f"PROP_{i}"]["dist_mm"] > 0 for i in range(1, 7))
    wheelbase = next(d for d in r["analysis"]["dimensions"] if d["id"] == "wheelbase")
    assert wheelbase["value_mm"] == pytest.approx(2 * r["params"]["arm_length"], abs=0.2)
