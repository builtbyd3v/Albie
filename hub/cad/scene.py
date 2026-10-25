"""What Unity draws on and around the model, and what the panel says about it, per version: the analysis block's
markers, dimensions, explode, mass_breakdown, demo, verdict and constants (CAD_PLAN section 5, round 4).

A part script can supply any of them from analyze() (part_api.Analysis, in part millimetres, Z up). This module
converts them to the model frame and works out generic ones for whatever it leaves out, so a new design gets
all of them for free:
- markers: the CG (id "cg") always, plus the part's own (the rocket's CP, the drone's thrust centre).
- dimensions: the part's, else the overall width, depth and height (part_api.box_dimensions).
- explode: an entry for every leaf part; the part's moves (parts without one stay put), else auto_explode.
- mass_breakdown: by the part's mass_groups, else by top-level assembly; point masses (motor, parachute) get rows.
- demo: the part's, else "launch" for a design with simulate_flight and a RocketSpec, else "none".
- verdict: the part's, else from the worst metric. constants: the part's, else none.

The model frame is the glTF scene space of the version's model.glb, the same space as analysis.cg_gltf and
markers_gltf: metres, Y up, right-handed, origin at the part script's origin (the GLB's root node turns the part's
Z up into Y up, so a part point (x, y, z) mm is [x, z, -y] / 1000). Directions are unit vectors in those axes.
In Unity, glTFast flips X: [x, y, z] is new Vector3(-x, y, z) in CadModel.Root's local space (CadViewer's holder).
"""
from __future__ import annotations

import math
import re
from typing import Any, Iterable

from cad.analysis import Masses, leaves, to_gltf
from cad.export import object_id
from cad.flight import INCLINATION_DEG, RAIL_M
from cad.part_api import (Analysis, BuildResult, Demo, Dimension, Explode, ExplodeMove, Marker, Motion, Vec, Verdict,
                          box_dimensions)

MAIN_AXIS_BIAS = 2.0    # auto explode: an offset along the design's longest side counts double...
AUTO_SPREAD = 0.9       # ...each part moves 0.9x its (biased) offset from the centre,
AUTO_MIN, AUTO_MAX = 0.12, 0.6   # at least 0.12 and at most 0.6 of the longest side
SEVERITY = {"ok": 0, "warn": 1, "bad": 2}


# The model frame

def point(p: Vec) -> list[float]:
    """Part point (mm, Z up) in the model frame (m, Y up)."""
    return to_gltf((float(p[0]), float(p[1]), float(p[2])))


def direction(d: Vec) -> list[float]:
    """Part direction as a unit vector in the model frame."""
    x, y, z = (float(v) for v in d)
    n = math.sqrt(x * x + y * y + z * z)
    if not (math.isfinite(n) and n > 1e-9):
        raise ValueError(f"direction {tuple(d)} has no length")
    return [round(x / n, 5) + 0.0, round(z / n, 5) + 0.0, round(-y / n, 5) + 0.0]


def rotation(q: tuple[float, float, float, float]) -> list[float]:
    """Part rotation (quaternion x, y, z, w; Z up) in the model frame: the same turn about the converted axis."""
    x, y, z, w = (float(v) for v in q)
    n = math.sqrt(x * x + y * y + z * z + w * w) or 1.0
    return [round(x / n, 6) + 0.0, round(z / n, 6) + 0.0, round(-y / n, 6) + 0.0, round(w / n, 6) + 0.0]


def title(label: str) -> str:
    return label.replace("_", " ").capitalize()


def slug(name: str) -> str:
    return re.sub(r"[^0-9A-Za-z]+", "_", name).strip("_").upper() or "GROUP"


def centre(shape, masses: Masses) -> tuple[float, float, float]:
    """A part's centre of mass if it has a mass model, else its bounding box centre (part mm)."""
    if shape.label in masses:
        return masses[shape.label][1]
    c = shape.bounding_box().center()
    return c.X, c.Y, c.Z


# The five blocks

def extras(result: BuildResult, masses: Masses, plan: Analysis, *, cg_mm: Vec, total_g: float,
           metrics: list[dict[str, Any]], features: Iterable[str] = ()) -> dict[str, Any]:
    """The analysis block's markers, dimensions, explode, mass_breakdown, demo, verdict and constants for one built
    version. `metrics`: the block's metrics (schema dicts), for the generic verdict."""
    labels = [s.label for s in leaves(result.assembly)]
    dims = plan.dimensions if plan.dimensions is not None else box_dimensions(result.assembly)
    return {
        "markers": markers(plan.markers, cg_mm),
        "dimensions": [dimension(d) for d in dims],
        "explode": explode(plan.explode if plan.explode is not None else auto_explode(result, masses), labels),
        "mass_breakdown": mass_breakdown(result, masses, plan.mass_groups, total_g),
        "demo": demo(plan.demo if plan.demo is not None else default_demo(result, features), labels),
        "verdict": verdict(plan.verdict if plan.verdict is not None else worst_metric_verdict(metrics)),
        "constants": [{"label": c.label, "value": c.value, "note": c.note} for c in plan.constants],
    }


