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
FORGE_OFFLINE=1 python forge-sdk/examples/cifar10/train.py --epochs 2

# ML router dev server (until hub/main.py is pushed)
cd hub && uvicorn ml.dev_server:app --reload --port 8000
```

`FORGE_HUB_URL` (default `http://localhost:8000`) points the SDK at the hub.

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

Each `forge.watch(...)` starts a run and writes to `<run_dir>/<run_id>/` (`run_dir` argument, else `$FORGE_RUN_DIR`, else `./forge_runs`; the demo script uses the git-ignored `forge-sdk/runs`):

| File | Feeds |
|---|---|
| `graph.json` | `GET /ml/graph` (one entry per leaf module; shapes exclude batch) |
| `atlas.png` + `atlas.json` | `GET /ml/atlas` (hub adds `atlas_url`) |
| `dataset.json` | `GET /ml/dataset` (hub adds `thumb_url`) |
| `classes.json` | `GET /ml/classes` |
| `run.json` | run id, total epochs, sample count |

Thumbnails are taken from the dataset **before** its `transform`, so `Normalize` doesn't wreck the colors.
Layers must be modules (`nn.ReLU()`, not `F.relu`) to show up in the graph.
