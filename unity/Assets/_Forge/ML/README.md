# ML Unity panels

Open Unity **6000.6.7f1**, then run **Forge > ML > Build Prefabs and Sandbox**.
Open `Assets/_Forge/ML/MLSandbox.unity` and press Play. The builder uses the
existing Core ForgePanel prefab and never edits Main, packages, or project settings.
It replaces the generated ML prefabs and sandbox; save unrelated scene work first.

The sandbox runs from local mocks without the hub or a headset. Use a mouse to
click buttons, scroll the mistakes gallery, and scroll or drag over the dataset
stack. Hover the stack to scrub with a gamepad left stick or XR right controller
thumbstick. The inherited ForgePanel ray/poke canvas handles XR interaction.
Desktop camera mode is the default. To test through the standalone Meta XR
Simulator, activate it (Window > Meta > Meta XR Simulator > Activate), select
**Forge > ML > Use OpenXR Simulator**, then restart Play mode; the toggle is
remembered per machine. Saved XR settings are unchanged.

All panels share the Forge look through `Shared/MLUI.cs`: Inter text, graphite cards,
Horizon pills, one palette (right/wrong/warning, train/val, per-class colors).

- **DataStack:** a cover-flow deck of GPU-instanced rounded cards (`Shaders/MLAtlas.shader`)
  with momentum scrolling that snaps to a card (wheel, drag, thumbstick, arrow keys).
  Click the focused card (or a neighbour) to pick it: the deck slides left and the
  metadata card shows name/path, split, image shape, file size and prediction.
  Scrolling more than 1.5 cards away puts it back (Put back / **X** / right **B** still
  work). The barcode at the bottom (`MLScrubBar`) has one tick per image in its class
  color; click or drag it to jump.
- **Model3D:** layers grouped into blocks (a conv block, the flatten, each dense layer),
  each an edge-lit glass slab (`Shaders/MLSlab.shader`) sized by its output (wide = big
  feature maps, deep = many channels), stacked on a light spine (`MLBeam.shader`) that
  carries a packet down on every metrics tick. Hover grows a block and shows a callout
  (shapes, params, layers); click it or **Open** to unfold it into one slab per layer
  with labels (hover or click a layer for in/out shapes and params). **Open all** /
  **Close all**, drag to turn, scroll to zoom, **Reset view**.
- **ResultsWall** (the Results panel), five tabs drawn from `Shared/MLReport.cs`:
  - **Overview:** fit verdict (good fit / overfitting / underfitting, with why), an
    accuracy dial that counts up when a run finishes, train vs val, best epoch;
    accuracy, macro F1, top-3, mean confidence, calibration error, mistakes;
    strongest and weakest classes.
  - **Classes:** precision, recall, F1 and image count per class, best first.
  - **Confusion:** the matrix (click a cell for its images) and the most confused pairs
    with example thumbnails.
  - **Confidence:** how sure it was when right vs wrong, and a calibration chart with ECE.
  - **Images:** every image as a dot (`MLDotField`, one UI mesh), one block per class
    with its accuracy, green right / red wrong. Hover previews, select opens the detail
    card (probabilities, Relabel / Remove / Looks fine). **Show mistakes**, **Suspect
    labels** (wrong images ranked by loss, most likely mislabelled first) and **By class**
    open a 24-image paged gallery. Clicking a class, pair or cell anywhere opens this tab
    filtered to it.
- **TrainingDash:** epoch, losses, val accuracy and LR as big numbers, a status chip,
  **Train** (POST `/ml/train`, needs the hub) and **Replay**; loss and accuracy curves
  with the best epoch marked; a banner for overfitting, hub notices or the finished
  run; per-class accuracy over the epochs and the 16-image probe strip.
- **VizCard:** reusable ForgePanel renderer with line, bar, heatmap, scatter,
  table, image, and text modes. `LoadJson()` accepts the section 7.6 card contract;
  scatter points emit their sample id. The sandbox's local `MLVizCardMocks.json`
  exercises all seven kinds with Prev/Next.
- **RegressionResults:** predicted-versus-actual scatter; selecting a point opens
  a row card with actual, predicted, and residual values. It uses deterministic
  local mock rows until the hub supplies the shared regression fixture.

## Mock data and scope

The default input is the generated fixture set in `../Mocks/ml/`, merged from
`origin/satyabrata_dev_main` (lowercase **s**). It contains 1,000 CIFAR validation
images, the model graph, prediction probabilities, confusion matrix, and a
36-event / 15-epoch replay. The replay uses each event's `t` timestamp, including
run states and the explicit overfit alert, and finishes around 25 seconds.

