"""Forge SDK: three calls that stream a training run to the hub.

    import forge
    forge.watch(model, val_dataset, classes=CLASSES)
    for epoch ...:
        forge.log(epoch=e, train_loss=..., train_acc=..., val_loss=..., val_acc=..., lr=...)
    forge.evaluate(model, val_loader)

Each run's artifacts (graph, atlas, dataset, events.jsonl, ...) are written to `<run dir>/<run_id>/`:
the `run_dir` argument, else `$FORGE_RUN_DIR`, else `.forge/runs` in a repo set up for forge, else
`./forge_runs`. Events also stream to the hub (see forge.client). None of these calls ever raises:
a failure becomes one warning line and training continues.
"""

from __future__ import annotations

import json
import os
import time
import uuid
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any, Callable, Sequence

from forge import atlas, client, graph

OVERFIT_EPOCHS = 2


@dataclass
class _Run:
    run_id: str
    dir: Path
    total_epochs: int = 0
    t0: float = field(default_factory=time.monotonic)
    prev: tuple[float, float] | None = None  # (train_loss, val_loss) of the last log
    rises: int = 0
    alerted: bool = False

    def emit(self, event_type: str, payload: dict[str, Any]) -> None:
        client.emit(event_type, payload, run_id=self.run_id, run_dir=self.dir, t0=self.t0)

    def write_json(self, name: str, data: Any) -> None:
        (self.dir / name).write_text(json.dumps(data))


_run: _Run | None = None


def _run_root(run_dir: str | os.PathLike | None) -> Path:
    if run_dir or os.environ.get("FORGE_RUN_DIR"):
        return Path(run_dir or os.environ["FORGE_RUN_DIR"])
    return Path(".forge/runs") if Path(".forge").is_dir() else Path("forge_runs")


def _start_run(total_epochs: int = 0, run_dir: str | os.PathLike | None = None) -> _Run:
    """FORGE_RUN_ID (set by the hub's POST /ml/train) names the first run, so the hub knows it up front."""
    global _run
    run_id = os.environ.pop("FORGE_RUN_ID", None) or uuid.uuid4().hex[:8]
    path = _run_root(run_dir) / run_id
    path.mkdir(parents=True, exist_ok=True)
    _run = _Run(run_id, path, total_epochs)
    _run.write_json("run.json", {"run_id": run_id, "total_epochs": total_epochs, "started_at": time.time()})
    return _run


def _current() -> _Run:
    """The active run; `log` without `watch` starts one."""
    return _run if _run is not None else _start_run()


def _end_run() -> None:
    global _run
    _run = None


def _attempt(what: str, fn: Callable[[], Any]) -> Any:
    try:
        return fn()
    except Exception as exc:  # noqa: BLE001 - never raise into the training script
        client.warn(f"{what} failed: {type(exc).__name__}: {exc}", key=what)
        return None


def _num(x: Any) -> float:
    """Plain float from a float, int, numpy scalar or 0-d tensor."""
    return float(x.item() if hasattr(x, "item") else x)


def watch(
    model: Any,
    val_dataset: Any,
    classes: Sequence[str],
    epochs: int = 0,
    thumb_size: int = 64,
    run_dir: str | os.PathLike | None = None,
) -> str | None:
    """Start a run: capture the model graph and the val dataset atlas. Returns the run id."""
    run = _attempt("watch", lambda: _start_run(epochs, run_dir))
    if run is None:
        return None
    run.emit("ml.run_state", {"run_id": run.run_id, "status": "training", "epoch": 0, "total_epochs": epochs})
    _attempt("classes", lambda: run.write_json("classes.json", [str(c) for c in classes]))

    def save_atlas() -> None:
        image, atlas_meta, items = atlas.build(val_dataset, cell=thumb_size)
        image.save(run.dir / "atlas.png")
        run.write_json("atlas.json", atlas_meta)
        run.write_json("dataset.json", items)

    _attempt("dataset atlas", save_atlas)

    def save_graph() -> None:
        example, _ = val_dataset[0]
        run.write_json("graph.json", graph.describe(model, example))
        run.emit("ml.graph_ready", {})

    _attempt("model graph", save_graph)
    return run.run_id


def log(
    epoch: int,
    train_loss: float,
    train_acc: float,
    val_loss: float,
    val_acc: float,
    lr: float,
    step: int = 0,
) -> None:
    """Send one ml.metrics tick (end of each epoch); fires ml.overfit_alert when it sees overfitting."""

    def send() -> None:
        run = _current()
        metrics = {"run_id": run.run_id, "epoch": int(epoch), "step": int(step), "train_loss": _num(train_loss),
                   "train_acc": _num(train_acc), "val_loss": _num(val_loss), "val_acc": _num(val_acc),
                   "lr": _num(lr)}
        run.emit("ml.run_state", {"run_id": run.run_id, "status": "training", "epoch": metrics["epoch"],
                                  "total_epochs": run.total_epochs})
        run.emit("ml.metrics", metrics)
        _check_overfit(run, metrics["train_loss"], metrics["val_loss"], metrics["epoch"])

    _attempt("log", send)


def _check_overfit(run: _Run, train_loss: float, val_loss: float, epoch: int) -> None:
    """Val loss up OVERFIT_EPOCHS epochs in a row while train loss goes down: alert once.
    Re-arms when val loss improves again."""
    if run.prev is not None:
        prev_train, prev_val = run.prev
        if val_loss > prev_val and train_loss < prev_train:
            run.rises += 1
        elif val_loss < prev_val:
            run.rises, run.alerted = 0, False
        else:
            run.rises = 0
    run.prev = (train_loss, val_loss)
    if run.rises >= OVERFIT_EPOCHS and not run.alerted:
        run.alerted = True
        run.emit("ml.overfit_alert", {"run_id": run.run_id, "epoch": epoch,
                                      "reason": f"val loss rose {run.rises} epochs in a row while train loss fell"})


def evaluate(model: Any, val_loader: Any) -> None:
    """Send per-sample predictions and the confusion matrix, then end the run."""

    def finish() -> None:
        run = _current()
        total = run.total_epochs
        run.emit("ml.run_state", {"run_id": run.run_id, "status": "evaluating", "epoch": total, "total_epochs": total})
        # TODO (A3): run model over val_loader -> results.json + confusion.json
        run.emit("ml.results_ready", {"run_id": run.run_id})
        run.emit("ml.run_state", {"run_id": run.run_id, "status": "done", "epoch": total, "total_epochs": total})

    _attempt("evaluate", finish)
    _end_run()
    client.poster.flush()
