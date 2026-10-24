"""Single-stage spur gearbox: a pinion drives a gear inside a split housing. Save this file and it regenerates.

Millimetres, Z up, housing on z=0. Shafts run along Y (input out the front at -Y, output out the back at +Y)
and the housing splits at the shaft centreline like a real one, so the top half lifts off to show the gears.
Every part needs a unique label: it becomes the GLB node name and the object ID Unity selects and explodes.
"""
import math

from build123d import Align, Box, Circle, Color, Compound, Cylinder, Location, Plane, Polygon, Solid

from cad.part_api import BuildResult, Metric, Param, SolidMass, band

TITLE = "Gearbox"
DESCRIPTION = "Spur gear reducer: pinion, gear, shafts, bearings, split housing. Ratio = gear teeth / pinion teeth."

PARAMS = [
    Param("pinion_teeth", 15, 8, 40, "", 1, label="Pinion teeth"),
    Param("gear_teeth", 45, 16, 100, "", 1, label="Gear teeth"),
    Param("module", 1.5, 0.75, 3.0, "mm", 0.25, label="Gear module"),
    Param("face_width", 10.0, 4, 30, "mm", 1, label="Face width"),
    Param("shaft_diameter", 8.0, 4, 16, "mm", 1, label="Shaft diameter"),
]

# Applied on top of the current values. "High reduction" undercuts its 12-tooth pinion (a warning).
PRESETS = {
    "High reduction": {"pinion_teeth": 12, "gear_teeth": 72},
    "1:1 transfer": {"pinion_teeth": 30, "gear_teeth": 30},
    "Heavy duty": {"module": 2.5, "face_width": 18, "shaft_diameter": 12},
    "Compact": {"module": 1.0, "face_width": 6, "shaft_diameter": 5},
}

PRESSURE_ANGLE = math.radians(20)
UNDERCUT_BELOW = 14          # 20 deg full-depth teeth: fewer than ~14 on the pinion get undercut when cut
INPUT_RPM, INPUT_TORQUE_NM, MESH_EFFICIENCY = 3000.0, 0.5, 0.98   # assumed motor on the input shaft
DENSITY = {"aluminium": 2.7, "steel": 7.85, "brass": 8.5}
HOUSING, LID, BRASS, STEEL, SHAFT, BEARING = ((0.55, 0.58, 0.62), (0.2, 0.45, 0.85), (0.85, 0.62, 0.22),
                                              (0.72, 0.74, 0.78), (0.3, 0.31, 0.35), (0.84, 0.86, 0.9))
Y_AXIS = (90, 0, 0)          # rotation that lays a Z-axis cylinder along Y


def labelled(shape, label, rgb):
    shape.label, shape.color = label, Color(*rgb)
    return shape


def involute(alpha):
    return math.tan(alpha) - alpha


def gear_outline(module, teeth, phase=0.0, flank_points=5):
    """Spur gear outline as (x, y) points: involute flanks, tip, root. Tooth 0 is centred at angle `phase`."""
    r = module * teeth / 2
    rb, ra, rf = r * math.cos(PRESSURE_ANGLE), r + module, r - 1.25 * module
    half_base = math.pi / (2 * teeth) + involute(PRESSURE_ANGLE)  # half a tooth, as an angle, at the base circle

    def half(rho):  # half the tooth's angular thickness at radius rho
        return half_base - involute(math.acos(min(1.0, rb / rho)))

    start = max(rb, rf)
    flank = [start + (ra - start) * i / (flank_points - 1) for i in range(flank_points)]
    points = []
    for k in range(teeth):
        c = phase + 2 * math.pi * k / teeth
        side = [(rho, c - half(rho)) for rho in flank]
        other = [(rho, c + half(rho)) for rho in reversed(flank)]
        root = [(rf, c - half(start))] * (rf < rb) + side + other + [(rf, c + half(start))] * (rf < rb)
        points += root + [(rf, c + math.pi / teeth)]  # root midpoint before the next tooth
    return [(rho * math.cos(a), rho * math.sin(a)) for rho, a in points]


def spur_gear(module, teeth, width, bore, phase=0.0):
    """A gear on the Y axis: the outline with its bore as one face, swept along Y. Solid.extrude is a plain
    prism; extrude(both=True) fuses two halves, which takes over a second on a gear outline."""
    face = (Plane.XZ * (Polygon(*gear_outline(module, teeth, phase), align=None) - Circle(bore / 2))).face()
    return Solid.extrude(face, (0, width, 0)).moved(Location((0, -width / 2, 0)))


def tube(outer_d, inner_d, length):
    return Cylinder(outer_d / 2, length, rotation=Y_AXIS) - Cylinder(inner_d / 2, length + 1, rotation=Y_AXIS)


