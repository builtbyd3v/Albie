import json

import pytest
from build123d import Compound, Box

from cad.analysis import analyze, barrowman, mass_properties
from cad.export import export_part, read_glb_json
from cad.part_api import BuildResult
from cad.worker import build_part

from conftest import ROCKET

DEMO = {
    "baseline": {},
    "smaller_fins": {"fin_root": 42, "fin_tip": 21, "fin_span": 36, "fin_sweep": 21},
}
DEMO["four_fins"] = {**DEMO["smaller_fins"], "fin_count": 4}
DEMO["four_fins_longer"] = {**DEMO["four_fins"], "body_length": 360}


def built(params=None):
    _, values, _, result = build_part(ROCKET, params or {})
    return values, result


def test_fin_geometry_matches_the_aero_description():
    """The stability math must describe the fins that are actually drawn (sweep = root LE to tip LE)."""
    values, result = built({"fin_sweep": 30, "fin_root": 70, "fin_tip": 25, "fin_span": 50})
    fin = next(s for s in result.assembly.leaves if s.label == "fin_1")  # fin_1 lies along +X
    r = values["body_diameter"] / 2
    root_z = [v.Z for v in fin.vertices() if abs(v.X - r) < 1e-6]
    tip_z = [v.Z for v in fin.vertices() if abs(v.X - (r + 50)) < 1e-6]
    assert max(root_z) - min(root_z) == pytest.approx(70)
    assert max(tip_z) - min(tip_z) == pytest.approx(25)
    assert max(root_z) - max(tip_z) == pytest.approx(result.rocket.fins.sweep) == pytest.approx(30)
    assert max(root_z) == pytest.approx(result.rocket.fins.root_le_z)


def test_barrowman_matches_rocketpy():
    rocketpy = pytest.importorskip("rocketpy")
    for params in [{}, DEMO["smaller_fins"], DEMO["four_fins_longer"], {"nose_shape": "cone", "fin_count": 5}]:
        _, result = built(params)
        spec, mm = result.rocket, 1e-3
        rocket = rocketpy.Rocket(radius=spec.diameter_mm / 2 * mm, mass=0.1, inertia=(1e-3, 1e-3, 1e-5),
                                 power_off_drag=0.5, power_on_drag=0.5,
                                 center_of_mass_without_motor=spec.length_mm / 2 * mm,
                                 coordinate_system_orientation="nose_to_tail")
        rocket.add_nose(length=spec.nose_length_mm * mm, position=0,
                        kind="tangent" if spec.nose_shape == "ogive" else "conical")
        f = spec.fins
        rocket.add_trapezoidal_fins(n=f.count, root_chord=f.root_chord * mm, tip_chord=f.tip_chord * mm,
                                    span=f.span * mm, sweep_length=f.sweep * mm,
                                    position=(spec.length_mm - f.root_le_z) * mm)
        cp, cna = barrowman(spec)
        assert cp == pytest.approx(rocket.cp_position(0) * 1000, abs=0.5), params
        assert cna == pytest.approx(rocket.total_lift_coeff_der(0), rel=0.01), params


def test_demo_story_stability():
    """CAD_PLAN section 10: green -> 'smaller fins' red -> 'fourth fin' amber -> '20% longer' green."""
    def stability(name):
        _, result = built(DEMO[name])
        return analyze(result, mass_properties(result))["stability"]
    assert stability("baseline") == "stable"
    assert stability("smaller_fins") == "unstable"
    assert stability("four_fins") == "marginal"
    assert stability("four_fins_longer") == "stable"


def test_masses_are_plausible():
    _, result = built()
    masses = {label: m for label, (m, _) in mass_properties(result).items()}
    assert 5 < masses["nose_cone"] < 25          # hollow shell, not a solid plastic plug
    assert 15 < masses["body_tube"] < 40
    total = analyze(result, mass_properties(result))["mass_g"]
    assert 60 < total < 113                        # Estes C6 max liftoff weight is 113 g


def test_export_is_cadvision_valid_in_metres_and_y_up(tmp_path):
    values, result = built(DEMO["four_fins_longer"])
    masses = mass_properties(result)
    out = export_part(result, tmp_path, part="rocket", params=values, masses=masses)  # validates mapping
    gltf = read_glb_json(tmp_path / "model.glb")
    names = [n.get("name") for n in gltf["nodes"]]
    for label in ["rocket", "nose_cone", "body_tube", "motor_mount", "fin_set", "fin_1", "fin_4"]:
        assert names.count(label) == 1
    root = gltf["nodes"][gltf["scenes"][0]["nodes"][0]]
    assert root["rotation"] == pytest.approx([-0.7071068, 0, 0, 0.7071068])  # Z-up -> glTF Y-up
    extents = [abs(x) for a in gltf["accessors"] if len(a.get("max", [])) == 3 for x in a["max"] + a["min"]]
    assert max(extents) < 1.0  # metres; a 1000x unit bug would show ~360
    nose = gltf["nodes"][names.index("nose_cone")]
    assert nose["translation"][2] == pytest.approx(0.36)
    ids = {o["id"] for o in out["metadata"]["objects"]}
    assert {"ROCKET", "FIN_4", "CENTERING_RING_FWD"} <= ids
    assert json.loads((tmp_path / "metadata.json").read_text())["schemaVersion"] == "1.0"


def test_duplicate_labels_are_rejected(tmp_path):
    a, b = Box(1, 1, 1), Box(2, 2, 2)
    a.label = b.label = "same"
    with pytest.raises(ValueError, match="Duplicate label"):
        export_part(BuildResult(Compound(label="root", children=[a, b])), tmp_path, part="x", params={}, masses={})
