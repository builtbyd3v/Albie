# Forge (working name): Spatial Workspace for Engineers

> Single source of truth for the hackathon. Every human and every coding agent reads this first.
> "Forge" (project) and "Bit" (pet) are placeholder names. Rename freely.
> v2: switched to Unity + Meta XR SDK. Laptop-first until the Quest 3 arrives.

---

## 1. The pitch

Meta just announced its VR Glasses (Project Phoenix) at Connect 2026: a lightweight headset shipping spring 2027 for $1,299 that connects to a PC or Mac, gives you a virtual keyboard and touchpad, and sells multiple virtual monitors as the killer productivity feature. That demo is "your laptop, but with more screens."

**We are building what comes after more screens.** You sit down at your desk, the workspace unfolds around you, and the space itself understands your engineering work:

- An ML engineer sees their data, their model, and their training run as physical objects they can grab, inspect, and fix.
- A mechanical engineer sees their CAD part next to them and changes it by talking.
- An AI pet (your always-on agent) follows you around, sits on your desk while you work, dives into your screens to investigate, and handles life admin (WhatsApp messages).

**One-liner:** Jarvis for engineers. The future of engineering productivity in XR.

**Runs on:** Meta Quest 3 (passthrough mixed reality, hands or controllers) built natively in Unity with the Meta XR SDK. Developed and demoable on laptops via the Meta XR Simulator (which also has a Meta VR Glasses device profile) and a desktop first-person mode.

---

## 2. The demo we are building toward (demo spine)

Everything we build serves this 3 to 4 minute script. If a feature is not in the script, it is a stretch goal.

1. **Enter the room.** User is standing. Bit trots alongside, following them.
2. **Sit at the desk.** The desk is detected (Quest: Mixed Reality Utility Kit finds the `TABLE`) or walked up to (laptop). Bit hops onto the desk. The workspace unfolds: keyboard and trackpad glow in, the main screen (real VS Code) rises, side panels fan out.
3. **ML mode.** VS Code shows an image classifier project instrumented with our 3-line Python SDK. User hits "Train."
   - **Above:** live training curves (loss, accuracy, train vs val) animate epoch by epoch. Overfitting warning fires if val loss climbs.
   - **Center left:** 3D diagram of the model, layers as blocks, input shape on top, output shape on bottom, pulses as batches flow.
   - **Left:** the dataset as a physical stack of photos. Scrub, pull one out, see it big with its label, put it back.
   - **Right:** results wall. Every validation image tinted green (correct) or red (wrong). Click a red one: image, predicted label, probability bars for every class, true label.
   - User spots a mislabeled image and hits **Relabel** or **Remove**. Model was right, data was wrong.
   - Confusion matrix appears automatically when training finishes.
4. **Ask the pet.** "Hey Bit, why is my model so bad at cats?" Bit says "one sec," morphs into a glowing blob, leaps into the results panel, the cat row lights up in the confusion matrix, then Bit pops back out with an answer card and suggested actions.
5. **CAD mode.** A model rocket appears with a parameter panel. "Bit, make the body tube 20% longer and add a fourth fin." Bit walks to the rocket, tinkers, the rocket regenerates on the spot.
6. **Life admin.** A WhatsApp message arrives. Bit perks up: "Your mom says dinner in 30, will you be ready?" User: "Yeah, tell her I'll be there." Bit confirms, sends it, the reply lands on the teammate's real phone.
7. **Close:** "Every engineer gets a desk that understands their work and a teammate that never sleeps."

---

## 3. Feasibility: what is possible, what is not, what we do instead

