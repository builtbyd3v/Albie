"""Mass properties and static stability (Barrowman 1966) for a built part."""
from __future__ import annotations

import math
from typing import Any

from build123d import CenterOf, GeomType

from cad.part_api import BuildResult, RocketSpec, ShellMass, SolidMass

# Static margin bands in calibers (NASA Student Launch: stable >= 1, 1.5 to 2.0 desirable).
UNSTABLE_BELOW, MARGINAL_BELOW, OVERSTABLE_ABOVE = 1.0, 1.5, 2.5

# Fin-to-fin interference for 5 to 8 fins replaces N/2 (Niskanen 2013, OpenRocket technical documentation).
FIN_COUNT_FACTOR = {5: 2.37, 6: 2.74, 7: 2.99, 8: 3.24}

ASSUMPTIONS = [
    "Barrowman static CP: subsonic, zero angle of attack, body tube adds no lift.",
    "CG at liftoff with a loaded motor (the least stable moment of a normal flight).",
    "Masses from part geometry and the densities in the part script; glue and paint ignored.",
]


def leaves(shape):
    if shape.children:
        for child in shape.children:
            yield from leaves(child)
    else:
        yield shape


def part_mass(shape, model) -> tuple[float, tuple[float, float, float]] | None:
    """(grams, centre in part mm) for one part, or None if the script gave it no mass model."""
    if isinstance(model, SolidMass):
        c = shape.center(CenterOf.MASS)
        return shape.volume * model.density_g_cm3 / 1000, (c.X, c.Y, c.Z)
    if isinstance(model, ShellMass):
        faces = [f for f in shape.faces() if f.geom_type != GeomType.PLANE]
        area = sum(f.area for f in faces)
        centres = [f.center(CenterOf.MASS) for f in faces]
        c = tuple(sum(f.area * getattr(p, axis) for f, p in zip(faces, centres)) / area for axis in "XYZ")
        return area * model.wall_mm * model.density_g_cm3 / 1000, c
    return None


def mass_properties(result: BuildResult) -> dict[str, tuple[float, tuple[float, float, float]]]:
    out = {}
    for shape in leaves(result.assembly):
        props = part_mass(shape, result.masses.get(shape.label))
        if props is not None:
            out[shape.label] = props
    return out


def barrowman(spec: RocketSpec) -> tuple[float, float]:
    """(CP distance from nose tip in mm, normal force slope CN_alpha per radian)."""
    d, ln, f = spec.diameter_mm, spec.nose_length_mm, spec.fins
    r = d / 2
    cn_nose = 2.0
    x_nose = (2 / 3 if spec.nose_shape == "cone" else 0.466) * ln
    cr, ct, s, xr, n = f.root_chord, f.tip_chord, f.span, f.sweep, f.count
    mid_chord = math.sqrt(s * s + (xr + ct / 2 - cr / 2) ** 2)
    n_eff = 2 * FIN_COUNT_FACTOR.get(n, n / 2)
    cn_fins = (1 + r / (s + r)) * (4 * n_eff * (s / d) ** 2) / (1 + math.sqrt(1 + (2 * mid_chord / (cr + ct)) ** 2))
    x_root_le = spec.length_mm - f.root_le_z
    x_fins = x_root_le + xr / 3 * (cr + 2 * ct) / (cr + ct) + (cr + ct - cr * ct / (cr + ct)) / 6
    cn = cn_nose + cn_fins
    return (cn_nose * x_nose + cn_fins * x_fins) / cn, cn


def stability_label(margin: float) -> str:
    if margin < UNSTABLE_BELOW:
        return "unstable"
    if margin < MARGINAL_BELOW:
        return "marginal"
    return "overstable" if margin > OVERSTABLE_ABOVE else "stable"


def on_axis_gltf(z_mm: float) -> list[float]:
    """Part point (0, 0, z) in glTF scene space: metres, Y-up (the GLB root rotates Z-up to Y-up)."""
    return [0.0, round(z_mm / 1000, 5), 0.0]


def analyze(result: BuildResult, masses: dict[str, tuple[float, tuple[float, float, float]]]) -> dict[str, Any]:
    items = [(m, c[2]) for m, c in masses.values()] + [(p.mass_g, p.z_mm) for p in result.point_masses]
    total = sum(m for m, _ in items)
    cg_z = sum(m * z for m, z in items) / total if total else 0.0
    out: dict[str, Any] = {
        "mass_g": round(total, 1),
        "cg_gltf": on_axis_gltf(cg_z),
        "breakdown": [{"id": label.upper(), "mass_g": round(m, 2)} for label, (m, _) in masses.items()]
                     + [{"name": p.name, "mass_g": p.mass_g} for p in result.point_masses],
    }
    spec = result.rocket
    if spec is None:
        return out
    cp_from_tip, cna = barrowman(spec)
    cg_from_tip = spec.length_mm - cg_z
    margin = (cp_from_tip - cg_from_tip) / spec.diameter_mm
    out.update({
        "length_mm": round(spec.length_mm, 1),
        "diameter_mm": round(spec.diameter_mm, 1),
        "cg_mm": round(cg_from_tip, 1),
        "cp_mm": round(cp_from_tip, 1),
        "margin_cal": round(margin, 2),
        "stable": margin >= UNSTABLE_BELOW,
        "stability": stability_label(margin),
        "cna_per_rad": round(cna, 2),
        "motor": spec.motor,
        "markers_gltf": {"cg": on_axis_gltf(cg_z), "cp": on_axis_gltf(spec.length_mm - cp_from_tip)},
        "assumptions": ASSUMPTIONS,
    })
    return out
