"""`python -m forge.check <repo>`: run the repo's adapter on 8 samples (ML_PLAN 6.5).

Writes `<repo>/.forge/check.json` and exits 1 on any failure. Failure details carry the traceback,
which the hub sends back to the LLM for another adapter round. Never trains: eval mode, no_grad.
"""

from __future__ import annotations

import argparse
import json
import re
import sys
import time
import traceback
from pathlib import Path
from typing import Any, Callable

import numpy as np

from forge import adapter as forge_adapter

TIME_LIMIT_S = 60
# Sample ids end up in hub URLs and file names (/ml/sample/{id}, images/<id>.png): plain characters only.
SAFE_ID = re.compile(r"^[A-Za-z0-9][A-Za-z0-9_.-]{0,79}$")
CLASSIFICATION = ("image_classification", "text_classification")


class _Checks:
    def __init__(self) -> None:
        self.results: list[dict] = []

    def run(self, name: str, fn: Callable[[], Any]) -> Any:
        """Run one check; fn returns a detail string (or a value) or raises. Returns fn's value or None."""
        try:
            value = fn()
            self.results.append({"name": name, "ok": True, "detail": value if isinstance(value, str) else ""})
            return value
        except Exception as exc:  # noqa: BLE001 - every failure is reported, not raised
            detail = "".join(traceback.format_exception(exc)) if not isinstance(exc, AssertionError) else str(exc)
            self.results.append({"name": name, "ok": False, "detail": detail})
            return None

    def skip(self, name: str, why: str) -> None:
        self.results.append({"name": name, "ok": False, "detail": f"skipped: {why}"})


def run_checks(repo: str | Path, n: int = 8) -> dict:
    repo = Path(repo).resolve()
    start = time.monotonic()
    c = _Checks()

    def profile() -> dict:
        data = json.loads((repo / ".forge" / "profile.json").read_text())
        assert data.get("archetype") in forge_adapter.ARCHETYPES, f"unknown archetype {data.get('archetype')!r}"
        return data

    prof = c.run("profile", profile)
    ad = c.run("load_adapter", lambda: forge_adapter.load(repo))
    if ad is None:
        return _finish(repo, c, start)
    if prof is not None:
        c.run("archetype_matches", lambda: _assert(ad.archetype == prof["archetype"],
                                                   f"adapter says {ad.archetype!r}, profile says {prof['archetype']!r}"))

    model = c.run("build_model", lambda: _not_none(ad.build_model(), "build_model returned None"))
    example = c.run("example_input", ad.example_input)
    if model is not None and example is not None and hasattr(model, "named_modules"):
        c.run("graph", lambda: f"{len(_layers(model, example))} layers")

    def samples() -> list:
        first, second = ad.samples("val", limit=n), ad.samples("val", limit=n)
        ids = [s.id for s in first]
        assert 0 < len(ids) <= n, f"expected 1..{n} samples, got {len(ids)}"
        assert len(set(ids)) == len(ids), "sample ids are not unique"
        assert ids == [s.id for s in second], "sample ids differ between two calls"
        bad = [i for i in ids if not SAFE_ID.match(str(i))]
        assert not bad, (f"sample ids must match {SAFE_ID.pattern} (letters, digits, _ . -; used in URLs and "
                         f"file names), got {bad[:3]}")
        return first

    batch = c.run("samples", samples)
    trained = c.run("load_model", ad.load_model)
    predict_model = trained if trained is not None else model
    if batch and predict_model is not None:
        probs = c.run("predict", lambda: _predict(ad, predict_model, batch))
        if probs is not None and ad.archetype in CLASSIFICATION:
            c.run("classes_match_outputs", lambda: _assert(
                len(ad.classes()) == probs.shape[1],
                f"{len(ad.classes())} classes but predict returns {probs.shape[1]} columns"))
    else:
        c.skip("predict", "no samples or no model")

    elapsed = time.monotonic() - start
    c.run("extra_viz", lambda: _check_extra_viz(repo, prof))
    c.run("time", lambda: _assert(elapsed < TIME_LIMIT_S, f"took {elapsed:.1f}s, limit {TIME_LIMIT_S}s"))
    return _finish(repo, c, start)


def _check_extra_viz(repo: Path, prof: dict | None) -> str:
    """`profile.extra_viz` ids must each have a `viz_<id>(ctx)` function in `.forge/extra_viz.py` (ML_PLAN A7).
    No file and no extras: nothing to check. The charts themselves run later, at the end of a training run."""
    wanted = [str(v.get("viz_id")) for v in (prof or {}).get("extra_viz", []) if isinstance(v, dict) and v.get("viz_id")]
    if not wanted:
        return ""  # no extras picked: nothing to validate (a leftover file is inert)
    path = repo / ".forge" / "extra_viz.py"
    if not path.exists():
        raise AssertionError(f"the profile asks for {', '.join(f'viz_{v}(ctx)' for v in wanted)} but "
                             f".forge/extra_viz.py is missing; write those functions")
    from forge import viz

    functions = viz.chart_functions(viz.load_module(path))
    missing = [viz_id for viz_id in wanted if viz_id not in functions]
    assert not missing, ("extra_viz.py must define " + ", ".join(f"viz_{v}(ctx)" for v in missing) +
                         f"; it defines {', '.join(sorted(functions)) or 'nothing'}")
    return f"{len(wanted)} charts"


def _assert(cond: bool, msg: str) -> str:
    assert cond, msg
    return ""


def _not_none(value: Any, msg: str) -> Any:
    assert value is not None, msg
    return value


def _layers(model: Any, example: Any) -> list:
    from forge import graph  # torch: only for repos with a layer graph

    sample = example[0] if getattr(example, "dim", lambda: 0)() > 0 else example
    layers = graph.describe(model, sample)["layers"]
    assert layers, "forward hooks saw no layers"
    return layers


def _predict(ad: forge_adapter.Adapter, model: Any, batch: list) -> np.ndarray:
    out = np.asarray(ad.predict(model, [s.item for s in batch]))
    assert out.shape[0] == len(batch), f"predict returned {out.shape[0]} rows for {len(batch)} items"
    if ad.archetype in CLASSIFICATION:
        assert out.ndim == 2, f"classification predict must be [N, C], got shape {out.shape}"
        sums = out.sum(axis=1)
        assert np.allclose(sums, 1, atol=1e-3), f"probabilities don't sum to 1: {sums.round(3).tolist()}"
    return out


def _finish(repo: Path, c: _Checks, start: float) -> dict:
    result = {"ok": all(r["ok"] for r in c.results), "seconds": round(time.monotonic() - start, 2),
              "checks": c.results}
    out = repo / ".forge" / "check.json"
    if out.parent.is_dir():
        out.write_text(json.dumps(result, indent=1))
    return result


def main() -> None:
    parser = argparse.ArgumentParser(prog="python -m forge.check")
    parser.add_argument("repo")
    parser.add_argument("-n", type=int, default=8, help="samples to check")
    args = parser.parse_args()
    result = run_checks(args.repo, args.n)
    for r in result["checks"]:
        print(f"{'PASS' if r['ok'] else 'FAIL'}  {r['name']}" + (f"\n{r['detail']}" if not r["ok"] else
                                                                  f"  {r['detail']}" if r["detail"] else ""))
    print(f"{'OK' if result['ok'] else 'FAILED'} in {result['seconds']}s")
    sys.exit(0 if result["ok"] else 1)


if __name__ == "__main__":
    main()