| Idea | Possible? | How |
|---|---|---|
| Native Quest app | **Yes** | Unity 6 + Meta XR All-in-One SDK (Core, Interaction SDK, MR Utility Kit). Building Blocks for passthrough, hands, controllers. |
| Develop on laptops without a headset | **Yes, two ways** | (1) **Meta XR Simulator**: real XR runtime on the laptop, keyboard/mouse drive head and hands, synthetic rooms with furniture (including tables) for MR testing. (2) **Desktop rig**: plain first-person controller (WASD + mouse) used when no XR runtime is present. This is the "laptop version" and our guaranteed demo. |
| Detect the table and auto-spawn the workspace | **Yes on Quest 3** | MRUK gives labeled scene anchors (`TABLE`, `WALL_FACE`, `FLOOR`...). Requires the user to have done Space Setup on the headset. Works against synthetic rooms in the simulator. Fallback: point and pinch to place. Desktop: walk up and press `E`. |
| Real VS Code inside the app | **Yes, via streaming** | Real VS Code runs on the laptop. A "Screen Bridge" web page captures the window (`getDisplayMedia`) and sends it over WebRTC; Unity receives with the `com.unity.webrtc` package into a texture on the main panel. Works in the Mac/Windows editor and on Quest. Windows-only shortcut for laptop mode: `uWindowCapture`. Never rebuild VS Code. |
| Virtual keyboard + trackpad "popping up" | **Yes, as visuals** | Animated glowing models aligned over the real ones. Real input is the physical laptop. |
| ML visualizations | **Yes, strongest piece** | Python SDK streams JSON to the hub; Unity renders. Charts via XCharts (open source Unity charting) or LineRenderers. |
| 3D model architecture | **Yes** | PyTorch forward hooks give layer type, params, in/out shapes. Unity builds blocks from JSON. |
| SolidWorks / Fusion / AutoCAD integration | **No** | Closed and slow. Time sink. |
| AI-editable CAD | **Yes: code-CAD** | Default: **build123d** (Python, OpenCascade). Part = parametric script. Hub regenerates and exports GLB; Unity loads it at runtime with **glTFast**. Candidate repos Hudson is sending may replace or augment this; the Unity side (load GLB on update) does not change. |
| Voice conversation with tools | **Yes** | Grok Voice Agent API (speech to speech, function calling) as the pet's ears and mouth, with a `delegate` tool to a stronger reasoning model for heavy tasks. See section 11. |
| Real WhatsApp messages | **Yes, with the official Cloud API, with limits** | The pet gets its own WhatsApp number (Meta test number). It can only message up to 5 verified numbers, and free-form replies only within 24 hours of the other person messaging it. It cannot read your personal WhatsApp. Perfect for the demo: "mom" is a teammate's phone on the allowlist. |
| Pet morphs into a blob and dives into a screen | **Yes, animation** | Swap to a blob mesh (noise shader), squash/stretch, DOTween arc into the panel, panel glows. |
| Multiple pets | **Data model yes, demo 1** | Pet = persona + tool set + prefab. |
| Kitchen counter context trigger | Stretch | Same MRUK labels, different anchor. |

---

## 4. Tech stack

