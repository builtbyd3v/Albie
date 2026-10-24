"""Stand-in for screen-bridge/start.js in tests: prints the same lines, never touches Chrome.

    python fake_screen_bridge.py --pidfile P [--mode run|die|die-early|hang]

run:       print the full happy path, spawn a grandchild (same process group), then sleep until signalled.
die:       same lines, then exit 1 (a bridge that worked and then crashed).
die-early: print only "signaling on", exit 2 (never reaches sharing: exhausts the restart budget).
hang:      like run but ignores SIGINT, so only SIGKILL on the process group ends it.
The pidfile gets "<own pid> <grandchild pid>" so the test can check both died with the hub.
"""
from __future__ import annotations

import argparse
import signal
import subprocess
import sys
import time


def say(line: str) -> None:
    print(line, flush=True)


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--pidfile", required=True)
    ap.add_argument("--mode", default="run", choices=["run", "die", "die-early", "hang"])
    a = ap.parse_args()
    import os

    say("signaling on ws://0.0.0.0:8766")
    if a.mode == "die-early":
        with open(a.pidfile, "w", encoding="utf-8") as f:
            f.write(f"{os.getpid()} 0\n")
        return 2
    child = subprocess.Popen([sys.executable, "-c", "import time; time.sleep(600)"])  # stands in for Chrome
    with open(a.pidfile, "w", encoding="utf-8") as f:
        f.write(f"{os.getpid()} {child.pid}\n")
    say('Sharing "| demo". If Chrome shows a picker instead, the title didn\'t match; pick the window by hand.')
    say("signaling: connected")
    say("12 fps, 1280x720")
    print("adb: no device", file=sys.stderr, flush=True)
    if a.mode == "die":
        time.sleep(0.2)
        return 1
    if a.mode == "hang":
        signal.signal(signal.SIGINT, signal.SIG_IGN)
    while True:
        time.sleep(0.1)


if __name__ == "__main__":
    try:
        sys.exit(main())
    except KeyboardInterrupt:
        sys.exit(0)
