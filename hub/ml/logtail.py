"""A6 log-tail fallback (ML_PLAN 9): live curves for a repo whose training script doesn't call forge (the user
picked Skip, or the repo was never patched). Reads what the script already logs and sends it as ml.metrics:

- Hugging Face Trainer: `trainer_state.json` (log_history)
- PyTorch Lightning CSVLogger: `metrics.csv`
- TensorBoard: `events.out.tfevents.*` (SummaryWriter, Lightning's default logger, Keras, TF2)

No tensorboard dependency: event files are read with the small TFRecord + protobuf decoder below.

`LogTail.poll()` is called every POLL_S seconds by the router while it's active. It returns the new events, and
also writes them to `.forge/runs/<run_id>/events.jsonl`, so replay and GET /ml/metrics work for tailed runs too.
If the forge SDK starts the same run (its run.json appears), the tail stands down: the SDK's events win.
"""

from __future__ import annotations

import csv
import json
import math
import struct
import time
import uuid
from pathlib import Path
from typing import Any, Iterator

POLL_S = 2.0
IDLE_DONE_S = 60.0  # follow mode: a source quiet this long counts as a finished run
MAX_TICKS = 200     # step-based logs (no validation series) are thinned to this many points
SKIP_DIRS = {".git", ".forge", ".venv", "venv", "env", "node_modules", "__pycache__", "data", "datasets", "wandb"}
IGNORED = {"epoch", "step", "global_step", "total_flos", "train_runtime", "train_samples_per_second",
           "train_steps_per_second", "eval_runtime", "eval_samples_per_second", "eval_steps_per_second",
           "grad_norm", "hp_metric"}
VAL_WORDS = ("val", "valid", "validation", "eval", "test")

Tick = dict[str, Any]  # {epoch, train_loss, train_acc, val_loss, val_acc, lr, extra}


# --- finding logs -----------------------------------------------------------------------------------------
def is_source(path: Path) -> bool:
    return path.name in ("trainer_state.json", "metrics.csv") or path.name.startswith("events.out.tfevents.")


def find_sources(repo: Path, since: float = 0.0) -> list[Path]:
    """Log files under the repo modified at or after `since`, newest first. Skips .forge, venvs and data."""
    found: list[tuple[float, Path]] = []
    stack = [repo]
    while stack:
        folder = stack.pop()
        try:
            entries = list(folder.iterdir())
        except OSError:
            continue
        for entry in entries:
            if entry.is_dir():
                if entry.name not in SKIP_DIRS and not entry.name.startswith("."):
                    stack.append(entry)
            elif is_source(entry):
                mtime = entry.stat().st_mtime
                if mtime >= since:
                    found.append((mtime, entry))
    return [p for _, p in sorted(found, key=lambda x: x[0], reverse=True)]


def read(path: Path) -> list[Tick]:
    """Per-epoch ticks from one log file (oldest first). A half-written file gives what's readable so far."""
    if path.name == "trainer_state.json":
        return read_trainer_state(path)
    if path.name == "metrics.csv":
        return read_lightning_csv(path)
    return read_tensorboard(path)


def preferred(sources: list[Path]) -> Path:
    """The richest log among sources (newest first): HF state, then Lightning CSV, then TensorBoard."""
    for rank in ("trainer_state.json", "metrics.csv"):
        for path in sources:
            if path.name == rank:
                return path
    return sources[0]


# --- metric names -----------------------------------------------------------------------------------------
def classify(name: str) -> str | None:
    """Map a logged name to train_loss / train_acc / val_loss / val_acc / lr, or None for anything else."""
    n = name.lower().replace("/", "_").replace("-", "_").replace(".", "_")
    words = set(n.split("_"))
    if "lr" in words or "learning_rate" in n or n.startswith("lr"):
        return "lr"
    kind = "loss" if "loss" in n else "acc" if "acc" in n else None
    if kind is None:
        return None
    split = "val" if any(w in words or n.startswith(w) for w in VAL_WORDS) else "train"
    return f"{split}_{kind}"


def to_tick(epoch: int, values: dict[str, float]) -> Tick:
    """One ml.metrics row from {logged name: value}. Accuracy in percent (e.g. 87.5) becomes 0.875."""
    tick: Tick = {"epoch": epoch, "train_loss": None, "train_acc": None, "val_loss": None, "val_acc": None, "lr": None}
    extra: dict[str, float] = {}
    # Lightning logs both train_loss_step and train_loss_epoch: the per-epoch value goes last, so it wins.
    for name, value in sorted(values.items(), key=lambda item: item[0].lower().endswith("_epoch")):
        if name.lower() in IGNORED or value is None or not math.isfinite(value):
            continue
        key = classify(name)
        if key is None:
            if len(extra) < 8:
                extra[name] = value
            continue
        if key.endswith("_acc") and value > 1.5:
            value = value / 100
        tick[key] = value
    if extra:
        tick["extra"] = extra
    return tick


