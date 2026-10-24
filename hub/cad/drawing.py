"""Engineering drawing of one built version: drawing.png (2048 x 1448) and drawing.svg, rendered with matplotlib (Agg).

An A3 landscape sheet in third-angle projection: front view, top view above it, right view to its right, all at one
standard scale, plus an isometric view. Visible edges solid, hidden edges dashed (none in the iso view). Overall
dimensions, a parts list and a title block. Parts that changed in this version (relative to the version it came
from) are drawn in orange.

Hidden-line removal is OCCT's exact HLRBRep_Algo, the algorithm build123d's project_to_viewport wraps, called
directly so every part goes in on its own and each projected edge knows its part (for the orange highlight).
A part with many faces (a gear) is projected as itself only in the view that looks along its thinnest side (its
face); the other two orthographic views draw its bounding box, as gear drawings do. That keeps the gearbox fast
and its side views readable (500 tooth edges seen edge-on are a grey smear).

Sized for a ~0.5 m panel in VR at desk distance: thick lines, big text. Units are model millimetres.
"""
from __future__ import annotations

import os
import re
import time
from datetime import datetime
from pathlib import Path
from typing import Any, Iterable

import matplotlib
import numpy as np
from build123d import Box, Location
from matplotlib.backends.backend_agg import FigureCanvasAgg
from matplotlib.collections import LineCollection
from matplotlib.figure import Figure
from matplotlib.font_manager import FontProperties
from matplotlib.patches import Circle, Polygon, Rectangle
from matplotlib.textpath import TextToPath
from PIL import Image
from OCP.BRepAdaptor import BRepAdaptor_Curve
from OCP.BRepLib import BRepLib
from OCP.GCPnts import GCPnts_TangentialDeflection
from OCP.GeomAbs import GeomAbs_Line
from OCP.gp import gp_Ax2, gp_Dir, gp_Pnt
from OCP.HLRAlgo import HLRAlgo_Projector
from OCP.HLRBRep import HLRBRep_Algo, HLRBRep_HLRToShape
from OCP.TopAbs import TopAbs_EDGE, TopAbs_FACE
from OCP.TopExp import TopExp_Explorer
from OCP.TopoDS import TopoDS

from cad.analysis import center_of_mass, leaves
from cad.part_api import BuildResult

# Sheet: A3 landscape in mm, rendered at 2048 px wide.
SHEET_W, SHEET_H = 420.0, 297.0
PX_W, PX_H = 2048, 1448
DPI = PX_W / (SHEET_W / 25.4)
MARGIN = 8.0                       # frame inset
COLUMN = 150.0                     # right column: iso view, parts list, title block
COLUMN_X = SHEET_W - MARGIN - COLUMN
ROW_H = 7.4                        # parts list row

PAPER, INK, HIDDEN, DIM, ORANGE, ORANGE_FILL, MUTED, HEADER_FILL = (
    "#ffffff", "#1b1e23", "#3a4048", "#1d5bb0", "#ef7d00", "#ffecd9", "#5b636e", "#e9ecf0")
FONT = "DejaVu Sans"               # ships with matplotlib, so the PNG looks the same everywhere
LW_VISIBLE, LW_SMOOTH, LW_HIDDEN, LW_DIM, LW_FRAME, LW_RULE = 1.8, 0.8, 1.0, 0.9, 2.4, 1.2   # points
DASH = (0, (7.0, 2.5))             # hidden lines: dash, gap in line widths (ISO 128 uses 12:3)
RC = {"svg.fonttype": "none", "svg.hashsalt": "forge-drawing", "font.family": FONT}

# Views: direction pointing at the viewer, and up. The projector sits at the origin, so a view's 2D coordinates
# are model millimetres: front (X, Z), top (X, Y), right (Y, Z).
VIEWS = {"front": ((0, -1, 0), (0, 0, 1)), "top": ((0, 0, 1), (0, 1, 0)), "right": ((1, 0, 0), (0, 0, 1)),
         "iso": ((1, -1, 1), (0, 0, 1))}
VIEW_AXIS = {"front": 1, "top": 2, "right": 0}   # the model axis each orthographic view looks along
COMPLEX_FACES = 100                # more faces than this: drawn as a box in views that see it edge-on
SCALES = (20, 10, 5, 4, 3, 2, 1.5, 1, 1 / 1.5, 1 / 2, 1 / 2.5, 1 / 3, 1 / 4, 1 / 5, 1 / 6, 1 / 8, 1 / 10, 1 / 15,
          1 / 20, 1 / 25, 1 / 30, 1 / 40, 1 / 50, 1 / 75, 1 / 100)

