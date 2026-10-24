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
- Undo / Reset / Explode, and **Fly** only for designs with a flight sim (`features` contains `flight`).
- **Inspect** / **Back to desk**, and **Reassemble** while inspecting (lit when parts are off).

## What the viewer does

- Loads the hub's GLB + cad-vision `metadata.json` (glTFast + cad-vision's `CadMetadata`), tagging each part with `CadPart.id`.
- On a new version: changed parts glow (cad-vision's outline shaders), a ghost of what they were fades out,
  the CG (blue) and CP (red) collars slide to their new heights (rocket only: they need `markers_gltf`).
- Fits the first version of each design with headroom and keeps that scale, so "20% longer" looks longer. Opening
  another design fits it afresh (no glow or ghost across designs).
- `cad.view`: explode (parts spread along the axis and sideways) and focus (everything else dims).

## Inspect: the model up big (`CadViewer`)

- **Inspect** (panel, or key **I**): the model flies off the desk to a stage 0.6 m in front of the eyes (toward the
  desk, not where you're looking) and 0.1 m below them, turned 30° to show a corner and, unless it's tall, tipped back
  25° to show its top. It's fitted to a 0.6 x 0.6 x 0.36 m box, which keeps it in front of CAD mode's VS Code screen
  (0.8 m away). A flat screen (desktop mode, sandbox) sees less than a headset, so there the stage steps back until the
  box fits in view. All of these are fields on `CadViewer` (Inspect section).
- **Back to desk** (or I again) flies it home. If CAD mode closes while inspecting (`Workspace.modeChanged`, or the
  viewer is disabled), it goes home first, instantly, so it folds with the volume.
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
E explode, U undo, R reset, F fly, I inspect.

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
