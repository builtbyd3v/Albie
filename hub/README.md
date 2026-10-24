# Forge hub

FastAPI on the laptop: REST + one `/ws` event bus. Unity is a view; the hub owns the state.
Modules: **CAD** (see `../CAD_PLAN.md`), the **agent** (pet; see [Agent module (pet)](#agent-module-pet)), **messaging** (Slack; see [Messaging (Slack)](#messaging-slack)), **screens** (the screen bridge child process; see [One process](#one-process)) and **ML** (see [hub/ml](ml/README.md)).

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
`quadcopter`, `gearbox`. Each keeps its own history (undo stays inside it); version numbers are unique across them.

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
- Keep a rebuild under about a second or two: build123d's `extrude(both=True)` and `RectangleRounded` are slow;
  `Solid.extrude` and `SlotOverall` are fast (see `gearbox.py`, `quadcopter.py`). The worker stops a build at 8 s.
- `GET /cad/drawing.png?v=N` (or `.svg`): a version's engineering drawing (`cad/drawing.py`, A3, 2048 x 1448): three
  views + iso, dimensions, parts list, title block; parts changed since its parent in orange. Made once (~1 s), cached.

## Test

```powershell
pytest            # a few minutes: CAD (geometry, RocketPy cross-check, designs, library API, watcher), agent, messaging, screens, VS Code
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
| `cad/scene.py` | What Unity draws on the model: markers, dimensions, explode vectors, mass breakdown, demo (part's or generic), in the GLB's frame. |
| `cad/flight.py` | RocketPy launch-to-apogee sim on an Estes C6 (`cad/data/estes_c6.eng`), then parachute descent to landing; fixed-rate samples + events. |
| `cad/worker.py` | Build process with timeout and respawn; reloads the part script on every job. |
| `cad/service.py` | The design library: version tree per design on disk, one regen at a time, events, undo/reset, folder watcher. |
| `cad/router.py` | `/cad/*` endpoints (CAD_PLAN section 5). |
| `cad/tools.py` | Bit's CAD tools (JSON schemas + `call_tool`) for the agent module. |

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
  crew.py     Bit's crew: agent types, roster, parallel member turns, the lead-only crew tools
  wake.py     "Hey Bit" / "<Crew name>, ..." routing for always-on wake turns
  speech.py   live voice sessions by pet id: speak(text) and note(text) for other modules (digest, focus)
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

Grok also gets Bit's fast tools (CAD, VS Code, Slack drafts, the crew) and `delegate` for the Claude runner. Unity's
`workspace.mode {mode}` tells the hub which view is open: Grok gets a one-line note about it, and the crew tools
only work while the CODE view is open (they answer an error otherwise). `VOICE_PROVIDER=fake` speaks a canned reply from
`agent/assets/fake_voice_24k.pcm` with no key, for Unity testing:

```bash
VOICE_PROVIDER=fake FAKE_AGENT=1 SCREEN_BRIDGE=0 .venv/bin/uvicorn main:app
```

### Say a gator's name (wake turns)

Unity's `WakeListener` keeps the mic on and sends each speech segment as `stt.start {wake: true}`. `agent/wake.py`
only answers segments that name a gator ("Hey Bit, ...", "Tester, run the tests"); a name on its own
("Hey Scout") comes back as `wake.listen` and that gator takes the next sentence. Anything else is dropped
without a caption (and deleted from the Grok conversation). In voice mode every segment is transcribed by xAI.

### Crew (extra gators, one per agent type)

The user hatches helper gators in Unity (the egg on the desk, the N key, or "Bit, hatch a tester"). Each one is
its own Claude runner with a role, a system prompt and a tool kit (`agent/crew.py` `TYPES`): Scout, Builder, Fixer,
Tester, Reviewer, Scribe, Shipper, Designer and Sentinel. Bit leads: Grok and Bit's Claude runner get four
lead-only tools, `spawn_agent`, `assign_task` (hatches the gator first if needed), `dismiss_agent` and
`crew_status`. Crew members work in parallel. A code task runs in its own git worktree (`AGENT_WORKTREE_DIR`,
default `<repo>-crew/`) on `pet/<agent>/<task>` and is committed there, so nobody switches the main checkout's
branch. When one finishes, Bit says so out loud (voice mode) and the gator shows the summary in its bubble.
Unity is the source of truth for who exists (`crew.roster`); an `agent.ask` whose `pet_id` is a crew member runs
that member's turn without touching Bit's.

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

## Messaging (Slack)

Bit reads **your** Slack inbox and replies **under your name**, never without a confirmation. The app is defined by
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
SLACK_USER_TOKEN=xoxp-...       # you: read your inbox, post as you. Empty = Bit sees only invited channels and posts as "Bit"
SLACK_USER_ID=U...              # optional, derived from the user token; only needed without it
SLACK_DEFAULT_CHANNEL=#forge-team
```

Bot + app tokens empty = messaging disabled (one warning at startup, every `/messages/*` endpoint but `/digest` answers
503; `/digest` returns an empty digest). `GET /health` shows `slack: {enabled, connected, as_user, bot_user_id, user_id, channels, open_draft}`.

Live check without Unity:

```bash
.venv/bin/python scripts/slack_smoke.py                              # listen: print every inbox message
.venv/bin/python scripts/slack_smoke.py --draft "#forge-team" "hi"   # write a draft, post nothing (notify.draft)
.venv/bin/python scripts/slack_smoke.py --send  "#forge-team" "hi"   # dev shortcut: draft + send with no confirmation
.venv/bin/python scripts/slack_smoke.py --digest                     # what Bit would say on sit-down
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
unsent drafts expire after 120 s (`expired`, an edit restarts the clock); Cancel gives `cancelled`. The composer
clears on `notify.sent` or `notify.draft_cancelled`, whichever path confirmed. A draft's `channel` is an id, a `#name`,
or a person's name: "tell Hudson ..." drafts into their DM (opened with `conversations.open` if there is none yet).

| | |
|---|---|
| `GET /messages/channels` | `{channels: ChannelSummary[]}`: every conversation you are in (`id, name, kind: channel\|private\|dm\|group-dm, unread, mentions, last_ts, last_text, topic, is_default`), DMs / group DMs with unread first, then by `last_ts` desc. `name` is `#forge-team`, the person (dm) or the people (group-dm). |
| `GET /messages/history?channel=&limit=50&before=` | `{channel, channel_name, kind, messages: SlackMessage[], has_more}`: the conversation's top-level messages oldest -> newest (limit max 100; `before` = a ts, pages backwards). Threaded messages carry `reply_count`; your own posts are there with `is_me`. From the store when it has the page, else `conversations.history` merged into it. |
| `POST /messages/channels/{id}/seen` | the conversation is open in the panel -> `{ok, unread: 0}`; `notify.channels` follows |
| `GET /messages/users/me` | `{user_id, name, display_name}`: who the hub posts as |
| `GET /messages/latest?n=5` | newest inbox messages across every conversation (`SlackMessage`: channel, channel_name, is_dm, user, user_name, text, ts, thread_ts, mentions_me, reply_count, edited, is_me, user_initials) |
| `POST /messages/drafts {text, reply_to_ts?, channel?, thread_ts?}` | write a draft -> `Draft {draft_id, channel, channel_name, text, thread_ts, reply_to, edited}`; `reply_to_ts` picks the channel and thread of the message being answered; `channel` is an id, `#name` or a person's name (their DM), omitted = that channel or `SLACK_DEFAULT_CHANNEL` |
| `PATCH /messages/drafts/{id} {text?, channel?, thread_ts?}` | the user edited the draft -> the `Draft` again, same `draft_id`, `edited: true`, `notify.draft` re-emitted; 404 if it is gone, 422 for empty text |
| `POST /messages/drafts/{id}/send` | post it as you -> `{ts}`; 404 if the draft is gone (sent, cancelled, expired, superseded) |
| `POST /messages/drafts/{id}/cancel` | discard it -> `{ok}` |
| `POST /messages/digest {pet_id?}` | Unity calls it when `DeskPresence` goes Seated: everything unread in every conversation -> `notify.digest` (newest 4), `pet.say` with the one-line summary, spoken on the live voice session (`agent/speech.py`). Marks every conversation seen. |
| `POST /messages/focus {pet_id?, channel, ts}` | "Reply with Bit" on a message (same as the ws message `messages.focus`): Bit says "<who> in <#channel or a DM> says: <text>. Want me to answer?" (voice, else `pet.say`), and the next `messages_send` without a target replies in that thread. 404 if the message isn't one of that conversation's last 200. |
| `/ws` events | `notify.message` (each message, your own included with `is_me`), `notify.channels {channels}` (sticky, debounced 300 ms), `notify.draft`, `notify.sent {draft_id, ...}`, `notify.draft_cancelled {draft_id, reason}`, `notify.digest`; client -> hub: `messages.focus {pet_id, channel, ts}` |
| Bit's tools | `messages_read_latest {n?}`, `messages_list_channels {}`, `messages_read_channel {channel, n?}`, `messages_send {text, reply_to_ts?, channel?, thread_ts?}` (draft only; no target = the message in focus; a person's name = their DM), `messages_confirm {send, draft_id?}` |
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
and channel records, history, drafts, digest), `router.py`, `tools.py`. Inbox rules: Bit's posts and other bots are
ignored; your own messages are stored and pushed with `is_me` but never counted as unread or as a mention; a post
delivered twice (bot + user subscription, or `app_mention` + `message`, or history + live) is recorded once; a DM or
group DM is `is_dm` and counts as a mention; thread replies are stored (for the digest and focus) but left out of
history, where the parent's `reply_count` grows instead.
