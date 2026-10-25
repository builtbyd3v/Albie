# Forge hub

FastAPI on the laptop: REST + one `/ws` event bus. Unity is a view; the hub owns the state.
Modules: **CAD** (see `../CAD_PLAN.md`), the **agent** (pet; see [Agent module (pet)](#agent-module-pet)), **messaging** (Slack; see [Messaging (Slack)](#messaging-slack)), **screens** (the screen bridge child process; see [One process](#one-process)), the **desk** extras (timers, notes, movie; see [Desk](#desk-timers-notes-movie)) and **ML** (`ml/`, mounted in `main.py`; see `ml/README.md`).

## Setup (Python 3.11)

```powershell
cd hub
py -3.11 -m venv .venv
.venv\Scripts\activate
pip install -r requirements-dev.txt   # build123d, rocketpy, fastapi, pytest...
```

Never `pip install cadquery` into this env: its OCP build breaks build123d's imports.

## Run

```powershell
uvicorn main:app --host 0.0.0.0 --port 8000     # no --reload (it restarts the CAD worker)
```

- `GET /health` once the first design is built (a few seconds on startup).
- Open `cad/parts/rocket.py` in VS Code: **saving the open design's script regenerates it** and pushes `cad.model_updated`. A broken save keeps the last good model and emits `cad.regen_failed` with the error line.
- Try it: `curl -X POST localhost:8000/cad/params -H "Content-Type: application/json" -d "{\"fin_count\": 4}"`
- Data (versions, GLBs) lives in `hub/.data/` (gitignored). Delete `.data/cad` to start fresh. Env: `FORGE_DATA_DIR`, `FORGE_CAD_SCRIPT` (the design opened the first time; after that the hub reopens the last one).

## One process

`uvicorn main:app` is the only thing to start on the laptop. Its lifespan brings up, in order: the CAD worker, Slack
Socket Mode (plus a history backfill, see Messaging), and the **screen bridge**: `hub/screens/` runs `npm start` in
`../screen-bridge/` as a child process group (the `prestart` installs the VS Code extension, then `start.js` starts
`signaling.py` on :8766 and the JPEG fallback `stream.py` on :8765 and launches Chrome sharing the VS Code window).
Its output shows up in the hub log as `[screen-bridge] ...`. On Ctrl+C the hub sends the whole group SIGINT (SIGKILL
after 3 s), so Chrome and both python servers die with it; if the child dies on its own it is restarted after 2, 5 and
10 s, then given up (`screen.status {state: "error"}`).

| | |
|---|---|
| `SCREEN_BRIDGE=0` | don't run it (default `1`). It is also skipped, with one warning, when `node` is not on PATH or the directory has no `start.js`. |
| `SCREEN_BRIDGE_DIR` | where `start.js` lives (default `../screen-bridge`). |
| `GET /health` | `screen: {enabled, state, source}`; `GET /screens/status` adds `detail`, `fps`, `pid`, `restarts`. |
| `/ws` | `screen.status {state: starting\|sharing\|stopped\|error, source?, detail?}`, sticky (replayed on connect). |

Everything else the screen bridge needs is unchanged (its `.venv`, `npm install`, the `window.titleSeparator` setting:
see `screen-bridge/start.js`). Running `npm start` by hand still works: set `SCREEN_BRIDGE=0` so the hub doesn't start a second copy.

## Designs (the CAD library)

Every `*.py` in `cad/parts/` (except `_`-files) is a design; its file name is its id. Today: `rocket` (the hero),
`quadcopter`, `gearbox`, `humanoid` (Unitree's G1, from its robot description). Each keeps its own history (undo stays inside it); version numbers are unique across them.

- `GET /cad/parts` lists them; `POST /cad/part {"part": "gearbox"}` opens one; `POST /cad/preset {"name": "Hexacopter"}`
  applies a named preset to the open one. Everything else (`/cad/params`, undo, ...) acts on the open design.
- **Add a design:** copy `cad/parts/_template.py` to `cad/parts/<name>.py`. It shows up in `GET /cad/parts` (and the
  Designs menu in Unity) straight away; open it and save to regenerate. A part script defines `PARAMS` and
  `build(params) -> BuildResult`, optionally `TITLE`, `DESCRIPTION`, `PRESETS`, `analyze(result, masses)` (metrics:
  give `min`/`max` for a gauge) and `simulate_flight` (the Fly button). Mass, size and part count come free.
- **Optional extras for the CAD UI** (all in part millimetres, Z up; see `_template.py`): `GLYPH = "rocket"` (the
  Designs menu icon: rocket | drone | gears | box). `Param(..., group="Fins", help="one short line", parts=("fin_*",),
  dimension="fin_span")` puts the parameter in a panel section (default "General"), says which parts it shapes and
  which dimension callout it changes (defaults: parts named like it, a dimension with its name); `step` is worked out
  from the range if you give none. `analyze()` can return `Analysis(metrics=..., markers=[Marker(...)],
  dimensions=[Dimension(...)], explode=Explode(axis, [ExplodeMove(part, direction, mm, order)]), demo=Demo(kind, label,
  spinners=[Spinner(..., radius_mm=)], blocked=, reveal=[...]), mass_groups={"Gears": ["pinion", "gear"]},
  verdict=Verdict(status, headline, sentence, metric), constants=[Constant(label, value, note)])`. Leave any out and
  `cad/scene.py` makes a generic one: the CG marker, overall width/depth/height, parts spread out from the centre, mass
  by top-level assembly, a demo of "launch" with `simulate_flight` (else none), a verdict from the worst metric, no
  constants. They land in the analysis block (`cad.model_updated`) in the GLB's frame: CAD_PLAN section 5, round 4.
- **Designs from meshes** (the humanoid): a mesh pack in `cad/data/<name>/` (`display.glb`, `outline.npz`, `parts.json`,
  made offline by a script in `cad/tools_offline/`) loads once per worker with `cad.meshes.pack(folder)`; `place(key,
  transform, label, rgb)` puts a piece where you want it (a surfaceless face sharing the cached mesh). Give mesh parts a
  `FixedMass(mass_g, cg_mm)` (no volume). `cad.robot.load(urdf)` reads a URDF: forward kinematics, limits, ratings and
  `static_torques`. Nest assemblies along the kinematic chain at identity and place the leaves, and
  `simulate_motion(result, masses) -> Motion` can animate the joints (`GET /cad/motion`, the Walk button). Licences and
  sources sit next to the data (`NOTICE.md`).
- Keep a rebuild under about a second or two: build123d's `extrude(both=True)` and `RectangleRounded` are slow;
  `Solid.extrude` and `SlotOverall` are fast (see `gearbox.py`, `quadcopter.py`). The worker stops a build at 8 s.
- `GET /cad/drawing.png?v=N` (or `.svg`): a version's engineering drawing (`cad/drawing.py`, A3, 2048 x 1448): three
  views + iso, dimensions, parts list, title block; parts changed since its parent in orange. Made once (~1 s), cached.
  Mesh designs take a mesh path (visible edges and silhouettes, no dashed hidden lines), about 6 s for the humanoid.
- `GET /cad/files?v=N`: a version's **files for a supplier** (`cad/package.py`): `<part>_vN.step` (the CAD model,
  AP214, mm), `.stl` (3D printing), `_drawing.pdf` (the drawing, no orange) and `_parts.csv` (part, material, qty,
  mass) -> `{part, version, files: [{kind: step|stl|drawing|parts, name, title, bytes}]}`; `GET /cad/files/{name}?v=N`
  downloads one. Made in one worker job on first request (a few seconds), cached in the version's `package/` folder.
  Bit attaches them to Slack drafts (`messages_send` `attach`, see "Bit runs the workspace").

## Test

```powershell
pytest            # a few minutes: CAD (geometry, RocketPy cross-check, designs, library API, watcher), agent, messaging, screens, VS Code
```

## Mocks for Unity (hub off)

```powershell
python -m cad.make_mocks       # -> hub/.data/mocks/cad/parts.json, <part>/vN/, regen_failed.json
```

Copy that folder to `unity/Assets/_Forge/Mocks/cad/` (see the CAD README there). The rocket's four states are the demo story: green 1.57 → "smaller fins" red 0.84 → "fourth fin" amber 1.25 → "20% longer" green 1.60. The quadcopter goes clear → "7-inch props" hit → longer arms clear; the gearbox goes 3:1 → 6:1 (undercut pinion) → heavy duty; the humanoid stands → squats so deep its ankles run out of range and it tips back → arms forward balance it → 5 kg with the forearms out (wrists at 90%). The humanoid's states also carry `motion.json` (its GLBs are ~4.8 MB each).

## CAD module map

| File | What |
|---|---|
| `cad/parts/rocket.py` | The rocket: `PARAMS`, `build(params)`, presets, `analyze` (Barrowman stability). The file on the main screen. |
| `cad/parts/quadcopter.py`, `gearbox.py` | Two more designs (prop clearance / thrust-to-weight; ratio / pinion rim, own involute gears). |
| `cad/parts/humanoid.py` | The Unitree G1 posed by 10 parameters: balance and joint loads ("Will it stand?"), Walk from a recorded lap of Unitree's trained policy. |
| `cad/parts/_template.py` | Copy me: a new file is a new design. |
| `cad/part_api.py` | The only Forge types a part script imports (`Param`, masses, `Metric`, `BuildResult`, rocket specs). |
| `cad/meshes.py` | Mesh packs: pieces as surfaceless faces (cached per worker), their outline meshes for drawings. |
| `cad/robot.py` | A robot from its URDF: kinematics, limits, ratings, static joint torques. |
| `cad/data/unitree_g1/` | The G1's URDF, meshes, walk clip, both BSD-3 licences, `NOTICE.md`. |
| `cad/tools_offline/` | One-off, dev-only scripts that made that data (their own venvs; see each docstring). |
| `cad/export.py` | GLB + cad-vision schema 1.0 metadata, validated with cad-vision's loader rules. |
| `cad/analysis.py` | Mass, CG, generic metrics (mass, size, parts), and the Barrowman helpers rockets use. |
| `cad/scene.py` | What Unity draws on the model: markers, dimensions, explode vectors, mass breakdown, demo (part's or generic), in the GLB's frame. |
| `cad/flight.py` | RocketPy launch-to-apogee sim on an Estes C6 (`cad/data/estes_c6.eng`), then parachute descent to landing; fixed-rate samples + events. |
| `cad/worker.py` | Build process with timeout and respawn; reloads the part script on every job. |
| `cad/service.py` | The design library: version tree per design on disk, one regen at a time, events, undo/reset, folder watcher. |
| `cad/package.py` | The supplier files of a version: STEP, STL, drawing PDF, parts CSV (`GET /cad/files`, Slack attachments). |
| `cad/router.py` | `/cad/*` endpoints (CAD_PLAN section 5). |
| `cad/tools.py` | Albie's CAD tools (JSON schemas + `call_tool`) for the agent module; `attachments()` for Slack drafts. |

## Agent module (pet)

Adrian's module: the gator pet answers questions about a repo and runs coding tasks through the
**Claude Agent SDK** (`claude-agent-sdk`). Protocol: [`docs/PET_AGENT_CONTRACT.md`](../docs/PET_AGENT_CONTRACT.md).

- REST: `GET /agent/projects`. There is no typed REST fallback: a typed question is `agent.ask` on the socket.
- WebSocket: **`/ws`**, the hub's one socket (Unity's ForgeNet). `main.py` accepts it, attaches the `EventBus`
  (CAD, Slack, screen events) and runs the agent session on it; the agent handler ignores message types it
  doesn't own, and other modules register theirs in `agent.ws.client_handlers` (`messages.focus`).
- `/health` adds `fake_agent`, `stt` and `agent_sockets`.
- `agent/` imports no CAD code; `tests/agent/` builds a tiny agent-only app (conftest) so it tests without build123d/rocketpy.

```
agent/
  app.py      load_env() (hub/.env) and health_fields() for main.py
  events.py   pydantic models for every agent WS message + builders + PET_STATE_FOR_EVENT
  bus.py      broadcast to all /ws sockets + per-turn Emitter (pet.state companions, path redaction)
  ws.py       the /ws session: hello, agent.ask, agent.cancel, stt.*, tts.stop, crew.roster, workspace.mode, binary audio
  router.py   GET /agent/projects
  runner.py   real turns via ClaudeSDKClient (qa = read-only, code = edits on a pet/* branch); crew tool allowlist
  crew.py     Albie's crew: agent types, roster, parallel member turns, the lead-only crew tools
  wake.py     "Hey Albie" / "<Crew name>, ..." routing for always-on wake turns
  speech.py   live voice sessions by pet id: speak(text) and note(text) for other modules (digest, focus)
  workspace.py Bit opens views: ui.mode out, workspace.mode back (the ui_open_view tool; CAD/Slack tools use ensure)
  fake.py     FAKE_AGENT=1 scripted turns (same signature)
  turn.py     auto-mode classifier + TurnControl (cancel/interrupt)
  stt.py      SpeechToText: NoneSTT, WhisperSTT (faster-whisper, optional)
  voice.py    VOICE_PROVIDER=grok|fake: one speech-to-speech session per socket (stt.* in, tts.* + PCM out)
  assets/     fake_voice_24k.pcm (regenerate: scripts/make_fake_voice.py)
config/projects.yaml   project_id -> repo path (paths never leave the server)
tests/agent/           pytest, fake mode, agent-only test app (never imports main.py)
scripts/ws_smoke.py    prints the event stream from a running hub
.env.example           copy to hub/.env (main.py loads it)
```

### Install

The deps are in `requirements.txt` / `requirements-dev.txt` (above). Agent only, without CAD (macOS/Linux):

```bash
cd hub
python3.11 -m venv .venv
.venv/bin/pip install fastapi "uvicorn[standard]" pydantic claude-agent-sdk pyyaml python-dotenv pytest httpx websockets
cp .env.example .env
# optional voice input: .venv/bin/pip install faster-whisper   then STT_PROVIDER=whisper
```

The SDK ships its own Claude Code CLI binary; no separate install.

### Run

```bash
FAKE_AGENT=1 .venv/bin/uvicorn main:app --host 0.0.0.0 --port 8000   # the hub; no API key, no model calls
curl localhost:8000/health
```

(Add `SCREEN_BRIDGE=0` to leave Chrome and the screen servers out while you only work on the pet.)

Fake turns: thinking, 3 cumulative partials, answer_done. A code ask gives task_started, 3 progress
events, task_complete (branch `pet/fake-123`). The text `error` gives agent.error. `FAKE_AGENT_DELAY`
(seconds, default 0.6) sets the pacing.

Real mode: `ANTHROPIC_API_KEY` in `hub/.env` (or the shell) and `FAKE_AGENT=0`.
`AGENT_MODEL` (default `claude-sonnet-5-5`), `AGENT_QA_EFFORT` (`low`), `AGENT_CODE_EFFORT` (`low`).

- **qa**: tools `Read, Grep, Glob` only, `permission_mode="dontAsk"`, `cwd` = repo, short spoken persona.
- **code**: the hub checks out `pet/<slug>-<id>` first (skipped, and said so, if not a git repo), then runs
  with `Read, Grep, Glob, Edit, Write, Bash`. `git push/checkout/switch/reset`, `rm -rf`, `sudo` are denied.
  Changes are left **uncommitted** on that branch for review.
- **auto**: imperative verb first (fix/add/change/make/refactor/...) → code, else qa. No `mode` → the project's `default_mode`.
- Start the hub from a normal terminal, **not inside a Claude Code session** (inherited `CLAUDECODE` /
  `ANTHROPIC_BASE_URL` make the SDK's CLI subprocess hang). No `--reload` in real mode either.

### Voice (Grok speech-to-speech)

`VOICE_PROVIDER=grok` + `XAI_API_KEY` gives each Unity socket its own Grok Voice session
(`grok-voice-3.0`, raw websockets). Unity keeps sending what it already sends; the hub answers with speech:

- `hello { audio_out_rate? }` -> `voice.ready { provider, sample_rate }` (owner socket only); the Grok socket is pre-warmed
- `stt.start` -> barge-in (stop speech) + new turn; binary PCM16 16 kHz -> Grok; `stt.stop` -> commit + `response.create`
- user transcript -> `stt.partial` / `stt.final { voice: true }` (Unity must not `agent.ask` it; the hub also ignores an echo)
- speech -> `tts.start { utterance_id, sample_rate, encoding: "pcm16" }` -> binary frames -> `tts.end` (or `tts.stop` on barge-in)
- spoken text -> `agent.partial_answer` / `agent.answer_done`, so captions work unchanged
- `tts.stop` from the client stops the current reply

Grok also gets Albie's fast tools (CAD, VS Code, Slack drafts, the crew) and `delegate` for the Claude runner. Unity's
`workspace.mode {mode}` tells the hub which view is open: Grok gets a one-line note about it, and the crew tools
only work while the CODE view is open (they answer an error otherwise). `VOICE_PROVIDER=fake` speaks a canned reply from
`agent/assets/fake_voice_24k.pcm` with no key, for Unity testing:

```bash
VOICE_PROVIDER=fake FAKE_AGENT=1 SCREEN_BRIDGE=0 .venv/bin/uvicorn main:app
```

### Say a gator's name (wake turns)

Unity's `WakeListener` keeps the mic on and sends each speech segment as `stt.start {wake: true}`. `agent/wake.py`
only answers segments that name a gator ("Hey Albie, ...", "Tester, run the tests"); a name on its own
("Hey Scout") comes back as `wake.listen` and that gator takes the next sentence. Anything else is dropped
without a caption (and deleted from the Grok conversation). In voice mode every segment is transcribed by xAI.

### Crew (extra gators, one per agent type)

The user hatches helper gators in Unity (the egg on the desk, the N key, or "Albie, hatch a tester"). Each one is
its own Claude runner with a role, a system prompt and a tool kit (`agent/crew.py` `TYPES`): Scout, Builder, Fixer,
Tester, Reviewer, Scribe, Shipper, Designer and Sentinel. Albie leads: Grok and Albie's Claude runner get four
lead-only tools, `spawn_agent`, `assign_task` (hatches the gator first if needed), `dismiss_agent` and
`crew_status`. Crew members work in parallel. A code task runs in its own git worktree (`AGENT_WORKTREE_DIR`,
default `<repo>-crew/`) on `pet/<agent>/<task>`, started from the main checkout's current state (uncommitted edits
included), so nobody switches the main checkout's branch. When one finishes, its changes are applied to the main
checkout as uncommitted edits (what VS Code shows) and the worktree is removed; if they conflict, the worktree and
branch are kept and named in the summary. Albie says so out loud (voice mode) and the gator shows the summary in its bubble.
Unity is the source of truth for who exists (`crew.roster`); an `agent.ask` whose `pet_id` is a crew member runs
that member's turn without touching Albie's.

### Bit runs the workspace (views, CAD, files to Slack)

From the desk with nothing open: "Bit, open the quadcopter. Expand it. Now the rocket, show me it flying. Make the
shaft 10% longer. Draft a message to Adrian with all the files for the supplier. Make it shorter. Send it." Each
sentence is one or two of Bit's fast tools, called straight from Grok (no `delegate`), so each answers in about a
second. The contract is `docs/PET_AGENT_CONTRACT.md` Addendum H; `tests/test_bit_script.py` runs that whole script.

- **Views.** `ui_open_view {view: cad|code|ml|messages|none}`. The hub sends `ui.mode {mode}`; Unity opens the view
  (only at the desk) and answers with `workspace.mode {mode, reason?}`, so Bit's result says what really happened
  (`reason: "not_at_desk"` -> "sit down at the desk first"). `agent/workspace.py` waits 1.5 s for that answer, and only
  0.4 s while the Unity build in use never answers (one without the handler), so no tool stalls on it.
- **CAD tools open the CAD view themselves** (`cad_open_part`, `cad_set_params`, presets, undo/reset, `cad_view`,
  `cad_demo`, `cad_restore_version`), so you never have to say "open CAD" first. If it can't open, the change still
  happens and the result carries `view_error`. New: `cad_open_part` takes what people say ("the drone", "gear box");
  `cad_view` also does `expand` (the model big in front of you), `markers`, `dimensions` and `tab`; `cad_demo {run}`
  presses the design's Fly / Hover / Run (the rocket's flight numbers come back too); `cad_list_versions`,
  `cad_restore_version {version}`; `cad_export_files` (the supplier files). Unity's buttons are pressed with
  `cad.action {action, on?, tab?, part, version, seq}` (not sticky).
- **Slack with files.** `messages_send {..., attach: [all|step|stl|drawing|parts], open_messenger?}` puts the open
  design's current files on the draft (`Draft.attachments`: name, title, kind, bytes; the hub keeps the paths) and, with
  files, opens the messenger first so the draft shows in its conversation. `messages_edit_draft {text}` rewords the open
  draft in place (same id, same files). Send (the card, or "send it" -> `messages_confirm`) uploads text and files in one
  `files_upload_v2` under your name. If Slack refuses, the draft stays open for another try. **Needs `files:write`**
  (and `files:read`, for the message's ts): added to `manifest.json`, so run `slack run` once in `hub/` and re-copy the
  User OAuth Token into `SLACK_USER_TOKEN` (README "Messaging"). Drafts now expire after 5 minutes, not 2.

### Test

```bash
FAKE_AGENT=1 .venv/bin/python -m pytest tests/agent -q    # fake mode, a few seconds, no CAD deps
```

(Plain `pytest` runs everything in `pytest.ini`'s `testpaths`: CAD, `tests/`, VS Code.)

### Smoke test against a running hub

```bash
.venv/bin/python scripts/ws_smoke.py "where is the websocket handler?" --mode qa
.venv/bin/python scripts/ws_smoke.py "add a docstring to agent/speech.py" --mode code
.venv/bin/python scripts/ws_smoke.py "hi" --url ws://10.0.0.5:8000/ws
```

### How Unity connects

Through ForgeNet's socket, `/ws` (hub address on the ForgeNet object or PlayerPrefs `Forge.HubUrl`); set
`unity/Assets/StreamingAssets/pet_config.json` → `eventSource: "hub"` (commit it as `"mock"`):

| Where Unity runs | hub address |
|---|---|
| Editor / desktop on the same laptop | `ws://127.0.0.1:8000/ws` |
| Quest on the same Wi-Fi | `ws://<laptop LAN IP>:8000/ws` (macOS: `ipconfig getifaddr en0`); bind `0.0.0.0`, allow it through the firewall |
| Quest over USB | `adb reverse tcp:8000 tcp:8000` (the screen bridge does this for you), keep `ws://127.0.0.1:8000/ws` |

The client sends `hello` first, then `agent.ask` / `agent.cancel` / `stt.*` / `workspace.mode` plus binary PCM16 frames
(mono, 16 kHz, ~100 ms chunks). Every event goes to every `/ws` socket; filter by `pet_id`.
The proposed `agent.*` / `stt.*` events are preceded by an existing-contract `pet.state` whenever the
pet's mode changes; `AGENT_EMIT_PET_SAY=1` also mirrors `agent.answer_done` as `pet.say`.

## Messaging (Slack and iMessage)

Albie reads **your** Slack inbox and replies **under your name**, never without a confirmation. The app is defined by
`hub/manifest.json` (bot user, bot + user scopes, bot + user events, Socket Mode, Messages tab); `hub/.slack/` makes
`hub/` a Slack CLI project. Socket Mode means the hub opens an outbound WebSocket to Slack, so there is no public
URL, tunnel or webhook. There is no fake or mock path: test against the workspace.

Setup with the Slack CLI (https://docs.slack.dev/tools/slack-cli), from `hub/`:

```bash
slack login                      # once per machine, into the team workspace
slack install -E local           # creates "Bit (local)" from manifest.json and installs it (bot + user scopes)
slack run                        # start hook (scripts/slack_cli_start.py) writes SLACK_BOT_TOKEN + SLACK_APP_TOKEN to hub/.env, then exits
slack app settings               # OAuth & Permissions > "User OAuth Token" (xoxp-...) -> SLACK_USER_TOKEN in hub/.env (the CLI never hands it out)
```

Changed scopes or events? Edit `manifest.json`, run `slack run` again (it syncs the manifest and reinstalls), and
re-copy the user token if user scopes changed. Another checkout of the repo: `slack app link -E local` (app id from
`slack app list`); `.slack/apps.dev.json` is gitignored on purpose, it is per-checkout. Manual fallback:
api.slack.com/apps with the same manifest.

`hub/.env` after setup:

```
SLACK_BOT_TOKEN=xoxb-...        # written by `slack run`: the app's identity
SLACK_APP_TOKEN=xapp-...        # written by `slack run`: Socket Mode
SLACK_USER_TOKEN=xoxp-...       # you: read your inbox, post as you. Empty = Albie sees only invited channels and posts as "Bit"
SLACK_USER_ID=U...              # optional, derived from the user token; only needed without it
SLACK_DEFAULT_CHANNEL=#forge-team
```

### Optional iMessage (macOS)

Off by default. With `IMESSAGE_ENABLED=1` on macOS, the hub reads `~/Library/Messages/chat.db` read-only (texts from
the last `IMESSAGE_RECENT_DAYS`, default 3) and adds those conversations to the shared inbox and Albie's messaging tools.

**Privacy allowlist (required).** `IMESSAGE_ALLOW` is a comma-separated list of phone numbers (any format: `(352)
555-0123`, `352.555.0123` and `+1 352 555 0123` are the same; a bare 10-digit number is US, `+1`) or emails (case does
not matter). Only 1:1 chats with those handles are read, filtered in the SQL query itself, so no other text ever reaches
the hub, events, headset, Albie, the digest or logs. Group chats are always hidden, even when every member is allowed.
Sending is refused for anything else before `imsg` runs. Enabled with an empty or missing `IMESSAGE_ALLOW` loads
nothing (`/health`: `enabled, no allowed contacts (set IMESSAGE_ALLOW)`). Logs carry counts only.
It starts in the background, so it never delays Slack, screens or `/health`. Sending needs the `imsg` CLI, which the
hub never installs: without it iMessage is read-only, and the startup log and `/health` (`send_error`) say to run
`brew install steipete/tap/imsg` (a third-party tap) yourself. On macOS 26, `imsg` verifies sends against the Messages database because
AppleScript can report success without a real outgoing row.

In the Unity MSG mode, mentions and unread notifications appear on the left and Slack (`messages`) in the center. Only
when `/health` reports `imessage.available` does `MessagesLayout` add the iMessage panel (`messages-imessage`) in the
center and move Slack to the right. Selecting an unread item opens its conversation in the matching provider panel.

The first startup also checks whether the hub can read Messages. If macOS blocks access, grant **Full Disk Access**
to the app running the hub (for example Terminal, iTerm, or VS Code) in **System Settings > Privacy & Security >
Full Disk Access**, then restart the hub. macOS requires this explicit user action; `/health` returns the setup
instructions and a direct link to that Settings pane. The first send may prompt for **Automation** permission to
control Messages; allow it. Set `IMESSAGE_CLI` only when you intentionally use a custom `imsg` executable.
`/health` exposes `imessage.available` and `imessage.send_available` separately.

Albie uses the same confirmation flow for both providers: `messages_send` creates a draft, then only `messages_confirm`
after a clear user confirmation sends it. For an iMessage reply, pass the source message's `ts` as `reply_to_ts`; for
a chosen conversation, pass its `im:<chat-guid>` id as `channel`. To start a new draft, set `via: "imessage"` and
name an existing conversation. The current CLI adapter targets existing chats; it does not start a brand-new chat.
`IMESSAGE_DIGEST=false` keeps texts out of the spoken sit-down digest while leaving their unread marks intact.

Bot + app tokens empty = Slack is disabled, but iMessage (when enabled) can still power supported inbox and draft endpoints.
Slack-specific endpoints return 503. `/health` reports Slack and iMessage separately.

Live check without Unity:

```bash
.venv/bin/python scripts/slack_smoke.py                              # listen: print every inbox message
.venv/bin/python scripts/slack_smoke.py --draft "#forge-team" "hi"   # write a draft, post nothing (notify.draft)
.venv/bin/python scripts/slack_smoke.py --send  "#forge-team" "hi"   # dev shortcut: draft + send with no confirmation
.venv/bin/python scripts/slack_smoke.py --digest                     # what Albie would say on sit-down
.venv/bin/python scripts/slack_smoke.py --channels                   # the panel's channel list (names, kinds, unread)
.venv/bin/python scripts/slack_smoke.py --history "#forge-team" 10   # the last 10 top-level messages of one conversation
```

**The panel.** Unity's Slack-like panel is a channel list (`GET /messages/channels` once, then `notify.channels`, which
is sticky and pushed whenever a count or `last_ts` changes), the selected conversation's history
(`GET /messages/history?channel=&limit=&before=` for the page, `notify.message` for what arrives, `POST
/messages/channels/{id}/seen` when it is open) and the composer (the draft below). The hub keeps the last 200 messages
of every conversation and a seen mark per conversation, so a second open is instant and the unread counts survive a restart.

**Sending is always two steps.** `messages_send` (tool) or `POST /messages/drafts` writes a `Draft` and emits
`notify.draft`; Unity shows it in the composer with Send / Cancel. The user may edit it there: `PATCH
/messages/drafts/{id}` re-emits `notify.draft` with the same `draft_id` and `edited: true`. Only `POST
/messages/drafts/{id}/send` (the button) or the `messages_confirm` tool ("send it") posts it, with your token, then
`notify.sent`. One open draft at a time: a new one supersedes it (`notify.draft_cancelled`, reason `superseded`);
unsent drafts expire after 300 s (`expired`, an edit restarts the clock); Cancel gives `cancelled`. The composer
clears on `notify.sent` or `notify.draft_cancelled`, whichever path confirmed. A draft's `channel` is an id, a `#name`,
or a person's name: "tell Hudson ..." drafts into their DM (opened with `conversations.open` if there is none yet).

| | |
|---|---|
| `GET /messages/channels` | `{channels: ChannelSummary[]}`: Slack and available iMessage conversations, with provider `source`; DMs / group DMs include unread counts, sorted by `last_ts` desc. iMessage IDs start with `im:`. |
| `GET /messages/history?channel=&limit=50&before=` | `{channel, channel_name, kind, messages: SlackMessage[], has_more}`: Slack or iMessage history oldest -> newest; iMessage uses `chat.db` and `im:<chat-guid>`. |
| `POST /messages/channels/{id}/seen` | the conversation is open in the panel -> `{ok, unread: 0}`; `notify.channels` follows |
| `GET /messages/users/me` | `{user_id, name, display_name}`: who the hub posts as |
| `GET /messages/latest?n=5` | newest inbox messages across every conversation (`SlackMessage` includes `source: slack\|imessage`) |
| `POST /messages/drafts {text, reply_to_ts?, channel?, thread_ts?}` | write a draft -> `Draft {draft_id, channel, channel_name, text, thread_ts, reply_to, edited, attachments, source}` (`attachments`: files Albie put on it, `[{id, name, title, kind, bytes, source}]`, uploaded with the text on send; `[]` for typed drafts); `reply_to_ts` picks the channel and thread of the message being answered; `channel` is an id, `#name` or a person's name (their DM), omitted = that channel or `SLACK_DEFAULT_CHANNEL` |
| `PATCH /messages/drafts/{id} {text?, channel?, thread_ts?}` | the user edited the draft -> the `Draft` again, same `draft_id`, `edited: true`, `notify.draft` re-emitted; 404 if it is gone, 422 for empty text |
| `POST /messages/drafts/{id}/send` | post it as you -> `{ts}` (with files: one `files_upload_v2`, the text as its comment; `ts` from `files.info`, `""` if Slack hasn't shared it yet); 404 if the draft is gone (sent, cancelled, expired, superseded); if Slack refuses it (502), the draft stays open so Send can try again |
| `POST /messages/drafts/{id}/cancel` | discard it -> `{ok}` |
| `POST /messages/digest {pet_id?}` | Unity calls it when `DeskPresence` goes Seated: everything unread in every conversation -> `notify.digest` (newest 4), `pet.say` with the one-line summary, spoken on the live voice session (`agent/speech.py`). Marks every conversation seen. |
| `POST /messages/focus {pet_id?, channel, ts}` | "Reply with Albie" on a message (same as the ws message `messages.focus`): Albie says "<who> in <#channel or a DM> says: <text>. Want me to answer?" (voice, else `pet.say`), and the next `messages_send` without a target replies in that thread. 404 if the message isn't one of that conversation's last 200. |
| `/ws` events | `notify.message` (each message, your own included with `is_me`), `notify.channels {channels}` (sticky, debounced 300 ms), `notify.draft`, `notify.sent {draft_id, ...}`, `notify.draft_cancelled {draft_id, reason}`, `notify.digest`; client -> hub: `messages.focus {pet_id, channel, ts}` |
| Albie's tools | `messages_read_latest {n?}`, `messages_list_channels {}`, `messages_read_channel {channel, n?}`, `messages_send {text, reply_to_ts?, channel?, thread_ts?, via?, attach?, open_messenger?}` (draft only; no target = the message in focus; a person's name = their DM; iMessage replies route by source; `via: imessage` starts a new iMessage draft; `attach`: the open CAD design's files on a Slack draft, `all` or `step`/`stl`/`drawing`/`parts`; with files the messenger opens first), `messages_edit_draft {text}` (reword the open Slack draft, keeps its files), `messages_confirm {send, draft_id?}` |
| Rate limits | one `Retry-After` is waited out per request (per run for the backfill and the 60 s channel refresh); the next one is a `503 {"detail": {"error": "ratelimited", "retry_after"}}` and one log line |

**Startup backfill and the channel list.** Right after Socket Mode connects, a background task caches the workspace's
users (`users.list`, once), lists the conversations you are in (`conversations.list`, every type, archived out, members
only, up to 1000) and reads each one's history since its seen mark (the last two hours if there is none), 20 per
conversation, into the store with the same filtering as live events but **without** `notify.message`. So the sit-down
digest counts what arrived while the hub was off, and Unity sees no flood; the first `notify.channels` goes out at the
end. The list is refreshed every 60 s (a new channel, a left one, a renamed topic -> `notify.channels`). One rate limit
is waited out per run, a second one stops it; a missing scope skips that conversation. One summary line in the log.

Map: `messaging/settings.py` (env), `store.py` (last 200 messages per conversation, merged by ts; a seen mark per
conversation plus the digest's floor in `.data/messages/state.json`, the old single `last_seen_ts` migrates to the
floor), `slack_client.py` (Bolt `AsyncApp` + Socket Mode handler, bot and user `AsyncWebClient`s, event filtering, user
and channel records, history, drafts, digest), `router.py`, `tools.py`. Inbox rules: Albie's posts and other bots are
ignored; your own messages are stored and pushed with `is_me` but never counted as unread or as a mention; a post
delivered twice (bot + user subscription, or `app_mention` + `message`, or history + live) is recorded once; a DM or
group DM is `is_dm` and counts as a mention; thread replies are stored (for the digest and focus) but left out of
history, where the parent's `reply_count` grows instead.

## Desk (timers, notes, movie)

Extras Bit adds on top of any mode: named **focus timers**, the user's **notes** and a **movie screen** above the desk. They
don't belong to ML / CAD / CODE / MSG and stay open across mode switches. `ui.widgets` says which ones are showing;
the hub owns all of it (`desk/service.py`), Unity's buttons and Bit's tools both go through it, so they raise the
same events. Every desk event replays fresh to a client that connects later (`EventBus.sticky_fresh`): the timers'
remaining time and the movie's position are computed at that moment, not copied from the last message.

**Proposed contract additions** (PROJECT_CONTEXT section 7; models in `events.py`, mirror them in `ForgeEvents.cs`):

| | |
|---|---|
| `GET /timers` | `TimersUpdated {timers: TimerState[]}`: ringing first, then by time left (the minimized dot shows the first). `TimerState {timer_id, label, status: running\|paused\|ringing, duration_s, remaining_s}` |
| `POST /timers {minutes \| seconds, label?}` | a new timer (up to 5), named by `label` (default Focus; a repeat becomes "tea 2"). Rings at its end until stopped |
| `POST /timers/{id}/pause` · `/resume` · `/stop` · `/add {minutes}` · `POST /timers/stop_all` | stop also turns off its ring and removes it; add on a rung timer runs it again; negative takes time off |
| `GET /notes` | `NotesUpdated {notes: Note[] (newest first), last_removed?}`, `Note {id, text, done, created_at, updated_at}` |
| `POST /notes {text}` · `POST /notes/{id}/edit {text?, done?}` | add (opens the panel unless it's a dot) · edit or check off |
| `POST /notes/{id}/remove` · `POST /notes/undo` | remove (kept as `last_removed`) · bring it back |
| `GET /movie` · `GET /movie/library` | `MovieState {status: closed\|playing\|paused, movie?, position_s, volume, seek_rev}` · `MovieInfo[] {movie_id, title, year, duration_s, url, subtitles_url}` |
| `GET /movie/files/<id>.mp4` · `<id>.en.srt` | the file, with byte ranges (seeking); subtitles |
| `POST /movie/play {movie_id?, from_start?}` | opens the screen; resumes where you stopped (a movie stopped in its last 2 min starts over) |
| `POST /movie/pause` · `/resume` · `/close` · `/seek {position_s \| delta_s \| mark}` · `/volume {volume}` | close keeps your place; seek to a `mark` (id, label or its words) plays it, putting its movie on |
| `GET /movie/marks?movie_id=` · `POST /movie/marks {label, position_s?, movie_id?}` · `POST /movie/marks/{mark_id}/remove` | `MovieMark {mark_id, label, position_s}`: named spots per movie, by time (hub only, nothing on `/ws`; model in `desk/movie.py`). No position = a few seconds before where it is now; a label that exists moves |
| `POST /movie/position {position_s, duration_s?, ended?}` | Unity's player reports where it is (every few s, on pause, at the end). No event back: Unity is the clock |
| `GET /ui/widgets` · `POST /ui/widgets {timer?, notes?, movie?}` | `UiWidgets`, each `open\|minimized\|closed` (minimized = the dot). Closing the timers stops them, closing the movie closes it |
| `/ws` events | `ui.widgets`, `timers.updated`, `notes.updated`, `movie.state`; `pet.say` "Time's up! ..." when a timer rings (also spoken) |
| Bit's tools | `timer_start {minutes, label?}`, `timer_control {action: pause\|resume\|add\|stop, timer?, minutes?}`, `timer_status`, `notes_add {text}`, `notes_list`, `notes_edit {note, text?, done?}`, `notes_remove {note?, undo?}`, `notes_view {state}`, `movie_play {title?, from_start?}`, `movie_control {action: pause\|resume\|seek\|skip\|volume\|close, seconds?, mark?, volume?, by?}`, `movie_marks {action: save\|list\|remove, label?, seconds?, title?}`, `movie_status`. `note` / `timer` is an id or a few words of its text or name; a timer action with no `timer` means the only one it can (stop: the ringing ones) |

Unity, for the panels: count each timer down locally from `remaining_s` when `timers.updated` arrives (the Quest's
clock isn't the laptop's); play the chime while `status` is `ringing`. Seek the VideoPlayer only when `seek_rev`
changes, otherwise leave it alone. The `.srt` lines can carry ASS position tags like `{\an8}`: strip `{...}`.

**Adding a movie.** Files live in `.data/media/` (gitignored; never commit one, and nothing goes into the APK; the
Quest streams it from the hub over the LAN). Unity's VideoPlayer needs H.264 8-bit + stereo AAC, with the index at
the front. From a 10-bit HEVC / multi-language source, keeping only the English track (NVIDIA GPU, about 7x real
time on a laptop 3050 Ti):

```powershell
ffmpeg -hwaccel cuda -hwaccel_output_format cuda -i "in.mkv" -map 0:v:0 -map 0:a:m:language:eng -map_chapters -1 -map_metadata -1 `
  -vf "scale_cuda=format=nv12" -c:v h264_nvenc -preset p4 -rc vbr -cq 21 -b:v 0 -maxrate 8M -bufsize 16M -profile:v high -g 48 `
  -c:a aac -b:a 192k -ac 2 -ar 48000 -movflags +faststart .data\media\<id>.part.mp4    # then rename to <id>.mp4
ffmpeg -i "in.mkv" -map 0:s:0 -c:s srt .data\media\<id>.en.srt     # optional subtitles: one text track (ffprobe lists them; PGS won't convert)
```

No NVIDIA GPU: drop the two `-hwaccel` flags and the `-vf`, and use `-c:v libx264 -preset veryfast -crf 21 -pix_fmt yuv420p`
(slower). `<id>` is a file name without dots (`obsession`); a `*.part.mp4` still being written isn't listed. Optional
`<id>.json`: `{"title": "Obsession", "year": 2025, "duration_s": 6519.8}`.

**Mocks for Unity:** `python -m desk.make_mocks` writes `.data/mocks/desk/`: `Resources/Mocks/` (timers, notes, movie,
movie/library, ui/widgets REST answers) and `desk/sequence.json` (a ForgeMockPlayer run: a focus and a tea timer,
notes in, notes to a dot, movie on, the tea timer rings and is stopped). Copy both into `unity/Assets/_Forge/Mocks/`.

Map: `desk/timer.py` (`TimerBoard`, the spoken line), `notes.py` (`NotesStore`, `.data/desk/notes.json`), `movie.py`
(`MovieService`: library, places per movie, marks and volume in `.data/desk/movie.json`), `service.py` (`DeskService`:
widgets, events, Bit's voice), `router.py`, `tools.py`, `make_mocks.py`. Tests: `pytest tests/test_desk.py`. Not saved:
running timers (a hub restart forgets them).
