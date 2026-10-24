"""Types a part script uses to declare its parameters, physics and metrics.

Part scripts (and Bit, when it edits one) import only this module and build123d, so keep it small and stable.
Units are millimetres, Z up. Rocket parts put the tail at z=0 and the nose tip at +z.

A part script is one file in hub/cad/parts/ (the file name is the design's id). It defines PARAMS and
build(params) -> BuildResult, and optionally TITLE, DESCRIPTION, GLYPH (the Designs menu icon: rocket | drone |
gears | box), PRESETS ({name: {param: value}}), analyze(result, masses) -> list[Metric] | Analysis, and
simulate_flight(result, masses). See parts/_template.py.

analyze() can also hand Unity things to draw and say (all optional, in part millimetres; cad/scene.py converts them
to the GLB's frame and works out generic ones for whatever is left out): markers, dimensions, explode, demo,
mass_groups, verdict, constants.
"""
from __future__ import annotations

import math
from dataclasses import dataclass, field
from fnmatch import fnmatchcase
from typing import Any

DEFAULT_GROUP = "General"            # the panel section of a parameter that names none
DEFAULT_GLYPH = "box"                # the Designs menu icon of a design that names none

Vec = tuple[float, float, float]     # a point or direction in part millimetres / axes (Z up)


class ParamError(ValueError):
    """A parameter value the part cannot accept (wrong type, unknown option, unknown name)."""


def nice_step(x: float) -> float:
    """x rounded to 1, 2 or 5 times a power of ten (4.5 -> 5, 0.13 -> 0.1)."""
    exponent = math.floor(math.log10(x))
    mantissa = x / 10 ** exponent
    nice = 1 if mantissa < 1.5 else 2 if mantissa < 3.5 else 5 if mantissa < 7.5 else 10
    return float(f"{nice * 10 ** exponent:.6g}")


@dataclass(frozen=True)
class Param:
    name: str
    default: float | int | str
    min: float | None = None
    max: float | None = None
    unit: str = ""
    step: float | None = None     # the slider's and the fine stepper's increment; None: worked out from the range
    options: tuple[str, ...] | None = None
    label: str = ""
    group: str = ""               # the panel section it sits in ("Body", "Fins"); "" = "General"
    help: str = ""                # one short line on what it does (panel hint, Bit)
    parts: tuple[str, ...] = ()   # the parts it shapes, as label patterns ("fin_*"); () = guessed from its name
    dimension: str | None = None  # id of the dimension callout it changes; None = one with the same id as its name

    def shapes(self, labels: list[str]) -> list[str]:
        """The part labels this parameter shapes: those matching `parts`, else those sharing the first word of its
        name (fin_root -> fin_1, fin_2, ...; shaft_diameter -> input_shaft, ...), else all of them."""
        if self.parts:
            return [label for label in labels if any(fnmatchcase(label, pattern) for pattern in self.parts)]
        word = self.name.split("_")[0]
        return [label for label in labels if word in label.split("_")] or list(labels)

    def fine_step(self) -> float | None:
        """`step`, or for a number without one: 1 for whole numbers, else about a hundredth of the range."""
        if self.step is not None or self.options is not None:
            return self.step
        if isinstance(self.default, int):
            return 1
        if self.min is None or self.max is None or self.max <= self.min:
            return None
        return nice_step((self.max - self.min) / 100)

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

    def schema(self, value: Any, labels: list[str] | None = None,
               dimensions: list[str] | None = None) -> dict[str, Any]:
        """The panel row. With the version's leaf part labels and dimension ids it also says which parts this
        parameter shapes (`parts`, object ids) and which dimension callout it changes (`dimension`, or None)."""
        out = {"name": self.name, "value": value, "min": self.min, "max": self.max, "unit": self.unit,
               "step": self.fine_step(), "options": list(self.options) if self.options else None,
               "label": self.label or self.name.replace("_", " "),
               "group": self.group or DEFAULT_GROUP, "help": self.help, "default": self.coerce(self.default)[0]}
        if labels is not None:
            out["parts"] = [label.upper() for label in self.shapes(labels)]
        if dimensions is not None:
            wanted = self.dimension if self.dimension is not None else self.name
            out["dimension"] = wanted if wanted in dimensions else None
        return out