def build(p) -> BuildResult:
    m, z1, z2, b, d = p["module"], p["pinion_teeth"], p["gear_teeth"], p["face_width"], p["shaft_diameter"]
    r1, r2 = m * z1 / 2, m * z2 / 2
    a = r1 + r2                                      # centre distance
    x1, x2 = -a / 2, a / 2
    bearing_od, bearing_w = max(d + 6, 2.75 * d), max(4.0, 0.875 * d)
    gap, wall = 3 + m, bearing_w + 1                 # clearance round the gears; walls hold the bearings
    xmin, xmax = x1 - r1 - m - gap, x2 + r2 + m + gap   # tip circles plus clearance
    zc = wall + max(r1, r2) + m + gap                # shaft centreline = the split line
    inner_depth = b + 2 * gap
    depth = inner_depth + 2 * wall

    outer = Box(xmax - xmin + 2 * wall, depth, 2 * zc, align=(Align.MIN, Align.CENTER, Align.MIN))
    cavity = Box(xmax - xmin, inner_depth, 2 * zc - 2 * wall, align=(Align.MIN, Align.CENTER, Align.MIN))
    seats = [Cylinder(bearing_od / 2, depth + 2, rotation=Y_AXIS).moved(Location((x, 0, zc))) for x in (x1, x2)]
    shell = outer.moved(Location((xmin - wall, 0, 0))) - cavity.moved(Location((xmin, 0, wall))) - seats
    big = 4 * (xmax - xmin + depth + zc)
    lower = Box(big, big, zc, align=(Align.CENTER, Align.CENTER, Align.MIN))
    housing = Compound(label="housing", children=[
        labelled(shell & lower, "housing_bottom", HOUSING),
        labelled(shell & lower.moved(Location((0, 0, zc))), "housing_top", LID)])

    # A bore wider than the pinion's root circle would cut it in pieces: keep a 0.8 mm rim (the metric says why).
    def bore(teeth):
        return min(d, 2 * (m * teeth / 2 - 1.25 * m) - 1.6)

    def stage(name, short, x, gear, stick_out):
        """Shaft (sticking out of the front or back by stick_out mm), gear, and the bearings in both walls."""
        shaft =Cylinder(d / 2, depth + abs(stick_out), rotation=Y_AXIS).moved(Location((x, stick_out / 2, zc)))
        bearings = [labelled(tube(bearing_od, d, bearing_w).moved(Location((x, s * (depth - wall) / 2, zc))),
                             f"bearing_{short}_{wall_name}", BEARING) for s, wall_name in ((-1, "front"), (1, "back"))]
        return Compound(label=f"{name}_stage", children=[labelled(shaft, f"{name}_shaft", SHAFT),
                                                          gear.moved(Location((x, 0, zc))), *bearings])

    pinion = labelled(spur_gear(m, z1, b, bore(z1)), "pinion", BRASS)
    gear = labelled(spur_gear(m, z2, b, bore(z2), phase=math.pi + math.pi / z2), "gear", STEEL)  # a gap faces the pinion
    stages = [stage("input", "in", x1, pinion, -25), stage("output", "out", x2, gear, 30)]

    steel = SolidMass(DENSITY["steel"], "steel")
    return BuildResult(
        assembly=Compound(label="gearbox", children=[housing, *stages]),
        masses={"housing_bottom": SolidMass(DENSITY["aluminium"], "aluminium"),
                "housing_top": SolidMass(DENSITY["aluminium"], "aluminium"),
                "pinion": SolidMass(DENSITY["brass"], "brass"), "gear": steel, "input_shaft": steel,
                "output_shaft": steel, **{f"bearing_{s}_{e}": steel for s in ("in", "out") for e in ("front", "back")}},
    )


def analyze(result: BuildResult, masses) -> list[Metric]:
    """Ratio, output speed and torque for an assumed input motor, and whether the pinion survives its shaft."""
    p = result.params
    m, z1, z2 = p["module"], p["pinion_teeth"], p["gear_teeth"]
    ratio = z2 / z1
    rim = m * z1 / 2 - 1.25 * m - p["shaft_diameter"] / 2   # metal left between the pinion's bore and its roots
    rim_bands = ((1.2 * m, "bad"), (2.5 * m, "warn"), (8 * m, "ok"))
    rim_status = band(rim, rim_bands)
    return [
        Metric("rim", "Pinion rim", rim, "mm", rim_status, 0.0, 8 * m, rim_bands, digits=1,
               note={"bad": "too thin", "warn": "thin", "ok": "solid"}[rim_status]),
        Metric("ratio", "Ratio", ratio, "", display=f"{ratio:.2f} : 1"),
        Metric("speed", "Output speed", INPUT_RPM / ratio, "rpm"),
        Metric("torque", "Output torque", INPUT_TORQUE_NM * ratio * MESH_EFFICIENCY, "N m", digits=2),
        Metric("centre", "Centre distance", m * (z1 + z2) / 2, "mm", digits=1),
        Metric("pinion", "Pinion teeth", z1, "", "warn" if z1 < UNDERCUT_BELOW else "ok",
               display=f"{z1} (undercut)" if z1 < UNDERCUT_BELOW else f"{z1}"),
    ]
