"""FPV drone frame: carbon plates and arms, motors, props, battery. Save this file and the drone regenerates.

Millimetres, Z up, frame centre at the origin, bottom plate on z=0. With 4 arms it's an X frame; the
standoffs sit between the arms. Every part needs a unique label: it becomes the GLB node name and the
object ID Unity selects, explodes and highlights.
"""
import math

from build123d import Align, Axis, Box, Color, Compound, Cylinder, Location, SlotOverall, extrude, fillet

from cad.part_api import (Analysis, BuildResult, Demo, Dimension, Explode, ExplodeMove, Marker, Metric, Param,
                          SolidMass, Spinner, band, box_dimensions)

TITLE = "Quadcopter"
DESCRIPTION = "FPV drone: carbon plates and arms, motors, props, battery. The props have to clear each other."

PARAMS = [
    Param("arm_count", 4, 4, 8, "", 2, label="Arms", group="Frame",
          help="4 is a quad, 6 a hexacopter. Props spin in opposite pairs, so it's always even."),
    Param("arm_length", 110.0, 60, 220, "mm", 5, label="Arm length", group="Frame",
          help="Centre to motor. Longer arms make room for bigger props."),
    Param("prop_diameter", 5.0, 2, 10, "in", 0.5, label="Prop diameter", group="Props",
          help="Bigger props lift more, but they have to clear their neighbours."),
    Param("motor_size", 28.0, 14, 40, "mm", 1, label="Motor diameter", group="Power",
          help="Bigger motors push harder and weigh more."),
    Param("plate_size", 70.0, 40, 140, "mm", 5, label="Plate size", group="Frame",
          help="The square carbon plates in the middle."),
    Param("battery_mah", 1300, 450, 3000, "mAh", 50, label="Battery", group="Power",
          help="Capacity: longer hover time, but a heavier drone."),
]

# Applied on top of the current values. "7-inch props" alone is too big for 110 mm arms: the props hit.
PRESETS = {
    "7-inch props": {"prop_diameter": 7.0},
    "Long range 7-inch": {"prop_diameter": 7.0, "arm_length": 160, "motor_size": 32, "battery_mah": 2200},
    "Hexacopter": {"arm_count": 6, "arm_length": 150, "plate_size": 90},
    "3-inch micro": {"prop_diameter": 3.0, "arm_length": 65, "motor_size": 16, "plate_size": 45, "battery_mah": 650},
}

INCH = 25.4
PLATE, TOP_PLATE, ARM, STANDOFF = 3.0, 2.0, 4.0, 25.0   # plate and arm thickness, gap between the plates
# Effective densities for the motor (copper, steel, magnets: a 28 mm motor is ~33 g) and the pack (1300 mAh 4S ~150 g).
DENSITY = {"carbon fibre": 1.55, "aluminium": 2.7, "brushless motor": 3.2, "polycarbonate": 1.2, "LiPo": 1.9,
           "PCB": 1.0}
CARBON, ALUMINIUM, MOTOR, PROP = (0.13, 0.13, 0.15), (0.85, 0.2, 0.2), (0.22, 0.23, 0.26), (1.0, 0.55, 0.1)

# Assumed 4S power system. Static thrust per motor: the prop or the motor limits it, whichever is smaller.
THRUST_PER_IN2, THRUST_PER_MM2 = 55.0, 2.2   # g per (prop inches)^2 (5" ~ 1.4 kg), per (motor mm)^2 (28 mm ~ 1.7 kg)
PACK_V, USABLE, HOVER_EFFICIENCY = 14.8, 0.8, 0.25
THRUST_COEFF, AIR = 0.15, 1.225              # static C_T of a 2-blade FPV prop (T = C_T rho n^2 D^4); air, kg/m^3
CLEARANCE_BANDS = ((0.0, "bad"), (5.0, "warn"), (60.0, "ok"))   # tip-to-tip gap between neighbouring props, mm
TWR_BANDS = ((2.0, "bad"), (4.0, "warn"), (16.0, "ok"))
HOVER_ASSUMPTIONS = [
    "Max static thrust per motor: the prop's (55 g per square inch of prop) or the motor's (2.2 g per mm^2 of motor "
    "diameter), whichever is lower, on a 4S pack.",
    f"Thrust grows with rpm squared (static thrust coefficient {THRUST_COEFF}) and rpm with throttle, so hover "
    "throttle = sqrt(hover thrust / max thrust).",
    "Props alternate direction round the frame (prop 1 counter-clockwise seen from above), so their torques cancel.",
]


def labelled(shape, label, rgb):
    shape.label, shape.color = label, Color(*rgb)
    return shape


def polar(radius, angle_deg, z=0.0):
    a = math.radians(angle_deg)
    return Location((radius * math.cos(a), radius * math.sin(a), z))


def plate(size, thickness, z=0.0):
    square = Box(size, size, thickness, align=(Align.CENTER, Align.CENTER, Align.MIN))
    return fillet(square.edges().filter_by(Axis.Z), size * 0.18).moved(Location((0, 0, z)))