def _number(text: Any) -> float | None:
    try:
        value = float(text)
    except (TypeError, ValueError):
        return None
    return value if math.isfinite(value) else None


# --- Hugging Face Trainer ---------------------------------------------------------------------------------
def read_trainer_state(path: Path) -> list[Tick]:
    """log_history rows carry a fractional epoch: train rows every logging_steps, eval rows at each eval.
    Group by epoch (0.5 -> 1, 1.0 -> 1), last value per key; eval_* names are validation."""
    try:
        history = json.loads(path.read_text()).get("log_history") or []
    except (OSError, ValueError):
        return []
    by_epoch: dict[int, dict[str, float]] = {}
    for row in history:
        if "train_runtime" in row or "epoch" not in row:  # the end-of-training summary row
            continue
        epoch = max(1, math.ceil(float(row["epoch"]) - 1e-9))
        values = by_epoch.setdefault(epoch, {})
        for name, value in row.items():
            number = _number(value)
            if number is not None:
                values["train_loss" if name == "loss" else name] = number
    return [to_tick(e, by_epoch[e]) for e in sorted(by_epoch)]


# --- Lightning CSVLogger ----------------------------------------------------------------------------------
def read_lightning_csv(path: Path) -> list[Tick]:
    """metrics.csv: one sparse row per log call with an `epoch` column (0-based). Last value per column per epoch."""
    try:
        rows = list(csv.DictReader(path.read_text().splitlines()))
    except (OSError, csv.Error):
        return []
    by_epoch: dict[int, dict[str, float]] = {}
    for row in rows:
        epoch = _number(row.get("epoch"))
        if epoch is None:
            continue
        values = by_epoch.setdefault(int(epoch) + 1, {})
        for name, value in row.items():
            number = _number(value)
            if name and number is not None:
                values[name] = number
    return [to_tick(e, by_epoch[e]) for e in sorted(by_epoch)]


# --- TensorBoard event files ------------------------------------------------------------------------------
def read_tensorboard(path: Path) -> list[Tick]:
    """Scalars per tag, then one tick per validation step (usually once per epoch) with the latest train values
    at that step. No validation series: one tick per train-loss step, thinned to MAX_TICKS."""
    series: dict[str, list[tuple[int, float]]] = {}
    for file, prefix in _split_event_files(path):
        try:
            data = file.read_bytes()
        except OSError:
            continue
        for step, tag, value in scalars(data):
            series.setdefault(prefix + tag, []).append((step, value))
    if not series:
        return []
    keys = {tag: classify(tag) for tag in series}
    val_tags = [t for t, k in keys.items() if k in ("val_loss", "val_acc")]
    anchor = val_tags or [t for t, k in keys.items() if k == "train_loss"] or list(series)
    steps = sorted({step for tag in anchor for step, _ in series[tag]})
    if not val_tags and len(steps) > MAX_TICKS:
        stride = math.ceil(len(steps) / MAX_TICKS)
        steps = steps[stride - 1::stride]
    ticks = []
    for i, step in enumerate(steps, start=1):
        values = {}
        for tag, points in series.items():
            latest = [v for s, v in points if s <= step]
            if latest:
                values[tag] = latest[-1]
        ticks.append(to_tick(i, values))
    return ticks


def _split_event_files(path: Path) -> list[tuple[Path, str]]:
    """Keras writes logs/train/ and logs/validation/ with the same tags (epoch_loss, ...): read the newest
    event file of each split folder, its tags prefixed with the folder name. Anything else: just this file."""
    split = path.parent.name.lower()
    if split not in ("train", "validation", "val", "eval"):
        return [(path, "")]
    files = []
    for folder in path.parent.parent.iterdir():
        if folder.is_dir() and folder.name.lower() in ("train", "validation", "val", "eval"):
            events = sorted(folder.glob("events.out.tfevents.*"), key=lambda f: f.stat().st_mtime)
            if events:
                files.append((events[-1], folder.name.lower() + "_"))
    return files or [(path, "")]


def scalars(data: bytes) -> Iterator[tuple[int, str, float]]:
    """(step, tag, value) for every scalar in a TFRecord file of tf.Event protos."""
    for record in _records(data):
        step, summary = 0, None
        for field, _, value in _fields(record):
            if field == 2:
                step = value
            elif field == 5:
                summary = value
        if summary is None:
            continue
        for field, _, value in _fields(summary):
            if field == 1:
                scalar = _scalar(value)
                if scalar is not None:
                    yield step, scalar[0], scalar[1]


