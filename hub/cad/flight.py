"""'How high will it go?': a built rocket's flight on an Estes C6, from the pad to landing. GET /cad/flight.

The climb is RocketPy's 6-DOF simulation, launch to apogee: it sets apogee_m, time_to_apogee_s, max_speed_mps, the
rail numbers and trajectory_m. The way down is a quick point-mass model: the rocket falls until the motor's ejection
charge fires (its delay after burnout, but not before apogee), the parachute fills, and it drifts down at its
terminal speed.

For Unity to play the flight back: `samples`, the whole flight at a fixed rate (every 0.05 s, or a whole multiple
of that to stay within 400 points: `sample_dt_s`; the last sample is the landing, at most one step after the one
before it), and `events` (liftoff, rail_exit, burnout, apogee, deploy, landing; no deploy if it lands first).
"""
from __future__ import annotations

import math
from pathlib import Path
from typing import Any

import numpy as np

from cad.part_api import BuildResult, RocketSpec

THRUST_CURVE = Path(__file__).parent / "data" / "estes_c6.eng"  # NAR certification data via thrustcurve.org
DRAG_CD = 0.6                         # typical for a small model rocket, power on and off
RAIL_M, INCLINATION_DEG = 0.9, 85.0   # 3 ft launch rod, tilted a little so the arc reads in the room
TRAJECTORY_POINTS = 48
PARACHUTE_CD, CHUTE_OPEN_S = 0.75, 0.5   # flat round plastic chute (on its flat diameter); it fills in ~0.5 s
SAMPLE_DT_S, MAX_SAMPLES = 0.05, 400
STEP_S, G = 0.01, 9.80665                # descent integration step (RK4); standard gravity

ASSUMPTIONS = [
    f"Estes C6 thrust curve (NAR certification), constant drag coefficient {DRAG_CD}.",
    f"{RAIL_M} m launch rod at {INCLINATION_DEG:g} deg, no wind, standard atmosphere at sea level.",
    "Inertia from a slender-rod approximation of the airframe.",
    f"Way down: the parachute opens when the ejection charge fires (the motor's delay after burnout), not before "
    f"apogee, and fills in {CHUTE_OPEN_S:g} s. Point mass with drag: body Cd {DRAG_CD}, parachute Cd {PARACHUTE_CD} "
    "on its flat diameter.",
]


def air_density(z_m: float) -> float:
    """Standard-atmosphere air density (kg/m^3) at z metres above sea level (the troposphere formula)."""
    return 1.225 * max(1.0 - 2.25577e-5 * z_m, 0.0) ** 4.25588


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
    out: dict[str, Any] = {
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
        "assumptions": list(ASSUMPTIONS),
    }
    way_down, warning, note = recovery(flight, ground, spec, dry_kg + float(motor.total_mass(motor.burn_out_time)),
                                       float(motor.burn_out_time))
    if warning:
        out["warning"] = " ".join(w for w in (out["warning"], warning) if w)
    if note:
        out["assumptions"].append(note)
    return {**out, **way_down}


