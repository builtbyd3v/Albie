"""`forge` command line. `forge replay <run dir>` is the same as `python -m forge.replay <run dir>`."""

from __future__ import annotations

import sys


def main() -> None:
    if len(sys.argv) < 2 or sys.argv[1] != "replay":
        sys.exit("usage: forge replay <run dir> [--speed N] [--max-gap S] [--print]")
    from forge.replay import main as replay_main

    replay_main(sys.argv[2:])
