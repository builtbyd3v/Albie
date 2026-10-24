"""The humanoid: Unitree's G1 meshes posed by its URDF. Mesh parts, the statics behind "Will it stand?", the Walk
motion and the drawing's mesh path. test_designs runs the shared checks on every preset."""
import json
import math
import time

import numpy as np
import pytest
from build123d import Face
from PIL import Image

from cad import meshes, robot as rb
from cad.analysis import analyze, mass_properties
from cad.part_api import Motion, MotionJoint
from cad.scene import motion as motion_block
from cad.worker import build_part, load_part, run_job

from conftest import PARTS

SCRIPT = PARTS / "humanoid.py"


@pytest.fixture(scope="module")
def humanoid():
    return load_part(SCRIPT)


def analysis(params: dict) -> dict:
    module, _, _, result = build_part(SCRIPT, params)
    return analyze(result, mass_properties(result), module.analyze)


def metric(a: dict, metric_id: str) -> dict:
    return next(m for m in a["metrics"] if m["id"] == metric_id)


def test_mesh_pack_has_every_piece_with_normals_and_an_outline(humanoid):
    pack = meshes.pack(humanoid.DATA)
    keys = [p["key"] for e in pack.manifest["links"].values() for p in e["pieces"]]
    assert len(keys) == len(set(keys)) == len(pack.faces) == len(pack.outlines) == 88
    assert set(keys) == set(pack.faces) == set(pack.outlines)
    shin = Face(pack.faces["left_shin"])
    assert meshes.is_mesh(shin) and shin.bounding_box().size.Z > 200   # mm, a shin's length
    placed = pack.place("left_shin", np.eye(4), "left_shin", (0.5, 0.5, 0.5))
    assert placed.label == "left_shin" and getattr(placed, meshes.OUTLINE)[1].shape[1] == 3
    assert "logo_link" not in pack.manifest["links"], "Unitree's logo plate stays out"


def test_stand_stands_and_leaning_forward_with_arms_out_falls(humanoid):
    stand = analysis(humanoid.PRESETS["Stand"])
    assert metric(stand, "balance")["value"] > 25 and stand["verdict"]["headline"] == "Stands steady"
    lean = analysis(humanoid.PRESETS["Lean forward"])
    assert metric(lean, "balance")["value"] < 0 and lean["verdict"]["status"] == "bad"
    assert "ahead of its toes" in lean["verdict"]["sentence"]


def test_a_deep_squat_runs_the_ankles_out_of_range_and_arms_forward_save_it(humanoid):
    deep = analysis(humanoid.PRESETS["Deep squat"])
    assert deep["verdict"]["headline"] == "Falls over"
    assert "behind its heels" in deep["verdict"]["sentence"] and "ankles" in deep["verdict"]["sentence"]
    saved = analysis(humanoid.PRESETS["Squat, arms forward"])
    assert metric(saved, "balance")["value"] > 25 and saved["verdict"]["status"] != "bad"


def test_carrying_with_forearms_out_overloads_the_wrists(humanoid):
    heavy = analysis(humanoid.PRESETS["Carry 15 kg"])
    load = metric(heavy, "joint_load")
    assert load["value"] > 100 and "wrists" in load["display"] and heavy["verdict"]["headline"] == "Motors overloaded"
    down = analysis(humanoid.PRESETS["Carry 10 kg, arms down"])
    assert metric(down, "joint_load")["value"] < 60 and down["verdict"]["status"] == "ok"
    assert any(row["label"] == "Load" and row["mass_g"] == pytest.approx(10000) for row in down["mass_breakdown"])


def test_joints_clamp_to_the_urdf_limits(humanoid):
    pose = humanoid.Pose({**humanoid.PRESETS["Stand"], "squat": 150})
    ankle = humanoid.robot().joints["left_ankle_pitch_joint"]
    assert pose.angles["left_ankle_pitch_joint"] == pytest.approx(ankle.lower)
    assert "left_ankle_pitch_joint" in pose.clamped and "left_knee_joint" not in pose.clamped


