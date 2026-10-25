"""`python -m forge.replay <run dir> --speed 4`: stream a recorded run to the hub as if it were live.

Reads `<run dir>/events.jsonl` (written by every run, ML_PLAN 7.3) and re-sends each event at its
recorded time divided by --speed, so the demo never depends on training behaving live.
"""

from __future__ import annotations

import argparse
import json
import sys
import time
from pathlib import Path
from typing import Callable

from forge import client


def load_events(run_dir: str | Path) -> list[dict]:
    path = Path(run_dir) / "events.jsonl"
    events = [json.loads(line) for line in path.read_text().splitlines() if line.strip()]
    return sorted(events, key=lambda e: e.get("t", 0.0))


def replay(run_dir: str | Path, speed: float = 1.0, max_gap: float | None = None,
           send: Callable[[str, dict], None] | None = None, sleep: Callable[[float], None] = time.sleep) -> int:
    """Re-send every event with its original spacing / speed (each gap capped at max_gap seconds).
    `send(run_id, event)` defaults to the background hub poster. Returns the number of events sent."""
    run_dir = Path(run_dir)
    run_id = json.loads((run_dir / "run.json").read_text())["run_id"] if (run_dir / "run.json").exists() \
        else run_dir.name
    send = send or client.poster.put
    prev_t = None
    events = load_events(run_dir)
    for e in events:
        t = e.get("t", 0.0)
        if prev_t is not None:
            gap = max(0.0, t - prev_t) / speed
            sleep(min(gap, max_gap) if max_gap is not None else gap)
        prev_t = t
        send(run_id, {"type": e["type"], "payload": e["payload"]})
    return len(events)


def main(argv: list[str] | None = None) -> None:
    parser = argparse.ArgumentParser(prog="python -m forge.replay")
    parser.add_argument("run_dir", help="a run folder, e.g. .forge/runs/2171328f or demo/fallback/cifar10")
    parser.add_argument("--speed", type=float, default=1.0, help="4 = four times faster than recorded")
    parser.add_argument("--max-gap", type=float, default=None, help="cap any pause at this many seconds")
    parser.add_argument("--print", action="store_true", help="print events instead of sending to the hub")
    args = parser.parse_args(argv)

    if not (Path(args.run_dir) / "events.jsonl").exists():
        sys.exit(f"no events.jsonl in {args.run_dir}")
    send = (lambda run_id, e: print(json.dumps(e), flush=True)) if args.print else None
    target = "stdout" if args.print else client.hub_url()
    print(f"replaying {args.run_dir} to {target} at {args.speed}x", file=sys.stderr)
    n = replay(args.run_dir, args.speed, args.max_gap, send)
    if not args.print and not client.poster.flush(timeout=10):
        sys.exit("timed out sending events to the hub")
    print(f"replayed {n} events", file=sys.stderr)


if __name__ == "__main__":
    main()