def verdict(v: Verdict) -> dict[str, Any]:
    if v.status not in SEVERITY:
        raise ValueError(f"verdict status must be ok, warn or bad, got {v.status!r}")
    return {"status": v.status, "headline": v.headline, "sentence": v.sentence, "metric": v.metric}


def worst_metric_verdict(metrics: list[dict[str, Any]]) -> Verdict:
    """The generic verdict: the first of the worst metrics (bad, then warn) speaks; all fine says so."""
    worst = max(metrics, key=lambda m: SEVERITY.get(m.get("status"), 0), default=None)
    if worst is None or SEVERITY.get(worst.get("status"), 0) == 0:
        shown = ", ".join(f"{m['label'].lower()} {m['display']}" for m in metrics[:2])
        return Verdict("ok", "Looks good", f"Nothing out of range: {shown}." if shown else "Nothing out of range.",
                       metrics[0]["id"] if metrics else None)
    note = f" ({worst['note']})" if worst.get("note") else ""
    return Verdict(worst["status"], "Needs work" if worst["status"] == "bad" else "Check it",
                   f"{worst['label']} is {worst['display']}{note}.", worst["id"])


def markers(own: list[Marker], cg_mm: Vec) -> list[dict[str, Any]]:
    """The CG first, then the part's own markers (one with id "cg" replaces the hub's)."""
    out = {"cg": {"id": "cg", "label": "CG", "kind": "cg", "pos": point(cg_mm)}}
    for m in own:
        out[m.id] = {"id": m.id, "label": m.label, "kind": m.kind, "pos": point(m.position)}
    return list(out.values())


def dimension(d: Dimension) -> dict[str, Any]:
    return {"id": d.id, "label": d.label, "value_mm": round(math.dist(d.start, d.end), 1),
            "from": point(d.start), "to": point(d.end), "offset_dir": direction(d.offset),
            "offset_mm": round(float(d.offset_mm), 1)}


def explode(plan: Explode, labels: list[str]) -> dict[str, Any]:
    """Every leaf part, in assembly order; parts the plan doesn't move get dist_mm 0 (and an arbitrary up dir)."""
    moves: dict[str, ExplodeMove] = {}
    for m in plan.moves:
        if m.part not in labels:
            raise ValueError(f"explode moves {m.part!r}, which isn't a part; the parts are {labels}")
        moves[m.part] = m
    parts = []
    for label in labels:
        m = moves.get(label)
        moving = m is not None and m.distance_mm > 0
        parts.append({"id": object_id(label), "dir": direction(m.direction) if moving else [0.0, 1.0, 0.0],
                      "dist_mm": round(float(m.distance_mm), 1) if moving else 0.0,
                      "order": int(m.order) if m is not None else 0})
    return {"axis": plan.axis, "parts": parts}


def auto_explode(result: BuildResult, masses: Masses) -> Explode:
    """The generic exploded view: each part moves away from the assembly's centre, along the line from the centre to
    its own centre with the component along the longest side doubled (a rocket comes apart along its length, a box's
    lid lifts off), 0.9x that far, kept between 0.12 and 0.6 of the longest side. A part at the centre stays put.
    The outermost parts move first."""
    shapes = list(leaves(result.assembly))
    bb = result.assembly.bounding_box()
    c0 = (bb.center().X, bb.center().Y, bb.center().Z)
    size = (bb.size.X, bb.size.Y, bb.size.Z)
    longest = max(max(size), 1e-6)
    main = size.index(max(size))
    offsets = []
    for s in shapes:
        c = centre(s, masses)
        d = [c[i] - c0[i] for i in range(3)]
        d[main] *= MAIN_AXIS_BIAS
        offsets.append(d)
    reach = max((math.hypot(*d) for d in offsets), default=0.0)
    moves = []
    for s, d in zip(shapes, offsets):
        n = math.hypot(*d)
        if n < 0.01 * longest:
            continue
        dist = min(max(AUTO_SPREAD * n, AUTO_MIN * longest), AUTO_MAX * longest)
        moves.append(ExplodeMove(s.label, (d[0], d[1], d[2]), dist, order=min(3, int(4 * (1 - n / reach)))))
    return Explode("auto", moves)


