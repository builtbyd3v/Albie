"""Types a part script uses to declare its parameters and physics.

Part scripts (and Bit, when it edits one) import only this module and build123d, so keep it small and stable.
Units are millimetres. Rocket parts put the tail at z=0 and the nose tip at +z.
"""
from __future__ import annotations

import math
from dataclasses import dataclass, field
from typing import Any


class ParamError(ValueError):
    """A parameter value the part cannot accept (wrong type, unknown option, unknown name)."""


@dataclass(frozen=True)
class Param:
    name: str
    default: float | int | str
    min: float | None = None
    max: float | None = None
    unit: str = ""
    step: float | None = None
    options: tuple[str, ...] | None = None
    label: str = ""

    def coerce(self, value: Any) -> tuple[Any, bool]:
        """Return (value, was_clamped). Integer params round; numbers clamp to [min, max]."""
        if self.options is not None:
            if value not in self.options:
                raise ParamError(f"{self.name} must be one of {list(self.options)}, got {value!r}")
            return value, False
        if isinstance(value, bool) or not isinstance(value, (int, float)) or not math.isfinite(value):
            raise ParamError(f"{self.name} must be a number, got {value!r}")
        clamped = value
        if self.min is not None:
            clamped = max(self.min, clamped)
        if self.max is not None:
            clamped = min(self.max, clamped)
        if isinstance(self.default, int):
            clamped = int(round(clamped))
        else:
            clamped = float(clamped)
        return clamped, clamped != value

    def schema(self, value: Any) -> dict[str, Any]:
        return {"name": self.name, "value": value, "min": self.min, "max": self.max, "unit": self.unit,
                "step": self.step, "options": list(self.options) if self.options else None,
                "label": self.label or self.name.replace("_", " ")}


@dataclass(frozen=True)
class SolidMass:
    """Mass from the part's solid volume."""
    density_g_cm3: float
    material: str = ""


@dataclass(frozen=True)
class ShellMass:
    """Thin-walled part drawn solid for looks: mass from its curved surface area times wall thickness."""
    density_g_cm3: float
    wall_mm: float
    material: str = ""


@dataclass(frozen=True)
class PointMass:
    """Something with mass but no geometry (motor, parachute). z is along the part's Z axis."""
    name: str
    mass_g: float
    z_mm: float
    is_motor: bool = False  # the flight sim swaps this for the real motor model (mass burns off)


@dataclass(frozen=True)
class FinSet:
    """Trapezoidal fins using standard rocketry definitions.

    sweep: axial distance from the root leading edge back to the tip leading edge.
    root_le_z: z of the root leading edge; the root trailing edge is at root_le_z - root_chord.
    """
    count: int
    root_chord: float
    tip_chord: float
    span: float
    sweep: float
    root_le_z: float

    def profile(self, body_radius: float) -> list[tuple[float, float]]:
        """Fin outline as (radial, z) points: root LE, root TE, tip TE, tip LE."""
        le, r = self.root_le_z, body_radius
        return [(r, le), (r, le - self.root_chord),
                (r + self.span, le - self.sweep - self.tip_chord), (r + self.span, le - self.sweep)]


@dataclass(frozen=True)
class RocketSpec:
    """What the stability analysis needs. Build it from the same values that make the geometry."""
    length_mm: float
    diameter_mm: float
    nose_length_mm: float
    nose_shape: str  # "cone" | "ogive"
    fins: FinSet
    motor: str = ""


@dataclass
class BuildResult:
    assembly: Any  # build123d Compound; every node needs a unique label (GLB node name, metadata ID)
    masses: dict[str, SolidMass | ShellMass] = field(default_factory=dict)  # by part label
    point_masses: list[PointMass] = field(default_factory=list)
    rocket: RocketSpec | None = None
