# CAD module (Unity)

The open design on the desk (rocket, quadcopter, gearbox, or any part script in the hub's library), its design
panel and its engineering drawing. Inspect brings the model up big in front of you, where you can take it apart.
Hub side: `hub/cad/`; plan: `CAD_PLAN.md`.

## Prefabs (slot these into the CAD mode on `Workspace`)

| Item id (Workspace) | Prefab | What |
|---|---|---|
| `cad-viewer` (DeskCenter, Volume) | `Prefabs/CadViewer.prefab` | ForgeVolume variant + `CadViewer`. Size 0.26 x 0.5 x 0.26 m, slow turntable. Its label shows the open design's title. |
| `cad-params` (Right, Panel) | `Prefabs/CadParamsPanel.prefab` | ForgePanel variant, UI under `Content`. 0.48 x 0.6 m (Workspace doesn't resize custom prefabs); resizable. |
| `cad-drawing` (Left, Panel) | `Prefabs/CadDrawingPanel.prefab` | ForgePanel variant: the version's engineering drawing. 0.64 x 0.54 m. Replaces the `cad-timeline` placeholder (Core: `Workspace.DefaultModes` + `MainSceneBuilder`). |

All of them find or create one shared `CadSession`, so they work wherever they're spawned.

## Where the data comes from: `CadSession`

- **Auto** (default): follows `ForgeNet` (Core/Net). Hub live = hub mode, no hub = mocks; if the hub appears later it
  switches to the hub.
- **Hub**: REST to the hub (`POST /cad/params` on slider release, `/cad/part` to open a design, `/cad/preset`,
  undo, reset, view, flight). Every change, wherever it came from (the panel, Bit's tools, saving a part script),
  arrives as ForgeNet's `/ws` events (`cad.model_updated`, `cad.view`, `cad.regen_failed`). `GET /cad/latest` is only
  a fallback when the socket is down. The params for a version come from `GET /cad/params?v=N`.
- **Mock**: `Assets/_Forge/Mocks/cad/` (a few stored versions of each design). Sliders and presets jump to the stored
  version closest to what you asked for; the arrows step through them.
- Hub address: ForgeNet's `hubUrl` (on the Quest, the laptop's LAN IP). One setting for every module.
- The CAD event payload classes (`CadModelUpdated`, `CadAnalysis`, `CadRegenFailed`, `CadView`) live in
  `Core/Net/ForgeEvents.cs` with the other hub events; `Scripts/CadData.cs` keeps the REST shapes and `CadMetric`.

## The design panel (`CadParamsPanel`)

Generic over designs; everything comes from the hub:

- Header: the design's title and version, and **Designs** (the library; refreshed each time it opens, so a new part
  script shows up).
- Presets row: the design's `PRESETS`.
- Metrics (`analysis.metrics`): a `CadMetricGauge` for each metric with a min and max (bands, needle, a one-word chip:
  the rocket's static margin, the drone's prop clearance), and a stats line for the rest, coloured by status.
- Parameter rows from the design's `PARAMS`, in a scroll view with the stats line (drag the rows or the scrollbar;
  desktop mode sends no wheel events to uGUI).
- Undo / Reset / Explode, and the design's demo button (`analysis.demo`: **Fly** for the rocket, **Hover** for the
  drone, **Run** for the gearbox; hidden for a design with none, disabled if `demo.blocked`, **Stop** while it runs;
  Undo and Explode pause meanwhile). Versions older than the demo data fall back to Fly for flight designs.
- **Expand** / **Back to desk**, and **Reassemble** while expanded (lit when parts are off).

## What the viewer does

