# PET_PLAN: how Bit lives in Main (startup sequence, one process, one scene)

> Owner: Adrian. Written 2026-10-24 from what is on `main` at `11bd351`, not from the spec.
> Companion to `PROJECT_CONTEXT.md` sections 8.6 to 8.8. Hudson has agreed that the Core edits in PR C are done by Adrian.

## 1. What is on main today

- **Main.unity has no gator.** `PetMain.unity` is a week-old copy of Main with the gator added, no `ForgeNet` object, and empty placeholder prefabs in every mode. Only Main is in the build settings. Nothing merges the two.
- **The hub is already one process** (`hub/main.py`: CAD, agent, VS Code tools, Slack). What is missing is sequencing: nothing knows about arrival, no event opens a workspace mode, each Unity piece runs on its own trigger.
- **Dev's screen share is a second terminal.** `screen-bridge/start.js` (`npm start`) installs the Bit VS Code extension, starts the WebRTC signaling and JPEG fallback servers, launches Chrome through Playwright with the VS Code window auto-selected, clicks Share, and keeps `adb reverse` up for a USB Quest.
- **Voice input works through `WakeListener`** (always-on mic, wake turns, hub-side look-to-talk) plus `GatorFollowUp`. The Space push-to-talk falls through to `TypedSpeechToText`, a keyboard text box, because `HubSpeechToText` is on no prefab. The text box is deprecated.
- **Two mock switches.** `ForgeNet` Auto/Live/Mock, and `pet_config.json` `eventSource: "mock"`. The committed value disables the gator's hub client even when ForgeNet is live.
- **Two desk detections.** `DeskPresence.Table` (Core) follows the table you walk up to. `GatorMrukAdapter` uses `DeskLocator.Desk` (nearest at load) or the whistle's table. They can disagree.
- **Messages are built but not slotted.** The MSG mode item has an empty prefab; `MessagesPanel.prefab` / `DraftCard.prefab` were never built or committed. Same for the ML panels on `ml/unity-panels` and the ML router on `satyabrata_dev_main`: built in isolation, not wired into Main or `main.py`.
- **Crew runners get every tool** (`cad_*`, `messages_*`, `vscode_*`). The hub has no idea which screen the user is looking at.
- **Deprecated or dead:** `POST /agent/text`, the `/ws/agent` socket, `agent/app.py` standalone mode, `agent/tts.py`, `ui.highlight` (never emitted), `vscode.event` (never consumed), `docs/PET_TESTING.md` pointing at `/ws/agent`.

## 2. Target experience

Walk up, Bit hops on the desk, Bit tells you about Slack, the VS Code screen rises. Sub-agents (the crew) live on that screen. Switch to CAD and the crew keep working in the background while Bit alone handles the CAD and Slack tool calls. One hub process, one Unity scene.

## 3. Startup sequence

`DeskPresence` (Core) is the clock. One Pet script, `ArrivalDirector`, is the only thing that reacts to it. Everything else is driven by the director or by hub events.

| Step | Trigger | Unity | Hub |
|---|---|---|---|
| 0 Boot | Play | `ForgeNet` connects `/ws`, `AgentClient` sends `hello`. Bit spawns on the floor and follows you. | Opens and pre-warms the Grok session. Starts the screen bridge (Chrome + VS Code share). Backfills unread Slack since `last_seen_ts`. |
| 1 Approach | `DeskPresence` Near | Dock lights up (Core, as today). Director: `HopToUserDesk` onto `DeskPresence.Table`, the same table the dock uses. | nothing |
| 2 Seated | `DeskPresence` Seated | Director waits for the hop to land, then `POST /messages/digest`, once per sit. Cards stack beside Bit. | `notify.digest`; Bit speaks the line through the voice session. |
| 3 Hand-off | `tts.end` of the digest line, or 1.5 s if the count was zero | Director waits up to 3 s for `screen.status = sharing`, then `Workspace.Open("code")`. The VS Code screen rises. Cards fold after 20 s. | nothing |
| 4 Working | Dock ring or voice | `Workspace.modeChanged` → director sends `workspace.mode {mode}`. | Stores the mode on the session, tells Grok in one line. Crew tools only in `code`. |
| 5 Away | `DeskPresence` Away | Workspace closes the mode (Core, as today). Bit hops down and follows. Digest re-arms. | Crew runs keep going. |

**Crew lives on the CODE screen.** Egg, picker and the N/X keys are active only while the active mode is `code`. `spawn_agent` / `assign_task` are usable by Grok only in that mode. Crew runners get their kit plus `vscode_*`, never `cad_*` or `messages_*`. Leaving `code` keeps the crew gators on the desk in their working antics with captions muted; their hub runs continue. Returning surfaces whatever finished and Bit announces it as he does now.