def build(p) -> BuildResult:
    n, arm_len, size = p["arm_count"], p["arm_length"], p["plate_size"]
    motor_d, prop_d = p["motor_size"], p["prop_diameter"] * INCH
    arm_angles = [360 * i / n + 180 / n for i in range(n)]
    arm_w = max(10.0, motor_d * 0.55)
    arm_start = max(size * 0.22, arm_w / (2 * math.sin(math.pi / n)) + 2)  # arms don't overlap at the centre
    arm_len_total = arm_len + motor_d / 2 + 3 - arm_start
    deck = PLATE + ARM          # top of the arms: standoffs and motors sit here
    top_z = deck + STANDOFF

    arms = [labelled(extrude(SlotOverall(arm_len_total, arm_w), ARM)
                     .moved(Location((arm_start + arm_len_total / 2, 0, PLATE))).rotate(Axis.Z, a),
                     f"arm_{i + 1}", CARBON) for i, a in enumerate(arm_angles)]
    standoffs = [labelled(Cylinder(2.5, STANDOFF, align=(Align.CENTER, Align.CENTER, Align.MIN))
                          .moved(polar(size / 2 - 6, a - 180 / n, deck)), f"standoff_{i + 1}", ALUMINIUM)
                 for i, a in enumerate(arm_angles)]
    frame = Compound(label="frame", children=[labelled(plate(size, PLATE), "bottom_plate", CARBON),
                                              labelled(plate(size, TOP_PLATE, top_z), "top_plate", CARBON),
                                              *standoffs, *arms])

    motor_h = motor_d * 0.6
    motors, props = [], []
    for i, a in enumerate(arm_angles):
        bell = (Cylinder(motor_d / 2, motor_h, align=(Align.CENTER, Align.CENTER, Align.MIN))
                + Cylinder(2.5, motor_h + 6, align=(Align.CENTER, Align.CENTER, Align.MIN)))
        motors.append(labelled(bell.moved(polar(arm_len, a, deck)), f"motor_{i + 1}", MOTOR))
        blades = extrude(SlotOverall(prop_d, prop_d * 0.1), 2).moved(Location((0, 0, 2)))
        hub = Cylinder(5, 6, align=(Align.CENTER, Align.CENTER, Align.MIN))
        props.append(labelled((blades + hub).rotate(Axis.Z, a + 90).moved(polar(arm_len, a, deck + motor_h + 1)),
                              f"prop_{i + 1}", PROP))

    fc = labelled(Box(30, 30, 6, align=(Align.CENTER, Align.CENTER, Align.MIN)).moved(Location((0, 0, deck + 8))),
                  "flight_controller", (0.1, 0.5, 0.25))
    battery = labelled(Box(75 * p["battery_mah"] / 1300, 35, 30, align=(Align.CENTER, Align.CENTER, Align.MIN))
                       .moved(Location((0, 0, top_z + TOP_PLATE))), "battery", (0.15, 0.3, 0.75))

    def solid(material):
        return SolidMass(DENSITY[material], material)

    return BuildResult(
        assembly=Compound(label="quadcopter", children=[frame, Compound(label="motors", children=motors),
                                                        Compound(label="props", children=props), fc, battery]),
        masses={"bottom_plate": solid("carbon fibre"), "top_plate": solid("carbon fibre"),
                **{s.label: solid("aluminium") for s in standoffs}, **{s.label: solid("carbon fibre") for s in arms},
                **{s.label: solid("brushless motor") for s in motors}, **{s.label: solid("polycarbonate") for s in props},
                "flight_controller": solid("PCB"), "battery": solid("LiPo")},
    )


def analyze(result: BuildResult, masses) -> Analysis:
    """Prop clearance, thrust-to-weight and hover time for the assumed power system. Plus, for Unity: the hover
    demo (spinning props), the thrust centre, dimension callouts and the exploded view."""
    p = result.params
    n, prop_in = p["arm_count"], p["prop_diameter"]
    weight_g = sum(m for m, _ in masses.values())
    clearance = 2 * p["arm_length"] * math.sin(math.pi / n) - prop_in * INCH
    thrust_g = min(THRUST_PER_IN2 * prop_in ** 2, THRUST_PER_MM2 * p["motor_size"] ** 2)
    twr = n * thrust_g / weight_g
    # Momentum theory: hover power per prop = T^1.5 / sqrt(2 rho A), over the power system's efficiency.
    disk_m2 = math.pi * (prop_in * INCH / 2000) ** 2
    hover_w = n * (weight_g / n * 9.81e-3) ** 1.5 / math.sqrt(2 * 1.225 * disk_m2) / HOVER_EFFICIENCY
    minutes = PACK_V * p["battery_mah"] / 1000 * USABLE / hover_w * 60
    gap, lift = band(clearance, CLEARANCE_BANDS), band(twr, TWR_BANDS)
    metrics = [
        Metric("clearance", "Prop clearance", clearance, "mm", gap, -40.0, 60.0, CLEARANCE_BANDS,
               note={"bad": "props hit", "warn": "tight", "ok": "clear"}[gap]),
        Metric("twr", "Thrust to weight", twr, "", lift, 0.0, 16.0, TWR_BANDS,
               note={"bad": "too heavy", "warn": "sluggish", "ok": "agile"}[lift], display=f"{twr:.1f} : 1"),
        Metric("hover", "Hover time", minutes, "min", "bad" if minutes < 3 else "warn" if minutes < 6 else "ok",
               digits=1),
        Metric("thrust", "Thrust per motor", thrust_g, "g"),
    ]
    boxes = {s.label: s.bounding_box() for s in result.assembly.leaves}
    props = [centre(boxes[f"prop_{i + 1}"]) for i in range(n)]
    thrust_centre = tuple(sum(c[k] for c in props) / n for k in range(3))
    return Analysis(metrics=metrics,
                    markers=[Marker("thrust", "Thrust centre", "thrust", thrust_centre)],
                    dimensions=dimensions(result, boxes, n),
                    explode=explode(boxes, p["arm_length"]),
                    demo=hover(n, prop_in, weight_g, thrust_g, twr, lift, hover_w, props, thrust_centre))


