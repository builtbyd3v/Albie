"""`forge` command line: `forge replay <run_dir>`."""

from __future__ import annotations

import argparse


def main() -> None:
    parser = argparse.ArgumentParser(prog="forge")
    sub = parser.add_subparsers(dest="cmd", required=True)
    replay = sub.add_parser("replay", help="stream a recorded run to the hub at demo speed")
    replay.add_argument("run_dir", help="e.g. demo/fallback/run1")
    replay.add_argument("--speed", type=float, default=1.0)
    args = parser.parse_args()

    if args.cmd == "replay":
        # TODO: read recorded events from run_dir and re-emit them with original timing / speed
        raise SystemExit(f"replay not implemented yet: {args.run_dir}")
