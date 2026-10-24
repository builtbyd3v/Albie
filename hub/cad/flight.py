"""'How high will it go?': RocketPy 6-DOF flight of a built rocket on an Estes C6, launch to apogee."""
from __future__ import annotations

from pathlib import Path
from typing import Any

from cad.part_api import BuildResult

THRUST_CURVE = Path(__file__).parent / "data" / "estes_c6.eng"  # NAR certification data via thrustcurve.org
DRAG_CD = 0.6                         # typical for a small model rocket, power on and off
RAIL_M, INCLINATION_DEG = 0.9, 85.0   # 3 ft launch rod, tilted a little so the arc reads in the room
TRAJECTORY_POINTS = 48

ASSUMPTIONS = [
    f"Estes C6 thrust curve (NAR certification), constant drag coefficient {DRAG_CD}.",
    f"{RAIL_M} m launch rod at {INCLINATION_DEG:g} deg, no wind, standard atmosphere at sea level.",
    "Inertia from a slender-rod approximation of the airframe.",
]


def simulate(result: BuildResult, masses: dict[str, tuple[float, tuple[float, float, float]]]) -> dict[str, Any]:
    from rocketpy import Environment, Flight, GenericMotor, Rocket

    spec, mm = result.rocket, 1e-3
    if spec is None:
        raise ValueError("Flight simulation needs a rocket part (BuildResult.rocket)")
    items = [(m, c[2]) for m, c in masses.values()] + [(p.mass_g, p.z_mm) for p in result.point_masses
                                                       if not p.is_motor]
    dry_kg = sum(m for m, _ in items) / 1000
    cg_z = sum(m * z for m, z in items) / sum(m for m, _ in items) * mm
    length, radius = spec.length_mm * mm, spec.diameter_mm / 2 * mm
    lateral = dry_kg * length ** 2 / 12

    motor = GenericMotor.load_from_eng_file(str(THRUST_CURVE), nozzle_radius=0.0045, chamber_radius=0.009,
                                            chamber_height=0.07, chamber_position=0.035,
                                            center_of_dry_mass_position=0.035)
    rocket = Rocket(radius=radius, mass=dry_kg, inertia=(lateral, lateral, dry_kg * radius ** 2 / 2),
                    power_off_drag=DRAG_CD, power_on_drag=DRAG_CD, center_of_mass_without_motor=cg_z,
                    coordinate_system_orientation="tail_to_nose")
    rocket.add_motor(motor, position=0)
    rocket.add_nose(length=spec.nose_length_mm * mm, position=length,
                    kind="tangent" if spec.nose_shape == "ogive" else "conical")
    f = spec.fins
    rocket.add_trapezoidal_fins(n=f.count, root_chord=f.root_chord * mm, tip_chord=f.tip_chord * mm,
                                span=f.span * mm, sweep_length=f.sweep * mm, position=f.root_le_z * mm)
    env = Environment()
    flight = Flight(rocket=rocket, environment=env, rail_length=RAIL_M, inclination=INCLINATION_DEG, heading=0,
                    terminate_on_apogee=True, max_time=120)

    t_apogee = float(flight.apogee_time)
    ground = env.elevation
    times = [t_apogee * i / (TRAJECTORY_POINTS - 1) for i in range(TRAJECTORY_POINTS)]
    rail_margin = float(flight.out_of_rail_stability_margin)
    return {
        "warning": (f"Static margin {rail_margin:.2f} cal is under 1.0: a gust could make it weathercock or tumble; "
                    "this no-wind sim won't show that.") if rail_margin < 1.0 else None,
        "apogee_m": round(float(flight.apogee) - ground, 1),
        "time_to_apogee_s": round(t_apogee, 2),
        "max_speed_mps": round(float(flight.max_speed), 1),
        "rail_exit_speed_mps": round(float(flight.out_of_rail_velocity), 1),
        "rail_exit_margin_cal": round(rail_margin, 2),
        "liftoff_mass_g": round(dry_kg * 1000 + float(motor.total_mass(0)) * 1000, 1),
        "motor": spec.motor,
        # [east, up, north] metres from the pad: Y-up like glTF, so Unity only needs to scale it to the room.
        "trajectory_m": [[round(float(flight.x(t)), 2), round(float(flight.z(t)) - ground, 2),
                          round(float(flight.y(t)), 2)] for t in times],
        "assumptions": ASSUMPTIONS,
    }
