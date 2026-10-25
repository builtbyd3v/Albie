#!/bin/sh
# Puts the ML demo back to its 60% baseline after Albie "improves" it, so the improve flow can be rehearsed again.
# Only touches the sandbox's ml/ folder (CAD, web and bug stay as they are). No retraining, under a second.
#   sandbox/reset-ml.sh               # ~/Documents/GitHub/albie-sandbox/ml (or ALBIE_SANDBOX=<dir>)
# What it undoes, same as improve.undo plus the leftovers undo misses:
#   train.py + .forge/profile.json   back to the committed baseline (git checkout, ml/ only)
#   .forge/improve/                  the proposal, applied or not
#   runs trained with the change     moved to .forge/runs-undone/ (started at or after the plan's applied_at)
#   checkpoints/                     deleted when best.pt beats every baseline run (an improved run wrote it)
set -eu
ml=${ALBIE_SANDBOX:-$HOME/Documents/GitHub/albie-sandbox}/ml
[ -f "$ml/../.albie-sandbox" ] || { echo "$ml is not in an Albie sandbox. Not touching it."; exit 1; }
# ponytail: a process-name check, not the hub's train_proc; good enough while only the hub trains here
if pgrep -f "train.py --epochs" >/dev/null; then echo "A training run is still going. Wait for it, then reset."; exit 1; fi

git -C "$ml" checkout -q -- .
git -C "$ml" clean -fdq .  # new untracked files only; ignored data, .venv, runs and project/ stay

python3 - "$ml/.forge" "$ml/checkpoints" <<'PY'
import json, shutil, sys
from pathlib import Path

forge, ckpt = Path(sys.argv[1]), Path(sys.argv[2])
plan = forge / "improve" / "plan.json"
applied_at = json.loads(plan.read_text()).get("applied_at") if plan.exists() else None
moved = []
if applied_at and (forge / "runs").is_dir():
    for run in sorted((forge / "runs").iterdir()):
        info = run / "run.json"
        started = json.loads(info.read_text()).get("started_at") if info.exists() else run.stat().st_mtime
        if started and started >= applied_at:
            (forge / "runs-undone").mkdir(exist_ok=True)
            shutil.move(str(run), str(forge / "runs-undone" / run.name))
            moved.append(run.name)
shutil.rmtree(forge / "improve", ignore_errors=True)

def best(run):
    try:
        return float(json.loads((run / "summary.json").read_text()).get("best_val_acc") or 0)
    except (OSError, ValueError):
        return 0.0

kept = max((best(r) for r in (forge / "runs").iterdir()), default=0.0) if (forge / "runs").is_dir() else 0.0
try:
    saved = float(json.loads((ckpt / "best.json").read_text())["val_acc"])
except (OSError, ValueError, KeyError):
    saved = None
if saved is not None and saved > kept + 0.005:  # the improved model: the next baseline run writes a fresh one
    shutil.rmtree(ckpt)
print(f"ML reset: moved runs {moved or 'none'}; best remaining run {kept:.1%}; "
      f"checkpoint {'removed' if saved is not None and saved > kept + 0.005 else 'kept'}")
PY
git -C "$ml" status --short .
