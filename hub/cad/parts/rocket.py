"""Model rocket for an Estes C6 motor. Save this file and the rocket regenerates in the headset.

Millimetres. Tail at z=0, nose tip at +z. Every part needs a unique label: it becomes the GLB node
name and the object ID Unity selects, explodes and highlights.
"""
import math

from build123d import (Axis, BuildLine, BuildSketch, Color, Compound, Cylinder, Location, Plane, Polygon,
                       Polyline, Spline, Align, extrude, make_face, revolve)

from cad.analysis import (MARGIN_BANDS, MARGIN_GAUGE_MAX, STABILITY_ASSUMPTIONS, STABILITY_STATUS, barrowman,
                          center_of_mass, on_axis_gltf, stability_label)
from cad.flight import simulate as simulate_flight  # noqa: F401 - the Fly button (GET /cad/flight)
from cad.part_api import (Analysis, BuildResult, Dimension, Explode, ExplodeMove, FinSet, Marker, Metric, Param,
                          PointMass, RocketSpec, ShellMass, SolidMass)

PART = "rocket"
TITLE = "Model rocket"
DESCRIPTION = "Estes C6 model rocket: nose cone, body tube, motor mount, fins. Live Barrowman stability."

PARAMS = [
    Param("body_length", 300.0, 150, 600, "mm", 5, label="Body length", group="Body",
          help="Body tube length; the nose adds to it. Longer is more stable."),
    Param("body_diameter", 40.0, 28, 60, "mm", 1, label="Body diameter", group="Body",
          help="Outside diameter of the body tube. Wider adds drag and weight."),
    Param("nose_length", 90.0, 40, 200, "mm", 5, label="Nose length", group="Nose",
          help="A longer nose is slimmer and slips through the air more easily."),
    Param("nose_shape", "ogive", options=("ogive", "cone"), label="Nose shape", group="Nose",
          help="Ogive: rounded shoulders, low drag. Cone: straight sides."),
    Param("fin_count", 3, 3, 6, "", 1, label="Fin count", group="Fins",
          help="Fins, evenly spaced round the tail. More fins, more stability."),
    Param("fin_root", 70.0, 25, 120, "mm", 1, label="Fin root chord", group="Fins",
          help="Fin length along the body tube."),
    Param("fin_tip", 35.0, 5, 100, "mm", 1, label="Fin tip chord", group="Fins",
          help="Fin length at its outer edge."),
    Param("fin_span", 60.0, 15, 100, "mm", 1, label="Fin span", group="Fins",
          help="How far each fin sticks out. Bigger fins pull the CP back: more stable."),
    Param("fin_sweep", 35.0, 0, 100, "mm", 1, label="Fin sweep", group="Fins",
          help="How far back the tip's leading edge sits from the root's."),
]

# Applied on top of the current values. "Smaller fins" is the demo's 60% fins (goes unstable).
PRESETS = {
    "Smaller fins": {"fin_root": 42, "fin_tip": 21, "fin_span": 36, "fin_sweep": 21},
    "Four fins": {"fin_count": 4},
    "Longer body": {"body_length": 360},
    "Cone nose": {"nose_shape": "cone"},
}

BODY_WALL = 0.8        # kraft paper tube
NOSE_WALL = 1.5        # blow-moulded polystyrene
FIN_THICKNESS = 3.2    # 1/8" balsa
MOUNT_ID, MOUNT_WALL, MOUNT_LENGTH = 18.3, 0.5, 76.0
RING_THICKNESS = 3.0

DENSITY = {"paper": 0.70, "polystyrene": 1.05, "balsa": 0.16, "plywood": 0.68}  # g/cm^3
MOTOR = ("Estes C6 (loaded)", 24.1, 70.0)  # name, g, length mm: thrustcurve.org / NAR certification
EJECTION_DELAY_S = 5.0                     # a C6-5: the ejection charge fires 5 s after burnout
RECOVERY_G, PARACHUTE_MM = 12.0, 305.0     # 12" parachute, shock cord, wadding


def tube(outer_r, inner_r, length, z=0.0):
    return (Cylinder(outer_r, length, align=(Align.CENTER, Align.CENTER, Align.MIN))
            - Cylinder(inner_r, length, align=(Align.CENTER, Align.CENTER, Align.MIN))).moved(Location((0, 0, z)))


