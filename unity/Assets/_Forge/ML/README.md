# ML Unity panels

Open Unity **6000.6.7f1**, then run **Forge > ML > Build Prefabs and Sandbox**.
Open `Assets/_Forge/ML/MLSandbox.unity` and press Play. The builder uses the
existing Core ForgePanel prefab and never edits Main, packages, or project settings.
It replaces the generated ML prefabs and sandbox; save unrelated scene work first.

The sandbox runs without the hub or a headset. Use a mouse to click buttons, scroll
the results grid, and scroll or drag over the dataset stack. Hover the stack to
scrub with a gamepad left stick or XR right controller thumbstick. XR hands use
the inherited ForgePanel ray/poke canvas interaction and the same drag handlers.
The sandbox itself uses a desktop UI event module and a transient in-memory XR
settings clone to skip simulator startup; Main supplies the XR module. Saved XR
settings are unchanged.

- **DataStack:** atlas-backed GPU-instanced quads, fanning around the scrub position.
  Inspect pulls the focused card forward; Put back returns it. Previous/Next also
  work via ray/poke. Only nearby cards are drawn; all samples remain scrubbable.
- **Model3D:** one block per layer, logarithmic output-dimension sizing, type colors,
  input/output shape labels, sequential pulse on each metrics tick, and hover/select
  details for each layer.
- **ResultsWall:** an atlas-backed GPU-instanced dot wall (up to 1,000 validation
  images in one draw call) with green/red correctness tint. All/Wrong/class and
  confusion-pair filters rebuild the wall without creating sample UI objects.
  Click or poke a dot for the expanded image and per-class probabilities.
  Relabel opens a class picker, Remove excludes the sample, Looks fine dismisses
  inspection.
- **TrainingDash:** train/val loss and accuracy curves, epoch progress, LR,
  overfit detection (two successive validation-loss increases as train loss falls),
  final confusion matrix and per-class accuracy. Click a matrix cell to filter
  ResultsWall by true/predicted pair. Replay restarts the training animation.
- **VizCard:** reusable ForgePanel renderer with line, bar, heatmap, scatter,
  table, image, and text modes. Configure its public fields, then call `Render()`
  after changing data at runtime.

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

- `MLSession` consumes Core's `ml.run_state`, `ml.metrics`, `ml.overfit_alert`,
  and `ml.sample_updated` events automatically after ForgeNet reaches Live. It
  remains mock-first until then; set `useLiveEvents = false` to force replay.
- `MLSession.ApplyMetrics(MLMetrics)` also remains available for direct adapters.
- `SetRunState(status, totalEpochs)` controls completion.
- `ApplySampleUpdate(id, label_idx, removed)` applies acknowledged sample edits.
- `SelectPair(true_idx, pred_idx)` connects matrix selection to ResultsWall.
- Set `replay = false` to disable local mutation and replay, subscribe to
  `RelabelRequested(id, label)` / `RemoveRequested(id)`, POST the existing REST
  routes, then apply the acknowledged updates. Keep the generated reference bundle available
  until a full live dataset/graph/atlas loader is implemented.

Live dataset/atlas fetching, arbitrary-length live metric history, Core WS
subscriptions, and persistent edits are future adapter work. The mock atlas has
metadata-driven UVs; a live loader must supply the same atlas metadata and
ID mapping together with the texture.

## Verification checklist

1. Build assets; verify there are no C# or shader errors in the Console.
2. In Play, scrub, inspect and return dataset cards; check model pulses each epoch.
3. Inspect a red result, Relabel to its prediction; verify green tint and counts.
4. Remove a result; verify it disappears from the grid and dataset stack.
5. At epoch 8+, verify overfit banner; at completion verify confusion and accuracy.
6. Click a populated off-diagonal matrix cell; verify ResultsWall's pair filter.
7. Use All to reset filters and Replay to restart curves.
8. In Main/simulator, verify inherited ray/poke and thumbstick/drag on hardware.

Automated Play-mode check: invoke Unity with `-batchmode -nographics
-projectPath <repo>/unity -executeMethod MLSmokeCheck.Run -logFile <log>`.
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
