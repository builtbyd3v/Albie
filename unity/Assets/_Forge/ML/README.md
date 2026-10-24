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

- **DataStack:** atlas-backed GPU-instanced quads, fanning around the scrub position.
  Click a card or select Inspect to open a larger metadata card to its right. It
  shows available name/path, split, source image shape, file size and model
  prediction/confidence. Missing mock metadata is shown as unavailable. Put back,
  desktop **X** or right-controller **B** returns it. Only nearby cards are drawn;
  all samples remain scrubbable.
- **Model3D:** one block per layer, logarithmic output-dimension sizing, type colors,
  input/output shape labels, sequential pulse on each metrics tick, and hover/select
  details for each layer.
- **ResultsWall:** an atlas-backed GPU-instanced dot wall (up to 1,000 validation
  images in one draw call), ordered by true class and tinted green/red by correctness.
  Hover shows a thumbnail. All/Wrong and confusion-pair filters update the wall.
  **Show mistakes** and **Most confident** open a 24-image paged gallery; **By class**
  opens a class picker for that gallery. Select a dot or image for the detail card,
  predicted confidence and per-class probability bars. Relabel opens a class picker,
  Remove excludes the sample, Looks fine closes the card, and **X / right B** returns
  to the wall/gallery.
- **TrainingDash:** train/val loss and accuracy curves, epoch progress, LR,
  overfit detection (two successive validation-loss increases as train loss falls),
  final confusion matrix and per-class accuracy. Click a matrix cell to filter
  ResultsWall by true/predicted pair. Replay restarts the training animation.
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
items to **Panel** when integrating. Core currently calls its placeholder dataset
`ml-dataset`; the contract ID for this module is `ml-datastack`. Main is untouched.
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
