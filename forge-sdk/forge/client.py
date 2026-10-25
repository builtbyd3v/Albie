"""Transport from the SDK to the hub (ML_PLAN 7.3).

Every event is appended to the run's `events.jsonl` ({t, type, payload}, t = seconds since the run
started), which makes every run a replayable recording. It is also queued for a background thread
that batches events and POSTs them to `$FORGE_HUB/ml/ingest`, so training never waits on the network.

FORGE_HUB      hub address (default http://127.0.0.1:8000)
FORGE_OFFLINE  "1": don't contact the hub; echo events to stdout instead
"""

from __future__ import annotations

import atexit
import json
import os
import queue
import sys
import threading
import time
from pathlib import Path
from typing import Any, Callable

import httpx

BATCH_MAX = 50
BATCH_WAIT_S = 0.2
POST_TIMEOUT_S = 2.0
FLUSH_TIMEOUT_S = 3.0

_warned: set[str] = set()


def warn(msg: str, key: str | None = None) -> None:
    """One stderr line per distinct problem; the SDK never raises into the training script."""
    key = key or msg
    if key in _warned:
        return
    _warned.add(key)
    print(f"forge: {msg} (training continues)", file=sys.stderr, flush=True)


def hub_url() -> str:
    return os.environ.get("FORGE_HUB", "http://127.0.0.1:8000").rstrip("/")


def offline() -> bool:
    return os.environ.get("FORGE_OFFLINE") == "1"


def _http_post(url: str, body: dict) -> None:
    httpx.post(url, json=body, timeout=POST_TIMEOUT_S).raise_for_status()


class Poster:
    """Background thread: drains the queue in batches of up to BATCH_MAX, one POST per run id."""

    def __init__(self, post: Callable[[str, dict], None] = _http_post) -> None:
        self.post = post
        self._queue: queue.Queue[tuple[str, dict]] = queue.Queue()
        self._thread: threading.Thread | None = None
        self._lock = threading.Lock()

    def put(self, run_id: str, event: dict) -> None:
        with self._lock:
            if self._thread is None:
                self._thread = threading.Thread(target=self._loop, name="forge-poster", daemon=True)
                self._thread.start()
        self._queue.put((run_id, event))

    def _loop(self) -> None:
        while True:
            batch = [self._queue.get()]
            deadline = time.monotonic() + BATCH_WAIT_S
            while len(batch) < BATCH_MAX and (left := deadline - time.monotonic()) > 0:
                try:
                    batch.append(self._queue.get(timeout=left))
                except queue.Empty:
                    break
            self._send(batch)
            for _ in batch:
                self._queue.task_done()

    def _send(self, batch: list[tuple[str, dict]]) -> None:
        by_run: dict[str, list[dict]] = {}
        for run_id, event in batch:
            by_run.setdefault(run_id, []).append(event)
        for run_id, events in by_run.items():
            try:
                self.post(f"{hub_url()}/ml/ingest", {"run_id": run_id, "events": events})
            except Exception as exc:  # noqa: BLE001 - hub down must not affect training
                warn(f"hub at {hub_url()} unreachable ({type(exc).__name__}); events are still saved to "
                     f"events.jsonl and can be replayed", key="post")

    def flush(self, timeout: float = FLUSH_TIMEOUT_S) -> bool:
        """Wait for queued events to be sent. Returns False if it timed out."""
        deadline = time.monotonic() + timeout
        while self._queue.unfinished_tasks:
            if time.monotonic() > deadline:
                return False
            time.sleep(0.02)
        return True


poster = Poster()
atexit.register(poster.flush)


def emit(event_type: str, payload: dict[str, Any], *, run_id: str, run_dir: Path, t0: float) -> None:
    """Record one `{type, payload}` event: events.jsonl always, then the hub (or stdout when offline)."""
    event = {"type": event_type, "payload": payload}
    try:
        with open(run_dir / "events.jsonl", "a") as f:
            f.write(json.dumps({"t": round(time.monotonic() - t0, 3), **event}) + "\n")
    except OSError as exc:
        warn(f"can't write {run_dir / 'events.jsonl'}: {exc}", key="jsonl")
    if offline():
        print(json.dumps(event), flush=True)
    else:
        poster.put(run_id, event)