The atlas is **2048×2048**, with **64×64 px** cells and **32 columns**. Cell rows
are counted from the top. Both instanced cards and UI images use the same ID-to-
cell mapping and `v = 1 - (row + 1) * 64 / 2048`; UVs use the texture dimensions,
not the number of occupied rows. The texture remains point-filtered with no mips
as specified by the generated importer. The imported texture is never destroyed
or modified by the session.

**Do not edit or regenerate the shared mock files.** Ask ML A to regenerate them
if data changes are needed. The SDK README's older atlas dimensions are stale;
use the atlas JSON and this handoff's dimensions. `Resources/MLMockAssets.asset`
is a module-owned reference bundle, built by the menu command; it includes the
shared assets in standalone builds without copying their contents into Resources.

Relabel/remove change only deserialized in-memory state. Replay retains those
edits; exiting Play resets them. Evaluation counts update from the edited samples.
The old 64-sample synthetic fixture is retained in `LegacyMocks/Resources/` only
as a fallback for an explicitly assigned legacy TextAsset or a missing bundle.

`MLSession` is shared across the four prefabs. Assigned sessions take precedence;
otherwise panels resolve a scene session and create a mock session if necessary.
Avoid multiple sessions unless panels have explicit assignments.

## Core integration

Assign generated prefabs to Workspace's ML items:

| ID | Prefab | Suggested slot |
| --- | --- | --- |
| `ml-datastack` | `Prefabs/DataStack.prefab` | FarLeft |
| `ml-model3d` | `Prefabs/Model3D.prefab` | Left |
| `ml-results` | `Prefabs/ResultsWall.prefab` | Right |
| `ml-training` | `Prefabs/TrainingDash.prefab` | Above |
| `ml-viz-card` | `Prefabs/VizCard.prefab` | Any visualization slot |
| `ml-regression-results` | `Prefabs/RegressionResults.prefab` | Regression results |

The stack and model ship inside ForgePanels with actual 3D meshes in front of
the canvas, so their controls inherit ray/poke interaction. Set these Workspace
items to **Panel** when integrating. Main is wired like CAD: `Workspace.DefaultModes`
uses `ml-datastack` / `ml-model3d` as Panels, and `MainSceneBuilder.ModulePrefabs` slots
the four default prefabs, so **Forge > Build Main Scene** puts them in the ML mode.
The dashboard is taller than Core's placeholder to keep ten-class evaluation
readable; the integrator should allow space above the other panels.

## Hub handoff

These are module-local DTOs and events, not a replacement for Core ForgeEvents.
Keep networking in the Core/hub adapter and call the view methods on Unity's main
thread. The local fixture groups REST-shaped records into one file for replay.

- `MLSession` is mock-first. When ForgeNet reaches Live it loads the hub's
  classes, dataset, atlas, graph and the latest finished run's results and
  confusion (`LoadFromHub`), raises `Reloaded` (panels rebind the atlas, Model3D
  rebuilds), and reloads on `ml.results_ready` / `ml.graph_ready`. It follows
  `ml.run_state`, `ml.metrics`, `ml.overfit_alert`, `ml.sample_updated` and
  `ml.epoch_preds` (live dot wall: each epoch's predictions recolor the dots;
  grey = not predicted yet). Set `useLiveEvents = false` to force replay.
- Live Relabel / Remove POST `/ml/samples/{id}/relabel|remove`; the hub's
  `ml.sample_updated` echo applies them, so they persist in `labels_override.json`.
- VizCard swaps its mock cards for `GET /ml/viz` when Live, refetches on
  `ml.results_ready`, takes single `ml.viz` cards, and downloads image cards' PNGs.
- `MLSession.ApplyMetrics(MLMetrics)` also remains available for direct adapters.
- `SetRunState(status, totalEpochs)` controls completion.
- `ApplySampleUpdate(id, label_idx, removed)` applies acknowledged sample edits.
- `SelectPair(true_idx, pred_idx)` connects matrix selection to ResultsWall.
- Set `replay = false` to disable local mutation and replay, subscribe to
  `RelabelRequested(id, label)` / `RemoveRequested(id)`, POST the existing REST
  routes, then apply the acknowledged updates. Keep the generated reference bundle available
  until a full live dataset/graph/atlas loader is implemented.

