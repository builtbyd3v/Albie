Paste this into Claude Code: **"Set me up for Albie: follow SETUP.md in this repo. My env file is at <path>."**

# Albie setup (written for a coding agent)

- Goal: this machine runs everything the demo Mac runs: the hub (voice, CAD, ML, Slack, iMessage), Albie's sandbox, the VS Code screen bridge, and the Quest app built in Unity.
- Full setup needs macOS on Apple silicon. On Windows or Linux, do steps 1-4 and 9 only: there is no screen bridge capture and no iMessage.
- Every step is safe to rerun. Run its done-check. Go on only when the check prints what it says.
- **HUMAN** steps need the person. Ask them, wait for their answer, then run the check yourself.
- Never print values from `hub/.env`. Never commit `hub/.env`, `hub/.data/`, or a rebuilt `unity/Assets/_Forge/Core/Main.unity`.
- Shortcut on macOS: `sh dev-setup.sh <env file>` does steps 1-5 and prints the manual steps. Then do steps 5 (HUMAN part) to 10.

## 0. Detect first

Run these. Report the results to the person as one short list. Install what is missing, then run the line again.

| What | Command | Need / fix |
|---|---|---|
| OS + arch | `uname -sm` | `Darwin arm64` for the full setup |
| Package manager | `brew --version` (Windows: `winget --version`) | https://brew.sh |
| Python | `for v in python3.13 python3.12 python3.11 python3; do command -v $v; done` | the first one listed must be 3.11 or newer: `brew install python@3.12`. Call it `$PY` below (macOS's own `python3` is 3.9) |
| Node | `node --version` | 20 or newer: `brew install node` |
| VS Code CLI | `code --version` | VS Code > Command Palette > "Shell Command: Install 'code' command in PATH" |
| Google Chrome | `ls -d "/Applications/Google Chrome.app"` | the screen bridge shares VS Code through real Chrome |
| Unity Hub | `ls -d "/Applications/Unity Hub.app"` | https://unity.com/download |
| Unity | `ls /Applications/Unity/Hub/Editor/6000.6.7f1/PlaybackEngines/AndroidPlayer/SDK` | Unity Hub > Installs > 6000.6.7f1 with Android Build Support (OpenJDK, Android SDK & NDK) |
| adb | `ADB=/Applications/Unity/Hub/Editor/6000.6.7f1/PlaybackEngines/AndroidPlayer/SDK/platform-tools/adb; $ADB version` | else `brew install android-platform-tools`, then `ADB=adb` |
| Repo | `git rev-parse --show-toplevel` in the clone | call it `$REPO` below |
| Env file | the path the person gave | it exists; `grep -c '^XAI_API_KEY=\|^ANTHROPIC_API_KEY=' <path>` prints `2` (names only, never values) |

## 1. Hub env file

```sh
[ -f "$REPO/hub/.env" ] || cp "<env file path>" "$REPO/hub/.env"
```
The shared file uses `${HOME}` paths from the demo Mac layout. Per person: `SLACK_USER_TOKEN` (posts as that person, step 7) and `IMESSAGE_ALLOW` (their own contacts, step 8). Every key is explained in `hub/.env.example`.
Done-check: `ls "$REPO/hub/.env"`. The path check comes at the end of step 4.

## 2. Hub Python env

```sh
cd "$REPO/hub" && [ -x .venv/bin/python ] || $PY -m venv .venv
.venv/bin/pip install -q -r requirements-dev.txt -r ml/requirements.txt
```
Done-check: `cd "$REPO/hub" && BIT_VSCODE_PORT=9 .venv/bin/python -m pytest -q` ends with `passed`. Known fails: `test_github_import` folder-name assert (the clone folder is not named `gatorhack-dubz`), and sometimes `test_agent_live_edits`. `BIT_VSCODE_PORT=9` keeps the tests from opening files in the person's VS Code.

## 3. ML example env (CIFAR-10)

```sh
cd "$REPO/ml-examples/cifar10" && [ -x .venv/bin/python ] || $PY -m venv .venv
.venv/bin/pip install -q -r requirements.txt -e "$REPO/forge-sdk"
[ -d data ] || .venv/bin/python data.py     # downloads CIFAR-10 (~170 MB) into data/
```
Done-check: `.venv/bin/python -c "import torch, forge; print('ok')"` prints `ok`, and `ls data` lists files.

## 4. Albie's sandbox

```sh
[ -f ~/Documents/GitHub/albie-sandbox/.albie-sandbox ] || sh "$REPO/sandbox/setup.sh"
```
It copies the ML trainer, the CAD parts, a planted bug and a web page into their own git repo, and links the dataset and the humanoid's meshes. `sh sandbox/setup.sh --reset` rebuilds it after a demo; `sh sandbox/reset-ml.sh` puts the ML demo back to its 60% baseline after an improve demo. The sandbox's `ml/.venv` links to `ml-examples/cifar10/.venv` (step 3): the trainer runs with it.
Set these in `hub/.env` (replace a line that is already there):
```
DEFAULT_PROJECT=sandbox
FORGE_CAD_SCRIPT=${HOME}/Documents/GitHub/albie-sandbox/cad/rocket.py
FORGE_ML_REPO=${HOME}/Documents/GitHub/albie-sandbox/ml
FORGE_ML_VENV=<$REPO>/ml-examples/cifar10/.venv
VSCODE_FOLDER=${HOME}/Documents/GitHub/albie-sandbox
```
Done-check: `ls ~/Documents/GitHub/albie-sandbox/.albie-sandbox ~/Documents/GitHub/albie-sandbox/ml/data ~/Documents/GitHub/albie-sandbox/ml/.venv/bin/python ~/Documents/GitHub/albie-sandbox/data/unitree_g1` succeeds (missing `ml/.venv`: `ln -s "$REPO/ml-examples/cifar10/.venv" ~/Documents/GitHub/albie-sandbox/ml/.venv`), and the path check in `sh dev-setup.sh` prints `ok: every path exists`.

## 5. Screen bridge + Forge Bit extension (macOS)

```sh
cd "$REPO/screen-bridge" && npm install --silent
[ -x .venv/bin/python ] || $PY -m venv .venv
.venv/bin/pip install -q -r requirements.txt
node "$REPO/vscode-bit/install.js"     # links vscode-bit into ~/.vscode/extensions as forge.forge-bit-<version>
```
Add `"window.titleSeparator": " | "` to the VS Code user settings (Chrome finds the VS Code window by it). Relaunch VS Code.
**HUMAN:** System Settings > Privacy & Security > **Screen Recording**: turn on the terminal app that runs the hub, and Google Chrome. Do the same under **Accessibility** (the bridge resizes VS Code). Quit and reopen that terminal app.
Done-check: `ls -l ~/.vscode/extensions | grep forge.forge-bit` shows a link to `$REPO/vscode-bit`. With VS Code open: `curl -s -o /dev/null -w '%{http_code}\n' localhost:8770/context` prints `200` (read-only), and VS Code's status bar shows the Forge Bit item.

## 6. Run the hub

```sh
sh "$REPO/run-demo.sh"
```
It stops an old hub on :8000 and stale bridge children (`node start.js`, `stream.py`, `signaling.py` hold :8765/:8766), starts the hub (log: `/tmp/forge-hub.log`), opens the sandbox ML project, and wires up a plugged-in Quest. The hub opens VS Code on `VSCODE_FOLDER` and starts the screen bridge itself.
`run-demo.sh` clears the inherited `CLAUDECODE` / `ANTHROPIC_BASE_URL` (the agent SDK hangs on them), so an agent can run it too.
Done-check: `curl -s localhost:8000/health` returns JSON with `"ok": true`; its `screen` field shows the bridge state, `slack` and `imessage` theirs.
WebRTC video needs the Mac and the Quest on a phone hotspot (venue Wi-Fi blocks it). Without it the VS Code panel falls back to JPEG after about 10 s.

## 7. Slack user token (optional; the bot tokens come in the shared env file)

**HUMAN:** a user token belongs to the person who installed the app, so each teammate installs it for themselves. Install the Slack CLI (https://docs.slack.dev/tools/slack-cli). Ask Dev to add you as a collaborator on the Forge Slack app. In `$REPO/hub`: `slack login` (the team workspace), `slack app link -E local` (app id from `slack app list`), `slack install -E local` (installs it for you), then `slack app settings` > OAuth & Permissions > **User OAuth Token** (`xoxp-...`). Paste it into `SLACK_USER_TOKEN` in `hub/.env` yourself. Never use someone else's `xoxp-` token: Albie would post as them. Empty = Albie posts as "Bit".
Done-check: after `sh run-demo.sh`, `/health` shows Slack connected.

## 8. iMessage (optional, macOS only)

```sh
brew install steipete/tap/imsg     # third-party tap; needed for sending only
```
Set `IMESSAGE_ENABLED=1` and `IMESSAGE_ALLOW=<phones or emails of the person's own contacts>` in `hub/.env`.
**HUMAN:** System Settings > Privacy & Security > **Full Disk Access**: turn on the terminal app that runs the hub. Save each allowed person in Mac Contacts (so the name shows, not the number). Allow the Automation prompt on the first send.
Done-check: after `sh run-demo.sh`, `/health` shows `imessage.available: true` (and `send_available: true` with imsg).

## 9. Unity build + Quest

**HUMAN:** sign in to Unity Hub (license). On the headset: Developer Mode on for the headset's **primary** account (a secondary user cannot turn it on). Plug in USB and accept **Allow USB debugging** for this computer. Run Space Setup with exactly **one** table (each table gets its own whistle).
0. **HUMAN, before the first open:** Albie's model is a paid Unity Asset Store pack, not in git (its EULA forbids redistribution). Import Quirky Series Ultimate Pack Vol.1 (or its River Vol.2 alligator) in any Unity project, then copy its `Quirky Series` folder, with the `.meta` files, to `$REPO/unity/Assets/_Forge/Pet/ThirdParty/`. Get `Gator_Model.fbx` (to `unity/Assets/_Forge/Pet/Models/`) and `gator_anims.blend` (to `art/gator/`) from a teammate: they hold the pack's mesh, so they are git-ignored too. Without them Unity still compiles; Albie shows up untextured or missing. Do this before Unity opens the project, or it deletes `Gator_Model.fbx.meta` and the prefabs lose the model.
1. Open `$REPO/unity` in Unity 6000.6.7f1. The first import takes 5-15 min. Keep the Editor window in front while it imports and compiles: Unity compiles only while focused.
2. Menu **Forge > Build Main Scene**. It rewrites `Main.unity`: never commit that file. Run `git checkout -- unity/Assets/_Forge/Core/Main.unity` before every pull.
3. File > Build Profiles > Android > Switch Platform. Build to `unity/Builds/Forge.apk` with `Assets/_Forge/Core/Main.unity` as the only scene.
4. Install and launch:
```sh
$ADB install -r "$REPO/unity/Builds/Forge.apk"
for p in 8000 8765 8766; do $ADB reverse tcp:$p tcp:$p; done
$ADB shell monkey -p com.UnityTechnologies.com.unity.template.urpblank -c android.intent.category.LAUNCHER 1
```
Done-check: `$ADB devices` lists the headset as `device`; `$ADB reverse --list` shows the 3 ports.

## 10. End-to-end check

1. `curl -s localhost:8000/health` returns `"ok": true`.
2. `sh run-demo.sh` printed `ML desk: .../albie-sandbox/ml`.
3. The APK is installed and running, with the 3 ports reversed.
4. **HUMAN:** in the headset, sit at the desk. Albie lands on the desk and greets you. Say "Albie, open my ML project": the ML panels unfold.

## Gotchas

- Voice work (speech-to-speech) is hub-only: edit `hub/agent/`, run `sh run-demo.sh` again. No Unity build.
- Unity compiles only while its window is focused: bring it to the front after every pull or script change.
- WebRTC video needs the Mac and the Quest on a phone hotspot; else the VS Code panel falls back to JPEG after about 10 s.
- `Forge > Build Main Scene` rewrites `Main.unity`: `git checkout -- unity/Assets/_Forge/Core/Main.unity` before a pull.
- Hub tests: always `BIT_VSCODE_PORT=9`.
- Kill stale `stream.py` / `signaling.py` before a hub restart (`run-demo.sh` does).
- Never commit a rebuilt `Main.unity`, `hub/.env`, or `hub/.data/`.
- Meta's network security config blocks plain http except localhost: keep the Quest on USB with `adb reverse`.
