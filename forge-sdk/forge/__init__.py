"""Forge SDK: three calls that stream a training run to the hub.

    import forge
    forge.watch(model, val_dataset, classes=CLASSES)
    for epoch ...:
        forge.log(epoch=e, train_loss=..., train_acc=..., val_loss=..., val_acc=..., lr=...)
    forge.evaluate(model, val_loader)

Each run's artifacts (graph, atlas, dataset, ...) are written to `$FORGE_RUN_DIR/<run_id>/`
(default `./forge_runs`). Events are printed as JSON lines until the hub ingest lands.
"""

from __future__ import annotations

import json
import os
import uuid
from pathlib import Path
from typing import Any, Sequence

import torch

from forge import atlas, graph
from forge.client import emit

_run_id: str | None = None
_run_dir: Path | None = None
_total_epochs: int = 0


def _ensure_run() -> str:
    global _run_id
    if _run_id is None:
        _run_id = uuid.uuid4().hex[:8]
    return _run_id


def _write_json(name: str, data: Any) -> None:
    assert _run_dir is not None
    (_run_dir / name).write_text(json.dumps(data))


def watch(
    model: torch.nn.Module,
    val_dataset: Any,
    classes: Sequence[str],
    epochs: int = 0,
    thumb_size: int = 64,
    run_dir: str | os.PathLike | None = None,
) -> str:
    """Start a run: capture the model graph and the val dataset atlas. Returns the run id."""
    global _run_id, _run_dir, _total_epochs
    _run_id = uuid.uuid4().hex[:8]
    _total_epochs = epochs
    _run_dir = Path(run_dir or os.environ.get("FORGE_RUN_DIR", "forge_runs")) / _run_id
    _run_dir.mkdir(parents=True, exist_ok=True)

    image, atlas_meta, items = atlas.build(val_dataset, cell=thumb_size)
    image.save(_run_dir / "atlas.png")
    _write_json("atlas.json", atlas_meta)
    _write_json("dataset.json", items)
    _write_json("classes.json", list(classes))
    _write_json("run.json", {"run_id": _run_id, "total_epochs": epochs, "num_samples": len(items)})
    emit("ml.run_state", {"run_id": _run_id, "status": "training", "epoch": 0, "total_epochs": epochs})

    example, _ = val_dataset[0]
    _write_json("graph.json", graph.describe(model, example))
    emit("ml.graph_ready", {})
    return _run_id


def log(
    epoch: int,
    train_loss: float,
    train_acc: float,
    val_loss: float,
    val_acc: float,
    lr: float,
    step: int = 0,
) -> None:
    """Send one ml.metrics tick."""
    run_id = _ensure_run()
    emit(
        "ml.metrics",
        {
            "run_id": run_id,
            "epoch": epoch,
            "step": step,
            "train_loss": train_loss,
            "train_acc": train_acc,
            "val_loss": val_loss,
            "val_acc": val_acc,
            "lr": lr,
        },
    )
    # TODO: overfit rule (val loss up 2 epochs in a row while train loss down) -> ml.overfit_alert


def evaluate(model: Any, val_loader: Any) -> None:
    """Send per-sample predictions and the confusion matrix."""
    run_id = _ensure_run()
    # TODO: run model over val_loader -> samples [{id, true_idx, pred_idx, probs, correct}] + confusion
    emit("ml.run_state", {"run_id": run_id, "status": "done", "epoch": _total_epochs, "total_epochs": _total_epochs})
    emit("ml.results_ready", {"run_id": run_id})
