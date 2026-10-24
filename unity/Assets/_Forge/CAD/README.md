# CAD module (Unity)

The open design on the desk (rocket, quadcopter, gearbox, or any part script in the hub's library) and its design
panel. Hub side: `hub/cad/`; plan: `CAD_PLAN.md`.

## Prefabs (slot these into the CAD mode on `Workspace`)

| Item id (Workspace) | Prefab | What |
|---|---|---|
| `cad-viewer` (DeskCenter, Volume) | `Prefabs/CadViewer.prefab` | ForgeVolume variant + `CadViewer`. Size 0.26 x 0.5 x 0.26 m, slow turntable. Its label shows the open design's title. |
| `cad-params` (Right, Panel) | `Prefabs/CadParamsPanel.prefab` | ForgePanel variant, UI under `Content`. 0.48 x 0.6 m (Workspace doesn't resize custom prefabs); resizable. |

Both find or create one shared `CadSession`, so they work wherever they're spawned.

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

## What the viewer does

- Loads the hub's GLB + cad-vision `metadata.json` (glTFast + cad-vision's `CadMetadata`), tagging each part with `CadPart.id`.
- On a new version: changed parts glow (cad-vision's outline shaders), a ghost of what they were fades out,
  the CG (blue) and CP (red) collars slide to their new heights (rocket only: they need `markers_gltf`).
- Fits the first version of each design with headroom and keeps that scale, so "20% longer" looks longer. Opening
  another design fits it afresh (no glow or ghost across designs).
- `cad.view`: explode (parts spread along the axis and sideways) and focus (everything else dims).

## Sandbox

Open `CADSandbox.unity` and press Play (hub off = mocks; start the hub for live). Mouse works on the panel.
Keys (Game view must have focus): P next design, 1-4 stored versions, Left/Right step, E explode, U undo, R reset, F fly.

Rebuild everything with **Forge > CAD > Build CAD Prefabs and Sandbox** (prefabs, materials, mock set, sandbox).
Refresh the mocks after changing a design: in `hub/` run `python -m cad.make_mocks`, then copy
`hub/.data/mocks/cad/` here (`parts.json`, `regen_failed.json` and each `<part>/vN/`), renaming `model.glb` to
`model.glb.bytes` (so Unity keeps raw bytes instead of importing a prefab), and rebuild.

## Notes for Core

- Mouse: DesktopRig drives uGUI itself (EventSystem raycast + ExecuteEvents), which the panel supports, including
  slider drag and release (that release triggers the regen). If you ever add a standard uGUI input module instead:
  `PointableCanvasModule` turns on **exclusive mode** when added in the Editor (its `Reset()`) and disables other
  input modules at Start; set `ExclusiveMode = false`, as the sandbox does.
- `Workspace.DefaultModes` still titles the volume "Rocket"; `CadViewer` replaces it with the open design's title at
  Start, so nothing needs changing there.
- Player builds: glTFast's shader graph (`Shader Graphs/glTF-pbrMetallicRoughness`) must be in the build, or the
  model renders magenta. Add it to Always Included Shaders, or record the variants (play the sandbox, then
  Project Settings > Graphics > Shader Preloading > save to asset) and add that to Preloaded Shaders. See glTFast's
  `Documentation~/ProjectSetup.md`.
- Third party: `ThirdParty/CadVision/` (MIT), unchanged; see its README.
