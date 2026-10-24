# CAD module (Unity)

The rocket on the desk and its parameters panel. Hub side: `hub/cad/`; plan: `CAD_PLAN.md`.

## Prefabs (slot these into the CAD mode on `Workspace`)

| Item id (Workspace) | Prefab | What |
|---|---|---|
| `cad-viewer` (DeskCenter, Volume) | `Prefabs/CadViewer.prefab` | ForgeVolume variant + `CadViewer`. Size 0.26 x 0.5 x 0.26 m, slow turntable. |
| `cad-params` (Right, Panel) | `Prefabs/CadParamsPanel.prefab` | ForgePanel variant, UI under `Content`. 0.48 x 0.8 m (Workspace doesn't resize custom prefabs). |

Both find or create one shared `CadSession`, so they work wherever they're spawned.

## Where the data comes from: `CadSession`

- **Auto** (default): pings `hubUrl` (`http://127.0.0.1:8000`) at start; hub up = live, hub down = mocks.
- **Hub**: REST to the hub (`POST /cad/params` on slider release, undo, reset, view, flight). Changes made elsewhere
  (Bit's voice tools, saving `rocket.py`) arrive by polling `GET /cad/latest` every 0.5 s. When `ForgeNet` lands,
  replace `CadSession.Poll` with its `/ws` events (`cad.model_updated`, `cad.view`); the payloads are the same.
- **Mock**: `Assets/_Forge/Mocks/cad/` (the demo's 4 designs). Sliders jump to the stored design that has the value
  you asked for; the arrows step through them.
- On the Quest, 127.0.0.1 is the headset: put a `CadSession` in Main with `hubUrl` = the laptop's IP (or read it from ForgeNet).

## What the viewer does

- Loads the hub's GLB + cad-vision `metadata.json` (glTFast + cad-vision's `CadMetadata`), tagging each part with `CadPart.id`.
- On a new version: changed parts glow (cad-vision's outline shaders), a ghost of what they were fades out,
  the CG (blue) and CP (red) collars slide to their new heights.
- Fits the first model with headroom and keeps that scale, so "20% longer" looks longer.
- `cad.view`: explode (parts spread along the axis, fins fan out) and focus (everything else dims).

## Sandbox

Open `CADSandbox.unity` and press Play (hub off = mocks; start the hub for live). Mouse works on the panel.
Keys (Game view must have focus): 1-4 designs, Left/Right step, E explode, U undo, R reset, F fly.

Rebuild everything with **Forge > CAD > Build CAD Prefabs and Sandbox** (prefabs, materials, mock set, sandbox).
Refresh the mocks after changing the rocket: in `hub/` run `python -m cad.make_mocks`, then copy each
`hub/.data/mocks/cad/vN/` here, renaming `model.glb` to `model.glb.bytes` (so Unity keeps raw bytes instead of
importing a prefab), and rebuild.

## Notes for Core

- `PointableCanvasModule` turns on **exclusive mode** when added in the Editor (its `Reset()`), which disables every
  other input module at Start. For mouse UI next to it (DesktopRig), set `ExclusiveMode = false`, as the sandbox does.
- Player builds: glTFast's shader graph (`Shader Graphs/glTF-pbrMetallicRoughness`) must be in the build, or the
  rocket renders magenta. Add it to Always Included Shaders, or record the variants (play the sandbox, then
  Project Settings > Graphics > Shader Preloading > save to asset) and add that to Preloaded Shaders. See glTFast's
  `Documentation~/ProjectSetup.md`.
- Third party: `ThirdParty/CadVision/` (MIT), unchanged; see its README.
