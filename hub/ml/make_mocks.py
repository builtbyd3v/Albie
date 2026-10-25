"""`python -m ml.make_mocks` (from hub/): mock project repos for the ML endpoints, no torch needed.

Writes hub/.data/mocks/ml/<archetype>/ laid out like a real repo (.forge/profile.json, .forge/project/,
.forge/runs/<id>/), so the hub serves it through the same code: FORGE_ML_REPO=.data/mocks/ml/image_classification

- image_classification: the Unity mocks (Assets/_Forge/Mocks/ml, from forge-sdk/tools/make_ml_mocks.py),
  so Unity sees the same data from files and over REST.
- regression: synthetic house prices, deterministic.
"""

from __future__ import annotations

import json
import math
import random
import shutil
from pathlib import Path

HUB = Path(__file__).resolve().parents[1]
UNITY_MOCKS = HUB.parent / "unity" / "Assets" / "_Forge" / "Mocks" / "ml"
OUT = HUB / ".data" / "mocks" / "ml"


def _write(path: Path, data) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(data, indent=1))


def _write_events(path: Path, events: list[dict]) -> None:
    path.write_text("".join(json.dumps(e) + "\n" for e in events))


def image_classification(out: Path) -> None:
    def unity(name: str):
        return json.loads((UNITY_MOCKS / name).read_text())

    classes = unity("ml_classes.json")
    run_id = "mock0001"
    _write(out / ".forge" / "profile.json", {
        "schema": 1, "archetype": "image_classification", "confidence": 1.0, "framework": "pytorch",
        "python": "python", "classes": classes, "summary": "Mock: image classifier, 10 classes (CIFAR-10).",
        "notes": "mock project from hub/ml/make_mocks.py"})
    project = out / ".forge" / "project"
    _write(project / "classes.json", classes)
    _write(project / "graph.json", unity("ml_graph.json"))
    _write(project / "dataset.json", [{k: v for k, v in d.items() if k != "thumb_url"}
                                      for d in unity("ml_dataset_val.json")])
    _write(project / "atlas.json", {k: v for k, v in unity("ml_atlas_val.json").items() if k != "atlas_url"})
    shutil.copyfile(UNITY_MOCKS / "ml_atlas_val.png", project / "atlas.png")
    run = out / ".forge" / "runs" / run_id
    _write(run / "run.json", {"run_id": run_id, "total_epochs": 15, "started_at": 0})
    _write(run / "classes.json", classes)
    _write(run / "results.json", unity("ml_results.json"))
    _write(run / "confusion.json", unity("ml_confusion.json"))
    _write(run / "summary.json", unity("ml_summary.json"))
    _write_events(run / "events.jsonl", unity("ml_events.json"))


def regression(out: Path, n: int = 200, seed: int = 7) -> None:
    rng = random.Random(seed)
    run_id = "mockreg1"
    _write(out / ".forge" / "profile.json", {
        "schema": 1, "archetype": "regression", "confidence": 1.0, "framework": "sklearn", "python": "python",
        "target": "price", "summary": "Mock: house price regression, Ridge on 3 features.",
        "notes": "mock project from hub/ml/make_mocks.py"})
    rows, samples, meta = [], [], {}
    for i in range(n):
        sqft, rooms, age = rng.uniform(600, 3500), rng.randint(1, 6), rng.uniform(0, 80)
        y = 50 + 0.12 * sqft + 15 * rooms - 0.6 * age + rng.gauss(0, 25)
        y_pred = 48 + 0.118 * sqft + 14 * rooms - 0.55 * age  # a decent but imperfect fit
        sid = f"row_{i:04d}"
        rows.append({"id": sid, "name": f"house {i}", "kind": "row", "y": round(y, 2)})
        meta[sid] = {"id": sid, "name": f"house {i}", "split": "val", "y": round(y, 2),
                     "meta": {"sqft": round(sqft), "rooms": rooms, "age": round(age, 1)}}
        samples.append({"id": sid, "y_true": round(y, 2), "y_pred": round(y_pred, 2),
                        "residual": round(y - y_pred, 2)})
    residuals = [s["residual"] for s in samples]
    mean_y = sum(s["y_true"] for s in samples) / n
    ss_tot = sum((s["y_true"] - mean_y) ** 2 for s in samples)
    project = out / ".forge" / "project"
    _write(project / "dataset.json", rows)
    _write(project / "samples.json", meta)
    run = out / ".forge" / "runs" / run_id
    _write(run / "run.json", {"run_id": run_id, "total_epochs": 10, "started_at": 0})
    _write(run / "results.json", {"samples": samples})
    r2 = round(1 - sum(r * r for r in residuals) / ss_tot, 4)
    _write(run / "summary.json", {
        "archetype": "regression", "num_samples": n, "r2": r2,
        "mae": round(sum(abs(r) for r in residuals) / n, 4),
        "rmse": round(math.sqrt(sum(r * r for r in residuals) / n), 4), "val_loss": None})
    events, t = [{"t": 0.0, "type": "ml.run_state",
                  "payload": {"run_id": run_id, "status": "training", "epoch": 0, "total_epochs": 10}}], 0.0
    for e in range(1, 11):
        t += 1.0
        loss = 4000 * math.exp(-0.45 * e) + 600
        events.append({"t": t, "type": "ml.metrics", "payload": {
            "run_id": run_id, "epoch": e, "step": e * 10, "train_loss": round(loss * 0.95, 1),
            "train_acc": None, "val_loss": round(loss, 1), "val_acc": None, "lr": 0.01}})
    events += [{"t": t + 0.5, "type": "ml.results_ready", "payload": {"run_id": run_id}},
               {"t": t + 0.55, "type": "ml.run_state",
                "payload": {"run_id": run_id, "status": "done", "epoch": 10, "total_epochs": 10}}]
    _write_events(run / "events.jsonl", events)


def main() -> None:
    if not UNITY_MOCKS.exists():
        raise SystemExit(f"{UNITY_MOCKS} missing: run forge-sdk/tools/make_ml_mocks.py first")
    for name, build in (("image_classification", image_classification), ("regression", regression)):
        target = OUT / name
        shutil.rmtree(target, ignore_errors=True)
        build(target)
        print(f"wrote {target.relative_to(HUB)}")


if __name__ == "__main__":
    main()