- Loads the hub's GLB + cad-vision `metadata.json` (glTFast + cad-vision's `CadMetadata`), tagging each part with `CadPart.id`.
- On a new version: changed parts glow (cad-vision's outline shaders), a ghost of what they were fades out,
  the CG (blue) and CP (red) collars slide to their new heights (rocket only: they need `markers_gltf`).
- Fits the first version of each design with headroom and keeps that scale, so "20% longer" looks longer. Opening
  another design fits it afresh (no glow or ghost across designs).
- `cad.view`: explode and focus (everything else dims).
- Everything below follows `HANDOFF_UI2/cad/CAD_UX_SPEC.md` (sections 3 to 5, 7, 9) and the hub's round 4 data
  (`CAD_PLAN.md` section 5). All of it is made at runtime under the viewer's `CadOverlay` child (no prefab change),
  works on the desk and on the stage, and falls back to the old behaviour for versions without the new keys.

### Explode (`CadExploder`)

- Each leaf part springs out along `analysis.explode` (`dir` x `dist_mm` x `explodeScale`; radial designs, the drone,
  1.5x more so its labels don't crowd). Spring omega 10.5, zeta 0.72 (settled in about 0.55 s, about 4% overshoot),
  started `order` x 70 ms apart plus 30 ms per copy (fins, arms); assembling runs in reverse order, 50 ms apart.
  On the desk the exploded model lifts so nothing sinks into the base; on the stage it stays centred. Parts taken off
  by hand stay where they are. No explode data: the old spread (axial 0.6, radial 1.5).
- One graphite chip per kind of part (`Fins  ×3`, material · mass, "each" for copies, an orange dot if this version
  changed it), stacked in one column beside the model on the side away from the design panel, 46 mm apart, sorted by
  height. Leaders (elbow 16 mm from the column, ring and dot on the part) draw on as each part arrives (55% to 90%).

### Fly / Hover / Run (`CadDemoPlayer`)

- **Fly** (`launch`, about 15.5 s): 1.2 s countdown on a pad of light (pulses three times; rail 5° off vertical;
  the sim's predicted path dashed with "Apogee 197 m · predicted"). Then `GET /cad/flight` (mock `flight.json`) in
  real time: apogee 1.3 m above the desk (`launchHeight`, kept 0.3 m under the MRUK ceiling, 2.5 m without a room),
  downrange away from you on the same scale (the C6 rocket lands about 0.24 m out). The nose follows the path (on the
  rail until rail exit), a flame while the motor burns, a glowing trail, tags at Burnout, Apogee and Parachute; under
  the 85 mm canopy it turns nose-up and the descent plays ×8 with a dashed trail. It holds 1.4 s on landing
  ("Landed 50.3 s · 36 m from the pad"), then glides home in 0.6 s. A chip beside the rocket reads the sim's real
  altitude, speed and time. From the stage, Fly first sends the model home.
- **Hover** (`hover`, 9.8 s): props (`spin_parts`, their own ccw / cw, capped at 4.5 turns/s on screen) blur into
  glowing discs, it lifts 0.22 m, hovers with an 8 mm bob and a 4° yaw sway; a thrust column per motor (dashed to max
  thrust = 150 mm, bright to the hover share; "max 1.38 kg", "hover 97 g"), a dashed height line ("22 cm"), and a chip
  ("Hovering · 27% throttle", "93% thrust to spare · 8,478 rpm"). Lands after 6 s or on Stop, props spool down.
- **Run** (`spin`, 10.4 s): the lid (`demo.reveal_ids`, else the part that explodes furthest straight up among those
  that don't turn) lifts to 55% of its explode distance and turns 45% glass; every spinner turns at rpm ÷ 100 about
  its own axis (the 3 : 1 shows); dashed pitch circles, rotation arrows at the input / output shaft ends, In / Out
  chips, "Slow motion · 100× slower". Stops after 7.6 s or on Stop, then the lid comes back.
- Every demo ends with the model exactly as it was. It stops at once if the version, the view (explode, focus), the
  stage or the mode changes. Pressing it while exploded first assembles.

### Markers, dimensions, parameter highlight (`CadAnnotations`)

- Markers (`analysis.markers`, on by default, key **N**): CG as a blue ⊕ (on the rocket, its collar), CP red, thrust
  in the CAD hue, the rest white, each with a chip ("CG 246 mm", "In 3,000 rpm"); a bracket between the rocket's CG
  and CP collars in the margin's colour, and on the stage the margin chip ("1.57 cal · static margin"). A hovering
  design shows its prop discs.
- Dimensions (`analysis.dimensions`, key **M**, the new panel's Measure; on by themselves on the stage): extension
  lines, the line with open arrowheads, a value chip ("390 mm" over "Length").
- `HighlightParam(name)`: the parameter's parts (`params[].parts`) get an orange outline and its dimension
  (`params[].dimension`) is drawn. Needs those two fields from the hub (not in the mocks yet: then it does nothing).
- Labels are graphite chips (Inter; title 12.5 mm and second line 10.5 mm at 0.8 m), resized every frame to stay about
  0.9° tall, never with the model. Lines are light (Forge/CadLight), chips Forge/CadChip: one mesh each, so a handful
  of draw calls in all.

### API for panels

`CadViewer.Active` finds the viewer (panels are spawned separately). Every call also has a `CadSession` twin, so a
panel can use either and both stay in sync:

| Viewer | Session | What |
|---|---|---|
| `Explode(bool)`, `Exploded` | `SetView(explode, focus)`, `View` | explode / assemble (posts `/cad/view`) |
| `Expand(bool)`, `Expanded`, `SetTrueSize(bool)` | `SetInspecting(bool)`, `Inspecting` | the stage; true size |
| `StartDemo()`, `StopDemo(instant)`, `DemoRunning`, `Demo` | `RequestDemo(bool)`, `DemoRunning` | Fly / Hover / Run |
| `DemoChanged`, `DemoFrame` (`CadDemoFrame`) | `DemoChanged`, `DemoFrame` | started / ended; every frame: phase, time, altitude, speed, progress, playback (×8), readouts |
| `ShowMeasure(bool)`, `Measuring` | `SetMeasuring(bool)`, `Measuring`, `OverlaysChanged` | dimensions |
| `ShowMarkers(bool)`, `MarkersShown` | `SetMarkersShown(bool)`, `MarkersShown` | markers and collars |
| `HighlightParam(name)`, `ClearParamHighlight()` | | a parameter's parts and dimension |

## Expand: the model up big (`CadViewer`; "Inspect" in code and the session)

- **Expand** (panel, the ring of light on the desk at the base's front right, or key **I**): the model springs off the
  desk to a stage 0.6 m in front of the eyes (toward the desk, not where you're looking) and 0.1 m below them
  (position and turn critically damped, omega 9, along an arc rising 0.14 m; scale omega 10 with a gentle 8% pop).
  Turned 30° to show a corner (a Run design faces you square on); tipped back 25°, 40° if it's flat, 0° if it's tall.
  It's fitted to a 0.6 x 0.6 x 0.36 m box, which keeps it in front of CAD mode's VS Code screen (0.8 m away). A flat
  screen (desktop mode, sandbox) sees less than a headset, so there the stage steps back until the box fits in view.
  A ring of light fades in under it with a chip of its scale ("1.4× real size"; `SetTrueSize` shows it 1 : 1);
  dimensions come on and a lid turns 38% glass. All of these are fields on `CadViewer` (Inspect section).
- **Back to desk** (or I again, or the ring) springs it home. If CAD mode closes while expanded (`Workspace.modeChanged`,
  or the viewer is disabled), it goes home first, instantly, so it folds with the volume.
- The desk and the stage each have their own fit, both from the model's mesh bounds: new versions load wherever the
  model is; a stage visit fits afresh and keeps that scale for the versions that arrive during it. Explode works there.
- While the model is away, the empty desk volume's model grab box is off (`VolumeManipulator.ModelAway`, Core); its
  base can still be moved.

## Take it apart (`CadTakeApart`, Inspect only)

On the desk, dragging still turns the whole model (Core's `VolumeManipulator`). On the stage:
- Point at a part: it gets a blue outline and a card with its name, material and mass (`metadata.json`).
- Grab it (mouse, controller ray, pinching hand ray), carry it, let go: it stays there. It keeps its pose relative to
  the pointer (cad-vision's `CADGrabSession` idea), so a controller or hand turns it by twisting. Mouse: **Shift** +
  drag turns it in place; scroll / thumbstick pushes it away or pulls it in.
- Let go within 3 cm of its place (at the design's real size; at most 8 cm in the room) and it snaps back, straight.
  **Reassemble** puts every part back. Back to desk puts them back on the way.
- View only: the design version never changes. A new version arriving while parts are off puts them back first
  (instantly), then swaps in with the glow.
- Each leaf part (`CadModel.partIds`) gets a `CadPartHandle`: a mesh collider on its own mesh, so the pointer takes the
  part under it, including a gear inside an open housing (bounding boxes nest). It only takes pointing: it collides with
  nothing (`excludeLayers`) and sits on the Ignore Raycast layer. They're added on the stage and removed on leaving.
- Input: XR rays reach the parts through a `RayInteractable` on each; a mouse through `CadPartRaycaster`, a uGUI
  raycaster that puts the parts into the EventSystem's raycast (DesktopRig and the sandbox's input module both use it;
  nearest wins against panels). `CadPointer` wraps Core's `ForgePointer`, or the plain mouse in the sandbox.

## The drawing panel (`CadDrawingPanel`)

- Shows the version's engineering drawing (`GET /cad/drawing.png?v=N`: front, top, right and iso views, overall
  dimensions, parts list, title block; parts changed since the version it came from in orange), or the mock's stored
  one. Tap the sheet to zoom 2x on that spot (read the parts list), tap again for the whole sheet.
- Asks as soon as the hub announces a version (`CadSession.VersionComing`), keeps the last sheet up meanwhile, and has
  one request out at a time (a slider drag skips to the newest), so drawing jobs don't queue in front of regens.
- The hub makes a drawing on its first request (it reuses the build that was just made): about 2 s after the model
  shows with the CPU pinned by two editors and the XR simulator, roughly a third of that on a quiet machine. Cached after.

## Sandbox

Open `CADSandbox.unity` and press Play (hub off = mocks; start the hub for live). Mouse works on the panels and, in
Inspect, on the parts. Keys (Game view must have focus): P next design, 1-4 stored versions, Left/Right step,
E explode, U undo, R reset, F the demo (Fly / Hover / Run; again to stop), I expand, M measure, N markers.
(I, M and N are CadViewer's own keys, so they work in desktop mode too.)

Rebuild everything with **Forge > CAD > Build CAD Prefabs and Sandbox** (prefabs, materials, mock set, sandbox).
Refresh the mocks after changing a design: in `hub/` run `python -m cad.make_mocks`, then copy
`hub/.data/mocks/cad/` here (`parts.json`, `regen_failed.json` and each `<part>/vN/`), renaming `model.glb` to
`model.glb.bytes` and `drawing.png` to `drawing.png.bytes` (so Unity keeps raw bytes instead of importing them), and
rebuild.

Driving the sandbox from the CLI (agents): simulated input only reaches the game if it's queued from inside a Dynamic
input update (`InputSystem.onBeforeUpdate`, checking `InputState.currentUpdateType`), with
`InputSettings.backgroundBehavior = IgnoreFocus`, `editorInputBehaviorInPlayMode = AllDeviceInputAlwaysGoesToGameView`,
and the mouse / keyboard re-enabled (`InputSystem.EnableDevice`): an unfocused editor disables them. The settings are
an in-memory default here (no asset), so this doesn't touch Project Settings.

## Notes for Core

- Mouse: DesktopRig drives uGUI itself (EventSystem raycast + ExecuteEvents), which the panel supports, including
  slider drag and release (that release triggers the regen). If you ever add a standard uGUI input module instead:
  `PointableCanvasModule` turns on **exclusive mode** when added in the Editor (its `Reset()`) and disables other
  input modules at Start; set `ExclusiveMode = false`, as the sandbox does.
- `Workspace.DefaultModes` still titles the volume "Rocket"; `CadViewer` replaces it with the open design's title at
  Start. But `VolumeManipulator` saves the title in `Init` (before that) and puts the saved one back after each hover,
  so in Main the label reads "Rocket" again once pointed at. Fix on Core's side: read `volume.Title` when the hover
  starts instead of in `Init`.
- To put the drawing on the left screen: in `Workspace.DefaultModes` (CAD), `cad-timeline` "Versions" becomes
  `new WorkspaceItem("cad-drawing", "Drawing", WorkspaceSlot.Left)`, and `MainSceneBuilder`'s slots gain
  `("cad-drawing", "Assets/_Forge/CAD/Prefabs/CadDrawingPanel.prefab")`; then rebuild Main.
- Player builds: glTFast's shader graph (`Shader Graphs/glTF-pbrMetallicRoughness`) must be in the build, or the
  model renders magenta. Add it to Always Included Shaders, or record the variants (play the sandbox, then
  Project Settings > Graphics > Shader Preloading > save to asset) and add that to Preloaded Shaders. See glTFast's
  `Documentation~/ProjectSetup.md`.
- Third party: `ThirdParty/CadVision/` (MIT), unchanged; see its README.
