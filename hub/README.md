# Forge hub

FastAPI on the laptop: REST + one `/ws` event bus. Unity is a view; the hub owns the state.
Modules so far: **CAD** (see `../CAD_PLAN.md`) and the **agent** (pet; see [Agent module (pet)](#agent-module-pet)). `main.py` and `events.py` are a minimal skeleton for Person 5 to grow.

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

## Designs (the CAD library)

Every `*.py` in `cad/parts/` (except `_`-files) is a design; its file name is its id. Today: `rocket` (the hero),
`quadcopter`, `gearbox`. Each keeps its own history (undo stays inside it); version numbers are unique across them.

- `GET /cad/parts` lists them; `POST /cad/part {"part": "gearbox"}` opens one; `POST /cad/preset {"name": "Hexacopter"}`
  applies a named preset to the open one. Everything else (`/cad/params`, undo, ...) acts on the open design.
- **Add a design:** copy `cad/parts/_template.py` to `cad/parts/<name>.py`. It shows up in `GET /cad/parts` (and the
  Designs menu in Unity) straight away; open it and save to regenerate. A part script defines `PARAMS` and
  `build(params) -> BuildResult`, optionally `TITLE`, `DESCRIPTION`, `PRESETS`, `analyze(result, masses)` (metrics:
  give `min`/`max` for a gauge) and `simulate_flight` (the Fly button). Mass, size and part count come free.
- Keep a rebuild under about a second or two: build123d's `extrude(both=True)` and `RectangleRounded` are slow;
  `Solid.extrude` and `SlotOverall` are fast (see `gearbox.py`, `quadcopter.py`). The worker stops a build at 8 s.

## Test

```powershell
pytest            # a few minutes: geometry, RocketPy cross-check, every design + preset, library API, watcher, timeouts
```

## Mocks for Unity (hub off)

```powershell
python -m cad.make_mocks       # -> hub/.data/mocks/cad/parts.json, <part>/vN/, regen_failed.json
```

Copy that folder to `unity/Assets/_Forge/Mocks/cad/` (see the CAD README there). The rocket's four states are the demo story: green 1.57 → "smaller fins" red 0.84 → "fourth fin" amber 1.25 → "20% longer" green 1.60. The quadcopter goes clear → "7-inch props" hit → longer arms clear; the gearbox goes 3:1 → 6:1 (undercut pinion) → heavy duty.

## CAD module map

| File | What |
|---|---|
| `cad/parts/rocket.py` | The rocket: `PARAMS`, `build(params)`, presets, `analyze` (Barrowman stability). The file on the main screen. |
| `cad/parts/quadcopter.py`, `gearbox.py` | Two more designs (prop clearance / thrust-to-weight; ratio / pinion rim, own involute gears). |
| `cad/parts/_template.py` | Copy me: a new file is a new design. |
| `cad/part_api.py` | The only Forge types a part script imports (`Param`, masses, `Metric`, `BuildResult`, rocket specs). |
| `cad/export.py` | GLB + cad-vision schema 1.0 metadata, validated with cad-vision's loader rules. |
| `cad/analysis.py` | Mass, CG, generic metrics (mass, size, parts), and the Barrowman helpers rockets use. |
| `cad/flight.py` | RocketPy launch-to-apogee sim on an Estes C6 (`cad/data/estes_c6.eng`). |
| `cad/worker.py` | Build process with timeout and respawn; reloads the part script on every job. |
| `cad/service.py` | The design library: version tree per design on disk, one regen at a time, events, undo/reset, folder watcher. |
| `cad/router.py` | `/cad/*` endpoints (CAD_PLAN section 5). |
| `cad/tools.py` | Bit's CAD tools (JSON schemas + `call_tool`) for the agent module. |

## Agent module (pet)

Adrian's module: the gator pet answers questions about a repo and runs coding tasks through the
**Claude Agent SDK** (`claude-agent-sdk`). Protocol: [`docs/PET_AGENT_CONTRACT.md`](../docs/PET_AGENT_CONTRACT.md).

- REST: `POST /agent/text` (typed fallback, returns the final answer), `GET /agent/projects`.
- WebSocket: **`/ws/agent`** (not `/ws`). `/ws` belongs to the `EventBus` in `events.py`; the agent has its
  own broadcast bus (`agent/bus.py`). Folding the agent onto `/ws` is Person 5's call (the agent handler
  already ignores unknown message types, so sharing a socket would work).
- `/health` adds `fake_agent`, `stt` and `agent_sockets`.
- `agent/` imports no CAD code, so it runs and tests without build123d/rocketpy.

```
agent/
  app.py      create_agent_app(): standalone app (/agent, /ws/agent, /health); health_fields(); load_env()
  __main__.py python -m agent (runs create_agent_app with uvicorn)
  events.py   pydantic models for every agent WS message + builders + PET_STATE_FOR_EVENT
  bus.py      broadcast to all /ws/agent sockets + per-turn Emitter (pet.state companions, path redaction)
  ws.py       /ws/agent session: hello, agent.ask, agent.cancel, stt.start/stop, binary audio
  router.py   POST /agent/text, GET /agent/projects
  runner.py   real turns via ClaudeSDKClient (qa = read-only, code = edits on a pet/* branch)
  fake.py     FAKE_AGENT=1 scripted turns (same signature)
  turn.py     auto-mode classifier + TurnControl (cancel/interrupt)
  stt.py      SpeechToText: NoneSTT, WhisperSTT (faster-whisper, optional)
  voice.py    VOICE_PROVIDER=grok|fake: one speech-to-speech session per socket (stt.* in, tts.* + PCM out)
  assets/     fake_voice_24k.pcm (regenerate: scripts/make_fake_voice.py)
  tts.py      TextToSpeech protocol + NullTTS stub
config/projects.yaml   project_id -> repo path (paths never leave the server)
tests/agent/           pytest, fake mode, never imports main.py
scripts/ws_smoke.py    prints the event stream from a running hub
.env.example           copy to hub/.env (main.py and python -m agent both load it)
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
FAKE_AGENT=1 .venv/bin/python -m agent              # agent alone, no API key, no model calls (port 8000)
FAKE_AGENT=1 .venv/bin/uvicorn main:app --host 0.0.0.0 --port 8000   # full hub: CAD + agent
curl localhost:8000/health
```

Fake turns: thinking, 3 cumulative partials, answer_done. A code ask gives task_started, 3 progress
events, task_complete (branch `pet/fake-123`). The text `error` gives agent.error. `FAKE_AGENT_DELAY`
(seconds, default 0.6) sets the pacing.

Real mode: `ANTHROPIC_API_KEY` in `hub/.env` (or the shell) and `FAKE_AGENT=0`.
`AGENT_MODEL` (default `claude-sonnet-5-5`), `AGENT_QA_EFFORT` (`low`), `AGENT_CODE_EFFORT` (`medium`).

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

Conversational only for now (no tools, no `delegate`). `VOICE_PROVIDER=fake` speaks a canned reply from
`agent/assets/fake_voice_24k.pcm` with no key, for Unity testing:

```bash
VOICE_PROVIDER=fake FAKE_AGENT=1 .venv/bin/python -m agent
```

### Test

```bash
FAKE_AGENT=1 .venv/bin/python -m pytest tests/agent -q    # fake mode, a few seconds, no CAD deps
```

(Plain `pytest` still runs only `cad/tests`, per `pytest.ini`.)

### Smoke test against a running hub

```bash
.venv/bin/python scripts/ws_smoke.py "where is the websocket handler?" --mode qa
.venv/bin/python scripts/ws_smoke.py "add a docstring to agent/tts.py" --mode code
.venv/bin/python scripts/ws_smoke.py "hi" --url ws://10.0.0.5:8000/ws/agent
```

### How Unity connects

`unity/Assets/StreamingAssets/pet_config.json` → `hubUrl`, with `eventSource: "hub"` (commit it as `"mock"`):

| Where Unity runs | hubUrl |
|---|---|
| Editor / desktop on the same laptop | `ws://127.0.0.1:8000/ws/agent` |
| Quest on the same Wi-Fi | `ws://<laptop LAN IP>:8000/ws/agent` (macOS: `ipconfig getifaddr en0`); bind `0.0.0.0`, allow it through the firewall |
| Quest over USB | `adb reverse tcp:8000 tcp:8000`, keep `ws://127.0.0.1:8000/ws/agent` |

The client sends `hello` first, then `agent.ask` / `agent.cancel` / `stt.*` plus binary PCM16 frames
(mono, 16 kHz, ~100 ms chunks). Every event goes to every `/ws/agent` socket; filter by `pet_id`.
The proposed `agent.*` / `stt.*` events are preceded by an existing-contract `pet.state` whenever the
pet's mode changes; `AGENT_EMIT_PET_SAY=1` also mirrors `agent.answer_done` as `pet.say`.