**Bit goes to the panel he is working on.** Every tool call already emits `pet.state working` with `target_panel`. The gator resolves it through `Workspace.TryGetItemTransform(id)` and walks there: `cad-viewer` is the rocket, `messages` the card stack, `screen-main` the VS Code screen.

**Reply with Bit is a voice turn.** Clicking a card sends `messages.focus {ts, channel}`. The hub reads the message aloud with "want me to answer?", seeds the Grok context with the thread, and `GatorFollowUp` opens hands-free listening. Your dictated reply goes through the draft → confirm card → send flow (8.8).

**Mouth.** PR #26 adds a `jaw` bone. `GatorTalkAnimator` already has the loudness envelope from the TTS stream; the jaw rotates by that envelope (about 20° open). Every spoken path arrives as `tts.*` audio frames, so the digest, Bit's unprompted lines and Grok answers all move the mouth.

## 4. New plumbing (not section 7; documented in `docs/PET_AGENT_CONTRACT.md`)

Client → hub on `/ws`:
```
workspace.mode   { pet_id, mode }            mode id ("code" | "cad" | "ml" | "messages") or "" when closed
messages.focus   { pet_id, ts, channel }     Bit reads the message aloud, asks to answer, replies land in that thread
```
Hub → client:
```
screen.status    { state: "starting"|"sharing"|"stopped"|"error", source?, detail? }   sticky replay on connect
```
Removed: `POST /agent/text` (section 7 "typed fallback"), `/ws/agent`.

Core API added in PR C: `Workspace.TryGetItemTransform(string itemId, out Transform)`, `Workspace.Current`, mode `code` (label CODE, item `screen-main` "VS Code") replacing `screens` / SCR. Panel ids are unchanged.

Hub: `hub/screens/` supervises `screen-bridge/start.js` as a child of the hub process (restart with backoff, killed on shutdown). `SCREEN_BRIDGE=0` disables it.

## 5. Work split and merge order

Merge first, in this order: **#29** (Hudson's restyle; rewrites Main.unity), **#26** (gator mouth), **#14** (Dev's proactive Bit; touches `ws.py`, `main.py`). Then the ML pair open the PR for `ml/unity-panels` and get **#6** reviewed, so PR C can slot both.

| PR | Branch | Contents |
|---|---|---|
| **A** hub | `feat/one-process-hub` | `hub/screens/` supervisor + `screen.status`; `workspace.mode` session message, crew tools gated to `code`; crew tool allowlist by kit; `messages.focus`; Slack history backfill; remove `/agent/text`, `/ws/agent`, standalone agent app, `tts.py`; docs. |
| **B** Unity Pet + Messages | `feat/pet-in-main` | `ArrivalDirector`; `HubSpeechToText` on the prefab, `TypedSpeechToText` and `MockEventSource` sandbox-only, `eventSource` defaults to hub; gator takes its desk from `DeskPresence`; crew gated by mode; walk-to-panel; jaw lip sync; `PetRuntime.prefab` (gator + voice + whistle + crew + hatchery + director); Messages prefabs built and committed; "Reply with Bit" → `messages.focus`; delete `PetMain.unity`. |
| **C** Core | `feat/main-wiring` (stacked on #29) | `screens` → `code`; `TryGetItemTransform` / `Current`; `MainSceneBuilder` drops in `PetRuntime.prefab`, sets the MSG item to `MessagesPanel.prefab`, slots the ML prefabs; rebuild Main. |

C is where everything built in isolation lands in Main. It merges last, in one editor pass: `Forge > Pet > Build All`, `Forge > Pet > Build PetRuntime Prefab`, `Forge > Messages > Build Prefabs`, `Forge > Build Main Scene`, commit prefabs + `.meta`.

## 6. Running it

```
cd hub && .venv/bin/uvicorn main:app --host 0.0.0.0 --port 8000      # hub + Slack + Grok + screen bridge (Chrome opens by itself)
```
Unity: open `Main.unity`, Play. `.env` needs `FAKE_AGENT=0`, `VOICE_PROVIDER=grok`, `XAI_API_KEY`, `ANTHROPIC_API_KEY`, the three Slack tokens. `SCREEN_BRIDGE=0` if you don't want Chrome.

## 7. Open questions

- Which Slack workspace is the demo workspace. The app is currently installed in "Prepify AI"; moving it is `slack login` + `slack install -E local` + re-copy the user token.
- Whether the two extra screen slots from the old SCR mode come back later (Dev).
- The ML router is not mounted in `main.py` until #6 is reviewed; PR A leaves a one-line hook for it.
