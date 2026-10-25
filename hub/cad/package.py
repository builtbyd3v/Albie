"""A version's files for a supplier or a print shop, written by the worker's "package" op (CadService.package):

    <part>_v<N>.step          the CAD model (AP214, millimetres): what machinists, CAM tools and other CAD tools open
    <part>_v<N>.stl           a mesh for 3D printing
    <part>_v<N>_drawing.pdf   the engineering drawing (cad/drawing.py), without the "changed" highlight
    <part>_v<N>_parts.csv     the parts list: part, material, quantity, mass

files.json, written last, lists them; CadService treats it as the "done" marker.
"""
from __future__ import annotations

import csv
import json
from pathlib import Path
from typing import Any

from cad.analysis import center_of_mass
from cad.part_api import BuildResult

MANIFEST = "files.json"
KINDS = ("step", "stl", "drawing", "parts")
TITLES = {"step": "CAD model (STEP)", "stl": "Mesh for 3D printing (STL)", "drawing": "Engineering drawing (PDF)",
          "parts": "Parts list (CSV)"}


def file_names(part: str, version: int) -> dict[str, str]:
    stem = f"{part}_v{version}"
    return {"step": f"{stem}.step", "stl": f"{stem}.stl", "drawing": f"{stem}_drawing.pdf", "parts": f"{stem}_parts.csv"}


def write_parts_csv(result: BuildResult, masses, path: Path) -> None:
    from cad.drawing import parts_rows

    total_g, _ = center_of_mass(result, masses)
    with path.open("w", newline="", encoding="utf-8") as f:
        w = csv.writer(f)
        w.writerow(["Item", "Part", "Material", "Qty", "Mass (g)"])
        for i, row in enumerate(parts_rows(result, masses, set()), start=1):
            w.writerow([i, row["name"], row["material"].replace("—", ""), row["qty"], f"{row['mass']:.1f}"])
        w.writerow(["", "Total", "", "", f"{total_g:.1f}"])


def write_package(result: BuildResult, masses, out_dir: Path, *, part: str, title: str, version: int,
                  created_at: float | None = None) -> list[dict[str, Any]]:
    """Write the four files and files.json into out_dir; returns the manifest rows {kind, name, title, bytes}."""
    from build123d import Unit, export_step, export_stl

    from cad.drawing import draw

    out_dir.mkdir(parents=True, exist_ok=True)
    names = file_names(part, version)
    if not export_step(result.assembly, str(out_dir / names["step"]), unit=Unit.MM):
        raise RuntimeError("STEP export failed")
    export_stl(result.assembly, str(out_dir / names["stl"]))
    draw(result, masses, out_dir, title=title, part=part, version=version, created_at=created_at,
         files=(names["drawing"],))
    write_parts_csv(result, masses, out_dir / names["parts"])
    rows = [{"kind": kind, "name": names[kind], "title": TITLES[kind], "bytes": (out_dir / names[kind]).stat().st_size}
            for kind in KINDS]
    tmp = out_dir / (MANIFEST + ".tmp")
    tmp.write_text(json.dumps(rows, indent=1), encoding="utf-8")
    tmp.replace(out_dir / MANIFEST)
    return rows