def param_groups(schema: list[dict[str, Any]]) -> list[str]:
    """The panel's sections in order: each parameter's group, first appearance first."""
    return list(dict.fromkeys(p.get("group") or DEFAULT_GROUP for p in schema))


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
    parachute_mm: float = 305.0     # flat diameter of the recovery parachute (12")
    ejection_delay_s: float = 5.0   # the motor's delay: its ejection charge fires this long after burnout (C6-5)


@dataclass
class BuildResult:
    assembly: Any  # build123d Compound; every node needs a unique label (GLB node name, metadata ID)
    masses: dict[str, SolidMass | ShellMass] = field(default_factory=dict)  # by part label
    point_masses: list[PointMass] = field(default_factory=list)
    rocket: RocketSpec | None = None
    params: dict[str, Any] = field(default_factory=dict)  # the values build() got; the hub fills this in for analyze()


@dataclass(frozen=True)
class Metric:
    """One number on the design panel (and for Bit). Give min and max to draw it as a gauge.

    bands colour the gauge: (upper bound, status) pairs in ascending order, the last one reaching max.
    """
    id: str
    label: str
    value: float
    unit: str = ""
    status: str = "ok"            # ok | warn | bad
    min: float | None = None
    max: float | None = None
    bands: tuple[tuple[float, str], ...] = ()
    note: str = ""                # one word for the gauge's chip, e.g. "stable"
    digits: int = 0               # decimals in the display text
    display: str = ""             # replaces "<value> <unit>"

    def schema(self) -> dict[str, Any]:
        return {"id": self.id, "label": self.label, "value": round(float(self.value), 4), "unit": self.unit,
                "display": self.display or f"{self.value:.{self.digits}f} {self.unit}".strip(),
                "status": self.status, "min": self.min, "max": self.max,
                "bands": [{"to": to, "status": status} for to, status in self.bands], "note": self.note}


def primary_metric(metrics: list[dict[str, Any]]) -> dict[str, Any] | None:
    """A design's main metric among analysis metrics (schema dicts), as {id, label, value, display, status}: the
    first with a range (the panel's dial: the rocket's margin, the drone's prop clearance), else the first."""
    main = next((m for m in metrics if m.get("min") is not None and m.get("max") is not None),
                metrics[0] if metrics else None)
    return None if main is None else {k: main.get(k) for k in ("id", "label", "value", "display", "status")}


def band(value: float, bands: tuple[tuple[float, str], ...]) -> str:
    """Status of value in ascending (upper bound, status) bands; past the last bound counts as the last band."""
    for upper, status in bands:
        if value < upper:
            return status
    return bands[-1][1]


@dataclass(frozen=True)
class Marker:
    """A labelled point on the model, e.g. the rocket's CP. The hub adds the CG (id "cg") itself.
    kind lets Unity pick a style: cg | cp | thrust | input | output | ..."""
    id: str
    label: str
    kind: str
    position: Vec


@dataclass(frozen=True)
class Dimension:
    """A length to call out on the model, measured from `start` to `end` (points on the model; the value is the
    distance between them). The dimension line is drawn `offset_mm` away from them along `offset`, with extension
    lines back to the points, so pick an offset that clears the geometry."""
    id: str
    label: str
    start: Vec
    end: Vec
    offset: Vec = (0.0, 0.0, 1.0)
    offset_mm: float = 10.0


def box_dimensions(shape) -> list[Dimension]:
    """Overall width (X), depth (Y) and height (Z) along the edges of `shape`'s bounding box: width on the front
    bottom edge, depth on the right bottom edge, height on the front right edge. The hub's default dimensions."""
    bb = shape.bounding_box()
    (x0, y0, z0), (x1, y1, z1) = (bb.min.X, bb.min.Y, bb.min.Z), (bb.max.X, bb.max.Y, bb.max.Z)
    gap = 0.06 * max(x1 - x0, y1 - y0, z1 - z0) + 4.0
    return [Dimension("width", "Width", (x0, y0, z0), (x1, y0, z0), (0.0, -1.0, 0.0), gap),
            Dimension("depth", "Depth", (x1, y0, z0), (x1, y1, z0), (1.0, 0.0, 0.0), gap),
            Dimension("height", "Height", (x1, y0, z0), (x1, y0, z1), (1.0, 0.0, 0.0), gap)]