def _records(data: bytes) -> Iterator[bytes]:
    """TFRecord framing: uint64 length, uint32 length CRC, payload, uint32 payload CRC. Stops at a partial
    record (the writer is mid-flush). CRCs aren't checked: a torn record just ends the read."""
    pos = 0
    while pos + 12 <= len(data):
        (length,) = struct.unpack_from("<Q", data, pos)
        start = pos + 12
        if start + length + 4 > len(data):
            return
        yield data[start:start + length]
        pos = start + length + 4


def _scalar(value_msg: bytes) -> tuple[str, float] | None:
    """Summary.Value: tag (1), simple_value (2, PyTorch/TF1), or a 0-d float tensor (8, TF2/Keras)."""
    tag, number = None, None
    for field, wire, value in _fields(value_msg):
        if field == 1 and wire == 2:
            tag = value.decode("utf-8", "replace")
        elif field == 2 and wire == 5:
            number = struct.unpack("<f", value)[0]
        elif field == 8 and wire == 2:
            number = _tensor_number(value)
    return (tag, float(number)) if tag is not None and number is not None else None


def _tensor_number(tensor: bytes) -> float | None:
    dtype, content, floats, doubles = 0, b"", [], []
    for field, wire, value in _fields(tensor):
        if field == 1:
            dtype = value
        elif field == 4:
            content = value
        elif field == 5:
            floats += list(struct.unpack(f"<{len(value) // 4}f", value)) if wire == 2 else [struct.unpack("<f", value)[0]]
        elif field == 6:
            doubles += list(struct.unpack(f"<{len(value) // 8}d", value)) if wire == 2 else [struct.unpack("<d", value)[0]]
    if floats or doubles:
        return (floats or doubles)[0]
    if content and dtype == 1 and len(content) >= 4:  # DT_FLOAT
        return struct.unpack_from("<f", content)[0]
    if content and dtype == 2 and len(content) >= 8:  # DT_DOUBLE
        return struct.unpack_from("<d", content)[0]
    return None


def _fields(buf: bytes) -> Iterator[tuple[int, int, Any]]:
    """Protobuf wire format: (field number, wire type, value). Varints as int, fixed32/64 and
    length-delimited fields as raw bytes. Stops quietly on a malformed buffer."""
    pos, end = 0, len(buf)
    while pos < end:
        key, pos = _varint(buf, pos)
        if pos < 0:
            return
        field, wire = key >> 3, key & 7
        if wire == 0:
            value, pos = _varint(buf, pos)
            if pos < 0:
                return
        elif wire == 1:
            value, pos = buf[pos:pos + 8], pos + 8
        elif wire == 2:
            length, pos = _varint(buf, pos)
            if pos < 0 or pos + length > end:
                return
            value, pos = buf[pos:pos + length], pos + length
        elif wire == 5:
            value, pos = buf[pos:pos + 4], pos + 4
        else:
            return
        if pos > end:
            return
        yield field, wire, value


def _varint(buf: bytes, pos: int) -> tuple[int, int]:
    result, shift = 0, 0
    while pos < len(buf):
        byte = buf[pos]
        pos += 1
        result |= (byte & 0x7F) << shift
        if not byte & 0x80:
            return result, pos
        shift += 7
    return 0, -1