**Unity (the app)**
- **Unity 6.6, editor `6000.6.7f1`. Everyone on this exact version.** (Matches Hudson's working Meta XR test project.)
- Meta XR All-in-One SDK `com.meta.xr.sdk.all` **207.0.1** (Core, Interaction SDK, MR Utility Kit), OpenXR plugin `com.unity.xr.openxr` **1.18.0** with the Meta XR feature group enabled, Input System **1.20.1**, URP **17.6.0**
- Meta XR Simulator **v207.1**, installed separately (Windows: `C:\Program Files\MetaXRSimulator\v207.1`; Apple Silicon Mac is officially experimental, expect some fiddling). See 6.1 for setup.
- `com.unity.webrtc` (screen streaming), `glTFast` (runtime GLB loading for CAD), `NativeWebSocket` (WS client that works on Android), `com.unity.nuget.newtonsoft-json`
- TextMeshPro, DOTween (free) for animation, XCharts for charts
- Render pipeline: URP, Android target for Quest, Vulkan

**Hub (backend on the laptop):** Python 3.11, FastAPI, one process, routers per module (`ml`, `cad`, `agent`, `messaging`), one WebSocket `/ws` event bus. Unity talks to it over REST + WS.

**Python ML SDK (`forge` package):** the thing that makes "drop it in any repo and the visualizations pop up" true. See 8.3.

**Voice:** Grok Voice Agent API (realtime WebSocket). Run the voice session inside the hub (Pipecat has a Grok realtime service) using the laptop mic for now. When the Quest arrives, stream headset mic audio from Unity to the hub.

**Pet brain heavy lifting:** a strong reasoning model (Claude or Grok reasoning) called through the `delegate` tool.

**Messaging:** WhatsApp Cloud API webhook into the hub, exposed with a `cloudflared` or `ngrok` HTTPS tunnel.

**Assets:** pet from Quaternius "Ultimate Animated Animals" (CC0, comes animated) or an animated Sketchfab CC-BY model. Room/desk from Kenney/Quaternius or primitives.

---

## 5. Architecture

```
                 LAPTOP                                           UNITY APP (simulator / desktop / Quest 3)
 ┌──────────────────────────────────────┐                ┌─────────────────────────────────────────┐
 │ VS Code / terminal / browser         │                │ Rig: OVRCameraRig (XR) or DesktopRig    │
 │       │ screen capture               │  WebRTC video  │ Workspace (anchored to desk / TABLE)    │
 │ Screen Bridge web page ──────────────┼───────────────►│  ├─ Screen panels (WebRTC textures)     │
 │                                      │                │  ├─ ML: DataStack, Model3D,             │
 │ User's training script               │                │  │      ResultsWall, TrainingDash       │
 │   import forge; forge.watch(...) ────┼──► Hub         │  ├─ CAD: GLB viewer + params panel      │
 │                                      │                │  ├─ Pet: follow, idle, blob, speak      │
 │ Hub (FastAPI)                        │  REST + WS     │  └─ Notifications                       │
 │  ├─ ml        ◄──────────────────────┼───────────────►│                                         │
 │  ├─ cad (build123d → GLB)            │                └─────────────────────────────────────────┘
 │  ├─ agent (Grok voice + delegate)    │
 │  ├─ messaging (WhatsApp webhook)  ◄──┼── cloudflared tunnel ◄── WhatsApp Cloud API
 │  └─ /ws event bus                    │
 └──────────────────────────────────────┘
```

Rules:
- The hub owns all state. Unity is a view.
- The pet changes the world only by calling the same REST endpoints the UI uses.
- Every Unity module must run from **mock JSON** (`Assets/_Forge/Mocks/`) with the hub off.

---

## 6. Unity project layout and team git rules (read this, merge conflicts kill hackathons)

```
gatorhack-dubz/                   <- repo root
  PROJECT_CONTEXT.md
  CLAUDE.md
  unity/                          <- the Unity project
    Assets/_Forge/
      Core/                       <- Hudson: rigs, desk/sit flow, panel system, Main.unity, Net (WS/REST client)
      Screens/                    <- Hudson: WebRTC screen panels
      ML/                         <- ML pair: DataStack, Model3D, ResultsWall, TrainingDash (one subfolder each)
      CAD/
      Pet/                        <- Adrian
      Mocks/                      <- JSON mocks matching section 7
      ThirdParty/
  hub/                            <- FastAPI: ml/, cad/, agent/, messaging/, events.py
  forge-sdk/                      <- pip-installable `forge` package for training scripts
  screen-bridge/                  <- static web page for capture + WebRTC
  demo/                           <- script.md, fallback/ (recorded runs, backup video)
```

Rules:
1. Same Unity version for everyone. Never upgrade mid-hackathon.
2. Project Settings: **Asset Serialization = Force Text**, **Version Control = Visible Meta Files**. Use the standard Unity `.gitignore` (never commit `Library/`, `Temp/`, `Obj/`, `Build/`, `UserSettings/`). Git LFS for `.fbx .glb .png .psd .wav`.
3. **Only Hudson edits `Main.unity`.** Everyone else builds in their own sandbox scene (`Assets/_Forge/<Module>/<Module>Sandbox.unity`) and ships **prefabs**. Integration = dropping prefabs into Main.
4. Only touch files in your own module folder. `Packages/manifest.json` and Project Settings changes are announced in chat first.
5. Commit and push small and often. Pull before you start each chunk.

### 6.1 Unity setup (one-time, per machine)

**If `unity/` is already in the repo (teammates):**
1. Install Unity Hub, then editor **6000.6.7f1** with the **Android Build Support** module (includes OpenJDK + Android SDK/NDK). Android is only needed for the Quest build, but install it now so nobody has to switch later.
2. Install the **Meta XR Simulator** v207.1: https://developers.meta.com/horizon/downloads/package/meta-xr-simulator-windows/ (or in Unity: `Window > Meta > Meta XR Simulator` > Install).
3. Clone the repo, then in Unity Hub: **Add > Add project from disk** and pick `gatorhack-dubz/unity`. First open takes a while (packages resolve from `Packages/manifest.json`).
4. Run the simulator check below.

**Creating the project (Hudson, once):**
1. Unity Hub > **New project** > editor `6000.6.7f1` > template **Universal 3D (URP)**. Name `unity`, location the repo root, so the project lands at `gatorhack-dubz/unity/`. Turn off Unity Cloud and Unity Version Control.
2. Add the `.gitignore` (Unity standard) and `.gitattributes` (LFS) at the repo root **before the first commit** so `Library/` never enters history.
3. `Edit > Project Settings > Editor`: Asset Serialization = **Force Text**, Version Control = **Visible Meta Files** (Force Text is the default).
4. Packages: add these to `unity/Packages/manifest.json` (or install them by name in Package Manager):
   ```json
   "com.meta.xr.sdk.all": "207.0.1",
   "com.unity.xr.openxr": "1.18.0",
   "com.unity.inputsystem": "1.20.1"
   ```
   When Unity asks to enable the new Input System backend and restart, say yes.
5. `Edit > Project Settings > XR Plug-in Management`: on the **Windows/Standalone** tab tick **OpenXR**, then tick the **Meta XR** feature group. Do the same on the **Android** tab. Use OpenXR, not the legacy Oculus plugin.
6. `Meta > Tools > Project Setup Tool`: **Fix All**, then **Apply All**, for both Standalone and Android.
7. Create the `Assets/_Forge/...` folders from section 6 and save `Assets/_Forge/Core/Main.unity`.
8. Close Unity, commit, push.

**Simulator check (everyone):**
1. Camera rig: `Window > Meta > Building Blocks > Camera Rig` (or the `OVRCameraRig` prefab). Delete the default `Main Camera`.
2. Click the **Meta XR Simulator** toggle in the toolbar next to Play (or `Window > Meta > Meta XR Simulator > Activate`), then press Play.
3. In the simulator window: **Inputs** panel, set left/right to keyboard + mouse. **Choose environment** picks a synthetic room (living room, game room, bedroom...); these rooms have tables for MRUK testing.
4. For passthrough + MRUK scenes: set Passthrough and Scene support to **Supported** in `OVRProjectConfig` (Project Setup Tool can do this). If passthrough shows black, re-run the Project Setup Tool and check URP post-processing/HDR isn't overwriting alpha.
5. Play-mode logs are in `unity/Logs/Editor.log`.

---

## 7. Contracts (lock in hour 1, then build in parallel)

### REST (hub at `http://<laptop-ip>:8000`)

```
GET  /ml/classes                      -> ["airplane", "automobile", ...]
GET  /ml/dataset?split=val            -> [{ id, label_idx, thumb_url }]
GET  /ml/atlas?split=val              -> { atlas_url, cell: [w,h], cols, index: { id: cellIndex } }
GET  /ml/image/{id}                   -> PNG
GET  /ml/graph                        -> { layers: [{ name, type, in_shape: int[], out_shape: int[], params }] }
POST /ml/train   { epochs }           -> { run_id }
GET  /ml/results/{run_id}             -> { samples: [{ id, true_idx, pred_idx, probs: float[], correct }] }
GET  /ml/confusion/{run_id}           -> { labels: string[], matrix: int[][] }
POST /ml/samples/{id}/relabel { label_idx }
POST /ml/samples/{id}/remove

GET  /cad/params                      -> { part, params: [{ name, value, min, max, unit }] }
POST /cad/params  { name: value, ...} -> { version }
GET  /cad/model.glb?v={version}

POST /agent/text  { pet_id, text }    -> { reply }        (typed fallback)
POST /dev/fake_message { from, text } (demo safety net)
```

### WebSocket events (`/ws`, JSON `{ "type": ..., "payload": {...} }`)

```
ml.run_state     { run_id, status: "idle"|"training"|"evaluating"|"done", epoch, total_epochs }
ml.metrics       { run_id, epoch, step, train_loss, train_acc, val_loss, val_acc, lr }
ml.overfit_alert { run_id, epoch, reason }
ml.graph_ready   { }
ml.results_ready { run_id }
ml.sample_updated{ id, label_idx, removed }

cad.model_updated{ version, params }

pet.state        { pet_id, mode: "follow"|"idle"|"listening"|"thinking"|"working"|"speaking", target_panel? }
pet.say          { pet_id, text, card?: { title, bullets: string[], actions?: [{ label, action_id }] } }
ui.highlight     { panel, item_ids?: string[], class_idx? }

notify.message   { from, text, thread_id }
notify.sent      { to, text }
```

Panel IDs (used in `ui.highlight` and `target_panel`): `screen-main`, `ml-datastack`, `ml-model3d`, `ml-results`, `ml-training`, `cad-viewer`.

C# side: one `ForgeEvents.cs` with matching `[Serializable]` classes and a static event per type, owned by Hudson. Python side mirrors it in `hub/events.py`.

---

## 8. Module specs

### 8.1 Core: rigs, room, sit flow, panel system (Hudson)
- `RigSelector`: if an XR runtime is active (headset or simulator) use `OVRCameraRig` with Interaction SDK hands/controllers; else spawn `DesktopRig` (CharacterController, WASD, mouse look, click raycast).
- Room scene with desk (desktop and simulator). On Quest: passthrough on, virtual room off, MRUK finds nearest `TABLE`.
- Sit flow: near desk + `E` (desktop) or approach table (XR) -> `Workspace.Unfold()` staggered DOTween in of keyboard glow, trackpad glow, main screen, side panels in an arc anchored to the desk's near edge.
- `ForgePanel` prefab: world-space canvas + frame + title + grab handle. Interactable by mouse (desktop) and by ray/poke (Interaction SDK `PointableCanvas`). Every module's UI lives inside a ForgePanel.
- `ForgeNet`: WS client (NativeWebSocket) + REST helper + mock mode toggle that replays `Mocks/*.json`.
- Mode switch: ML mode / CAD mode.
- Performance: Quest at 72 fps. GPU instancing for repeated meshes, 2048 max textures, no allocations in `Update`.

### 8.2 Screens (Hudson)
- `screen-bridge/index.html`: "Share window" buttons, each a WebRTC video track labeled (`vscode`, `terminal`, `browser`). Signaling through the hub WS.
- Unity: `com.unity.webrtc` receiver -> `RenderTexture` on a curved panel. Main screen center, extras angled left/right.
- Acceptance: VS Code readable at 1 m in the simulator.
- Fallback: hub serves screenshots at 5 fps over WS.

### 8.3 ML: Python SDK + hub (ML person A)
- `forge-sdk`: the magic. In any training script:
  ```python
  import forge
  forge.watch(model, val_dataset, classes=CLASSES)   # sends graph + dataset atlas
  for epoch ...:
      forge.log(epoch=e, train_loss=..., train_acc=..., val_loss=..., val_acc=..., lr=...)
  forge.evaluate(model, val_loader)                  # sends per-sample preds + confusion
  ```
  Optional `forge.lightning.Callback()` / `forge.keras.Callback()` if time allows.
- Demo project: **CIFAR-10**, small CNN, 1,000 image val subset shown in XR. **Inject ~3% label noise on purpose** (record which) so the "bad data" moment is guaranteed.
- Overfit rule: val loss up 2 epochs in a row while train loss down -> `ml.overfit_alert`.
- Relabel/remove -> `labels_override.json`, used on the next run.
- **Record-and-replay**: save a full run to `demo/fallback/`; `forge replay` streams it at demo speed. The demo never depends on training behaving live.

### 8.4 ML: Unity panels (ML person B, plus A on TrainingDash)
- **DataStack (left):** thin cards (instanced quads, atlas UVs). Scroll / thumbstick / swipe scrubs (cards fan as you pass). Select: card flies forward large with label. Put back.
- **Model3D (center left):** block per layer, size from log of out_shape, color by type (conv, pool, linear, norm, activation). Input shape label above, output shape below. Pulse travels down per metrics tick.
- **ResultsWall (right):** grid of all val images, green/red tint. Filters All / Wrong / by class. Click: expanded card with image, predicted, true, probability bars, buttons Relabel (class picker) / Remove / Looks fine.
- **TrainingDash (above):** loss + accuracy curves (train vs val), epoch bar, LR, overfit banner. On done: confusion matrix heatmap (click a cell to filter ResultsWall to that pair) and per-class accuracy.
- All four run from mocks first.

### 8.5 CAD
- Default part: model rocket in build123d with params: body length, body diameter, nose length, nose shape (cone/ogive), fin count, fin span, fin sweep.
- `POST /cad/params` regenerates, exports GLB, emits `cad.model_updated`. Target under 2 s.
- Unity: glTFast loads `/cad/model.glb?v=N`, swaps the mesh with a quick dissolve. Turntable, grab to rotate. Params panel with sliders.
- Hand-edit path: `rocket.py` open in VS Code on the main screen; file watcher regenerates on save.
- **Pending:** review the open-source repos Hudson found and decide what to reuse.

### 8.6 Pet body (Adrian)
- States driven by `pet.state`: `follow` (about 1 m beside and behind the user, NavMesh or simple steering, faces user when they stop), `idle` (on desk when seated, sit, look around, fidgets), `listening`, `thinking`, `working` (blob morph, arc jump into `target_panel`, panel pulses), `speaking` (pops out, speech bubble + answer card with action buttons).
- CAD tinkering: walks to rocket, sparks loop while regenerating.
- Name tag and soft glow so it reads on passthrough.
- Works standalone in `PetSandbox.unity` with a keyboard to trigger each state.

### 8.7 Pet brain, voice, WhatsApp (person 5)
- Voice: Grok Voice Agent session in the hub. Push-to-talk (spacebar on desktop, trigger/pinch-hold in XR) to start; server VAD ends the turn.
- **Fast tools** on the voice model (answer in under a second): `ml_get_run_summary`, `ml_relabel`, `ml_remove`, `ml_start_training`, `cad_get_params`, `cad_set_params`, `ui_highlight`, `pet_go_to`, `messages_read_latest`, `messages_send` (confirms with the user first).
- **`delegate(task)` tool** for anything that needs real analysis ("why is it bad at cats?", "suggest a design change"): the hub runs a reasoning model with read access to run results / CAD script, returns a card. While it runs, emit `pet.state working` (blob dive) and have the voice model say "give me a sec."
- Every tool call also emits `pet.state` so the body animates.
- Persona: short, cute, competent. 1 to 3 spoken sentences, details go in the card.
- WhatsApp: Meta app + Cloud API test number, add teammate phones to the allowlist, webhook through the tunnel -> `notify.message`. Replies via the Cloud API within the 24-hour window. `/dev/fake_message` as backup.

---

## 9. Scope tiers

**MUST:** desktop mode end to end; sit flow + unfold; one streamed VS Code screen; all four ML panels with a real or replayed run; relabel/remove; pet follow/idle/blob/speak; voice (or typed) Q&A with ML tools; CAD rocket changed by voice; one WhatsApp round trip.

**SHOULD:** runs in Meta XR Simulator; Quest 3 build with passthrough + MRUK table detection; multiple screens; confusion matrix click filtering; overfit alert.

**STRETCH:** wake word; second pet; AI edits CAD script directly; second CAD part; counter trigger; Lightning/Keras callbacks.

---

## 10. Team split (5 people)

| Who | Owns | Hour 1 to 2 deliverable | By midpoint |
|---|---|---|---|
| **Hudson** | Core + Screens + integration (Main.unity, ForgeNet, ForgeEvents, panel system) | Unity project pushed with packages, git rules, empty room, desk, DesktopRig, sit + unfold with placeholder panels, `ForgePanel` prefab | VS Code streaming in; all module prefabs integrated in Main |
| **ML A** | `forge-sdk` + hub `ml` router + replay; co-owns TrainingDash | Contracts + mock JSON committed; CIFAR-10 script training with `forge.log` printing events | Live run streaming to Unity; fallback run recorded |
| **ML B** | Unity ML panels: DataStack, Model3D, ResultsWall (+ TrainingDash with A) | DataStack and ResultsWall rendering from mocks in sandbox | All four panels live against hub |
| **Adrian** | Pet body (model, animations, follow, desk hop, blob, speech bubble, cards) | Pet in `PetSandbox` following a DesktopRig, keyboard-triggered states | Driven by `pet.state` / `pet.say` from the hub |
| **Person 5** | Hub skeleton + agent brain + voice + WhatsApp + CAD backend until CAD gets an owner | Hub running with `/ws` broadcasting mock events; Grok voice round trip from laptop mic | Tools wired to ML + CAD endpoints; WhatsApp round trip |

Hand-offs: Hudson publishes `ForgePanel` + `ForgeNet` within ~2 hours; until then everyone uses a plain world-space canvas and a local mock loader. CAD Unity viewer goes to whoever frees up first (likely Hudson after integration).

When the Quest 3 arrives: Hudson + Adrian do the device pass (APK build, passthrough, MRUK table, hand interactions on panels, mic streaming). Windows laptop with Quest Link is the fastest loop; on Mac, build and sideload the APK each time.

---

## 11. Decisions

1. **Engine:** Unity 6 + Meta XR SDK. Decided.
2. **Voice:** Grok Voice Agent API (`grok-voice-think-fast-1.0`) + `delegate` to a reasoning model. ElevenLabs Agents is the fallback if Grok's voices don't fit the pet's personality.
3. **Messaging:** WhatsApp Cloud API with test number. Decided.
4. **CAD engine:** build123d unless Hudson's repos give something better. **Pending repo review.**
5. **Headset:** Quest 3 expected in about a day. Laptop/simulator first.
6. **Unity version:** `6000.6.7f1` (Unity 6.6), Meta XR SDK 207.0.1, OpenXR 1.18.0, simulator v207.1. Decided.
7. **Pet model:** Adrian picks in hour 1.

---

## 12. Rules for coding agents (copy into CLAUDE.md)

- Read this file first. Work only inside your module folder (section 6) unless told otherwise.
- Contracts in section 7 are law. Propose changes, never silently change them.
- Mock first. Every Unity module runs in its own sandbox scene from `Assets/_Forge/Mocks/` with the hub off.
- Never edit `Main.unity`, `Packages/manifest.json`, or Project Settings unless you are the Core owner.
- Ship features as prefabs. No scene-only setup that someone else has to recreate.
- UI goes inside `ForgePanel` and must work with mouse (DesktopRig) and Interaction SDK ray/poke.
- Quest performance: no allocations or LINQ in `Update`, instancing for repeated meshes, textures 2048 max.
- Unity networking: NativeWebSocket for WS, `UnityWebRequest` for REST, Newtonsoft for JSON. Marshal WS callbacks to the main thread.
- Python: FastAPI routers per module, type hints, pydantic models mirroring `events.py`.
- Never send a real message or take an irreversible action from the pet without a user confirmation step.
- Small commits, pull often, never break `main`.
