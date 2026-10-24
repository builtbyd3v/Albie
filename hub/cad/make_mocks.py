"""Write CAD mocks for Unity's hub-off sandbox: the demo's four design states, exactly as the hub serves them.

    python -m cad.make_mocks [out_dir]     (from hub/; default hub/.data/mocks/cad)

Per state vN/: model.glb, metadata.json, analysis.json, flight.json (GET /cad/flight), params.json
(GET /cad/params) and model_updated.json (the /ws message). Plus regen_failed.json.
Copy the folder to Assets/_Forge/Mocks/cad/.
"""
from __future__ import annotations

import json
import shutil
import sys
from pathlib import Path

from cad.worker import run_job
from events import CadModelUpdated, CadRegenFailed

HUB = Path(__file__).resolve().parents[1]
SCRIPT = HUB / "cad" / "parts" / "rocket.py"

# CAD_PLAN section 10: green, "smaller fins" red, "add a fourth fin" amber, "20% longer" green.
STATES = [
    ("baseline", {}),
    ("smaller_fins", {"fin_root": 42, "fin_tip": 21, "fin_span": 36, "fin_sweep": 21}),
    ("four_fins", {"fin_count": 4}),
    ("four_fins_longer", {"body_length": 360}),
]


def main(out: Path) -> None:
    shutil.rmtree(out, ignore_errors=True)
    params, previous = {}, None
    for version, (name, changes) in enumerate(STATES, start=1):
        params = {**params, **changes}
        vdir = out / f"v{version}"
        result = run_job({"op": "build", "script": str(SCRIPT), "params": params, "out_dir": str(vdir)})
        flight = run_job({"op": "flight", "script": str(SCRIPT), "params": params, "out_dir": str(vdir)})
        if not result["ok"] or not flight["ok"]:
            raise SystemExit(f"{name}: {result.get('error') or flight.get('error')}")
        changed = removed = []
        if previous:
            changed = [i for i, f in result["fingerprints"].items() if previous.get(i) != f]
            removed = [i for i in previous if i not in result["fingerprints"]]
        event = CadModelUpdated(version=version, params=result["params"], previous_version=version - 1 or None,
                                changed_ids=changed, removed_ids=removed, source="mock",
                                analysis=result["analysis"])
        (vdir / "params.json").write_text(json.dumps(
            {"part": result["part"], "version": version, "params": result["schema"]}, indent=2))
        (vdir / "model_updated.json").write_text(json.dumps(
            {"type": "cad.model_updated", "payload": event.model_dump()}, indent=2))
        a = result["analysis"]
        print(f"v{version} {name:17} margin {a['margin_cal']:5} cal  {a['stability']:10} "
              f"apogee {flight['flight']['apogee_m']:5} m  changed {changed}")
        previous = result["fingerprints"]
    failed = CadRegenFailed(source="script", kind="build", error="ZeroDivisionError: division by zero",
                            error_line=76, kept_version=len(STATES))
    (out / "regen_failed.json").write_text(json.dumps({"type": "cad.regen_failed", "payload": failed.model_dump()},
                                                      indent=2))
    print(f"wrote {out}")


if __name__ == "__main__":
    main(Path(sys.argv[1]) if len(sys.argv) > 1 else HUB / ".data" / "mocks" / "cad")