`MLLayoutController` consumes `ml.layout` through ForgeNet and maps `ml-dots`,
`ml-live`, `ml-viz`, and `ml-regression` to module panels without modifying Core
Workspace. The sandbox includes hidden VizCard and regression panels so switching
from image classification to regression updates visible panels without a scene
reload. `MLWorkspaceBridge` assigns Resources prefabs to Workspace's ML mode before
it starts and rebuilds that mode when a layout event arrives. The prefab builder
creates the runtime-loadable Resources copies.

Both mock and hub atlases use metadata-driven UVs (`/ml/atlas` supplies the same
cell/cols/index mapping as the mock). Hub data is not saved into the scene;
exiting Play returns to the mocks.

**XR:** the sandbox has Main's rig (OVRCameraRig with passthrough + Interaction SDK
hands/controllers, floor collider). `MLSandboxRig` uses it when an XR runtime is
running and the desktop camera otherwise. **Forge > ML > Use OpenXR Simulator** is
remembered per machine; also activate the simulator (Window > Meta > Meta XR
Simulator > Activate). `MLSandboxBuilder.BuildScene()` rebuilds only the scene.

## Verification checklist

1. Build assets; verify there are no C# or shader errors in the Console.
2. In Play, scrub, click/inspect and return dataset cards; check metadata and model pulses.
3. Inspect a red result, Relabel to its prediction; verify green tint and counts.
4. Open Show mistakes, page, filter by class, inspect a sample and Remove it.
5. At epoch 8+, verify overfit banner; at completion verify confusion and accuracy.
6. Click a populated off-diagonal matrix cell; verify ResultsWall's pair filter.
7. Use All to reset filters and Replay to restart curves.
8. Switch to the regression layout and select scatter points; then cycle the seven VizCards.
9. For XR Simulator, enable **Forge > ML > Use OpenXR Simulator**, restart Play,
   and check Meta XR Simulator's connected-app status. A physical Quest is still
   needed to check Quest performance and controller/hand behavior.

Automated Play-mode check (builds the scene/prefabs first):
`Unity -batchmode -nographics -projectPath <repo>/unity
-executeMethod MLSmokeCheck.BuildAndRun -logFile <log>`.
Omit `-quit`; the check exits after initialization and assertions.

Validated with Unity 6000.6.7f1 on 2026-10-23: asset generation and the batch
Play-mode smoke assertions passed (loading, all four panels, relabel/remove,
pair filtering, overfit, completion, replay). No ML C# compile errors or Meta
canvas assertions remained in the final run. This was a `-nographics` run;
rendering, actual mouse/ray/poke input, and headset performance still need the
manual checklist above. The local editor logged a search-index exception and
terminated with exit 134 during native shutdown *after* printing the smoke PASS.

PR dependency: merge `satyabrata_dev_main` into `main` before the Unity-panel PR.
The dependency branch is merged here for development; this branch does not merge
either PR into `main`.

Generated-mock integration validation (2026-10-23): all four supplied
`forge-sdk/tests/test_mocks.py` tests passed. An isolated Unity 6000.6.7f1
batch Play-mode run passed all 1,000-sample assertions, 2048 atlas size, first-row,
row-transition and final-cell UV checks, initial confusion counts, relabel/remove,
pair filters, all 15 replay epochs, explicit overfit alert, completion and restart.
The editor again exited with native shutdown code 134 after the smoke PASS;
rendering and headset checks remain separate from this `-nographics` test.
The open editor lost its Pipeline connection after package reload; validation
therefore used an isolated copy of the same project and copied back only ML-owned
assets generated by the builder. Shared mock source hashes remained unchanged.

Completion validation (2026-10-24): Unity 6000.6.7f1 compiled and ran the expanded
ML smoke check in an isolated copy with the deprecated Meta XR Simulator 81 package
omitted. It generated all six prefabs plus their Resources copies. Assertions
passed for metadata-card selection, all seven VizCard JSON fixtures and scatter
sample IDs, image/regression layout switching, regression row selection, the
24-image paged mistakes gallery, class filtering, relabel/remove, pair filtering,
replay completion and restart. Unity exited with native code 134 after printing
PASS. The standalone Meta XR Simulator is active, but its XR session still needs
manual verification with the new **Use OpenXR Simulator** toggle. On 2026-10-24,
the toggle was enabled and Play restarted, but Unity reported `OpenXR is not
available` and the simulator reported no app connected. No Quest headset was
connected for ray/poke/thumbstick or performance validation. The editor log also
showed repeated `MaterialPropertyBlock` null references in the two instanced panels
from a prior no-domain-reload Play session; both now initialize the property block
lazily, and the updated smoke run passed.
