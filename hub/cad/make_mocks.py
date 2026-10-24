"""Write CAD mocks for Unity's hub-off sandbox: a few states of each design, exactly as the hub serves them.

    python -m cad.make_mocks [out_dir]     (from hub/; default hub/.data/mocks/cad)

parts.json is GET /cad/parts. Per state <part>/vN/: model.glb, metadata.json, analysis.json, params.json
(GET /cad/params), model_updated.json (the /ws message), drawing.png (GET /cad/drawing.png; changed parts in
orange) and, for the rocket, flight.json (GET /cad/flight).
Version numbers run on across designs, as on the hub. Plus regen_failed.json.
Copy the folder to Assets/_Forge/Mocks/cad/ (see the CAD README).
"""
from __future__ import annotations

import json
import shutil
import sys
from pathlib import Path

from cad.part_api import param_groups
from cad.worker import run_job
from events import CadModelUpdated, CadRegenFailed

HUB = Path(__file__).resolve().parents[1]
PARTS = HUB / "cad" / "parts"

# Each state's changes apply on top of the one before. The first design is the one the sandbox opens. The label is
# what the hub would call the version (cad.model_updated.label): Bit's request in the user's words, or a preset.
STATES = {
    # CAD_PLAN section 10: green, "smaller fins" red, "add a fourth fin" amber, "20% longer" green.
    "rocket": [("baseline", {}, "Defaults"),
               ("smaller_fins", {"fin_root": 42, "fin_tip": 21, "fin_span": 36, "fin_sweep": 21},
                "Make the fins smaller"),
               ("four_fins", {"fin_count": 4}, "Add a fourth fin"),
               ("four_fins_longer", {"body_length": 360}, "Make the body 20% longer")],
    # 7-inch props hit each other on 110 mm arms; longer arms fix it.
    "quadcopter": [("baseline", {}, "Defaults"),
                   ("seven_inch_props", {"prop_diameter": 7.0}, "7-inch props"),
                   ("long_range", {"arm_length": 160, "motor_size": 32, "battery_mah": 2200}, "Long range 7-inch")],
    "gearbox": [("baseline", {}, "Defaults"),
                ("high_reduction", {"pinion_teeth": 12, "gear_teeth": 72}, "High reduction"),
                ("heavy_duty", {"module": 2.5, "face_width": 18, "shaft_diameter": 12}, "Heavy duty")],
}


def preset_list(presets: dict) -> list[dict]:
    return [{"name": name, "params": params} for name, params in presets.items()]


def write_json(path: Path, data) -> None:
    path.write_text(json.dumps(data, indent=2), encoding="utf-8")


def main(out: Path) -> None:
    shutil.rmtree(out, ignore_errors=True)
    version, library = 0, []
    for part, states in STATES.items():
        script = str(PARTS / f"{part}.py")
        params, previous, first = {}, None, None
        for name, changes, label in states:
            version += 1
            params = {**params, **changes}
            vdir = out / part / f"v{version}"
            result = run_job({"op": "build", "script": script, "params": params, "out_dir": str(vdir)})
            if not result["ok"]:
                raise SystemExit(f"{part} {name}: {result['error']}")
            if "flight" in result["features"]:
                flight = run_job({"op": "flight", "script": script, "params": params, "out_dir": str(vdir)})
                if not flight["ok"]:
                    raise SystemExit(f"{part} {name} flight: {flight['error']}")
            changed = removed = []
            if previous:
                changed = [i for i, f in result["fingerprints"].items() if previous.get(i) != f]
                removed = [i for i in previous if i not in result["fingerprints"]]
            drawing = run_job({"op": "drawing", "script": script, "params": params, "out_dir": str(vdir),
                               "version": version, "highlight": changed})
            if not drawing["ok"]:
                raise SystemExit(f"{part} {name} drawing: {drawing['error']}")
            (vdir / "drawing.svg").unlink()  # Unity shows the PNG
            event = CadModelUpdated(version=version, part=part, title=result["title"], params=result["params"],
                                    previous_version=version - 1 if previous else None, changed_ids=changed,
                                    removed_ids=removed, source="mock", analysis=result["analysis"], label=label)
            write_json(vdir / "params.json", {
                "part": part, "title": result["title"], "description": result["description"], "version": version,
                "params": result["schema"], "presets": preset_list(result["presets"]), "features": result["features"],
                "groups": param_groups(result["schema"]), "glyph": result["glyph"]})
            write_json(vdir / "model_updated.json", {"type": "cad.model_updated", "payload": event.model_dump()})
            headline = result["analysis"]["metrics"][0]
            print(f"{part:10} v{version:<2} {name:17} {headline['label']} {headline['display']} ({headline['status']})"
                  f"  changed {changed}")
            previous, first = result["fingerprints"], first or result
        library.append({"part": part, "title": first["title"], "description": first["description"],
                        "presets": preset_list(first["presets"]), "features": first["features"],
                        "version": version - len(states) + 1, "active": not library, "glyph": first["glyph"]})
    write_json(out / "parts.json", {"active": next(iter(STATES)), "parts": library})
    failed = CadRegenFailed(source="script", kind="build", error="ZeroDivisionError: division by zero",
                            error_line=76, kept_version=len(STATES["rocket"]))
    write_json(out / "regen_failed.json", {"type": "cad.regen_failed", "payload": failed.model_dump()})
    print(f"wrote {out}")


if __name__ == "__main__":
    main(Path(sys.argv[1]) if len(sys.argv) > 1 else HUB / ".data" / "mocks" / "cad")