# --- the tail ---------------------------------------------------------------------------------------------
class LogTail:
    """Turns a repo's own training logs into ml.* events.

    With a run_id (a training started by POST /ml/train): follows the newest log written after `since`, under
    that run id, until the router sees the process exit and calls finish(). Without one (follow mode, after
    Skip): every new log source becomes a new run, marked done after IDLE_DONE_S of quiet.
    """

    def __init__(self, repo: Path, runs_dir: Path, run_id: str | None = None, since: float | None = None,
                 total_epochs: int = 0) -> None:
        self.repo, self.runs_dir = Path(repo), Path(runs_dir)
        self.follow = run_id is None
        self.run_id = run_id
        self.since = time.time() - 1 if since is None else since
        self.total_epochs = total_epochs
        self.source: Path | None = None
        self.seen: set[Path] = set()   # sources that existed when we last picked one
        self.sent = 0                  # ticks already sent for the current source
        self.prev: Tick | None = None  # last tick, for the overfit rule
        self.rises, self.alerted = 0, False
        self.t0 = time.monotonic()
        self.last_change = time.monotonic()
        self.done = False
        self.stopped = False  # the SDK owns this run

    def poll(self) -> list[tuple[str, dict]]:
        if self.stopped:
            return []
        if self.run_id and self._sdk_owns(self.run_id):
            self.stopped = True
            return []
        sources = find_sources(self.repo, self.since)
        if not sources:
            return self._idle()
        events: list[tuple[str, dict]] = []
        # Lock onto one source (a repo can write TensorBoard and CSV side by side); after Skip, a source that
        # wasn't there before is a new training run.
        fresh = [s for s in sources if s not in self.seen]
        if self.source is None or (self.follow and fresh):
            if self.source is not None:
                events += self.finish()
                self.run_id = None
            self.source = preferred(fresh or sources)
            self.sent, self.prev, self.rises, self.alerted, self.done = 0, None, 0, False, False
        self.seen.update(sources)
        ticks = read(self.source)
        if len(ticks) <= self.sent:
            return events + self._idle()
        if self.run_id is None:
            self.run_id = uuid.uuid4().hex[:8]
        self.last_change = time.monotonic()
        for tick in ticks[self.sent:]:
            events += self._tick_events(tick)
        self.sent = len(ticks)
        self._record(events)
        return events

    def finish(self) -> list[tuple[str, dict]]:
        """The run ended (process exited, or the source went quiet): one final run_state done."""
        if self.stopped:
            return self._close_sdk_run()
        if self.done or self.run_id is None or self.sent == 0:
            return []
        self.done = True
        epochs = self.total_epochs or self.sent
        events = [("ml.run_state", {"run_id": self.run_id, "status": "done", "epoch": self.sent, "total_epochs": epochs})]
        self._record(events)
        return events

    def _close_sdk_run(self) -> list[tuple[str, dict]]:
        """A script that calls forge.log but never forge.evaluate leaves its run open (no 'done'), and the
        panels would say TRAINING forever. When its process exits, close it with the last epoch it logged."""
        if self.done or self.run_id is None:
            return []
        self.done = True
        path = self.runs_dir / self.run_id / "events.jsonl"
        epoch, total = 0, self.total_epochs
        try:
            lines = path.read_text().splitlines()
        except OSError:
            return []
        for line in lines:
            try:
                event = json.loads(line)
            except ValueError:
                continue
            payload = event.get("payload") or {}
            if event.get("type") == "ml.run_state":
                if payload.get("status") == "done":
                    return []
                epoch, total = int(payload.get("epoch") or epoch), int(payload.get("total_epochs") or total)
            elif event.get("type") == "ml.metrics":
                epoch = max(epoch, int(payload.get("epoch") or 0))
        events = [("ml.run_state", {"run_id": self.run_id, "status": "done", "epoch": epoch, "total_epochs": total or epoch})]
        with open(path, "a") as f:
            for event_type, payload in events:
                f.write(json.dumps({"t": round(time.monotonic() - self.t0, 3), "type": event_type, "payload": payload}) + "\n")
        return events

    def _idle(self) -> list[tuple[str, dict]]:
        if self.follow and self.sent and not self.done and time.monotonic() - self.last_change > IDLE_DONE_S:
            return self.finish()
        return []

    def _tick_events(self, tick: Tick) -> list[tuple[str, dict]]:
        epoch = tick["epoch"]
        metrics = {"run_id": self.run_id, "step": 0, **tick}
        events = [("ml.run_state", {"run_id": self.run_id, "status": "training", "epoch": epoch,
                                    "total_epochs": self.total_epochs}),
                  ("ml.metrics", metrics)]
        # Same rule as the SDK: val loss up 2 epochs in a row while train loss falls; once per run.
        prev = self.prev
        if prev and None not in (tick["val_loss"], tick["train_loss"], prev["val_loss"], prev["train_loss"]):
            if tick["val_loss"] > prev["val_loss"] and tick["train_loss"] < prev["train_loss"]:
                self.rises += 1
            elif tick["val_loss"] < prev["val_loss"]:
                self.rises, self.alerted = 0, False
            if self.rises >= 2 and not self.alerted:
                self.alerted = True
                events.append(("ml.overfit_alert", {"run_id": self.run_id, "epoch": epoch,
                                                    "reason": "val loss rose 2 epochs in a row while train loss fell"}))
        self.prev = tick
        return events

    def _sdk_owns(self, run_id: str) -> bool:
        info = self.runs_dir / run_id / "run.json"
        if not info.exists():
            return False
        try:
            return json.loads(info.read_text()).get("source") != "logtail"
        except ValueError:
            return True

    def _record(self, events: list[tuple[str, dict]]) -> None:
        """Save under .forge/runs/<run_id>/ like the SDK does, so the run lists, replays and has curves."""
        if not events or self.run_id is None:
            return
        run = self.runs_dir / self.run_id
        run.mkdir(parents=True, exist_ok=True)
        info = run / "run.json"
        if not info.exists():
            info.write_text(json.dumps({"run_id": self.run_id, "total_epochs": self.total_epochs,
                                        "started_at": time.time(), "source": "logtail",
                                        "log": str(self.source.relative_to(self.repo)) if self.source else None}))
        t = round(time.monotonic() - self.t0, 3)
        with open(run / "events.jsonl", "a") as f:
            for event_type, payload in events:
                f.write(json.dumps({"t": t, "type": event_type, "payload": payload}) + "\n")
