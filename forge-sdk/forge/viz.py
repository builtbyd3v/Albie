"""Extra charts (ML_PLAN A7): `<repo>/.forge/extra_viz.py`, written by setup, run at the end of a run.

The classifier picks up to 3 extras (`profile.extra_viz`), each with a `viz_id`. Setup writes one function
per id into `.forge/extra_viz.py`:

    def viz_class_balance(ctx):
        fig, ax = plt.subplots()
        ...
        return fig                                  # saved as runs/<run_id>/viz/class_balance.png
        # or return {"kind": "bar", "title": ..., "x": [...], "series": [...]}

Functions are named `viz_<viz_id>` and receive a `VizContext` (model, adapter, classes, samples, ids,
labels, probs, results, summary). A returned figure is saved as a PNG (`kind: "image"` card); a returned
dict is a VizCard (ML_PLAN 7.6) and is written to `runs/<run_id>/viz.json`. The hub merges both into
`GET /ml/viz`.

This module runs inside the training process, after evaluation. It never raises: each chart that fails
becomes one warning line and the rest of the run continues.
"""

from __future__ import annotations

import importlib.util
import inspect
import json
import re
import sys
from dataclasses import dataclass, field
from pathlib import Path
from types import ModuleType
from typing import Any, Callable

CHART_PREFIX = "viz_"
VIZ_ID = re.compile(r"^[A-Za-z0-9][A-Za-z0-9_.-]{0,79}$")
KINDS = ("line", "bar", "heatmap", "scatter", "table", "image", "text")
CARD_KEYS = ("title", "subtitle", "panel", "kind", "x", "series", "cells", "x_labels", "y_labels", "points",
             "x_label", "y_label", "columns", "rows", "png_url", "alt", "text")


@dataclass
class VizContext:
    """What an extra-chart function can read. `predicted()` saves the usual argmax loop."""

    model: Any = None
    adapter: Any = None
    classes: list[str] = field(default_factory=list)
    samples: list[Any] = field(default_factory=list)   # adapter.samples("val"): id, item, label, name, meta
    ids: list[str] = field(default_factory=list)       # prediction order (dataset order)
    labels: list[int] = field(default_factory=list)
    probs: Any = None                                  # [N, C] numpy array
    results: dict = field(default_factory=dict)        # results.json
    summary: dict = field(default_factory=dict)        # summary.json
    run_dir: Path | None = None

    def predicted(self) -> list[int]:
        if self.probs is None:
            return []
        return [int(i) for i in self.probs.argmax(axis=1)]


def find_forge_dir(start: str | Path | None = None) -> Path | None:
    """The nearest `.forge/` at or above `start` (the training script usually runs from the repo root)."""
    path = Path(start or Path.cwd()).resolve()
    for folder in (path, *path.parents):
        candidate = folder / ".forge"
        if candidate.is_dir():
            return candidate
    return None


def wanted_ids(forge_dir: str | Path) -> list[str] | None:
    """The classifier's `profile.extra_viz` ids, in order (max 3).

    `None` means there is no usable profile, so a hand-written `extra_viz.py` may run all its charts; an
    empty list means the profile exists and picked none, so nothing runs.
    """
    path = Path(forge_dir) / "profile.json"
    try:
        profile = json.loads(path.read_text())
    except (OSError, ValueError):
        return None
    if not isinstance(profile, dict):
        return None
    extras = profile.get("extra_viz") or []
    ids = [str(v.get("viz_id")) for v in extras if isinstance(v, dict) and v.get("viz_id")]
    return [v for v in ids if VIZ_ID.match(v)][:3]  # ML_PLAN: up to 3


def load_module(path: str | Path) -> ModuleType:
    """Import `.forge/extra_viz.py` with the repo on `sys.path` (so it can import the repo's own code)."""
    path = Path(path).resolve()
    repo = path.parents[1]
    if str(repo) not in sys.path:
        sys.path.insert(0, str(repo))
    spec = importlib.util.spec_from_file_location("forge_repo_extra_viz", path)
    if spec is None or spec.loader is None:
        raise ImportError(f"cannot import {path}")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def chart_functions(module: ModuleType) -> dict[str, Callable[[VizContext], Any]]:
    """Top-level `viz_<id>(ctx)` functions defined by the module, keyed by id."""
    found: dict[str, Callable[[VizContext], Any]] = {}
    for name, fn in inspect.getmembers(module, inspect.isfunction):
        viz_id = name[len(CHART_PREFIX):]
        if name.startswith(CHART_PREFIX) and fn.__module__ == module.__name__ and VIZ_ID.match(viz_id):
            found[viz_id] = fn
    return found


