"""Model rocket for an Estes C6 motor. Save this file and the rocket regenerates in the headset.

Millimetres. Tail at z=0, nose tip at +z. Every part needs a unique label: it becomes the GLB node
name and the object ID Unity selects, explodes and highlights.
"""
import math

from build123d import (Axis, BuildLine, BuildSketch, Color, Compound, Cylinder, Location, Plane, Polygon,
                       Polyline, Spline, Align, extrude, make_face, revolve)

from cad.part_api import BuildResult, FinSet, Param, PointMass, RocketSpec, ShellMass, SolidMass

PART = "rocket"

PARAMS = [
    Param("body_length", 300.0, 150, 600, "mm", 5, label="Body length"),
    Param("body_diameter", 40.0, 28, 60, "mm", 1, label="Body diameter"),
    Param("nose_length", 90.0, 40, 200, "mm", 5, label="Nose length"),
    Param("nose_shape", "ogive", options=("ogive", "cone"), label="Nose shape"),
    Param("fin_count", 3, 3, 6, "", 1, label="Fin count"),
    Param("fin_root", 70.0, 25, 120, "mm", 1, label="Fin root chord"),
    Param("fin_tip", 35.0, 5, 100, "mm", 1, label="Fin tip chord"),
    Param("fin_span", 60.0, 15, 100, "mm", 1, label="Fin span"),
    Param("fin_sweep", 35.0, 0, 100, "mm", 1, label="Fin sweep"),
]

BODY_WALL = 0.8        # kraft paper tube
NOSE_WALL = 1.5        # blow-moulded polystyrene
FIN_THICKNESS = 3.2    # 1/8" balsa
MOUNT_ID, MOUNT_WALL, MOUNT_LENGTH = 18.3, 0.5, 76.0
RING_THICKNESS = 3.0

DENSITY = {"paper": 0.70, "polystyrene": 1.05, "balsa": 0.16, "plywood": 0.68}  # g/cm^3
MOTOR = ("Estes C6 (loaded)", 24.1, 70.0)  # name, g, length mm: thrustcurve.org / NAR certification
RECOVERY_G = 12.0                          # 12" parachute, shock cord, wadding


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
                          nose_shape=p["nose_shape"], fins=fins, motor="Estes C6"),
    )
