"""Single-stage spur gearbox: a pinion drives a gear inside a split housing. Save this file and it regenerates.

Millimetres, Z up, housing on z=0. Shafts run along Y (input out the front at -Y, output out the back at +Y)
and the housing splits at the shaft centreline like a real one, so the top half lifts off to show the gears.
Every part needs a unique label: it becomes the GLB node name and the object ID Unity selects and explodes.
"""
import math

from build123d import Align, Box, Circle, Color, Compound, Cylinder, Location, Plane, Polygon, Solid

from cad.part_api import (Analysis, BuildResult, Demo, Dimension, Explode, ExplodeMove, Marker, Metric, Param,
                          SolidMass, Spinner, band)

TITLE = "Gearbox"
DESCRIPTION = "Spur gear reducer: pinion, gear, shafts, bearings, split housing. Ratio = gear teeth / pinion teeth."

PARAMS = [
    Param("pinion_teeth", 15, 8, 40, "", 1, label="Pinion teeth", group="Gears",
          help="Teeth on the small input gear. Under 14 they get undercut."),
    Param("gear_teeth", 45, 16, 100, "", 1, label="Gear teeth", group="Gears",
          help="Teeth on the big output gear. Ratio = gear teeth / pinion teeth."),
    Param("module", 1.5, 0.75, 3.0, "mm", 0.25, label="Gear module", group="Gears",
          help="Tooth size: pitch diameter / teeth. Bigger teeth carry more load."),
    Param("face_width", 10.0, 4, 30, "mm", 1, label="Face width", group="Gears",
          help="How wide the gears are along their shafts."),
    Param("shaft_diameter", 8.0, 4, 16, "mm", 1, label="Shaft diameter", group="Shafts",
          help="Thicker shafts are stiffer but leave less metal round the pinion's bore."),
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
FRONT = (0.0, -1.0, 0.0)     # the shafts' direction, out of the front (where the input sticks out)
BEARINGS = [f"bearing_{s}_{end}" for s in ("in", "out") for end in ("front", "back")]
MASS_GROUPS = {"Housing": ["housing_bottom", "housing_top"], "Gears": ["pinion", "gear"],
               "Shafts": ["input_shaft", "output_shaft"], "Bearings": BEARINGS}


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


def analyze(result: BuildResult, masses) -> Analysis:
    """Ratio, output speed and torque for an assumed input motor, and whether the pinion survives its shaft.
    Plus, for Unity: the gear train running (spin demo), input/output markers, dimensions, the exploded view and
    the mass by kind of part."""
    p = result.params
    m, z1, z2 = p["module"], p["pinion_teeth"], p["gear_teeth"]
    ratio = z2 / z1
    rim = m * z1 / 2 - 1.25 * m - p["shaft_diameter"] / 2   # metal left between the pinion's bore and its roots
    rim_bands = ((1.2 * m, "bad"), (2.5 * m, "warn"), (8 * m, "ok"))
    rim_status = band(rim, rim_bands)
    metrics = [
        Metric("rim", "Pinion rim", rim, "mm", rim_status, 0.0, 8 * m, rim_bands, digits=1,
               note={"bad": "too thin", "warn": "thin", "ok": "solid"}[rim_status]),
        Metric("ratio", "Ratio", ratio, "", display=f"{ratio:.2f} : 1"),
        Metric("speed", "Output speed", INPUT_RPM / ratio, "rpm"),
        Metric("torque", "Output torque", INPUT_TORQUE_NM * ratio * MESH_EFFICIENCY, "N m", digits=2),
        Metric("centre", "Centre distance", m * (z1 + z2) / 2, "mm", digits=1),
        Metric("pinion", "Pinion teeth", z1, "", "warn" if z1 < UNDERCUT_BELOW else "ok",
               display=f"{z1} (undercut)" if z1 < UNDERCUT_BELOW else f"{z1}"),
    ]
    parts = {s.label: s for s in result.assembly.leaves}
    boxes = {label: parts[label].bounding_box() for label in ("housing_bottom", "housing_top", "input_shaft",
                                                             "output_shaft")}   # not the gears: slow, many faces
    shaft_in, shaft_out = boxes["input_shaft"], boxes["output_shaft"]
    axis_in = (shaft_in.center().X, 0.0, shaft_in.center().Z)    # on the shaft, in the gears' mid-plane
    axis_out = (shaft_out.center().X, 0.0, shaft_out.center().Z)
    return Analysis(
        metrics=metrics,
        markers=[Marker("input", "Input", "input", (axis_in[0], shaft_in.min.Y, axis_in[2])),
                 Marker("output", "Output", "output", (axis_out[0], shaft_out.max.Y, axis_out[2]))],
        dimensions=dimensions(result, boxes, axis_in, axis_out),
        explode=explode(boxes),
        demo=run(z1, z2, axis_in, axis_out),
        mass_groups=MASS_GROUPS)


def run(z1, z2, axis_in, axis_out) -> Demo:
    """The gear train turning at the assumed input speed: external gears turn opposite ways."""
    ratio, rel = z2 / z1, -z1 / z2
    out_rpm, out_torque = INPUT_RPM / ratio, INPUT_TORQUE_NM * ratio * MESH_EFFICIENCY
    return Demo(
        "spin", "Run", axis=(axis_in, FRONT),
        note=(f"A {INPUT_RPM:.0f} rpm motor on the input turns the output {out_rpm:.0f} rpm the other way, "
              f"with {out_torque:.2f} N m."),
        spinners=[Spinner("input_shaft", axis_in, FRONT, INPUT_RPM, 1.0), Spinner("pinion", axis_in, FRONT, INPUT_RPM, 1.0),
                  Spinner("gear", axis_out, FRONT, INPUT_RPM * rel, rel),
                  Spinner("output_shaft", axis_out, FRONT, INPUT_RPM * rel, rel)],
        readouts=[Metric("ratio", "Ratio", ratio, "", display=f"{ratio:.2f} : 1"),
                  Metric("input", "Input speed", INPUT_RPM, "rpm"), Metric("output", "Output speed", out_rpm, "rpm"),
                  Metric("torque", "Output torque", out_torque, "N m", digits=2)],
        values={"ratio": ratio, "input_rpm": INPUT_RPM, "output_rpm": out_rpm, "input_torque_nm": INPUT_TORQUE_NM,
                "output_torque_nm": out_torque, "efficiency": MESH_EFFICIENCY},
        assumptions=[f"Assumed input: a motor at {INPUT_RPM:.0f} rpm giving {INPUT_TORQUE_NM} N m.",
                     f"One spur mesh at {MESH_EFFICIENCY:.0%} efficiency; the output turns the opposite way."])


def dimensions(result: BuildResult, boxes, axis_in, axis_out) -> list[Dimension]:
    """Housing length and height on its front, depth over the shafts on the right, centre distance on the lid."""
    bottom, overall = boxes["housing_bottom"], result.assembly.bounding_box()
    x0, x1, front, top = bottom.min.X, bottom.max.X, bottom.min.Y, boxes["housing_top"].max.Z
    gap = 0.06 * max(overall.size.X, overall.size.Y, overall.size.Z) + 4.0
    return [Dimension("length", "Length", (x0, front, 0.0), (x1, front, 0.0), (0.0, -1.0, 0.0), gap),
            Dimension("height", "Height", (x1, front, 0.0), (x1, front, top), (1.0, 0.0, 0.0), gap),
            Dimension("depth", "Depth over shafts", (x1, overall.min.Y, 0.0), (x1, overall.max.Y, 0.0),
                      (1.0, 0.0, 0.0), gap),
            Dimension("centre_distance", "Centre distance", (axis_in[0], 0.0, top), (axis_out[0], 0.0, top),
                      (0.0, 0.0, 1.0), 8.0)]


def explode(boxes) -> Explode:
    """The lid lifts off; the shafts lift out of the bottom half with their gears and bearings, then the pinion
    slides forward and the gear back along their shafts, and the bearings slide off the ends."""
    height, depth = boxes["housing_top"].max.Z, boxes["housing_bottom"].size.Y
    lift = 0.45 * height

    def along(label, axial, order):   # lifted, then `axial` mm along the shaft (+Y is the back)
        return ExplodeMove(label, (0.0, axial, lift), math.hypot(axial, lift), order)

    return Explode("shaft", [
        ExplodeMove("housing_top", (0, 0, 1), height, order=0),
        along("input_shaft", 0.0, 1), along("output_shaft", 0.0, 1),
        along("pinion", -0.6 * depth, 1), along("gear", 0.6 * depth, 1),
        *(along(b, (-1.2 if b.endswith("front") else 1.2) * depth, 2) for b in BEARINGS)])