Lines = dict[tuple[str, bool], list[np.ndarray]]   # (style: visible|smooth|hidden, highlighted) -> polylines


# Projection

def count(shape, kind) -> int:
    explorer, n = TopExp_Explorer(shape, kind), 0
    while explorer.More():
        n += 1
        explorer.Next()
    return n


class Part:
    """A leaf part as the projector sees it: the shape, whether it's highlighted, and for a many-faced part its
    bounding-box stand-in and thinnest axis."""

    def __init__(self, shape, highlighted: bool):
        self.shape, self.highlighted = shape, highlighted
        self.proxy, self.thinnest = None, None
        if count(shape.wrapped, TopAbs_FACE) > COMPLEX_FACES:
            bb = shape.bounding_box()
            size = (bb.size.X, bb.size.Y, bb.size.Z)
            self.thinnest = min(range(3), key=lambda i: size[i])
            self.proxy = Box(*size).moved(Location(bb.center()))

    def shape_for(self, view: str):
        if self.proxy is None or view not in VIEW_AXIS or VIEW_AXIS[view] == self.thinnest:
            return self.shape
        return self.proxy


def polylines(compound, deflection: float) -> list[np.ndarray]:
    """The edges of an HLR result compound as 2D polylines (lines exact, curves within `deflection` mm)."""
    out: list[np.ndarray] = []
    if compound.IsNull():
        return out
    explorer = TopExp_Explorer(compound, TopAbs_EDGE)
    while explorer.More():
        edge = TopoDS.Edge(explorer.Current())
        explorer.Next()
        BRepLib.BuildCurves3d_s(edge, 1e-5)   # HLR edges come with 2D curves only (build123d does the same)
        curve = BRepAdaptor_Curve(edge)
        if curve.GetType() == GeomAbs_Line:
            a, b = curve.Value(curve.FirstParameter()), curve.Value(curve.LastParameter())
            line = np.array(((a.X(), a.Y()), (b.X(), b.Y())))
        else:
            sampler = GCPnts_TangentialDeflection(curve, 0.12, deflection)
            line = np.array([(p.X(), p.Y()) for p in (sampler.Value(i) for i in range(1, sampler.NbPoints() + 1))])
        if len(line) > 1 and np.abs(np.diff(line, axis=0)).sum() > 1e-4:
            out.append(line)
    return out


def project(parts: list[Part], view: str, *, deflection: float) -> Lines:
    """Hidden-line projection of the parts in one view. Every part is added on its own, so the edges come back per
    part (occlusion still counts all of them). No hidden lines in the iso view."""
    toward, up = VIEWS[view]
    shapes = [p.shape_for(view).wrapped for p in parts]
    algo = HLRBRep_Algo()
    for s in shapes:
        algo.Add(s)
    axes = gp_Ax2(gp_Pnt(0, 0, 0), gp_Dir(*toward))
    axes.SetYDirection(gp_Dir(*up))
    algo.Projector(HLRAlgo_Projector(axes))
    algo.Update()
    algo.Hide()
    result = HLRBRep_HLRToShape(algo)
    lines: Lines = {}
    for part, s in zip(parts, shapes):
        groups = {"visible": (result.VCompound(s), result.OutLineVCompound(s)),
                  "smooth": (result.Rg1LineVCompound(s),)}   # tangent edges: thin
        if view != "iso":
            groups["hidden"] = (result.HCompound(s), result.OutLineHCompound(s))
        for style, compounds in groups.items():
            for compound in compounds:
                lines.setdefault((style, part.highlighted), []).extend(polylines(compound, deflection))
    return lines