def nose_cone(r, length, shape):
    with BuildSketch(Plane.XZ) as profile:
        if shape == "cone":
            Polygon((0, 0), (r, 0), (0, length), align=None)
        else:  # tangent ogive: arc of radius rho, tangent to the body at the base
            rho = (r * r + length * length) / (2 * r)
            pts = [(math.sqrt(max(rho * rho - z * z, 0.0)) + r - rho, z)
                   for z in (length * i / 24 for i in range(1, 24))]
            with BuildLine():
                Spline((r, 0), *pts, (0, length))
                Polyline((0, length), (0, 0), (r, 0))
            make_face()
    return revolve(profile.sketch, Axis.Z)


def fin(fins: FinSet, body_r, angle_deg):
    with BuildSketch(Plane.XZ) as outline:
        Polygon(*fins.profile(body_r), align=None)
    return extrude(outline.sketch, FIN_THICKNESS / 2, both=True).rotate(Axis.Z, angle_deg)


def solid(material):
    return SolidMass(DENSITY[material], material)


def labelled(shape, label, rgb):
    shape.label, shape.color = label, Color(*rgb)
    return shape


def build(p) -> BuildResult:
    r = p["body_diameter"] / 2
    body_len, nose_len = p["body_length"], p["nose_length"]
    fins = FinSet(count=p["fin_count"], root_chord=p["fin_root"], tip_chord=p["fin_tip"],
                  span=p["fin_span"], sweep=p["fin_sweep"], root_le_z=p["fin_root"])

    nose = labelled(nose_cone(r, nose_len, p["nose_shape"]).moved(Location((0, 0, body_len))),
                    "nose_cone", (0.9, 0.3, 0.2))
    body = labelled(tube(r, r - BODY_WALL, body_len), "body_tube", (0.86, 0.86, 0.9))

    mount_or = MOUNT_ID / 2 + MOUNT_WALL
    mount = Compound(label="motor_mount", children=[
        labelled(tube(mount_or, MOUNT_ID / 2, MOUNT_LENGTH), "mount_tube", (0.8, 0.65, 0.45)),
        labelled(tube(r - BODY_WALL, mount_or, RING_THICKNESS, z=3), "centering_ring_aft", (0.7, 0.55, 0.35)),
        labelled(tube(r - BODY_WALL, mount_or, RING_THICKNESS, z=MOUNT_LENGTH - 8), "centering_ring_fwd",
                 (0.7, 0.55, 0.35)),
    ])
    fin_parts = [labelled(fin(fins, r, 360 * i / fins.count), f"fin_{i + 1}", (0.2, 0.4, 0.9))
                 for i in range(fins.count)]
    fin_set = Compound(label="fin_set", children=fin_parts)

    motor_name, motor_g, motor_len = MOTOR
    return BuildResult(
        assembly=Compound(label=PART, children=[nose, body, mount, fin_set]),
        masses={"nose_cone": ShellMass(DENSITY["polystyrene"], NOSE_WALL, "polystyrene"),
                "body_tube": solid("paper"), "mount_tube": solid("paper"),
                "centering_ring_aft": solid("plywood"), "centering_ring_fwd": solid("plywood"),
                **{f.label: solid("balsa") for f in fin_parts}},
        point_masses=[PointMass(motor_name, motor_g, motor_len / 2, is_motor=True),
                      PointMass("recovery (parachute, shock cord, wadding)", RECOVERY_G, body_len - 50)],
        rocket=RocketSpec(length_mm=body_len + nose_len, diameter_mm=2 * r, nose_length_mm=nose_len,
                          nose_shape=p["nose_shape"], fins=fins, motor="Estes C6", parachute_mm=PARACHUTE_MM,
                          ejection_delay_s=EJECTION_DELAY_S),
    )


