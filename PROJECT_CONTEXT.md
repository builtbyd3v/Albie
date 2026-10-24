# Forge (working name): Spatial Workspace for Engineers

> Single source of truth for the hackathon. Every human and every coding agent reads this first.
> "Forge" (project) and "Bit" (pet) are placeholder names. Rename freely.
> v2: switched to Unity + Meta XR SDK. Laptop-first until the Quest 3 arrives.
> v3 (2026-10-24): messaging switched from WhatsApp Cloud API to Slack (Bolt for Python, Socket Mode). No tunnel, no allowlist, no 24-hour window. Section 10 (team split) is stale; the real split lives in chat.

## 0. Status (update this as you go)

**2026-10-24, Hudson (Core, branch `core/dock-slide`):**
- **Done: slide the dock along the desk.** On a long desk it can now sit in front of you. Hold the right controller's grip (middle finger) while pointing anywhere on the dock and drag it left or right (middle mouse button in desktop mode). Or point at either end of the dock: a ‹ or › fades in, and each press moves it 15 cm. It stays on the desk with 12 cm free at each end (the whistle's spot and the crew egg's corner), dims the arrow that can't go further, remembers where you put it at that edge, and starts centered again at another edge or table. The hub status words moved above the corner light.
- **Done: the whistle sits beside the dock.** Once the dock shows on a table, that table's whistle sits just past the dock's right end and slides with it (‹ ›, going round the desk); it stays there while the dock is away.

**2026-10-24, Hudson (Core, UI round 2, branch `core/ui-restyle`):**
- **Fixed: text and UI in XR.** In single-pass stereo (simulator, Quest), everything drawn with a built-in Unity shader rendered from the middle of the head into the left eye only: TextMeshPro, uGUI images, panel backgrounds and the Interaction SDK cursor. That's why dock labels sat off their rings, drifted with the head and were missing from the right eye. URP 17.6 leaves the engine's legacy stereo matrices on the mono camera. `Core/Scripts/ForgeXRStereoFix.cs` installs itself in every scene and fixes it for all modules; nothing to do on your side. Verified in the simulator (both eyes); still to check on the Quest (multiview).
- **Done: polish.**
  - The dock's hover comet runs around the rim, and the hub status shows in words beside the corner light.
  - Modes unfold out of the ring you pressed, on springs, and fold back into it. Flat / Curve / Reset spring into place.
  - Panels stay out of the room's walls (`RoomFit`, from MRUK).
  - The panel badge is legible. The frame lights up when pointed at and lifts while held.
  - Placeholder panels and volumes show proper empty states.
  - Volumes show a hint line (`ForgeVolume.SetHint` / `SetCaption`; `Title` is never rewritten).
  - The desktop hint toast fades.

