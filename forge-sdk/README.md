# forge-sdk

Owner: ML A. Spec: PROJECT_CONTEXT 8.3.

```bash
# from repo root, once
conda create -n gatorhack-dubz python=3.11 -y
conda activate gatorhack-dubz
pip install -e "forge-sdk[demo,hub,dev]"

# tests
pytest forge-sdk/tests

# demo script, printing events instead of sending (FORGE_OFFLINE=1)
cd ml-examples/cifar10 && FORGE_OFFLINE=1 python train.py --epochs 2 && cd -

# ML router dev server (until hub/main.py is pushed)
cd hub && uvicorn ml.dev_server:app --port 8000     # /ml routes + the real /ws event bus
```

## Streaming and replay (A2)

- Every event is appended to the run's `events.jsonl` (`{t, type, payload}`, `t` = seconds since the run started), so **every run is a recording**.
- A background thread batches events and POSTs them to `$FORGE_HUB/ml/ingest` (default `http://127.0.0.1:8000`); the hub rebroadcasts them on `/ws`. Training never waits on the network.
- Hub down? One warning line on stderr, training continues, `events.jsonl` still complete. `FORGE_OFFLINE=1` skips the hub and echoes events to stdout.
- **The SDK never raises into the training script.** Each failure (graph, atlas, a bad metric) becomes one `forge: ... (training continues)` line.
- `forge.log` sends `ml.run_state` + `ml.metrics` and fires `ml.overfit_alert` when val loss rises 2 epochs in a row while train loss falls (once; re-arms if val loss improves).
- Replay a run: `python -m forge.replay <run dir> --speed 4 [--max-gap 2] [--print]` (or `forge replay ...`).

## Extra charts (A7)

The classifier can pick up to 3 charts that matter for one particular project (`profile.extra_viz`). Setup
writes them to `<repo>/.forge/extra_viz.py`; `forge.evaluate` runs the file after evaluation, in the repo's
own Python, before `ml.results_ready`:

```python
def viz_class_balance(ctx):          # named viz_<viz_id>, one per profile entry
    fig, ax = plt.subplots()         # a figure is saved as runs/<id>/viz/class_balance.png
    ax.bar(ctx.classes, counts)
    return fig

def viz_residual_hist(ctx):
    return {"kind": "bar", "title": "Residuals", "x": [...], "series": [{"name": "n", "y": [...]}]}
```                                   # a dict is a VizCard (ML_PLAN 7.6) in runs/<id>/viz.json

`ctx` carries `model`, `adapter`, `classes`, `samples`, `ids`, `labels`, `probs`, `results`, `summary`,
`run_dir` and `predicted()`. Only the profile's ids run; a broken file or chart is one warning line, never
a failed run. `python -m forge.check <repo>` checks the file imports and has every picked function.

## ML mocks for Unity (`unity/Assets/_Forge/Mocks/ml/`)

Regenerate with `python forge-sdk/tools/make_ml_mocks.py` (deterministic, stable GUIDs). Validated by `tests/test_mocks.py`.

| File | Stands in for |
|---|---|
| `ml_classes.json` | `GET /ml/classes` |
| `ml_dataset_val.json` | `GET /ml/dataset?split=val` (1,000 items, ids `val_0000`..`val_0999`) |
| `ml_atlas_val.json` + `ml_atlas_val.png` | `GET /ml/atlas?split=val`. 2048x2048, 64x64 cells (CIFAR's 32 px upscaled nearest-neighbor), 32 cols. Cell `i` is at column `i % cols`, row `i / cols` **counted from the top** (flip V for Unity UVs). Point filter, no mips. |
| `ml_graph.json` | `GET /ml/graph`. `type` is one of `conv pool linear norm activation reshape` (Model3D colors). Shapes exclude the batch dim. |
| `ml_results.json` | `GET /ml/results/mock0001` |
| `ml_confusion.json` | `GET /ml/confusion/mock0001` (rows = true, cols = predicted) |
| `ml_events.json` | WS stream: `[{t, type, payload}]`, `t` = seconds from start. 15 epochs, overfit alert, then results. |
| `ml_noise.json` | The ~3% flipped labels (`clean_idx` -> `noisy_idx`). The model predicts the clean label, so these show as confident red cards. |

`/ml/image/{id}` has no mock file: crop the cell out of the atlas (CIFAR images are only 32x32 anyway).

## Run folder (`forge.watch`)

Each `forge.watch(...)` starts a run and writes to `<run_dir>/<run_id>/` (`run_dir` argument, else `$FORGE_RUN_DIR`, else `.forge/runs` when run inside a repo with a `.forge/` folder like `ml-examples/cifar10`, else `./forge_runs`):

| File | Feeds |
|---|---|
| `graph.json` | `GET /ml/graph` (one entry per leaf module; shapes exclude batch) |
| `atlas.png` + `atlas.json` | `GET /ml/atlas` (hub adds `atlas_url`) |
| `dataset.json` | `GET /ml/dataset` (hub adds `thumb_url`) |
| `classes.json` | `GET /ml/classes` |
| `run.json` | run id, total epochs, sample count |
| `viz/<viz_id>.png` | `GET /ml/viz` image cards (extra charts, ML_PLAN A7) |
| `viz.json` | `GET /ml/viz` dict cards (extra charts, ML_PLAN A7) |

Thumbnails are taken from the dataset **before** its `transform`, so `Normalize` doesn't wreck the colors.
Layers must be modules (`nn.ReLU()`, not `F.relu`) to show up in the graph.

## Results and runners (A3)

- `forge.evaluate(model, val_loader)` predicts every val sample (in dataset order, even from a shuffled loader) and writes `results.json`, `confusion.json` (rows = true, cols = predicted) and `summary.json` (accuracy, val loss, best epoch, per-class accuracy, top confused pairs, the 50 most confident mistakes) to the run folder, then sends `ml.results_ready`.
- `python -m forge.run <graph|dataset|eval> --repo <path>`: the runners the hub calls through a repo's `.forge/adapter.py`. `graph` and `dataset` write to `<repo>/.forge/project/` (`dataset` also writes `samples.json`, the per-sample metadata for the picked-image card); `eval` writes a new `.forge/runs/eval-<id>/`. Regression repos get `{id, y_true, y_pred, residual}` results and R² / MAE / RMSE.
- Every output validates against `hub/ml/models.py` (`tests/test_evaluate.py`). `summary.json` / `Summary` is a proposed addition to the contract.