@pytest.mark.parametrize("preset", ["Stand", "Wide stance", "Mid-stride", "Lean forward"])
def test_feet_stay_flat_on_the_floor_between_them_on_the_axis(humanoid, preset):
    pose = humanoid.Pose(humanoid.PRESETS[preset])
    soles = {side: humanoid.homogeneous(humanoid.soles()[side], pose.poses[f"{side}_ankle_roll_link"])
             for side, _ in humanoid.SIDES}
    assert min(v[:, 2].min() for v in soles.values()) == pytest.approx(0.0, abs=1e-6)
    assert all(np.ptp(v[:, 2]) < 2.0 for v in soles.values()), "both soles flat"
    middle = np.mean([v.mean(axis=0) for v in soles.values()], axis=0)
    assert np.hypot(*middle[:2]) < 1e-6


def test_stance_and_step_land_where_asked(humanoid):
    pose = humanoid.Pose({**humanoid.PRESETS["Stand"], "stance": 400, "step": 300})
    centres = {side: humanoid.homogeneous(humanoid.soles()[side], pose.poses[f"{side}_ankle_roll_link"]).mean(axis=0)
               for side, _ in humanoid.SIDES}
    assert centres["left"][0] - centres["right"][0] == pytest.approx(400, abs=3)
    assert centres["right"][1] - centres["left"][1] == pytest.approx(300, abs=3)   # the left foot ahead (-Y)


def test_static_torque_matches_a_hand_calculation(tmp_path):
    """A 2 kg arm hanging off a joint 0.3 m out, axis along X: the motor holds 2 x 9.81 x 0.3 N·m."""
    urdf = tmp_path / "arm.urdf"
    urdf.write_text("""<robot name="arm">
      <link name="base"><inertial><mass value="5"/><origin xyz="0 0 0"/></inertial></link>
      <link name="arm"><inertial><mass value="2"/><origin xyz="0 0.3 0"/></inertial></link>
      <joint name="shoulder" type="revolute"><parent link="base"/><child link="arm"/>
        <origin xyz="0 0 1" rpy="0 0 0"/><axis xyz="1 0 0"/><limit lower="-3" upper="3" effort="10"/></joint>
    </robot>""")
    robot = rb.load(urdf)
    torques = rb.static_torques(robot, robot.poses({}))
    assert torques["shoulder"] == pytest.approx(2 * rb.G * 0.3, rel=1e-6)
    # Turned to hang straight down, it holds nothing.
    assert rb.static_torques(robot, robot.poses({"shoulder": -math.pi / 2}))["shoulder"] == pytest.approx(0, abs=1e-9)


def test_a_rebuild_is_well_inside_the_build_timeout(tmp_path):
    run_job({"op": "build", "script": str(SCRIPT), "params": {}, "out_dir": str(tmp_path / "a")})   # warm caches
    start = time.perf_counter()
    r = run_job({"op": "build", "script": str(SCRIPT), "params": {"squat": 40, "arms_out": 30},
                 "out_dir": str(tmp_path / "b")})
    assert r["ok"] and time.perf_counter() - start < 4.0   # the hub stops a build at 8 s