def recovery(flight, ground: float, spec: RocketSpec, mass_kg: float,
             t_burnout: float) -> tuple[dict[str, Any], str | None, str | None]:
    """The way down from RocketPy's apogee, the events and the samples. `mass_kg`: after burnout.
    Returns (fields for the flight result, a warning or None, a note for the assumptions or None)."""
    t_apogee = float(flight.apogee_time)
    t_eject = t_burnout + spec.ejection_delay_s
    t_open = max(t_eject, t_apogee)            # the parachute starts to fill
    body_cda = DRAG_CD * math.pi * (spec.diameter_mm / 2000) ** 2
    chute_cda = PARACHUTE_CD * math.pi * (spec.parachute_mm / 2000) ** 2

    def climb(t: float) -> tuple[float, float, float, float]:
        """RocketPy's state at t <= t_apogee: (downrange, altitude, downrange speed, vertical speed)."""
        x, y, vx, vy = (float(fn(t)) for fn in (flight.x, flight.y, flight.vx, flight.vy))
        s = math.hypot(x, y)
        vs = (x * vx + y * vy) / s if s > 1e-6 else math.hypot(vx, vy)
        return s, float(flight.z(t)) - ground, vs, float(flight.vz(t))

    def rates(t: float, state: tuple[float, ...]) -> tuple[float, ...]:
        _, z, vs, vz = state
        cda = body_cda + chute_cda * min(1.0, max(0.0, (t - t_open) / CHUTE_OPEN_S))
        k = 0.5 * air_density(z + ground) * cda * math.hypot(vs, vz) / mass_kg
        return vs, vz, -k * vs, -k * vz - G

    def nudge(state, slope, h):
        return tuple(a + h * b for a, b in zip(state, slope))

    # Fall from apogee (RK4) until the altitude crosses zero.
    t, state, h = t_apogee, climb(t_apogee), STEP_S
    ts, states = [t], [state]
    while state[1] > 0.0 and t < t_apogee + 900.0:
        k1 = rates(t, state)
        k2 = rates(t + h / 2, nudge(state, k1, h / 2))
        k3 = rates(t + h / 2, nudge(state, k2, h / 2))
        k4 = rates(t + h, nudge(state, k3, h))
        state = tuple(s + h / 6 * (a + 2 * b + 2 * c + d) for s, a, b, c, d in zip(state, k1, k2, k3, k4))
        t += h
        ts.append(t)
        states.append(state)
    if len(ts) > 1 and states[-1][1] <= 0.0:   # land exactly where the last step crossed the ground
        a, b = states[-2], states[-1]
        f = a[1] / (a[1] - b[1])
        landed = [p + f * (q - p) for p, q in zip(a, b)]
        landed[1] = 0.0
        ts[-1], states[-1] = ts[-2] + f * h, tuple(landed)
    t_land, land = ts[-1], states[-1]
    down_t, down = np.array(ts), np.array(states)

    def state_at(t: float) -> tuple[tuple[float, float, float, float], float]:
        """(downrange, altitude, downrange speed, vertical speed) and speed at any t in the flight."""
        if t <= t_apogee:
            return climb(t), float(flight.speed(t))
        s, z, vs, vz = (float(np.interp(t, down_t, down[:, i])) for i in range(4))
        return (s, max(z, 0.0), vs, vz), math.hypot(vs, vz)

    # Samples at a fixed rate, the landing last.
    dt = SAMPLE_DT_S * max(1, math.ceil(t_land / SAMPLE_DT_S / (MAX_SAMPLES - 2)))
    grid = [i * dt for i in range(int(t_land / dt + 1e-9) + 1)]
    if t_land - grid[-1] > 1e-6:
        grid.append(t_land)
    samples = []
    for t in grid:
        (s, z, _, vz), speed = state_at(t)
        samples.append({"t": round(t, 3), "alt_m": round(z, 2), "downrange_m": round(s, 2), "vz_mps": round(vz, 2),
                        "speed_mps": round(speed, 2)})

    deployed = t_open < t_land

    def event(key: str, label: str, t: float) -> dict[str, Any]:
        (_, z, _, _), speed = state_at(t)
        return {"id": key, "label": label, "t": round(t, 2), "alt_m": round(z, 1), "speed_mps": round(speed, 1)}

    t_rail = float(flight.out_of_rail_time)
    events = [event("liftoff", "Liftoff", 0.0), event("rail_exit", "Off the rail", t_rail),
              event("burnout", "Burnout", t_burnout), event("apogee", "Apogee", t_apogee)]
    if deployed:
        events.append(event("deploy", "Parachute", t_open))
    events.append(event("landing", "Landing", t_land))
    events.sort(key=lambda e: e["t"])

    warning = None if deployed else (f"It lands at {t_land:.1f} s, before the ejection charge fires at "
                                     f"{t_eject:.1f} s: no parachute.")
    note = (f"This motor's ejection charge fires at {t_eject:.1f} s, before apogee ({t_apogee:.1f} s); "
            "the parachute is drawn opening at apogee.") if t_eject < t_apogee else None
    return {
        "rail_exit_time_s": round(t_rail, 2),
        "burnout_time_s": round(t_burnout, 2),
        "deploy_time_s": round(t_open, 2) if deployed else None,
        "descent_rate_mps": round(abs(land[3]), 1),
        "landing_time_s": round(t_land, 2),
        "drift_m": round(land[0], 1),
        "sample_dt_s": round(dt, 3),
        "events": events,
        "samples": samples,
    }, warning, note
