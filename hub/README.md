# Forge hub

FastAPI on the laptop: REST + one `/ws` event bus. Unity is a view; the hub owns the state.
Modules: **CAD** (see `../CAD_PLAN.md`), the **agent** (pet; see [Agent module (pet)](#agent-module-pet)) and **messaging** (Slack; see [Messaging (Slack)](#messaging-slack)). The ML router is still to come.

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

- `GET /health` once the first rocket is built (a few seconds on startup).
- Open `cad/parts/rocket.py` in VS Code: **saving it regenerates the rocket** and pushes `cad.model_updated`. A broken save keeps the last good model and emits `cad.regen_failed` with the error line.
- Try it: `curl -X POST localhost:8000/cad/params -H "Content-Type: application/json" -d "{\"fin_count\": 4}"`
- Data (versions, GLBs) lives in `hub/.data/` (gitignored). Delete `.data/cad` to start fresh. Env: `FORGE_DATA_DIR`, `FORGE_CAD_SCRIPT`.

## Test

```powershell
pytest            # about 1 min: geometry, RocketPy cross-check, export validity, API, watcher, timeouts
```

## Mocks for Unity (hub off)

```powershell
python -m cad.make_mocks       # -> hub/.data/mocks/cad/v1..v4 + regen_failed.json
```

Copy that folder to `unity/Assets/_Forge/Mocks/cad/`. The four states are the demo story: green 1.57 → "smaller fins" red 0.84 → "fourth fin" amber 1.25 → "20% longer" green 1.60.

## CAD module map

| File | What |
|---|---|
| `cad/parts/rocket.py` | The part: `PARAMS` + `build(params)`. The file on the main screen. |
| `cad/part_api.py` | The only Forge types a part script imports (`Param`, masses, `FinSet`, `RocketSpec`, `BuildResult`). |
| `cad/export.py` | GLB + cad-vision schema 1.0 metadata, validated with cad-vision's loader rules. |
| `cad/analysis.py` | Mass, CG, Barrowman CP, static margin, markers for Unity. |
| `cad/flight.py` | RocketPy launch-to-apogee sim on an Estes C6 (`cad/data/estes_c6.eng`). |
| `cad/worker.py` | Build process with timeout and respawn; reloads the part script on every job. |
| `cad/service.py` | Version tree on disk, one regen at a time, events, undo/reset, file watcher. |
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

## Messaging (Slack)

Bit is a Slack bot user in the hackathon workspace (PROJECT_CONTEXT 8.7 has the 15-minute app setup: Socket Mode,
bot scopes, bot events, Messages tab, `/invite @Bit`). Socket Mode means the hub opens an outbound WebSocket to
Slack, so there is no public URL, tunnel or webhook. There is no fake or mock path: test against the workspace.

`hub/.env`:

```
SLACK_BOT_TOKEN=xoxb-...        # OAuth & Permissions > Bot User OAuth Token
SLACK_APP_TOKEN=xapp-...        # Socket Mode app-level token, scope connections:write
SLACK_USER_ID=U...              # your member id; messages containing <@U...> count as mentions of you
SLACK_DEFAULT_CHANNEL=#forge-team
```

Both tokens empty = messaging disabled (one warning at startup, `/messages/latest` and `/send` answer 503,
`/digest` returns an empty digest). `GET /health` shows `slack: {enabled, connected, bot_user_id}`.

Live check without Unity:

```bash
.venv/bin/python scripts/slack_smoke.py                            # connect and print every message Bit sees
.venv/bin/python scripts/slack_smoke.py --send "#forge-team" "hi"  # post as Bit
.venv/bin/python scripts/slack_smoke.py --digest                   # what Bit would say on sit-down
```

| | |
|---|---|
| `GET /messages/latest?n=5` | newest messages Bit has seen (`SlackMessage`: channel, channel_name, user, user_name, text, ts, thread_ts, mentions_me) |
| `POST /messages/send {channel?, text, thread_ts?}` | post as Bit; `channel` is an id or `#name`, omitted = `SLACK_DEFAULT_CHANNEL`; `thread_ts` replies in-thread -> `{ts}` |
| `POST /messages/digest {pet_id?}` | Unity calls it when `DeskPresence` goes Seated: everything unread -> `notify.digest` (newest 4), `pet.say` with the one-line summary, and the line is spoken on the live voice session (`agent/speech.py`). Marks all seen. |
| `/ws` events | `notify.message` (each inbound message), `notify.sent`, `notify.digest` |
| Bit's tools | `messages_read_latest {n?}`, `messages_send {text, channel?, thread_ts?}` (the model must read the text back and get a yes first) |

Map: `messaging/settings.py` (env), `store.py` (last 50 messages + `last_seen_ts` in `.data/messages/state.json`),
`slack_client.py` (Bolt `AsyncApp` + Socket Mode handler, event filtering, name caches, send, digest),
`router.py`, `tools.py`. Only events Bit can see count: DMs to the bot and channels it was invited to; its own
posts and other bots are ignored, and an `app_mention` that also arrives as a `message` is recorded once.