def mass_breakdown(result: BuildResult, masses: Masses, groups: dict[str, list[str]] | None,
                   total_g: float) -> list[dict[str, Any]]:
    """Mass by group: the part's mass_groups (parts in none get a row each), else one row per top-level child of the
    assembly; then one row per point mass (no part_ids: it isn't drawn). Rows add up to analysis.mass_g."""
    labels = [s.label for s in leaves(result.assembly)]

    def row(key: str, label: str, parts: list[str], grams: float) -> dict[str, Any]:
        return {"id": key, "label": label, "mass_g": round(grams, 1),
                "share": round(grams / total_g, 4) if total_g else 0.0, "part_ids": [object_id(p) for p in parts]}

    def grams(parts: Iterable[str]) -> float:
        return sum(masses[p][0] for p in parts if p in masses)

    rows = []
    if groups:
        used: set[str] = set()
        for name, parts in groups.items():
            unknown = [p for p in parts if p not in labels]
            if unknown:
                raise ValueError(f"mass group {name!r} names {unknown}, which aren't parts; the parts are {labels}")
            rows.append(row(slug(name), name, list(parts), grams(parts)))
            used.update(parts)
        rows += [row(object_id(p), title(p), [p], grams([p])) for p in labels if p not in used]
    else:
        for child in result.assembly.children or [result.assembly]:
            parts = [s.label for s in leaves(child)]
            rows.append(row(object_id(child.label), title(child.label), parts, grams(parts)))
    for p in result.point_masses:
        rows.append(row(slug(p.name), p.name[:1].upper() + p.name[1:], [], p.mass_g))
    return rows


def default_demo(result: BuildResult, features: Iterable[str]) -> Demo:
    """A design with a flight sim launches (the trajectory is GET /cad/flight); anything else has no demo."""
    spec = result.rocket
    if "flight" not in features or spec is None:
        return Demo()
    motor = f"{spec.motor}-{spec.ejection_delay_s:g}" if spec.motor else "its motor"
    return Demo(kind="launch", label="Fly", source="/cad/flight", axis=((0.0, 0.0, 0.0), (0.0, 0.0, 1.0)),
                note=f"Launches on an {motor} and comes down on a {spec.parachute_mm:g} mm parachute.",
                values={"rail_m": RAIL_M, "inclination_deg": INCLINATION_DEG,
                        "ejection_delay_s": spec.ejection_delay_s, "parachute_mm": spec.parachute_mm})


def demo(d: Demo, labels: list[str]) -> dict[str, Any]:
    spin = []
    for s in d.spinners:
        for label in (s.part, s.motor):
            if label is not None and label not in labels:
                raise ValueError(f"demo spinner names {label!r}, which isn't a part; the parts are {labels}")
        rpm = float(s.rpm)
        spin.append({"id": object_id(s.part), "origin": point(s.origin), "dir": direction(s.direction),
                     "rpm": round(rpm, 1), "spin": "ccw" if rpm > 0 else "cw" if rpm < 0 else "none",
                     "rel_speed": None if s.rel_speed is None else round(float(s.rel_speed), 4),
                     "motor_id": None if s.motor is None else object_id(s.motor),
                     "radius_mm": None if s.radius_mm is None else round(float(s.radius_mm), 2)})
    unknown = [label for label in d.reveal if label not in labels]
    if unknown:
        raise ValueError(f"demo reveals {unknown}, which aren't parts; the parts are {labels}")
    return {"kind": d.kind, "label": d.label, "note": d.note, "source": d.source,
            "axis": None if d.axis is None else {"origin": point(d.axis[0]), "dir": direction(d.axis[1])},
            "values": {k: round(float(v), 4) for k, v in d.values.items()},
            "spin_parts": spin, "readouts": [m.schema() for m in d.readouts], "assumptions": list(d.assumptions),
            "blocked": d.blocked or None, "reveal_ids": [object_id(label) for label in d.reveal]}


def motion(m: Motion, nodes: list[str]) -> dict[str, Any]:
    """A keyframed motion (GET /cad/motion, the walk) in the model frame. joints: the GLB nodes (assemblies) to turn,
    each about origin + dir by angles_deg[i] at sample i, relative to the version's pose (right-hand rule in glTF,
    like spin's rpm); root: where the whole model is at sample i, pos (m) and rot (x, y, z, w), applied as
    rot * p + pos to a model point p."""
    unknown = [j.part for j in m.joints if j.part not in nodes]
    if unknown:
        raise ValueError(f"motion turns {unknown}, which aren't nodes of the model")
    lengths = {len(j.angles_deg) for j in m.joints} | ({len(m.positions)} if m.positions else set())
    if len(lengths) > 1 or (m.rotations and len(m.rotations) != len(m.positions)):
        raise ValueError(f"motion tracks have different lengths: {sorted(lengths)}")
    samples = lengths.pop() if lengths else 0
    return {"rate_hz": float(m.rate_hz), "loop": bool(m.loop), "samples": samples,
            "duration_s": round(samples / m.rate_hz, 4) if m.rate_hz else 0.0,
            "joints": [{"id": object_id(j.part), "origin": point(j.origin), "dir": direction(j.direction),
                        "angles_deg": [round(float(a), 2) for a in j.angles_deg]} for j in m.joints],
            "root": {"pos": [point(p) for p in m.positions], "rot": [rotation(q) for q in m.rotations]}
                    if m.positions else None,
            "centre": None if m.centre is None else point(m.centre),
            "values": {k: round(float(v), 4) for k, v in m.values.items()},
            "tracks": {k: [round(float(v), 3) for v in track] for k, track in m.tracks.items()}}