**2026-10-24, CAD experience (branch `ui/cad-experience`, PR #35, stacked on #29):**
- **Done: CAD round 4, for every design (rocket, quadcopter, gearbox, and any new part file).**
  - **Hub (additive, `CAD_PLAN.md` section 5 "Round 4"):**
    - per-version `analysis`: explode vectors, demo (launch / hover / spin), markers, dimensions, mass breakdown, verdict, constants
    - flight samples and events on `/cad/flight`
    - parameter groups, help, defaults and the parts each one shapes; version labels
  - **Viewer:**
    - Explode springs each part out along its own direction, with labels and leaders.
    - Fly launches the rocket along its simulated trajectory, scaled to the room. Hover lifts the quad. Run turns the gearbox at its true ratio.
    - Markers and dimensions sit on the model.
    - Expand brings it up big, from the panel or from a ring on the desk.
  - **Panel:**
    - Tabs Overview · Parameters · Analysis · History.
    - A verdict card ("Will it fly?") with a dial and a to-scale view, and live telemetry while it flies.
    - Grouped parameters with steppers, analysis gauges and mass bars, version history.
  - Mocks cover all of it, so it works with the hub off.
  - Mockups: the design canvas row "CAD: the design panel and the viewer".

**2026-10-23, Hudson (Core):**
- **Done:** Unity project pushed in `unity/` (6000.6.7f1, Meta XR SDK 207.0.1, OpenXR 1.18.0, all Meta setup fixes applied). Teammates: clone and open it, see 6.1; no SDK setup needed.
- **Done:** `Assets/_Forge/Core/Main.unity`: passthrough camera rig + Interaction SDK hands/controllers (ray + poke) + MRUK room. Floor-level tracking origin.
- **Done: desk flow.** `DeskPresence` tracks Away / Near (head within 0.7 m of a table edge) / Seated (near + head below 1.35 m). Any table works. At the desk, `DeskDock` lays round mode buttons (ML, CAD, CODE, MSG) flat on the desk; pressing one makes `Workspace` unfold that mode's panels in an arc around your eyes and its volumes on the desk. Walking away folds everything. Verified in the simulator.
- **Done: building blocks for modules** (`Assets/_Forge/Core/Prefabs/`):
  - `ForgePanel.prefab`: flat window (world-space canvas, 1 unit = 1 mm, title bar). Put your UI under `Content`. Ray + poke already wired, so uGUI Buttons/Sliders/ScrollRects work with hands and controllers.
  - `ForgeVolume.prefab`: 3D container. `SetContent(go)` scales your model to fit `size` and stands it on the base; `turntable` spins it.
  - To plug a module in: make a prefab from these (a prefab variant is easiest), send it to Hudson, and it goes into that mode's item list on the `Workspace` object in Main (slot + prefab). Panel IDs come from section 7.
- **Done:** `Assets/_Forge/<Module>/` folders exist. Make your sandbox scene in yours.
- **Done: desktop mode.** Press Play with the simulator off (or tick **Forge > Desktop Mode (skip XR)**) and you get a first-person player instead of the VR rig: WASD walk, mouse look, click buttons under the crosshair, E to sit at the desk. The room is a JSON copy of the simulator's living room (`Core/Rooms/SimLivingRoom.json`) drawn as blocks, so the desk is in the same place. Same scene, same panels; build and test UI here, check the XR feel in the simulator. See 6.1.
- **Done: `ForgeNet` (hub connection).** In `Main` (and created automatically in any sandbox that uses it). Subscribe with `ForgeEvents.OnCadModelUpdated += ...` (one static event per section 7 type, classes in `Core/Net/ForgeEvents.cs`) or `ForgeNet.On("any.type", ...)`; call REST with `ForgeNet.Get/GetBytes/Post`, or `ForgeNet.Url(path)` for glTFast. Send on the socket with `ForgeNet.Send(type, payload)` (JSON event) or `ForgeNet.SendBinary(bytes, offset, count)`; binary frames from the hub arrive as `ForgeNet.BinaryReceived` (main thread, in order with events). Both drop while not Live. Pass `replayLast: true` to `On` to get the latest event right away (panels spawn after the hub sent the current state). No hub running: after 3 s it switches to mock mode, REST reads `Resources/Mocks/<path>.json` (or `ForgeNet.MockRest`), and a `ForgeMockPlayer` in your sandbox plays a JSON list of events. The desk dock's corner light shows green live / amber mock / grey connecting. Hub address: `hubUrl` on the ForgeNet object (Quest: the laptop's LAN IP).
- **Done: move, resize and turn panels and volumes** (mouse in desktop mode, controller/hand rays in XR; verified in both). Point at a panel and its handles fade in: drag the **title bar** to carry it (it stays on the end of your pointer; scroll wheel / thumbstick / reaching out pushes it back or pulls it in), drag a **bottom corner bracket** to resize, drag a **side bar** to turn it or the **bottom bar** to tilt it (snaps to straight / facing you), or press **Face me** / **Straight** in the title bar. Volumes: drag the **label/base** to slide it on the desk, drag the **model** to spin and tilt it, scroll to raise it (base) or scale it (model). The desk dock has **Flat / Curve** (panels in one straight row like a monitor wall, or the arc) and **Reset**. Each mode remembers what you moved for the session. Module owners: users can now resize your panel, so anchor your UI to stretch with `Content` (or scroll) instead of a fixed layout; resize through `ForgePanel.SizeMeters`, never the transform's scale. Not tested yet: hand pinch-ray and finger poke (same code path as the controller ray, should work), thumbstick push (no stick in the CLI-driven simulator).
- **Done: new look (2026-10-24, branch `core/ui-restyle`), verified in desktop mode and the simulator.** The desk dock is light drawn on the desk: one glowing frame line, mode buttons as neon rings that brighten as a pointer comes near, run a comet of light on hover, and spring up on a light column toward your head when their mode is open (mode hues: ML `#4D8DFF`, CAD `#FF8A3D`, CODE `#22CF86`, MSG `#A77BFF`). Panels are graphite with a 64 mm header (mode badge, title, optional subtitle), Horizon-style Straight / Face me pills and glowing white handles; volume bases are rings of light tinted with the mode hue. Core text is Inter (modules keep their own fonts). The dock's outer buttons (ML, Reset) now work with rays and pokes too. Module owners: `ForgePanel.Content` insets changed (18 mm sides, 16 mm bottom, 78 mm top), so check your panel still fits; `ForgePanel.Subtitle` gives the header a second line, and `ForgePanel.Accent` / `ForgeTheme` (`Core/Scripts/ForgeTheme.cs`) have the mode colors if you want to match. Your prefab variants picked the look up automatically (the base prefabs were restyled in place: **Forge > Restyle Panel Prefab / Restyle Volume Prefab**, never Rebuild Core Prefabs). The Screen Bridge web pages match too.
- **CAD hub done (phase 1 of `CAD_PLAN.md`):** `hub/` has a minimal FastAPI skeleton (`main.py`, `events.py`, `/ws`) and the full `hub/cad/` module: build123d rocket, GLB + cad-vision metadata, stability analysis, flight sim, undo, script-save regen, pet tools, 15 passing tests. Proposed CAD contract additions: `CAD_PLAN.md` section 5. Person 5: the skeleton is yours to grow. See `hub/README.md`.
- **CAD Unity done (phase 2):** `Assets/_Forge/CAD/Prefabs/CadViewer.prefab` (ForgeVolume variant, slot `cad-viewer`) and `CadParamsPanel.prefab` (ForgePanel variant, slot `cad-params`) work from mocks (`CADSandbox.unity`) and live against the hub. **Integrated into Main:** CAD mode on the desk now unfolds the real rocket and parameters panel (slotted by `MainSceneBuilder`), and `CadSession` gets changes from ForgeNet's `/ws` events instead of polling and uses ForgeNet's hub address (on the Quest, set `hubUrl` on the ForgeNet object to the laptop's IP). Still to do for player builds: include glTFast's shader graph (see "Notes for Core" in `Assets/_Forge/CAD/README.md`).
- **CAD model library (2026-10-24):** more than the rocket. The hub has a library of designs (rocket, quadcopter, gearbox; a new file in `hub/cad/parts/` is a new design, copy `_template.py`), each with its own history, presets and metrics. The right panel works for any design: a **Designs** menu to switch, presets, a gauge per metric with a range (the rocket's margin, the drone's prop clearance), scrolling rows, Fly only for the rocket. `cad.model_updated` gained `part`, `title` and `analysis.metrics` (`events.py` + `ForgeEvents.cs`). Details: `CAD_PLAN.md` section 5, `hub/README.md` "Designs".
- **CAD Inspect, take apart, drawing (2026-10-24):** **Inspect** on the design panel (or key I) brings the model up big in front of you; there you can pull parts off with the mouse or a controller / hand ray, turn them, snap them back or Reassemble (a card shows each part's name, material, mass). The hub draws every version as an engineering sheet (`GET /cad/drawing.png`), shown by `CadDrawingPanel.prefab` (slot `cad-drawing`, tap to zoom). Core change made: `VolumeManipulator.ModelAway`.
- **CAD screens (2026-10-24, branch `cad/versions-and-drawing`):** CAD mode is now: the **drawing on the middle screen** (in place of VS Code, which stays in ML and SCR), the model on the desk in front of it, the design panel on the right, and a real **Versions** panel on the left (`CadVersionsPanel.prefab`, slot `cad-timeline`: the metric across versions, then every version; tap one to go back). Core: the main panel now stands behind whatever is in the middle of the desk (`Workspace.BehindDesk`), so the model no longer pokes through the screen.
- **CAD humanoid (2026-10-24, branch `cad/humanoid`):** a fourth design, **Humanoid robot**: Unitree's G1 (88 parts, 29 joints and two 3-finger hands, BSD-3, vendored in `hub/cad/data/unitree_g1/`) posed by 10 parameters, answering "Will it stand?" (balance margin over its feet, each motor's static torque against its rating), and **Walk**: one lap of Unitree's own trained walking policy, recorded in MuJoCo, played on the desk or life size. Expand gains a **Life size** pill. Hub: mesh parts (`cad/meshes.py`), URDF kinematics (`cad/robot.py`), `FixedMass`, `GET /cad/motion`, a mesh path for drawings. Details: `CAD_PLAN.md` section 5 "Round 5", the CAD README.
- **Pet plan written: `PET_PLAN.md` (2026-10-24).** How Bit lives in Main: `DeskPresence` is the clock, an `ArrivalDirector` does hop → Slack digest → open the CODE screen, the crew is gated to CODE, the hub supervises Dev's screen bridge so `uvicorn` is the only process, and the deprecated typed paths go. PR order and the Core edits Adrian makes with Hudson's OK are in its section 5.
- **ML plan written: `ML_PLAN.md`.** Open any ML repo: an LLM classifier reads the code and picks its type (image classifier, regression, ...), each type has a fixed list of visuals, and a second step writes the glue code into `.forge/`. ML A + ML B: start with its section 0 (pick and clean your repos); the image classification layout is its section 5.3. Open: which LLM and which live training graphs (the ML team decides), and how VS Code connects (ML_PLAN section 3, doesn't block ML work).

**2026-10-24, Adrian (Main wiring, branch `feat/main-wiring`):**
- **Done: Main slots every module.** `Forge > Build Main Scene` now puts the pet in the room (`Pet/Prefabs/PetRuntime.prefab`), the Messages panel in MSG mode (`Messages/Resources/MessagesPanel.prefab`) and the ML panels in ML mode (`ML/Resources/MLPanels/*`, all ForgePanel variants, so `ml-datastack` and `ml-model3d` are panels now, not volumes), next to the CAD and Screens prefabs; a missing prefab is a warning and a placeholder. The SCR mode became **CODE** (just the `screen-main` VS Code panel): the landing mode the pet opens after the sit-down digest. For modules: `Workspace.Current`, `Workspace.TryGetItemTransform(itemId, out t)` (an open panel or volume by item id).

---

## 1. The pitch

Meta just announced its VR Glasses (Project Phoenix) at Connect 2026: a lightweight headset shipping spring 2027 for $1,299 that connects to a PC or Mac, gives you a virtual keyboard and touchpad, and sells multiple virtual monitors as the killer productivity feature. That demo is "your laptop, but with more screens."

**We are building what comes after more screens.** You sit down at your desk, the workspace unfolds around you, and the space itself understands your engineering work:

- An ML engineer sees their data, their model, and their training run as physical objects they can grab, inspect, and fix.
- A mechanical engineer sees their CAD part next to them and changes it by talking.
- An AI pet (your always-on agent) follows you around, sits on your desk while you work, dives into your screens to investigate, and handles the interruptions (Slack messages) so you never leave the flow.

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
   - **Right:** results panel and a dot wall: one green (correct) or red (wrong) dot per validation image. "Show mistakes" opens a gallery of the wrong ones; click one: image, predicted label, probability bars for every class, true label (ML_PLAN 5.3).
   - User spots a mislabeled image and hits **Relabel** or **Remove**. Model was right, data was wrong.
   - Confusion matrix appears automatically when training finishes.
4. **Ask the pet.** "Hey Bit, why is my model so bad at cats?" Bit says "one sec," morphs into a glowing blob, leaps into the results panel, the cat row lights up in the confusion matrix, then Bit pops back out with an answer card and suggested actions.
5. **CAD mode.** A model rocket appears with a parameter panel. "Bit, make the body tube 20% longer and add a fourth fin." Bit walks to the rocket, tinkers, the rocket regenerates on the spot.
6. **Team admin.** A Slack message arrives. Bit perks up: "Hudson in #forge-team asks if the demo build will be ready by 3, want me to answer?" User: "Yeah, tell him yes and I'll ping him when it's pushed." Bit writes the reply, a card pops up beside it with the exact text and Send / Cancel; the user taps Send (or says "send it"), and the message lands in the thread **under the user's own name** and buzzes the teammate's phone. (Sender and text are placeholders; "mom says dinner in 30" works just as well as a Slack DM.) Earlier, in step 2, Bit already greeted the user with the unread digest, see 8.8. Tested live against the real workspace, no fake path.
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
| Real Slack messages, as you | **Yes, Slack app + the user's own token** | One Slack app ("Bit") installed in the team workspace with both bot and **user** scopes. Over Socket Mode (outbound WebSocket, no tunnel) the hub receives every DM and channel message the user can see, and it posts with the user's token, so replies appear under the user's name on everyone's phone. The bot user is only the app's identity. No sandbox limits. |
| Pet morphs into a blob and dives into a screen | **Yes, animation** | Swap to a blob mesh (noise shader), squash/stretch, DOTween arc into the panel, panel glows. |
| Multiple pets | **Data model yes, demo 1** | Pet = persona + tool set + prefab. |
| Kitchen counter context trigger | Stretch | Same MRUK labels, different anchor. |

---

## 4. Tech stack

**Unity (the app)**
- **Unity 6.6, editor `6000.6.7f1`. Everyone on this exact version.** (Matches Hudson's working Meta XR test project.)
- Meta XR All-in-One SDK `com.meta.xr.sdk.all` **207.0.1** (Core, Interaction SDK, MR Utility Kit), OpenXR plugin `com.unity.xr.openxr` **1.18.0** with the Meta XR feature group enabled, Input System **1.20.1**, URP **17.6.0**
- Meta XR Simulator **v207.1**, installed separately (Windows: `C:\Program Files\MetaXRSimulator\v207.1`; Apple Silicon Mac is officially experimental, expect some fiddling). See 6.1 for setup.
- `com.unity.webrtc` (screen streaming), `glTFast` (runtime GLB loading for CAD), WebSocket: .NET `ClientWebSocket` (built in, works on Windows and Quest; no NativeWebSocket package needed), `com.unity.nuget.newtonsoft-json`
- TextMeshPro, DOTween (free) for animation, XCharts for charts
- Render pipeline: URP, Android target for Quest, Vulkan

**Hub (backend on the laptop):** Python 3.11, FastAPI, one process, routers per module (`ml`, `cad`, `agent`, `messaging`), one WebSocket `/ws` event bus. Unity talks to it over REST + WS.

**Python ML SDK (`forge` package):** the thing that makes "drop it in any repo and the visualizations pop up" true. See 8.3.

**Voice:** Grok Voice Agent API (realtime WebSocket). Run the voice session inside the hub (Pipecat has a Grok realtime service) using the laptop mic for now. When the Quest arrives, stream headset mic audio from Unity to the hub.

**Pet brain heavy lifting:** a strong reasoning model (Claude or Grok reasoning) called through the `delegate` tool.

**Messaging:** Slack app via `slack-bolt` (async) + `slack-sdk` in **Socket Mode** inside the hub process. `hub/.env`: bot token (`xoxb-`, the app's identity), app-level token (`xapp-`, the socket), and the **user token** (`xoxp-`, read the user's inbox and post as them). The user's id is derived from the user token. No tunnel.

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
 │  ├─ messaging (Slack Socket Mode) ◄──┼──► Slack (outbound WSS, no tunnel)
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
  ml-examples/                    <- ML test repos (CIFAR-10 + teammates' cleaned repos), see ML_PLAN.md section 4
  screen-bridge/                  <- static web page for capture + WebRTC
  demo/                           <- script.md, fallback/ (recorded runs, backup video)
```

Rules:
1. Same Unity version for everyone. Never upgrade mid-hackathon.
2. Project Settings: **Asset Serialization = Force Text**, **Version Control = Visible Meta Files**. Use the standard Unity `.gitignore` (never commit `Library/`, `Temp/`, `Obj/`, `Build/`, `UserSettings/`). Git LFS for `.fbx .glb .png .psd .wav`.
3. **Only Hudson edits `Main.unity`.** Everyone else builds in their own sandbox scene (`Assets/_Forge/<Module>/<Module>Sandbox.unity`) and ships **prefabs**. Integration = dropping prefabs into Main.
4. Only touch files in your own module folder. `Packages/manifest.json` and Project Settings changes are announced in chat first.
5. Commit and push small and often. Pull before you start each chunk.

### 6.1 Unity setup (teammates: ~20 min, mostly downloads)

The project is in the repo with all packages and settings already configured (Meta XR SDK, OpenXR, Project Setup Tool fixes, passthrough/scene support). **You don't redo any of that.** You only install the things that live on your machine, not in the repo:

1. **Unity Hub** and editor **6000.6.7f1** (exact version). Add the **Android Build Support** module (with OpenJDK + SDK/NDK) if you'll build for the Quest; not needed for the simulator.
2. **Meta XR Simulator v207.1** (Windows): https://developers.meta.com/horizon/downloads/package/meta-xr-simulator-windows/ (Apple Silicon Mac is experimental.)
3. **Git LFS**: `git lfs install` once, then `git clone https://github.com/hudmarr/gatorhack-dubz.git`.
4. Unity Hub > **Add > Add project from disk** > pick `gatorhack-dubz/unity`. First open takes 5 to 15 min while Unity downloads packages from `Packages/manifest.json`. Sign in to Unity Hub; no Asset Store purchase is needed (Meta packages come from the Unity registry).
5. Open `Assets/_Forge/Core/Main.unity` to test, or your own sandbox scene. Don't save changes to Main (section 6 rule 3).

If the console shows red errors right after the first import, wait for importing to finish and click into the editor once; a transient Meta Audio `UnityEditor.GUID` error clears itself on the next compile.

**Simulator check (everyone):**
1. `Window > Meta > Meta XR Simulator > Activate` (or the simulator toggle next to Play), then press Play in `Main.unity`.
2. The simulator window opens. Click **Choose environment** and pick the Living Room (without one, passthrough is black). Simulator keys: arrows look, WASD walk, R/F head up/down (F a few times = sitting), T trigger. Easiest clicking: Keyboard and mouse > **Point & click** on.
3. Pass: the console prints `[DeskLocator] Room ... loaded: 27 anchors, 1 table(s)`. Walk to the table (about 1 m to your right) and the mode buttons appear on it. The Unity Game view stays black; passthrough only renders in the simulator window.
4. Play-mode logs are also in `unity/Logs/Editor.log`.

**Desktop mode (no simulator, quickest for building UI):**
1. Simulator off (`Window > Meta > Meta XR Simulator > Deactivate`), or leave it on and tick **Forge > Desktop Mode (skip XR)**. Untick it to go back to the simulator.
2. Press Play and click the Game view. WASD walk (Shift runs), mouse look, left-click presses the button under the crosshair. Esc frees the cursor (then click UI under the cursor, hold right mouse to look, click empty space to grab the mouse again).
3. Walk to the table (about 1 m to your right), press **E** to sit, click a mode button. E again stands up.
4. The room is the simulator's living room as blocks (floor, walls, wood desk, grey furniture). Walls and furniture block you; the chair doesn't.

**Where things moved in SDK 207:** the Project Setup Tool is at `Edit > Project Settings > Meta XR` (no `Meta > Tools` menu). Building Blocks and the simulator are under `Window > Meta`.

**Rebuilding Main:** `Forge > Build Main Scene` regenerates `Main.unity` from code (`Assets/_Forge/Core/Editor/MainSceneBuilder.cs`). Core owner only.

**Coding agents driving the editor (optional):** the project has Unity's Pipeline package, so a Claude Code session can control an open editor with the `unity` CLI (`unity command ...`, `unity command eval '<C#>'`). Install the CLI (`irm https://unity.com/install.ps1 | iex` on Windows), keep Unity open on the project, and run commands from inside `unity/`. The skill is in `.claude/skills/unity-cli`. In a sandboxed agent `unity status` can wrongly say "no instances"; try `unity command` anyway.

<details><summary>How the project was created (reference only, already done)</summary>

Unity Hub > New project > 6000.6.7f1 > Universal 3D (URP), name `unity`, in the repo root, Unity Cloud/VCS off. Installed `com.meta.xr.sdk.all` 207.0.1 (My Assets), `com.unity.xr.openxr` 1.18.0 and `com.unity.inputsystem` 1.20.1 (Unity Registry), `com.unity.pipeline` (`unity pipeline install`). XR Plug-in Management: OpenXR + Meta XR feature group on Windows and Android. Project Setup Tool: Fix All on both tabs; graphics API forced to D3D11 on Windows; Subsampled Layout on; Application SpaceWarp left off (smears UI panels); Platform SDK items (DUC, app ID) ignored.
</details>

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

GET  /messages/latest?n=5            -> [SlackMessage]   SlackMessage = { channel, channel_name, is_dm, user, user_name, text, ts, thread_ts?, mentions_me,
                                                                        reply_count?, edited?, is_me?, user_initials? }   (the ? fields are null unless known)
GET  /messages/channels               -> { channels: ChannelSummary[] }   sorted: DMs / group DMs with unread first, then by last_ts desc
                                         ChannelSummary = { id, name, kind: "channel"|"private"|"dm"|"group-dm", unread, mentions, last_ts, last_text, topic, is_default }
                                         name: "#general" for channels; the other person's display name for a dm; the people, comma-joined, for a group-dm
GET  /messages/history?channel=<id>&limit=50&before=<ts>
                                      -> { channel, channel_name, kind, messages: SlackMessage[] (oldest -> newest, limit max 100), has_more }
                                         top-level only (a threaded message carries reply_count); the user's own messages included (is_me)
POST /messages/channels/{id}/seen     -> { ok, unread: 0 }   (that conversation is open in the panel; notify.channels follows)
GET  /messages/users/me               -> { user_id, name, display_name }   (who the hub posts as)
POST /messages/drafts { channel?, text, thread_ts?, reply_to_ts? } -> Draft   (Bit WRITES the reply; nothing is posted. Same as the messages_send tool.
                                         channel: an id, "#name", or a person's name = their DM)
PATCH /messages/drafts/{id} { text?, channel?, thread_ts? } -> Draft   (the user edited the composer; re-emits notify.draft with the same draft_id and edited: true)
POST /messages/drafts/{id}/send      -> { ts }   (the Send button, or "yes, send it" through messages_confirm; posts under the USER's own name)
POST /messages/drafts/{id}/cancel    -> { ok }
POST /messages/digest                 -> { count, mentions, line, messages: SlackMessage[] }   (Unity calls it when the user sits down; counts every conversation, marks all seen; see 8.8)
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

notify.message   SlackMessage (above; `ts` is the reply thread key). Also the user's OWN posts made elsewhere (is_me: true) so the open conversation stays live; the digest and unread counts skip those
notify.channels  { channels: ChannelSummary[] }   (the whole list whenever unread / mention counts or last_ts change, debounced 300 ms; sticky: replayed on connect)
notify.draft     Draft = { draft_id, channel, channel_name, text, thread_ts?, reply_to?: SlackMessage, edited }   (the composer shows it with Send / Cancel; edited after a PATCH)
notify.sent      { draft_id?, channel, channel_name, text, ts }
notify.draft_cancelled { draft_id, reason: "cancelled"|"superseded"|"expired" }
notify.digest    { count, mentions, line, messages: SlackMessage[] }   (newest first, at most 4; `line` is the spoken sentence, empty when count is 0)
```

Panel IDs (used in `ui.highlight` and `target_panel`): `screen-main`, `ml-datastack`, `ml-model3d`, `ml-results`, `ml-training`, `cad-viewer`, `messages`, `messages-mentions`.

Proposed voice addition: `stt.start` gains `facing: bool = false` (wake turns only; true = the user faced Albie at speech onset). Albie answers a hands-free turn only if it names him or the user faced him (facing counts as the name, minus `hub/agent/addressed.py` word rules: talk to a person, fragments, filler alone). An unnamed `require_name` turn (follow-up while looking away) is dropped; "stop" needs the name too.

Proposed ML additions (project setup, sample metadata, VizCard chart cards, live training events, new panel IDs `ml-viz`, `ml-dots`, `ml-live`): `ML_PLAN.md` section 7.

C# side: one `ForgeEvents.cs` with matching `[Serializable]` classes and a static event per type, owned by Hudson. Python side mirrors it in `hub/events.py`.

---

## 8. Module specs

### 8.1 Core: rigs, room, sit flow, panel system (Hudson)
- `RigSelector`: if an XR runtime is active (headset or simulator) use `OVRCameraRig` with Interaction SDK hands/controllers; else spawn `DesktopRig` (CharacterController, WASD, mouse look, click raycast).
- Room scene with desk (desktop and simulator). On Quest: passthrough on, virtual room off, MRUK finds nearest `TABLE`.
- Sit flow: near desk + `E` (desktop) or approach table (XR) -> `Workspace.Unfold()` staggered DOTween in of keyboard glow, trackpad glow, main screen, side panels in an arc anchored to the desk's near edge.
- `ForgePanel` prefab: world-space canvas + frame + title + grab handle. Interactable by mouse (desktop) and by ray/poke (Interaction SDK `PointableCanvas`). Every module's UI lives inside a ForgePanel.
- `ForgeNet`: WS client (`ClientWebSocket`) + REST helper + mock mode (falls back automatically when no hub answers; `ForgeMockPlayer` replays event lists).
- Mode switch: four modes on the desk dock, ML / CAD / CODE / MSG (`Workspace.DefaultModes()`); CODE (the VS Code panel alone) is the landing mode the pet opens after the sit-down digest.
- Performance: Quest at 72 fps. GPU instancing for repeated meshes, 2048 max textures, no allocations in `Update`.

**Core ideas backlog (from the first simulator run, 2026-10-23):**
- ~~Move and resize panels~~ done (`PanelManipulator`, `VolumeManipulator`); layout is remembered per mode for the session only (not saved to disk).
- ~~Layout style toggle (arc vs flat wall), space slots by panel width~~ done (dock Flat / Curve; panels also stay above the desk top).
- Easier pressing: toggle controller models off (hands-first). Mouse clicking: done in desktop mode; in the simulator, turn on Keyboard and mouse > Point & click.
- Make it look like Meta built it: Interaction SDK ships Meta's Horizon OS "UI Set" (buttons, toggles, sliders, backplates) in `com.meta.xr.sdk.interaction/Runtime/Sample/Objects/UISet`. Restyle `ForgePanel` and the dock with it.
- Sitting: on the Quest you just sit (head height). In the simulator press F to lower the head. The simulator room (chair included) is a passthrough image, so it can't be moved; MRUK has no chair label.

### 8.2 Screens (Hudson)
- `screen-bridge/index.html`: "Share window" buttons, each a WebRTC video track labeled (`vscode`, `terminal`, `browser`). Signaling through the hub WS.
- Unity: `com.unity.webrtc` receiver -> `RenderTexture` on a curved panel. Main screen center, extras angled left/right.
- Acceptance: VS Code readable at 1 m in the simulator.
- Fallback: hub serves screenshots at 5 fps over WS.

### 8.3 ML: Python SDK + hub (ML person A)
> **Extended by `ML_PLAN.md`:** for any repo, an LLM classifier picks the project type, then an adapter step writes the glue code and proposes this patch (applied only on Accept). CIFAR-10 below becomes test repo #0 and the stage backup. Task list: ML_PLAN section 9, A track.

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
> **Extended by `ML_PLAN.md`:** panels per project type (`ml.layout`) and a `VizCard` prefab for extra charts. For image classification, **ML_PLAN section 5.3 replaces the DataStack and ResultsWall details below**: a dot wall + mistakes gallery instead of a thumbnail grid, and a metadata card plus X to put an image back. Task list: ML_PLAN section 9, B track.

- **DataStack (left):** thin cards (instanced quads, atlas UVs). Scroll / thumbstick / swipe scrubs (cards fan as you pass). Select: card flies forward large with label. Put back.
- **Model3D (center left):** block per layer, size from log of out_shape, color by type (conv, pool, linear, norm, activation). Input shape label above, output shape below. Pulse travels down per metrics tick.
- **ResultsWall (right):** grid of all val images, green/red tint. Filters All / Wrong / by class. Click: expanded card with image, predicted, true, probability bars, buttons Relabel (class picker) / Remove / Looks fine.
- **TrainingDash (above):** loss + accuracy curves (train vs val), epoch bar, LR, overfit banner. On done: confusion matrix heatmap (click a cell to filter ResultsWall to that pair) and per-class accuracy.
- All four run from mocks first.

### 8.5 CAD
- Default part: model rocket in build123d with params: body length, body diameter, nose length, nose shape (cone/ogive), fin count, fin span, fin sweep.
- `POST /cad/params` regenerates, exports GLB, emits `cad.model_updated`. Target under 2 s.
- Unity: glTFast loads `/cad/model.glb?v=N`, swaps the mesh with a quick dissolve. Turntable, grab to rotate. Params panel with sliders.
- Hand-edit path: `rocket.py` open in VS Code (SCR mode's screens; CAD mode's middle screen shows the drawing); file watcher regenerates on save.
- **Decided:** build123d + pieces of cad-vision (MIT). Full plan, verified numbers and status: `CAD_PLAN.md`.

### 8.6 Pet body (Adrian)
- States driven by `pet.state`: `follow` (about 1 m beside and behind the user, NavMesh or simple steering, faces user when they stop), `idle` (on desk when seated, sit, look around, fidgets), `listening`, `thinking`, `working` (blob morph, arc jump into `target_panel`, panel pulses), `speaking` (pops out, speech bubble + answer card with action buttons).
- CAD tinkering: walks to rocket, sparks loop while regenerating.
- Name tag and soft glow so it reads on passthrough.
- Works standalone in `PetSandbox.unity` with a keyboard to trigger each state.

### 8.7 Pet brain, voice, Slack
- Voice: Grok Voice Agent session in the hub. Push-to-talk (spacebar on desktop, trigger/pinch-hold in XR) to start; server VAD ends the turn.
- **Fast tools** on the voice model (answer in under a second): `ml_get_run_summary`, `ml_relabel`, `ml_remove`, `ml_start_training`, `cad_get_params`, `cad_set_params`, `ui_highlight`, `pet_go_to`, `messages_read_latest`, `messages_list_channels`, `messages_read_channel`, `messages_send` (writes a **draft** and returns `awaiting_confirmation`; Bit says what it wrote and asks you to hit Send or say so), `messages_confirm` (`send: true|false` on the open draft, for "send it" / "never mind"). There is no tool that posts directly.
- **`delegate(task)` tool** for anything that needs real analysis ("why is it bad at cats?", "suggest a design change"): the hub runs a reasoning model with read access to run results / CAD script, returns a card. While it runs, emit `pet.state working` (blob dive) and have the voice model say "give me a sec."
- Every tool call also emits `pet.state` so the body animates.
- Persona: short, cute, competent. 1 to 3 spoken sentences, details go in the card.
- Slack: one app ("Bit", `hub/manifest.json`) with bot scopes and **user scopes** (`channels:history`, `groups:history`, `im:history`, `mpim:history`, `chat:write`, ...) and matching `bot_events` + `user_events`. `hub/messaging/` runs Bolt's `AsyncApp` + `AsyncSocketModeHandler` as a lifespan task. Inbound `message` events (bot or user subscription, deduped by `(channel, ts)`) -> `notify.message` on `/ws` and a per-conversation ring (last 200 each, `last_seen_ts` per channel) behind `messages_read_latest`, the channel list / history endpoints and the digest. Skipped: the user's own messages, anything with `bot_id`, the bot itself. `is_dm` from `conversations.info`, `mentions_me` = DM or `<@user_id>` in the text. **Sending is always two steps:** `messages_send` / `POST /messages/drafts` writes a `Draft` and emits `notify.draft`; only `POST /messages/drafts/{id}/send` (card button) or `messages_confirm` (voice) posts it, with the user token, then `notify.sent`. A new draft supersedes the open one (`notify.draft_cancelled`, reason `superseded`); drafts expire after 2 minutes. Without `SLACK_USER_TOKEN` the hub falls back to the bot identity (reads only invited channels, posts as Bit) and logs a warning. Tokens all missing -> module disabled, hub still boots. No fake or mock mode.
  - Tools register through `agent/tools.py` like CAD and VS Code do (`messages_` prefix -> panel `messages`).
  - **Setup (5 min, Slack CLI):** `hub/manifest.json` is the app and `hub/.slack/` is the CLI project. Install the CLI (https://docs.slack.dev/tools/slack-cli), `slack login` to the team workspace, then from `hub/`: `slack install -E local` (creates and installs "Bit (local)" with bot + user scopes), `slack run` once (the start hook writes `SLACK_BOT_TOKEN` and `SLACK_APP_TOKEN` into `hub/.env` and exits). The CLI never hands out the user token, so once: `slack app settings` > OAuth & Permissions > copy **User OAuth Token** (`xoxp-`) into `SLACK_USER_TOKEN`. Scope or event changes: edit `manifest.json`, `slack run` again, re-copy the user token if user scopes changed. Other checkouts: `slack app link -E local`. Manual path (api.slack.com/apps, same manifest) still works.
  - Teammates install the Slack mobile app and turn on notifications so the on-stage reply visibly buzzes a phone.

### 8.8 Messages: the Slack panel, Mentions, digest and Send / Cancel (Unity `messages` + `messages-mentions`, hub `messaging`)
- **Slack panel** (`Messages/Resources/SlackPanel.prefab`, ForgePanel variant, slot Main of MSG mode): conversations on the left (Channels / Direct messages from `notify.channels`, sticky on connect; unread pill, mention dot, bold when unread), the open conversation on the right (`GET /messages/history`, oldest at the top, auto-scroll to the newest unless you scrolled up, "Load earlier" when `has_more`), a composer at the bottom. Showing a conversation and new messages in it `POST /messages/channels/{id}/seen`. Rows are the module's translucent slate cards inside Hudson's graphite frame; `<@U..>` renders as @name, your mentions in the accent; your own messages (`is_me`) are inset and tinted.
- **Composer:** what you type becomes a draft (`POST /messages/drafts`) that is sent at once (`POST .../send`). A draft from Bit (`notify.draft`) lands in the field as editable text with Send / Cancel and "Bit wrote this"; Send `PATCH`es the edited text first; Cancel `POST`s `.../cancel`. **Ask Bit** (and tapping a message) sends `messages.focus {pet_id, ts, channel}`: Bit reads it aloud and offers to answer. Quest: the field opens the system keyboard; desktop: the keyboard types into it (Enter sends).
- **Mentions panel** (`MentionsPanel.prefab`, slot Left): "New for you", unread DMs and mentions across conversations, newest first, 8 at most plus "+N more"; tapping a card opens that conversation in the Slack panel; cards clear once seen.
- **Arrival digest:** when the pet has landed after `DeskPresence` goes Seated, Unity calls `POST /messages/digest`; the hub answers with `notify.digest` and a `pet.say` line Bit speaks ("You have 3 new messages, 1 mentions you."; `count == 0` -> silence). With MSG mode closed a floating Mentions panel shows the digest's cards (4 at most) beside Bit for ~20 s; tapping one opens MSG mode on it.
- **Send / Cancel card (`DraftCard.prefab`):** only while the Slack panel is closed: `notify.draft` pops the card beside Bit (recipient, the text, **Send** / **Cancel**, same slate look); it closes itself on `notify.sent` / `notify.draft_cancelled`. Nothing leaves Slack without a Send. Spec and files: `unity/Assets/_Forge/Messages/README.md`.

---

## 9. Scope tiers

**MUST:** desktop mode end to end; sit flow + unfold; one streamed VS Code screen; all four ML panels with a real or replayed run; relabel/remove; pet follow/idle/blob/speak; voice (or typed) Q&A with ML tools; CAD rocket changed by voice; one Slack round trip (arrival digest on sit-down, inbound message spoken by the pet, reply posted in-thread).

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
| **Person 5** | Hub skeleton + agent brain + voice + Slack + CAD backend until CAD gets an owner | Hub running with `/ws` broadcasting mock events; Grok voice round trip from laptop mic | Tools wired to ML + CAD endpoints; Slack round trip |

Hand-offs: Hudson publishes `ForgePanel` + `ForgeNet` within ~2 hours; until then everyone uses a plain world-space canvas and a local mock loader. CAD Unity viewer goes to whoever frees up first (likely Hudson after integration).

When the Quest 3 arrives: Hudson + Adrian do the device pass (APK build, passthrough, MRUK table, hand interactions on panels, mic streaming). Windows laptop with Quest Link is the fastest loop; on Mac, build and sideload the APK each time.

---

## 11. Decisions

1. **Engine:** Unity 6 + Meta XR SDK. Decided.
2. **Voice:** Grok Voice Agent API (`grok-voice-think-fast-1.0`) + `delegate` to a reasoning model. ElevenLabs Agents is the fallback if Grok's voices don't fit the pet's personality.
3. **Messaging:** Slack (Bolt for Python, Socket Mode) in the team workspace, with the **user's own token** so Bit reads the user's inbox and posts as the user, and a **draft -> confirm card -> send** flow so nothing is sent without a tap or a spoken yes. Decided (v3, 2026-10-24). Replaces WhatsApp Cloud API: no tunnel, no allowlist, no 24-hour window, and a work message fits the "Jarvis for engineers" pitch.
4. **CAD engine:** build123d 0.13 (hub) + cad-vision's GLB/metadata loader and shaders (Unity). Decided, see `CAD_PLAN.md`.
5. **Headset:** Quest 3 expected in about a day. Laptop/simulator first.
6. **Unity version:** `6000.6.7f1` (Unity 6.6), Meta XR SDK 207.0.1, OpenXR 1.18.0, simulator v207.1. Decided.
7. **Pet model:** Adrian picks in hour 1.
8. **ML auto-setup:** an LLM classifier picks the project type from a fixed list, each type has a fixed set of visuals, and the setup writes only into the repo's `.forge/`; the training-script patch is applied only on Accept; extra charts are VizCards with a PNG kind; every run is saved for replay; CIFAR-10 is kept as the test repo and backup. Decided, see `ML_PLAN.md`. Which LLM and which live graphs: the ML team decides.

---

## 12. Rules for coding agents (copy into CLAUDE.md)

- Read this file first. Work only inside your module folder (section 6) unless told otherwise.
- Contracts in section 7 are law. Propose changes, never silently change them.
- Mock first. Every Unity module runs in its own sandbox scene from `Assets/_Forge/Mocks/` with the hub off.
- Never edit `Main.unity`, `Packages/manifest.json`, or Project Settings unless you are the Core owner.
- Ship features as prefabs. No scene-only setup that someone else has to recreate.
- UI goes inside `ForgePanel` and must work with mouse (DesktopRig) and Interaction SDK ray/poke.
- Quest performance: no allocations or LINQ in `Update`, instancing for repeated meshes, textures 2048 max.
- Unity networking: go through `ForgeNet` (it uses `ClientWebSocket` for WS, `UnityWebRequest` for REST, Newtonsoft for JSON, and raises events on the main thread). Don't open your own sockets.
- Python: FastAPI routers per module, type hints, pydantic models mirroring `events.py`.
- Never send a real message or take an irreversible action from the pet without a user confirmation step.
- Small commits, pull often, never break `main`.
