"""`python -m ml.open <repo path>` (from hub/): tell the hub which repo is open and follow its setup (ML_PLAN 3).

Stands in for the VS Code connection until that's decided. Prints each step; on needs_approval it shows
the proposed patch and how to accept it.
"""

from __future__ import annotations

import argparse
import json
import sys
import time
import urllib.error
import urllib.request
from pathlib import Path

DONE = {"ready", "failed", "needs_approval", "confirm_type"}


def _call(url: str, body: dict | None = None) -> dict:
    data = json.dumps(body).encode() if body is not None else None
    req = urllib.request.Request(url, data=data, headers={"content-type": "application/json"},
                                 method="POST" if body is not None else "GET")
    with urllib.request.urlopen(req, timeout=30) as resp:
        return json.loads(resp.read())


def _follow(hub: str, quiet: bool = False) -> dict:
    """Poll GET /ml/project, printing each new status, until setup stops."""
    last = None
    while True:
        state = _call(f"{hub}/ml/project")
        if state["status"] != last and not quiet:
            extra = state.get("summary") or state.get("error") or ""
            print(f"{state['status']:<15} {extra}", flush=True)
        last = state["status"]
        if state["status"] in DONE:
            return state
        time.sleep(0.25)


def main() -> None:
    parser = argparse.ArgumentParser(prog="python -m ml.open")
    parser.add_argument("path", help="the ML repo to open")
    parser.add_argument("--hub", default="http://127.0.0.1:8000")
    parser.add_argument("--rescan", action="store_true", help="ignore an existing .forge/ and set up from scratch")
    args = parser.parse_args()
    hub = args.hub.rstrip("/")
    try:
        _call(f"{hub}/ml/project", {"path": str(Path(args.path).resolve())})
        if args.rescan:
            _follow(hub, quiet=True)  # the open's own setup must finish before a rescan can start
            _call(f"{hub}/ml/project/rescan", {})
    except urllib.error.HTTPError as exc:
        sys.exit(f"hub refused: {exc.code} {exc.read().decode()[:300]}")
    except urllib.error.URLError as exc:
        sys.exit(f"can't reach the hub at {hub}: {exc.reason}")
    state = _follow(hub)
    if state["status"] == "needs_approval":
        for f in state.get("patch", {}).get("files", []):
            print(f"\n--- proposed patch: {f['path']}\n{f['diff']}")
        accept = json.dumps({"accept": True})
        print(f"accept: curl -X POST {hub}/ml/project/patch -H 'content-type: application/json' -d '{accept}'")
    elif state["status"] == "confirm_type":
        print(f"candidates: {state.get('candidates')}; pick one with POST {hub}/ml/project/type")
    sys.exit(0 if state["status"] != "failed" else 1)


if __name__ == "__main__":
    main()
