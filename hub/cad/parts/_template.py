"""Template for a new design. Copy this file to hub/cad/parts/<your_design>.py (no leading underscore) and it
shows up in the Designs menu straight away; open it there (or ask Bit), then save the file to regenerate.

The file name is the design's id. Millimetres, Z up, the design standing on z=0. Every part needs a unique
label: it becomes the GLB node name and the object ID Unity selects, explodes and highlights.
"""
from build123d import Align, Box, Color, Compound, Location

from cad.part_api import Analysis, BuildResult, Explode, ExplodeMove, Metric, Param, SolidMass

TITLE = "Storage box"                    # the Designs menu and the label on the desk
DESCRIPTION = "A box with a lift-off lid."

# Each parameter is a slider (or one button per option with options=(...)): name, default, min, max, unit, step
# (the fine stepper's increment; leave it out and it's worked out from the range). Optional: label, group (the
# panel section; default "General") and help (one short line for the panel and for Bit).
PARAMS = [
    Param("width", 120.0, 40, 300, "mm", 5, label="Width", group="Size", help="Outside width, left to right."),
    Param("depth", 80.0, 40, 300, "mm", 5, label="Depth", group="Size", help="Outside depth, front to back."),
    Param("height", 60.0, 20, 200, "mm", 5, label="Height", group="Size", help="Outside height with the lid on."),
    Param("wall", 2.0, 1, 6, "mm", 0.5, label="Wall", group="Walls", help="Wall, floor and lid thickness."),
]

# Optional: named sets of values, applied on top of the current ones (buttons on the panel; Bit can use them too).
PRESETS = {
    "Pencil case": {"width": 200, "depth": 50, "height": 30},
    "Shoebox": {"width": 300, "depth": 180, "height": 110},
}

PLA = 1.24  # g/cm^3
BOTTOM = (Align.CENTER, Align.CENTER, Align.MIN)


def lid_height(p) -> float:
    return min(15.0, p["height"] / 3)


def build(p) -> BuildResult:
    w, d, h, t = p["width"], p["depth"], p["height"], p["wall"]
    lid_h = lid_height(p)
    tray = Box(w, d, h - lid_h, align=BOTTOM) - Box(w - 2 * t, d - 2 * t, h, align=BOTTOM).moved(Location((0, 0, t)))
    lid = (Box(w, d, lid_h, align=BOTTOM) - Box(w - 2 * t, d - 2 * t, lid_h - t, align=BOTTOM)).moved(
        Location((0, 0, h - lid_h)))
    tray.label, tray.color = "tray", Color(0.3, 0.6, 0.9)
    lid.label, lid.color = "lid", Color(0.95, 0.6, 0.2)
    return BuildResult(assembly=Compound(label="storage_box", children=[tray, lid]),
                       masses={"tray": SolidMass(PLA, "PLA"), "lid": SolidMass(PLA, "PLA")})


def analyze(result: BuildResult, masses) -> Analysis:
    """Optional: numbers for the panel (mass, size and part count come free). min/max make a metric a gauge;
    status (ok, warn, bad) colours it. Return a list of Metric, or an Analysis to also give Unity any of these
    (positions in part millimetres like the geometry; the hub works out whatever you leave out, cad/scene.py):
      markers=[Marker("tip", "Tip", "point", (x, y, z))]   labelled points (the CG comes free)
      dimensions=[Dimension(...)]                           callouts (default: overall width, depth, height)
      explode=Explode(...)                                  how it comes apart (default: spread out from the centre)
      demo=Demo(...)                                        the demo button (default: none; Fly for rockets). A fan:
          Demo("spin", "Run", spinners=[Spinner("rotor", origin=(0, 0, 20), direction=(0, 0, 1), rpm=1200)])
      mass_groups={"Shell": ["tray", "lid"]}                the mass breakdown (default: by top-level assembly)
    """
    p = result.params
    litres = (p["width"] - 2 * p["wall"]) * (p["depth"] - 2 * p["wall"]) * (p["height"] - 2 * p["wall"]) / 1e6
    return Analysis(
        metrics=[Metric("capacity", "Capacity", litres, "L", digits=2)],
        explode=Explode("stack", [ExplodeMove("lid", (0, 0, 1), 2 * lid_height(p) + 10)]))  # the tray stays put
