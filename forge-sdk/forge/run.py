"""`python -m forge.run <graph|dataset|eval> --repo <path>`: the runners the hub calls (ML_PLAN 2, 9 A3).

They go through the repo's `.forge/adapter.py`, so they work on any repo with an adapter, and run with
the repo's own Python (the hub puts forge-sdk on PYTHONPATH). Outputs validate against hub/ml/models.py.

    graph     build_model() + example_input() -> <out>/graph.json        (no trained weights needed)
    dataset   samples()                       -> <out>/dataset.json, samples.json, atlas.png/.json (images)
    eval      load_model() + predict()        -> <repo>/.forge/runs/<id>/results.json, summary.json
                                                 (+ confusion.json for classifiers)

<out> defaults to <repo>/.forge/project/: things known about the project before any run.
"""

from __future__ import annotations

import argparse
import json
import sys
import time
import uuid
from pathlib import Path
from typing import Any

import numpy as np

from forge import adapter as forge_adapter
from forge import client, evaluation

CLASSIFICATION = ("image_classification", "text_classification")
KIND = {"image_classification": "image", "text_classification": "text", "regression": "row"}
PREDICT_BATCH = 64
FULL_IMAGE_MAX = 512


def _write(path: Path, data: Any) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(data))


def run_graph(ad: forge_adapter.Adapter, out: Path) -> dict:
    from forge import graph

    example = ad.example_input()
    if example is None:
        raise ValueError("adapter has no example_input (e.g. sklearn): no layer graph for this project")
    sample = example[0] if getattr(example, "dim", lambda: 0)() > 0 else example
    result = graph.describe(ad.build_model(), sample)
    _write(out / "graph.json", result)
    return result


class _SampleSet:
    """Adapts list[Sample] to the (item, label) dataset that atlas.build reads."""

    def __init__(self, samples: list[forge_adapter.Sample]) -> None:
        self.samples = samples
        self.ids = [s.id for s in samples]
        self.names = [s.name for s in samples]

    def __len__(self) -> int:
        return len(self.samples)

    def __getitem__(self, i: int) -> tuple[Any, int]:
        s = self.samples[i]
        return s.item, int(s.label) if s.label is not None else -1


def run_dataset(ad: forge_adapter.Adapter, out: Path, split: str = "val", limit: int = 1000) -> list[dict]:
    samples = ad.samples(split, limit=limit)
    kind = KIND.get(ad.archetype)
    from forge import atlas  # images need PIL; rows and text don't, but sample ids are shared
    if kind is None:
        raise ValueError(f"no dataset view for archetype {ad.archetype!r}")
    if kind == "image":
        image, atlas_meta, items = atlas.build(_SampleSet(samples))
        out.mkdir(parents=True, exist_ok=True)
        image.save(out / "atlas.png")
        _write(out / "atlas.json", atlas_meta)
        # Full-size images for the detail card (GET /ml/image/{id}); the atlas only has 64 px thumbnails.
        (out / "images").mkdir(exist_ok=True)
        for s in samples:
            full = atlas.to_pil(s.item)
            full.thumbnail((FULL_IMAGE_MAX, FULL_IMAGE_MAX))
            full.save(out / "images" / f"{s.id}.png")
    elif kind == "row":
        items = [{"id": s.id, "name": s.name, "kind": "row", "y": float(s.label)} for s in samples]
    else:
        items = [{"id": s.id, "name": s.name, "kind": "text", "label_idx": int(s.label), "text": str(s.item)}
                 for s in samples]
    _write(out / "dataset.json", items)
    # Per-sample metadata for GET /ml/sample/{id} (picked-image card); predictions are added after eval.
    meta = {s.id: {"id": s.id, "name": s.name, "split": split, "meta": s.meta,
                   **({"y": float(s.label)} if kind == "row" else {"label_idx": int(s.label)})} for s in samples}
    _write(out / "samples.json", meta)
    return items


def run_eval(ad: forge_adapter.Adapter, repo: Path, split: str = "val", limit: int = 1000) -> Path:
    model = ad.load_model()
    if model is None:
        client.warn("no trained checkpoint; evaluating the untrained model from build_model()", key="untrained")
        model = ad.build_model()
    samples = ad.samples(split, limit=limit)
    preds = np.concatenate([np.asarray(ad.predict(model, [s.item for s in samples[i:i + PREDICT_BATCH]]))
                            for i in range(0, len(samples), PREDICT_BATCH)])
    ids = [s.id for s in samples]
    run_id = f"eval-{uuid.uuid4().hex[:6]}"
    run_dir = repo / ".forge" / "runs" / run_id
    if ad.archetype in CLASSIFICATION:
        results, confusion, summary = evaluation.classification(
            ids, [int(s.label) for s in samples], preds, ad.classes(), archetype=ad.archetype)
        _write(run_dir / "confusion.json", confusion)
    elif ad.archetype == "regression":
        results, summary = evaluation.regression(ids, [float(s.label) for s in samples], preds)
    else:
        raise ValueError(f"no eval for archetype {ad.archetype!r}")
    _write(run_dir / "results.json", results)
    _write(run_dir / "summary.json", summary)
    _write(run_dir / "run.json", {"run_id": run_id, "source": "forge.run eval", "started_at": time.time()})
    return run_dir


def main(argv: list[str] | None = None) -> None:
    parser = argparse.ArgumentParser(prog="python -m forge.run")
    parser.add_argument("job", choices=["graph", "dataset", "eval"])
    parser.add_argument("--repo", default=".")
    parser.add_argument("--out", help="graph/dataset output folder (default <repo>/.forge/project)")
    parser.add_argument("--split", default="val")
    parser.add_argument("--limit", type=int, default=1000)
    args = parser.parse_args(argv)

    repo = Path(args.repo).resolve()
    out = Path(args.out) if args.out else repo / ".forge" / "project"
    ad = forge_adapter.load(repo)
    start = time.monotonic()
    if args.job == "graph":
        result = run_graph(ad, out)
        print(f"graph: {len(result['layers'])} layers -> {out / 'graph.json'}")
    elif args.job == "dataset":
        items = run_dataset(ad, out, args.split, args.limit)
        print(f"dataset: {len(items)} {args.split} samples -> {out}")
    else:
        run_dir = run_eval(ad, repo, args.split, args.limit)
        summary = json.loads((run_dir / "summary.json").read_text())
        headline = (f"accuracy {summary['accuracy']}" if summary.get("accuracy") is not None
                    else f"r2 {summary.get('r2')}, mae {summary.get('mae')}")
        print(f"eval: {summary['num_samples']} samples, {headline} -> {run_dir}")
    print(f"done in {time.monotonic() - start:.1f}s", file=sys.stderr)


if __name__ == "__main__":
    main()
