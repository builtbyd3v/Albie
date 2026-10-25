#!/bin/sh
# Builds Albie's sandbox: its own git repo with everything Forge works on (the ML trainer, every CAD part, a planted
# bug, a web page), so voice edits land there and never in this repo. Copied from the live sources, so it's current.
#   sandbox/setup.sh            # create ~/Documents/GitHub/albie-sandbox (ALBIE_SANDBOX=<dir> to put it elsewhere)
#   sandbox/setup.sh --reset    # throw away Albie's edits and rebuild (after a demo)
set -eu
repo=$(cd "$(dirname "$0")/.." && pwd)
dest=${ALBIE_SANDBOX:-$HOME/Documents/GitHub/albie-sandbox}

if [ -e "$dest" ]; then
  [ "${1:-}" = "--reset" ] || { echo "$dest exists. Run with --reset to rebuild it."; exit 1; }
  [ -f "$dest/.albie-sandbox" ] || { echo "$dest has no .albie-sandbox marker, so it isn't ours. Not deleting it."; exit 1; }
  rm -rf "$dest"
fi
mkdir -p "$dest/ml" "$dest/cad"

# ML: the CIFAR-10 trainer the ML desk runs, with its .forge setup. Tracked files only; the 1.2 GB dataset is linked.
# Skipped until the ML desk (ml-examples/) is in this checkout.
if [ -d "$repo/ml-examples/cifar10" ]; then
  (cd "$repo/ml-examples/cifar10" && git ls-files -z . | xargs -0 tar -cf -) | (cd "$dest/ml" && tar -xf -)
  # The dataset is gitignored, so only checkouts that trained have it: link the first one found (this one first).
  for d in "$repo" $(git -C "$repo" worktree list --porcelain | sed -n 's/^worktree //p'); do
    if [ -d "$d/ml-examples/cifar10/data" ]; then ln -s "$d/ml-examples/cifar10/data" "$dest/ml/data"; break; fi
  done
fi

# CAD: every part script (the hub watches this folder through FORGE_CAD_SCRIPT and rebuilds the model on save).
cp "$repo"/hub/cad/parts/*.py "$dest/cad/"

# Sandbox-only: the planted bug, the web page, the README and .gitignore.
cp -R "$repo/sandbox/template/." "$dest/"
touch "$dest/.albie-sandbox"

cd "$dest"
git init -q -b main
git add -A
git -c user.name="Albie sandbox" -c user.email="sandbox@forge.local" commit -q \
  -m "Albie sandbox from gatorhack-dubz $(git -C "$repo" rev-parse --short HEAD)"

cat <<MSG
Albie's sandbox is ready: $dest
Point the hub at it: add these to hub/.env, then restart the hub (it reopens VS Code on the sandbox).
  DEFAULT_PROJECT=sandbox
  FORGE_CAD_SCRIPT=$dest/cad/rocket.py
  FORGE_ML_REPO=$dest/ml
  VSCODE_FOLDER=$dest
MSG