def centre(box) -> tuple[float, float, float]:
    c = box.center()
    return c.X, c.Y, c.Z


def hover(n, prop_in, weight_g, thrust_g, twr, lift, hover_w, props, thrust_centre) -> Demo:
    """Lift off and hover: the props spin at hover speed, alternating direction."""
    hover_g = weight_g / n
    throttle = math.sqrt(hover_g / thrust_g)       # thrust ~ rpm^2, rpm ~ throttle
    d_m = prop_in * INCH / 1000
    max_rpm = 60 * math.sqrt(thrust_g * 9.81e-3 / (THRUST_COEFF * AIR * d_m ** 4))
    rpm, spare = max_rpm * throttle, 100 * (1 - hover_g / thrust_g)
    note = (f"Hovers at {100 * throttle:.0f}% throttle with {spare:.0f}% of its thrust to spare." if twr > 1 else
            f"Too heavy to take off: its motors lift {100 * twr:.0f}% of its weight.")
    return Demo(
        "hover", "Hover", note=note, axis=(thrust_centre, (0.0, 0.0, 1.0)),
        spinners=[Spinner(f"prop_{i + 1}", c, (0.0, 0.0, 1.0), rpm if i % 2 == 0 else -rpm, motor=f"motor_{i + 1}")
                  for i, c in enumerate(props)],
        readouts=[Metric("twr", "Thrust to weight", twr, "", lift, display=f"{twr:.1f} : 1"),
                  Metric("throttle", "Hover throttle", 100 * throttle, "%", lift),
                  Metric("spare", "Thrust to spare", spare, "%", lift),
                  Metric("rpm", "Prop speed", rpm, "rpm", display=f"{rpm:,.0f} rpm"),
                  Metric("power", "Hover power", hover_w, "W")],
        values={"thrust_to_weight": twr, "hover_throttle_pct": 100 * throttle, "thrust_margin_pct": spare,
                "hover_rpm": rpm, "max_rpm": max_rpm, "hover_thrust_g": hover_g, "max_thrust_g": thrust_g,
                "weight_g": weight_g, "hover_power_w": hover_w},
        assumptions=HOVER_ASSUMPTIONS)


def dimensions(result: BuildResult, boxes, n) -> list[Dimension]:
    """Wheelbase (motor 1 to the motor opposite, the size FPV frames are sold by), overall width and height."""
    a, b = boxes["motor_1"], boxes[f"motor_{n // 2 + 1}"]
    top = max(box.max.Z for box in boxes.values())
    wheelbase = Dimension("wheelbase", "Wheelbase", (a.center().X, a.center().Y, a.max.Z),
                          (b.center().X, b.center().Y, b.max.Z), (0.0, 0.0, 1.0), top - a.max.Z + 10)
    overall = {d.id: d for d in box_dimensions(result.assembly)}
    return [wheelbase, overall["width"], overall["height"]]


def explode(boxes, length) -> Explode:
    """Out from the centre, in arm lengths: props off first, then motors and the top deck, then arms; the battery,
    top plate, flight controller and standoffs lift straight up. The bottom plate stays."""
    up = (0.0, 0.0, 1.0)
    moves = [ExplodeMove("battery", up, 0.65 * length), ExplodeMove("top_plate", up, 0.3 * length, order=1),
             ExplodeMove("flight_controller", up, 0.14 * length, order=2)]
    # (outward, upward, order) for each radial kind; standoffs only go up
    radial = {"prop": (0.5 * length, 0.4 * length, 0), "motor": (0.5 * length, 0.12 * length, 1),
              "arm": (0.28 * length, 0.0, 3)}
    for label, box in boxes.items():
        kind = label.rsplit("_", 1)[0]
        if kind == "standoff":
            moves.append(ExplodeMove(label, up, 0.14 * length, order=2))
        elif kind in radial:
            out, lift, order = radial[kind]
            x, y, _ = centre(box)
            r = math.hypot(x, y)
            moves.append(ExplodeMove(label, (x / r * out, y / r * out, lift), math.hypot(out, lift), order))
    return Explode("radial", moves)