def dedupe(lines: Lines) -> Lines:
    """Drop repeated edges (a gear's front and back outlines coincide face-on), keeping the most important copy, so
    dashes don't double up into a solid line."""
    order = [(s, h) for s in ("visible", "smooth", "hidden") for h in (True, False) if lines.get((s, h))]
    if not order:
        return {}
    flat = [(key, line) for key in order for line in lines[key]]
    a = np.round(np.array([line[0] for _, line in flat]), 2)
    b = np.round(np.array([line[-1] for _, line in flat]), 2)
    mid = np.round(np.array([line[len(line) // 2] if len(line) > 2 else (line[0] + line[-1]) / 2
                             for _, line in flat]), 2)
    swap = ((a[:, 0] > b[:, 0]) | ((a[:, 0] == b[:, 0]) & (a[:, 1] > b[:, 1])))[:, None]
    signatures = np.hstack([np.where(swap, b, a), np.where(swap, a, b), mid]).tolist()
    seen: set[tuple] = set()
    out: Lines = {}
    for (key, line), sig in zip(flat, map(tuple, signatures)):
        if sig not in seen:
            seen.add(sig)
            out.setdefault(key, []).append(line)
    return out


def chain(lines: list[np.ndarray], tol: float = 1e-3) -> list[np.ndarray]:
    """Join polylines that share end points, so a dash pattern runs on round a curve instead of restarting on
    every tiny segment."""
    def key(p) -> tuple[int, int]:
        return round(float(p[0]) / tol), round(float(p[1]) / tol)

    ends: dict[tuple[int, int], list[int]] = {}
    for i, line in enumerate(lines):
        ends.setdefault(key(line[0]), []).append(i)
        ends.setdefault(key(line[-1]), []).append(i)
    used = [False] * len(lines)
    out = []
    for i in range(len(lines)):
        if used[i]:
            continue
        used[i] = True
        head, tail = [], [lines[i]]
        for forward in (True, False):
            while True:
                end = key(tail[-1][-1]) if forward else key((head[-1] if head else tail[0])[0])
                nxt = next((j for j in ends.get(end, ()) if not used[j]), None)
                if nxt is None:
                    break
                used[nxt] = True
                line = lines[nxt]
                if forward:
                    tail.append((line if key(line[0]) == end else line[::-1])[1:])
                else:
                    head.append((line if key(line[-1]) == end else line[::-1])[:-1])
        out.append(np.vstack(head[::-1] + tail))
    return out


def all_points(lines: Lines) -> np.ndarray:
    chunks = [line for group in lines.values() for line in group]
    return np.vstack(chunks) if chunks else np.zeros((0, 2))


# Sheet helpers

_text_to_path = TextToPath()


def text_mm(text: str, size: float, bold: bool = False) -> float:
    """Width of `text` on the sheet in mm at `size` points."""
    prop = FontProperties(family=FONT, size=size, weight="bold" if bold else "normal")
    width, _, _ = _text_to_path.get_text_width_height_descent(text, prop, ismath=False)
    return width * 25.4 / 72


def fit(text: str, size: float, width: float, bold: bool = False, smallest: float = 11.0) -> tuple[str, float]:
    """Shrink `text` until it fits `width` mm (down to `smallest` points); then drop a trailing "(...)" note;
    then cut it short with an ellipsis. Text width scales with size, so one measurement sizes it."""
    def sized(t: str) -> float:
        measured = text_mm(t, size, bold)
        return size if measured <= width else max(smallest, size * width / measured * 0.98)  # glyphs don't scale exactly

    fitted = sized(text)
    if text_mm(text, fitted, bold) > width and " (" in text:
        text = text.split(" (")[0]
        fitted = sized(text)
    while len(text) > 1 and text_mm(text, fitted, bold) > width:
        text = text[:-2].rstrip() + "…"
    return text, fitted


def scale_text(s: float) -> str:
    return f"{s:g}:1" if s >= 1 else f"1:{1 / s:.4g}"


def mass_text(grams: float) -> str:
    if grams >= 1000:
        return f"{grams / 1000:.2f} kg"
    return f"{grams:.1f} g" if grams < 100 else f"{grams:.0f} g"


def length_text(mm: float) -> str:
    return f"{mm:.0f}" if abs(mm - round(mm)) < 0.05 else f"{mm:.1f}"


class Sheet:
    """A matplotlib figure whose data coordinates are sheet millimetres (origin bottom left)."""

    def __init__(self):
        self.fig = Figure(figsize=(PX_W / DPI, PX_H / DPI), dpi=DPI, facecolor=PAPER)
        FigureCanvasAgg(self.fig)
        self.ax = self.fig.add_axes((0, 0, 1, 1))
        self.ax.set_xlim(0, SHEET_W)
        self.ax.set_ylim(0, SHEET_W * PX_H / PX_W)
        self.ax.axis("off")

    def text(self, x, y, s, size, *, color=INK, bold=False, ha="left", va="baseline", rotation=0):
        self.ax.text(x, y, s, fontsize=size, color=color, fontweight="bold" if bold else "normal", ha=ha, va=va,
                     rotation=rotation, family=FONT)

    def line(self, xs, ys, lw=LW_RULE, color=INK):
        self.ax.plot(xs, ys, lw=lw, color=color, solid_capstyle="butt")

    def rect(self, x, y, w, h, lw=LW_RULE, fill=None):
        self.ax.add_patch(Rectangle((x, y), w, h, lw=lw, edgecolor=INK, facecolor=fill or "none",
                                    fill=fill is not None, zorder=0.5 if fill else 2))

    def lines(self, polylines: list[np.ndarray], *, lw, color, dashed=False, zorder=3):
        if polylines:
            self.ax.add_collection(LineCollection(polylines, linewidths=lw, colors=color, zorder=zorder,
                                                  linestyles=[DASH] if dashed else "solid", capstyle="round",
                                                  joinstyle="round"))

    def arrow(self, tip, direction):
        """Filled dimension arrowhead at `tip` pointing along unit `direction`."""
        (x, y), (dx, dy) = tip, direction
        length, half = 3.2, 0.95
        bx, by = x - dx * length, y - dy * length
        self.ax.add_patch(Polygon([(x, y), (bx - dy * half, by + dx * half), (bx + dy * half, by - dx * half)],
                                  closed=True, color=DIM, lw=0, zorder=4))

    def save(self, path: Path) -> None:
        tmp = path.with_name(path.name + ".tmp")
        if path.suffix == ".png":   # RGB (the sheet is opaque), light compression: quicker to write and to load
            canvas = self.fig.canvas
            canvas.draw()
            Image.frombuffer("RGBA", canvas.get_width_height(), canvas.buffer_rgba(), "raw", "RGBA", 0, 1) \
                .convert("RGB").save(tmp, format="PNG", compress_level=1)
        else:
            self.fig.savefig(tmp, format=path.suffix[1:], dpi=DPI, facecolor=PAPER, metadata={"Date": None})
        os.replace(tmp, path)   # never serve a half-written file


# Drawing

def placed(lines: Lines, scale: float, origin, offset) -> Lines:
    """View lines in sheet mm: model point p lands at offset + scale * (p - origin)."""
    o, d = np.asarray(origin), np.asarray(offset)
    return {k: [d + scale * (line - o) for line in group] for k, group in lines.items()}


def draw_view(sheet: Sheet, lines: Lines, thick: float = 1.0) -> None:
    for highlighted in (False, True):
        color = ORANGE if highlighted else INK
        sheet.lines(chain(lines.get(("hidden", highlighted), [])), lw=LW_HIDDEN,
                    color=ORANGE if highlighted else HIDDEN, dashed=True, zorder=3 + highlighted)
        sheet.lines(lines.get(("smooth", highlighted), []), lw=LW_SMOOTH, color=color, zorder=3.2 + highlighted)
        sheet.lines(lines.get(("visible", highlighted), []), lw=LW_VISIBLE * thick, color=color,
                    zorder=3.5 + highlighted)


def extreme(points: np.ndarray, axis: int, high: bool) -> np.ndarray:
    """The extreme point along `axis` (max if `high`); among ties, the one nearest the dimension line (lowest for a
    horizontal dimension under the view, leftmost for a vertical one left of it)."""
    values = points[:, axis]
    target = values.max() if high else values.min()
    ties = points[np.abs(values - target) < 1e-3 * max(1.0, float(np.ptp(values)))]
    return ties[ties[:, 1 - axis].argmin()]


def dim_horizontal(sheet: Sheet, a, b, y: float, value: float) -> None:
    """Horizontal dimension between sheet points a (left) and b (right), its line at height y below the view."""
    for p in (a, b):
        sheet.line([p[0], p[0]], [p[1] - 1.5, y - 2.0], lw=LW_DIM, color=DIM)
    sheet.line([a[0], b[0]], [y, y], lw=LW_DIM, color=DIM)
    sheet.arrow((a[0], y), (-1, 0))
    sheet.arrow((b[0], y), (1, 0))
    sheet.text((a[0] + b[0]) / 2, y + 1.0, length_text(value), 15, color=DIM, ha="center", va="bottom")


def dim_vertical(sheet: Sheet, a, b, x: float, value: float) -> None:
    """Vertical dimension between sheet points a (bottom) and b (top), its line at x left of the view."""
    for p in (a, b):
        sheet.line([p[0] - 1.5, x - 2.0], [p[1], p[1]], lw=LW_DIM, color=DIM)
    sheet.line([x, x], [a[1], b[1]], lw=LW_DIM, color=DIM)
    sheet.arrow((x, a[1]), (0, -1))
    sheet.arrow((x, b[1]), (0, 1))
    sheet.text(x - 1.0, (a[1] + b[1]) / 2, length_text(value), 15, color=DIM, ha="right", va="center", rotation=90)


def third_angle_symbol(sheet: Sheet, cx: float, cy: float, size: float = 6.0) -> None:
    """ISO 5456-2 third-angle symbol: a truncated cone's side view, its end view next to the narrow end."""
    big, small, length = size, size * 0.5, size * 1.6
    x0 = cx - length - size * 0.6
    sheet.ax.add_patch(Polygon([(x0, cy - big), (x0 + length, cy - small), (x0 + length, cy + small),
                                (x0, cy + big)], closed=True, fill=False, lw=1.1, edgecolor=INK))
    for r in (big, small):
        sheet.ax.add_patch(Circle((cx + size * 0.9, cy), r, fill=False, lw=1.1, edgecolor=INK))


def parts_rows(result: BuildResult, masses, highlight: set[str]) -> list[dict[str, Any]]:
    """Parts list rows: numbered copies of one part (fin_1, fin_2...) of one material share a row with a quantity.
    Point masses (motor, parachute) get a row too, so the masses add up to the title block's total."""
    rows: dict[tuple[str, str], dict[str, Any]] = {}
    for shape in leaves(result.assembly):
        model = result.masses.get(shape.label)
        material = getattr(model, "material", "") or "—"
        stem = re.sub(r"_\d+$", "", shape.label)
        row = rows.setdefault((stem, material), {"name": stem.replace("_", " "), "material": material, "qty": 0,
                                                 "mass": 0.0, "changed": False})
        row["qty"] += 1
        row["mass"] += masses.get(shape.label, (0.0,))[0]
        row["changed"] |= shape.label.upper() in highlight
    return list(rows.values()) + [{"name": p.name, "material": "(not drawn)", "qty": 1, "mass": p.mass_g,
                                   "changed": False} for p in result.point_masses]


def draw_parts_list(sheet: Sheet, rows: list[dict[str, Any]], max_rows: int, bottom: float, total_g: float) -> float:
    """Header, rows, total, sitting on `bottom` (the title block). At most `max_rows` lines in all: the tail folds
    into one "+N more" row. Returns its top edge."""
    x0, size = COLUMN_X, 14
    cols = [("#", 9, "left"), ("PART", 54, "left"), ("QTY", 12, "center"), ("MATERIAL", 44, "left"),
            ("MASS", 31, "right")]
    fits = max(1, max_rows - 2)
    if len(rows) > fits:
        rest = rows[fits - 1:]
        rows = rows[:fits - 1] + [{"name": f"+{len(rest)} more", "material": "", "qty": sum(r["qty"] for r in rest),
                                   "mass": sum(r["mass"] for r in rest), "changed": any(r["changed"] for r in rest)}]
    table = [("header", None)] + [("row", r) for r in rows] + [("total", None)]
    top = bottom + ROW_H * len(table)
    sheet.rect(x0, bottom, COLUMN, top - bottom)
    for i, (kind, row) in enumerate(table):
        y = top - ROW_H * (i + 1)
        changed = kind == "row" and row["changed"]
        if kind == "header" or changed:
            sheet.rect(x0, y, COLUMN, ROW_H, lw=0, fill=HEADER_FILL if kind == "header" else ORANGE_FILL)
        if i:
            sheet.line([x0, x0 + COLUMN], [y + ROW_H, y + ROW_H], lw=LW_RULE * (1.6 if kind == "total" else 0.6))
        if kind == "header":
            cells = [c[0] for c in cols]
        elif kind == "total":
            cells = ["", "TOTAL", "", "", mass_text(total_g)]
        else:
            cells = [str(i), row["name"], str(row["qty"]), row["material"], mass_text(row["mass"])]
        x = x0
        for text, (_, width, align) in zip(cells, cols):
            text, fs = fit(text, 11 if kind == "header" else size, width - 3.2, bold=kind != "row")
            tx = {"left": x + 1.6, "center": x + width / 2, "right": x + width - 1.6}[align]
            sheet.text(tx, y + ROW_H / 2, text, fs, color=ORANGE if changed else INK, bold=kind != "row", ha=align,
                       va="center_baseline")
            x += width
    x = x0
    for _, width, _ in cols[:-1]:   # column rules (not through the total row)
        x += width
        sheet.line([x, x], [bottom + ROW_H, top], lw=LW_RULE * 0.6)
    return top


def draw_title_block(sheet: Sheet, *, title: str, part: str, version: int | None, date: str, scale: float,
                     total_g: float) -> float:
    """Bottom right: title and projection symbol; version, scale, mass; date, design file, sheet. Returns its top."""
    x0, y0, w = COLUMN_X, MARGIN, COLUMN
    heights = (17.0, 17.0, 22.0)   # bottom to top
    y1, y2, top = y0 + heights[0], y0 + heights[0] + heights[1], y0 + sum(heights)
    sheet.rect(x0, y0, w, top - y0, lw=LW_FRAME)
    for y in (y1, y2):
        sheet.line([x0, x0 + w], [y, y])

    def cell(x, y, width, height, label, value, size=18.0, bold=False):
        sheet.text(x + 1.8, y + height - 1.6, label, 9.5, color=MUTED, va="top")
        value, size = fit(value, size, width - 3.6, bold)
        sheet.text(x + 1.8, y + 2.4, value, size, bold=bold)

    cell(x0, y2, 112, heights[2], "TITLE", title, size=26, bold=True)
    sheet.line([x0 + 112, x0 + 112], [y2, top])
    third_angle_symbol(sheet, x0 + 131, y2 + heights[2] / 2 + 2.5)
    sheet.text(x0 + 131, y2 + 2.0, "THIRD ANGLE", 7.5, color=MUTED, ha="center")
    for x, width, label, value in ((x0, 36, "VERSION", "—" if version is None else f"v{version}"),
                                   (x0 + 36, 40, "SCALE", scale_text(scale)),
                                   (x0 + 76, 74, "MASS", mass_text(total_g))):
        cell(x, y1, width, heights[1], label, value, bold=label == "MASS")
        if x > x0:
            sheet.line([x, x], [y1, y2])
    for x, width, label, value in ((x0, 54, "DATE", date), (x0 + 54, 60, "DESIGN", f"{part}.py"),
                                   (x0 + 114, 36, "SHEET", "A3  mm")):
        cell(x, y0, width, heights[0], label, value, size=16)
        if x > x0:
            sheet.line([x, x], [y0, y1])
    return top


def draw(result: BuildResult, masses, out_dir: Path, *, title: str, part: str, version: int | None = None,
         created_at: float | None = None, highlight: Iterable[str] = ()) -> dict[str, int]:
    """Write out_dir/drawing.png and drawing.svg. `masses` is analysis.mass_properties(result); `highlight` holds
    object IDs (upper-case labels) to draw in orange. Returns timings in ms."""
    t0 = time.perf_counter()
    highlight = {h.upper() for h in highlight}
    parts = [Part(shape, shape.label.upper() in highlight) for shape in leaves(result.assembly)]
    size = result.assembly.bounding_box().size
    deflection = max(size.X, size.Y, size.Z, 1.0) / 2500
    views = {name: dedupe(project(parts, name, deflection=deflection)) for name in VIEWS}
    t_project = time.perf_counter()
    with matplotlib.rc_context(RC):
        sheet = render(views, result, masses, highlight, title=title, part=part, version=version,
                       created_at=created_at)
        out_dir.mkdir(parents=True, exist_ok=True)
        sheet.save(out_dir / "drawing.png")
        sheet.save(out_dir / "drawing.svg")
    return {"project": round((t_project - t0) * 1000), "render": round((time.perf_counter() - t_project) * 1000)}


def render(views: dict[str, Lines], result: BuildResult, masses, highlight: set[str], *, title: str, part: str,
           version: int | None, created_at: float | None) -> Sheet:
    # Model extents per axis, from what was actually drawn (front: X, Z; top: X, Y; right: Y, Z).
    pts = {name: all_points(lines) for name, lines in views.items()}

    def span(*chunks) -> tuple[float, float]:
        values = np.concatenate([c for c in chunks if len(c)] or [np.zeros(1)])
        return float(values.min()), float(values.max())

    X = span(pts["front"][:, 0], pts["top"][:, 0])
    Y = span(pts["top"][:, 1], pts["right"][:, 0])
    Z = span(pts["front"][:, 1], pts["right"][:, 1])
    wx, wy, wz = (max(hi - lo, 1e-6) for lo, hi in (X, Y, Z))

    # The three views on the left at one standard scale, centred; the right column has the iso view, parts list
    # and title block. Pads leave room for the dimensions (left, below) and the view labels (above).
    pad_left, pad_right, pad_bottom, pad_top, gap_h, gap_v = 22.0, 4.0, 24.0, 12.0, 22.0, 18.0
    avail_w = COLUMN_X - MARGIN - pad_left - pad_right - gap_h
    avail_h = SHEET_H - 2 * MARGIN - pad_bottom - pad_top - gap_v
    fit_scale = min(avail_w / (wx + wy), avail_h / (wz + wy))
    scale = next((s for s in SCALES if s <= fit_scale * 1.0001), fit_scale)
    fx = MARGIN + pad_left + (avail_w - scale * (wx + wy)) / 2
    fy = MARGIN + pad_bottom + (avail_h - scale * (wz + wy)) / 2
    layout = {"front": ((X[0], Z[0]), (fx, fy), scale * wz),
              "top": ((X[0], Y[0]), (fx, fy + scale * wz + gap_v), scale * wy),
              "right": ((Y[0], Z[0]), (fx + scale * wx + gap_h, fy), scale * wz)}

    sheet = Sheet()
    sheet.rect(MARGIN, MARGIN, SHEET_W - 2 * MARGIN, SHEET_H - 2 * MARGIN, lw=LW_FRAME)
    sheet_pts = {}
    for name, (origin, offset, height) in layout.items():
        lines = placed(views[name], scale, origin, offset)
        draw_view(sheet, lines)
        sheet_pts[name] = all_points(lines)
        sheet.text(offset[0], offset[1] + height + 3.0, name.upper(), 12, color=MUTED, bold=True)

    # Overall dimensions: width (X) and height (Z) on the front view, depth (Y) under the right view.
    front, right = sheet_pts["front"], sheet_pts["right"]
    if len(front) and len(right):
        dim_horizontal(sheet, extreme(front, 0, False), extreme(front, 0, True), fy - 11.0, wx)
        dim_horizontal(sheet, extreme(right, 0, False), extreme(right, 0, True), fy - 11.0, wy)
        dim_vertical(sheet, extreme(front, 1, False), extreme(front, 1, True), fx - 11.0, wz)

    # Title block; parts list on it (leaving at least ~100 mm for the iso view); iso view above.
    total_g, _ = center_of_mass(result, masses)
    date = datetime.fromtimestamp(time.time() if created_at is None else created_at).strftime("%Y-%m-%d")
    block_top = draw_title_block(sheet, title=title, part=part, version=version, date=date, scale=scale,
                                 total_g=total_g)
    rows = parts_rows(result, masses, highlight)
    max_rows = int((SHEET_H - MARGIN - 100.0 - block_top) / ROW_H + 1e-6)
    list_top = draw_parts_list(sheet, rows, max_rows, block_top, total_g)
    sheet.line([COLUMN_X, COLUMN_X], [list_top, SHEET_H - MARGIN])
    label_y = SHEET_H - MARGIN - 8.0
    sheet.text(COLUMN_X + 2.0, label_y, "ISOMETRIC", 12, color=MUTED, bold=True)
    if any(r["changed"] for r in rows) and version is not None:
        sheet.text(SHEET_W - MARGIN - 2.0, label_y, f"orange: changed in v{version}", 12, color=ORANGE, bold=True,
                   ha="right")
    iso = views["iso"]
    iso_pts = all_points(iso)
    if len(iso_pts):
        x0, y0, x1, y1 = COLUMN_X + 6.0, list_top + 8.0, SHEET_W - MARGIN - 6.0, label_y - 6.0
        lo, hi = iso_pts.min(axis=0), iso_pts.max(axis=0)
        iso_scale = min((x1 - x0) / max(hi[0] - lo[0], 1e-6), (y1 - y0) / max(hi[1] - lo[1], 1e-6))
        draw_view(sheet, placed(iso, iso_scale, (lo + hi) / 2, ((x0 + x1) / 2, (y0 + y1) / 2)), thick=0.85)
    return sheet