def analyze(result: BuildResult, masses) -> Analysis:
    """Static stability: CP from Barrowman, CG at liftoff, margin in calibers (body diameters).
    Plus the CP marker, dimension callouts and the exploded view for Unity (the CG marker comes free)."""
    spec = result.rocket
    _, (_, _, cg_z) = center_of_mass(result, masses)
    cp_from_tip, cna = barrowman(spec)
    cg_from_tip = spec.length_mm - cg_z
    margin = (cp_from_tip - cg_from_tip) / spec.diameter_mm
    stability = stability_label(margin)
    return Analysis(
        metrics=[Metric("margin", "Static margin", margin, "cal", STABILITY_STATUS[stability], 0.0, MARGIN_GAUGE_MAX,
                        MARGIN_BANDS, note=stability, digits=2),
                 Metric("cg", "CG from tip", cg_from_tip, "mm"),
                 Metric("cp", "CP from tip", cp_from_tip, "mm")],
        extra={"length_mm": round(spec.length_mm, 1), "diameter_mm": round(spec.diameter_mm, 1),
               "cg_mm": round(cg_from_tip, 1), "cp_mm": round(cp_from_tip, 1), "margin_cal": round(margin, 2),
               "stable": stability != "unstable", "stability": stability, "cna_per_rad": round(cna, 2), "motor": spec.motor,
               "markers_gltf": {"cg": on_axis_gltf(cg_z), "cp": on_axis_gltf(spec.length_mm - cp_from_tip)},
               "assumptions": STABILITY_ASSUMPTIONS},
        markers=[Marker("cp", "CP", "cp", (0.0, 0.0, spec.length_mm - cp_from_tip))],
        dimensions=dimensions(spec, result.params["body_length"]),
        explode=explode(result, spec))


def dimensions(spec: RocketSpec, body_len: float) -> list[Dimension]:
    """Length (in front, in the gap between fins nearest the front, -Y), body diameter (in front, above the fins)
    and fin 1's span (above it)."""
    r, f = spec.diameter_mm / 2, spec.fins
    gaps = [(2 * k + 1) * math.pi / f.count for k in range(f.count)]   # fin k+1 is at 360 k / count degrees
    gap = min(gaps, key=lambda a: abs(math.remainder(a - 1.5 * math.pi, 2 * math.pi)))
    z = min(body_len - 10, max(f.root_le_z + 20, 0.6 * body_len))
    tip_z = f.root_le_z - f.sweep
    return [Dimension("length", "Length", (0.0, 0.0, 0.0), (0.0, 0.0, spec.length_mm),
                      (math.cos(gap), math.sin(gap), 0.0), r + 20),
            Dimension("diameter", "Body diameter", (-r, 0.0, z), (r, 0.0, z), (0.0, -1.0, 0.0), r + 15),
            Dimension("fin_span", "Fin span", (r, 0.0, tip_z), (r + f.span, 0.0, tip_z), (0.0, 0.0, 1.0), f.sweep + 12)]


def explode(result: BuildResult, spec: RocketSpec) -> Explode:
    """Along the rocket: the nose lifts off, the motor mount slides out of the tail with its centering rings
    spaced along it (forward ring between tail and tube, aft ring below the tube), the fins move out and down."""
    length, span = spec.length_mm, spec.fins.span
    boxes = {s.label: s.bounding_box() for s in result.assembly.leaves if not s.label.startswith("fin_")}
    tube = boxes["mount_tube"]
    drop = tube.max.Z + 0.12 * length                          # the tube's top ends 0.12 L below the tail
    fwd_z, aft_z = boxes["centering_ring_fwd"].center().Z, boxes["centering_ring_aft"].center().Z
    moves = [ExplodeMove("nose_cone", (0, 0, 1), 0.25 * length),
             ExplodeMove("mount_tube", (0, 0, -1), drop, order=1),
             ExplodeMove("centering_ring_fwd", (0, 0, -1), fwd_z - (tube.max.Z - drop) / 2, order=2),
             ExplodeMove("centering_ring_aft", (0, 0, -1), aft_z + drop + 0.06 * length, order=2)]
    out, down = 0.75 * span, 0.06 * length
    for fin in (s for s in result.assembly.leaves if s.label.startswith("fin_")):
        c = fin.bounding_box().center()                        # on the fin's radial line
        radius = math.hypot(c.X, c.Y)
        moves.append(ExplodeMove(fin.label, (c.X / radius * out, c.Y / radius * out, -down), math.hypot(out, down),
                                 order=1))
    return Explode("stack", moves)
