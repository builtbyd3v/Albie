#!/bin/sh
# Daily loop: stop the old hub and its screen bridge, start the hub, open the sandbox ML project, wire up the Quest.
#   sh run-demo.sh                 # hub on :8000, log in /tmp/forge-hub.log
#   PORT=8100 SCREEN_BRIDGE=0 NO_ADB=1 sh run-demo.sh   # a second hub for testing, no bridge, no headset
set -eu
repo=$(cd "$(dirname "$0")" && pwd)
port=${PORT:-8000}
log=${HUB_LOG:-/tmp/forge-hub.log}
sandbox=${ALBIE_SANDBOX:-$HOME/Documents/GitHub/albie-sandbox}
adb=${ADB:-/Applications/Unity/Hub/Editor/6000.6.7f1/PlaybackEngines/AndroidPlayer/SDK/platform-tools/adb}
[ -x "$adb" ] || adb=$(command -v adb || true)

old=$(lsof -ti "tcp:$port" -sTCP:LISTEN || true)
[ -n "$old" ] && { echo "stopping the hub on :$port ($old)"; kill $old; sleep 2; }
if [ "${SCREEN_BRIDGE:-1}" != "0" ]; then
  # Stale bridge children hold :8765/:8766, and the new bridge then exits.
  pkill -f "node start.js" || true; pkill -f screen-bridge/stream.py || true; pkill -f screen-bridge/signaling.py || true
fi

cd "$repo/hub"
SCREEN_BRIDGE_DIR="$repo/screen-bridge" nohup .venv/bin/uvicorn main:app --host 0.0.0.0 --port "$port" >> "$log" 2>&1 &
echo "hub starting on :$port (log: $log)"
i=0
until curl -sf "localhost:$port/health" >/dev/null; do
  i=$((i + 1)); [ $i -gt 60 ] && { echo "no /health after 60 s; see $log"; exit 1; }; sleep 1
done
echo "hub ok"

curl -sf -X POST "localhost:$port/ml/project" -H 'content-type: application/json' \
  -d "{\"path\": \"$sandbox/ml\"}" >/dev/null && echo "ML desk: $sandbox/ml" || echo "WARN: /ml/project failed (see $log)"

[ "${NO_ADB:-0}" = "1" ] && exit 0
if [ -z "$adb" ] || ! "$adb" devices | grep -q "	device$"; then echo "no headset on USB: plug it in, then rerun"; exit 0; fi
for p in 8000 8765 8766; do "$adb" reverse "tcp:$p" "tcp:$p"; done
"$adb" shell monkey -p com.UnityTechnologies.com.unity.template.urpblank -c android.intent.category.LAUNCHER 1 >/dev/null
echo "Quest: ports reversed, app launched"