@dataclass(frozen=True)
class ExplodeMove:
    """Where one part goes in the exploded view: `distance_mm` along `direction` (any length; the hub normalises it).
    `order` staggers the animation: 0 moves first, then 1, ..."""
    part: str          # part label
    direction: Vec
    distance_mm: float
    order: int = 0


@dataclass
class Explode:
    """How the design comes apart. `axis` names the idea: stack (along the long axis), radial (out from the centre),
    shaft (along the shafts), auto (the hub's guess). Parts without a move stay put."""
    axis: str = "auto"
    moves: list[ExplodeMove] = field(default_factory=list)


@dataclass(frozen=True)
class Spinner:
    """A part that turns in the demo, about the axis through `origin` along `direction`.
    rpm is signed: positive turns counter-clockwise seen from the tip of `direction` (right-hand rule)."""
    part: str                       # part label
    origin: Vec
    direction: Vec
    rpm: float
    rel_speed: float | None = None  # speed relative to the demo's input, signed (a gear train's ratio per gear)
    motor: str | None = None        # label of the motor part that drives it (a drone's prop)
    radius_mm: float | None = None  # what to draw round the axis: a prop's swept radius, a gear's pitch radius


@dataclass
class Demo:
    """What the panel's demo button does: kind launch (fly the trajectory from GET source), hover (lift off on
    the spinning props) or spin (run the spinners), or none. values are the numbers the animation and readouts use."""
    kind: str = "none"
    label: str = ""                         # the button: Fly, Hover, Run
    note: str = ""                          # one plain sentence for the panel
    source: str | None = None               # launch: the endpoint with the trajectory ("/cad/flight")
    axis: tuple[Vec, Vec] | None = None     # (origin, direction): launch / lift direction, or the input shaft
    spinners: list[Spinner] = field(default_factory=list)
    readouts: list[Metric] = field(default_factory=list)
    values: dict[str, float] = field(default_factory=dict)
    assumptions: list[str] = field(default_factory=list)
    blocked: str | None = None              # why the demo can't run (the button greys out), e.g. props collide
    reveal: list[str] = field(default_factory=list)   # part labels to lift / turn to glass while it runs (a lid)


@dataclass(frozen=True)
class Verdict:
    """The design's answer on the panel ("Will it fly?"): status ok | warn | bad, a headline of a word or two, one
    plain sentence with the numbers. metric: the id of the metric it's about."""
    status: str
    headline: str
    sentence: str
    metric: str | None = None


@dataclass(frozen=True)
class Constant:
    """A fixed input the design assumes rather than a parameter (the rocket's motor), shown as an info row."""
    label: str
    value: str
    note: str = ""


@dataclass
class Analysis:
    """What analyze() returns when it adds more than metrics: fields of its own for the analysis block (the
    rocket's margin) and, all optional, things for Unity to draw and say (part mm; None = the hub's generic one)."""
    metrics: list[Metric] = field(default_factory=list)
    extra: dict[str, Any] = field(default_factory=dict)
    markers: list[Marker] = field(default_factory=list)       # added to the hub's CG marker
    dimensions: list[Dimension] | None = None                 # None: box_dimensions (width, depth, height)
    explode: Explode | None = None                            # None: parts spread out from the centre
    demo: Demo | None = None                                  # None: launch with simulate_flight, else none
    mass_groups: dict[str, list[str]] | None = None           # {group label: part labels}; None: top-level assemblies
    verdict: Verdict | None = None                            # None: from the worst metric
    constants: list[Constant] = field(default_factory=list)   # fixed inputs (motor, power system, input motor)