def test_walk_motion_turns_assembly_nodes_and_loops(humanoid, tmp_path):
    build = run_job({"op": "build", "script": str(SCRIPT), "params": {}, "out_dir": str(tmp_path)})
    assert "motion" in build["features"]
    demo = build["analysis"]["demo"]
    assert demo["kind"] == "walk" and demo["source"] == "/cad/motion" and demo["blocked"] is None
    assert [r["id"] for r in demo["readouts"]] == ["speed", "steps", "walk_load"]
    r = run_job({"op": "motion", "script": str(SCRIPT), "params": {}, "out_dir": str(tmp_path)})
    assert r["ok"], r.get("traceback")
    m = json.loads((tmp_path / "motion.json").read_text())
    assert m == r["motion"] and m["loop"] and m["rate_hz"] == 50 and m["samples"] == 560
    meta = {o["id"]: o for o in json.loads((tmp_path / "metadata.json").read_text())["objects"]}
    assert len(m["joints"]) == 14
    for j in m["joints"]:
        assert meta[j["id"]]["type"] == "assembly" and len(j["angles_deg"]) == m["samples"]
        assert abs(math.hypot(*j["dir"]) - 1) < 1e-3
        steps = np.abs(np.diff(j["angles_deg"]))
        assert abs(j["angles_deg"][-1] - j["angles_deg"][0]) <= steps.max() + 0.05, "the loop seam is one more step"
    root = m["root"]
    assert len(root["pos"]) == len(root["rot"]) == m["samples"]
    assert all(abs(math.hypot(*q) - 1) < 1e-4 for q in root["rot"])
    assert math.degrees(2 * math.acos(min(1.0, abs(root["rot"][0][3])))) < 10, "it sets off as it stands, near enough"
    radius = np.median([np.hypot(p[0] - m["centre"][0], p[2] - m["centre"][2]) for p in root["pos"]])
    assert radius == pytest.approx(m["values"]["radius_mm"] / 1000, rel=0.15)
    assert len(m["tracks"]["load_pct"]) == m["samples"]


def test_walk_is_blocked_when_it_would_fall_or_the_load_is_too_much(humanoid):
    assert "falls over" in analysis(humanoid.PRESETS["Lean forward"])["demo"]["blocked"]
    assert "Too much to walk with" in analysis(humanoid.PRESETS["Carry 15 kg"])["demo"]["blocked"]


def test_the_drawing_takes_the_mesh_path(tmp_path):
    r = run_job({"op": "drawing", "script": str(SCRIPT), "params": {"carry": 5}, "out_dir": str(tmp_path),
                 "version": 3, "highlight": ["LEFT_SHIN"]})
    assert r["ok"], r.get("traceback")
    with Image.open(tmp_path / "drawing.png") as png:
        assert png.size == (2048, 1448)
        orange = np.all(np.abs(np.asarray(png.convert("RGB"), int) - (0xef, 0x7d, 0x00)) < 40, axis=2).sum()
    assert orange > 500, "the changed shin is drawn in orange"
    assert (tmp_path / "drawing.svg").stat().st_size < 2_000_000, "the views' lines are pixels in the SVG"


def test_scene_converts_a_motion_to_the_model_frame():
    q = (0.0, 0.0, math.sin(math.pi / 4), math.cos(math.pi / 4))   # 90° about part Z
    block = motion_block(Motion(25.0, [MotionJoint("knee", (10.0, 20.0, 30.0), (0.0, 0.0, 2.0), (0.0, 5.0))],
                                positions=[(0.0, 0.0, 0.0), (1000.0, 0.0, 0.0)], rotations=[(0, 0, 0, 1), q],
                                centre=(0.0, 500.0, 0.0)), ["robot", "knee"])
    j = block["joints"][0]
    assert j["id"] == "KNEE" and j["origin"] == [0.01, 0.03, -0.02] and j["dir"] == [0.0, 1.0, 0.0]
    assert block["root"]["pos"][1] == [1.0, 0.0, 0.0] and block["centre"] == [0.0, 0.0, -0.5]
    assert block["root"]["rot"][1] == pytest.approx([0.0, math.sin(math.pi / 4), 0.0, math.cos(math.pi / 4)])
    assert block["samples"] == 2 and block["duration_s"] == 0.08
    with pytest.raises(ValueError, match="aren't nodes"):
        motion_block(Motion(25.0, [MotionJoint("elbow", (0, 0, 0), (1, 0, 0), (0.0,))]), ["robot"])
