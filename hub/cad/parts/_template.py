"""Template for a new design. Copy this file to hub/cad/parts/<your_design>.py (no leading underscore) and it
shows up in the Designs menu straight away; open it there (or ask Bit), then save the file to regenerate.

The file name is the design's id. Millimetres, Z up, the design standing on z=0. Every part needs a unique
label: it becomes the GLB node name and the object ID Unity selects, explodes and highlights.
"""
from build123d import Align, Box, Color, Compound, Location

from cad.part_api import BuildResult, Metric, Param, SolidMass

TITLE = "Storage box"                    # the Designs menu and the label on the desk
DESCRIPTION = "A box with a lift-off lid."

# Each parameter is a slider (or one button per option with options=(...)): name, default, min, max, unit, step.
PARAMS = [
    Param("width", 120.0, 40, 300, "mm", 5, label="Width"),
    Param("depth", 80.0, 40, 300, "mm", 5, label="Depth"),
    Param("height", 60.0, 20, 200, "mm", 5, label="Height"),
    Param("wall", 2.0, 1, 6, "mm", 0.5, label="Wall"),
]

# Optional: named sets of values, applied on top of the current ones (buttons on the panel; Bit can use them too).
PRESETS = {
    "Pencil case": {"width": 200, "depth": 50, "height": 30},
    "Shoebox": {"width": 300, "depth": 180, "height": 110},
}

PLA = 1.24  # g/cm^3
BOTTOM = (Align.CENTER, Align.CENTER, Align.MIN)


def build(p) -> BuildResult:
    w, d, h, t = p["width"], p["depth"], p["height"], p["wall"]
    lid_h = min(15.0, h / 3)
    tray = Box(w, d, h - lid_h, align=BOTTOM) - Box(w - 2 * t, d - 2 * t, h, align=BOTTOM).moved(Location((0, 0, t)))
    lid = (Box(w, d, lid_h, align=BOTTOM) - Box(w - 2 * t, d - 2 * t, lid_h - t, align=BOTTOM)).moved(
        Location((0, 0, h - lid_h)))
    tray.label, tray.color = "tray", Color(0.3, 0.6, 0.9)
    lid.label, lid.color = "lid", Color(0.95, 0.6, 0.2)
    return BuildResult(assembly=Compound(label="storage_box", children=[tray, lid]),
                       masses={"tray": SolidMass(PLA, "PLA"), "lid": SolidMass(PLA, "PLA")})


def analyze(result: BuildResult, masses) -> list[Metric]:
    """Optional: numbers for the panel (mass, size and part count come free). min/max make it a gauge;
    status (ok, warn, bad) colours it."""
    p = result.params
    litres = (p["width"] - 2 * p["wall"]) * (p["depth"] - 2 * p["wall"]) * (p["height"] - 2 * p["wall"]) / 1e6
    return [Metric("capacity", "Capacity", litres, "L", digits=2)]
