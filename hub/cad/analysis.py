"""Mass properties and generic metrics for any built part, plus the static stability helpers (Barrowman 1966)
that rocket parts use in their analyze(). The analysis block's markers, dimensions, explode, mass breakdown and
demo come from cad/scene.py."""
from __future__ import annotations

import math
from typing import Any, Callable, Iterable

from build123d import CenterOf, GeomType

from cad.part_api import Analysis, BuildResult, Metric, RocketSpec, ShellMass, SolidMass

# Static margin bands in calibers (NASA Student Launch: stable >= 1, 1.5 to 2.0 desirable).
UNSTABLE_BELOW, MARGINAL_BELOW, OVERSTABLE_ABOVE = 1.0, 1.5, 2.5
MARGIN_GAUGE_MAX = 3.0
MARGIN_BANDS = ((UNSTABLE_BELOW, "bad"), (MARGINAL_BELOW, "warn"), (OVERSTABLE_ABOVE, "ok"), (MARGIN_GAUGE_MAX, "warn"))
STABILITY_STATUS = {"unstable": "bad", "marginal": "warn", "stable": "ok", "overstable": "warn"}

# Fin-to-fin interference for 5 to 8 fins replaces N/2 (Niskanen 2013, OpenRocket technical documentation).
FIN_COUNT_FACTOR = {5: 2.37, 6: 2.74, 7: 2.99, 8: 3.24}

STABILITY_ASSUMPTIONS = [
    "Barrowman static CP: subsonic, zero angle of attack, body tube adds no lift.",
    "CG at liftoff with a loaded motor (the least stable moment of a normal flight).",
    "Masses from part geometry and the densities in the part script; glue and paint ignored.",
]

Masses = dict[str, tuple[float, tuple[float, float, float]]]


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


def mass_properties(result: BuildResult) -> Masses:
    out = {}
    for shape in leaves(result.assembly):
        props = part_mass(shape, result.masses.get(shape.label))
        if props is not None:
            out[shape.label] = props
    return out


def center_of_mass(result: BuildResult, masses: Masses) -> tuple[float, tuple[float, float, float]]:
    """(total grams, centre in part mm) of the parts plus the point masses (which sit on the Z axis)."""
    items = list(masses.values()) + [(p.mass_g, (0.0, 0.0, p.z_mm)) for p in result.point_masses]
    total = sum(m for m, _ in items)
    if not total:
        return 0.0, (0.0, 0.0, 0.0)
    return total, tuple(sum(m * c[i] for m, c in items) / total for i in range(3))


def to_gltf(point_mm: tuple[float, float, float]) -> list[float]:
    """Part point in glTF scene space: metres, Y-up (the GLB root rotates the part's Z-up to Y-up)."""
    x, y, z = point_mm
    return [round(x / 1000, 5) + 0.0, round(z / 1000, 5) + 0.0, round(-y / 1000, 5) + 0.0]  # + 0.0: no -0.0


def on_axis_gltf(z_mm: float) -> list[float]:
    """Part point (0, 0, z) in glTF scene space."""
    return to_gltf((0.0, 0.0, z_mm))


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


def generic_metrics(total_g: float, size_mm: list[float], part_count: int) -> list[Metric]:
    """What every design shows: mass, overall size, number of parts."""
    mass = f"{total_g:.0f} g" if total_g < 1000 else f"{total_g / 1000:.2f} kg"
    return [Metric("mass", "Mass", total_g, "g", display=mass),
            Metric("size", "Size", max(size_mm), "mm", display=" x ".join(f"{v:.0f}" for v in size_mm) + " mm"),
            Metric("parts", "Parts", part_count)]


def analyze(result: BuildResult, masses: Masses,
            part_analyze: Callable[[BuildResult, Masses], list[Metric] | Analysis] | None = None, *,
            features: Iterable[str] = ()) -> dict[str, Any]:
    """The analysis block of cad.model_updated: generic fields and metrics, plus the part's own analyze(), plus
    what Unity draws and says (markers, dimensions, explode, mass_breakdown, demo, verdict, constants).
    `features`: the design's ("flight")."""
    from cad.scene import extras  # cad.scene imports this module, so not at the top

    total, cg = center_of_mass(result, masses)
    size = result.assembly.bounding_box().size
    size_mm = [round(size.X, 1), round(size.Y, 1), round(size.Z, 1)]
    count = sum(1 for _ in leaves(result.assembly))
    out: dict[str, Any] = {
        "mass_g": round(total, 1),
        "cg_gltf": to_gltf(cg),
        "size_mm": size_mm,
        "part_count": count,
        "breakdown": [{"id": label.upper(), "mass_g": round(m, 2)} for label, (m, _) in masses.items()]
                     + [{"name": p.name, "mass_g": p.mass_g} for p in result.point_masses],
    }
    own = part_analyze(result, masses) if part_analyze else []
    plan = own if isinstance(own, Analysis) else Analysis()
    if isinstance(own, Analysis):
        out.update(own.extra)
        own = own.metrics
    ids = {m.id for m in own}
    out["metrics"] = [m.schema() for m in [*own, *(g for g in generic_metrics(total, size_mm, count) if g.id not in ids)]]
    out.update(extras(result, masses, plan, cg_mm=cg, total_g=total, metrics=out["metrics"], features=features))
    return out