def _warn(message: str) -> None:
    try:
        from forge import client

        client.warn(message)
    except Exception:  # noqa: BLE001 - warning is best-effort, never raise into training
        print(f"forge: {message}", file=sys.stderr)


def run_extra_viz(forge_dir: str | Path, run_dir: str | Path, ctx: VizContext,
                  warn: Callable[[str], None] | None = None) -> list[dict]:
    """Run the repo's extra charts and write `viz/*.png` + `viz.json` into the run folder. Returns the cards."""
    warn = warn or _warn
    path = Path(forge_dir) / "extra_viz.py"
    if not path.exists():
        return []
    wanted = wanted_ids(forge_dir)
    try:
        functions = chart_functions(load_module(path))
    except Exception as exc:  # noqa: BLE001 - a broken extras file must not fail the run
        warn(f"extra_viz.py failed to import: {type(exc).__name__}: {exc}")
        return []
    missing = [viz_id for viz_id in wanted or [] if viz_id not in functions]
    if missing:
        warn(f"extra_viz.py has no function for {', '.join(missing)}")
    if wanted == []:
        return []  # the profile exists and picked no extras: nothing runs
    # Only the classifier's picks run; with no profile at all, every chart in the file runs (still max 3).
    order = [viz_id for viz_id in wanted if viz_id in functions] if wanted else sorted(functions)[:3]
    run = Path(run_dir)
    out_dir = run / "viz"
    cards: list[dict] = []
    for viz_id in order:
        try:
            result = functions[viz_id](ctx)
            if result is None:
                continue
            if hasattr(result, "savefig"):
                out_dir.mkdir(parents=True, exist_ok=True)
                result.savefig(out_dir / f"{viz_id}.png", dpi=110, bbox_inches="tight")
                _close(result)
            elif isinstance(result, dict):
                card = _card(viz_id, result)
                if card is None:
                    warn(f"extra chart {viz_id!r} is not a valid VizCard (needs kind + title)")
                    continue
                cards.append(card)
            else:
                warn(f"extra chart {viz_id!r} returned {type(result).__name__}, not a figure or a dict")
        except Exception as exc:  # noqa: BLE001 - one bad chart never fails the run
            warn(f"extra chart {viz_id!r} failed: {type(exc).__name__}: {exc}")
    if cards:
        existing = _existing_cards(run)
        merged = {c["viz_id"]: c for c in existing if c.get("viz_id")}
        merged.update({c["viz_id"]: c for c in cards})
        (run / "viz.json").write_text(json.dumps(list(merged.values())))
    return cards


def _existing_cards(run: Path) -> list[dict]:
    try:
        data = json.loads((run / "viz.json").read_text())
    except (OSError, ValueError):
        return []
    return [c for c in data if isinstance(c, dict)] if isinstance(data, list) else []


def _card(viz_id: str, data: dict) -> dict | None:
    """A VizCard dict (ML_PLAN 7.6) with only known keys. `png_url` is filled by the hub."""
    kind, title = data.get("kind"), data.get("title")
    if kind not in KINDS or not isinstance(title, str) or not title.strip():
        return None
    card: dict[str, Any] = {"viz_id": viz_id}
    for key in CARD_KEYS:
        if key in data and data[key] is not None and key != "png_url":
            card[key] = data[key]
    card["kind"] = kind
    card["title"] = title.strip()[:120]
    if card.get("panel") not in ("ml-viz", "ml-training"):
        card["panel"] = "ml-viz"
    try:
        json.dumps(card)  # numpy scalars and other non-JSON values would break the hub
    except (TypeError, ValueError):
        return None
    return card


def _close(figure: Any) -> None:
    try:
        import matplotlib.pyplot as plt

        plt.close(figure)
    except Exception:  # noqa: BLE001 - matplotlib's absence is fine, the figure is already saved
        pass
