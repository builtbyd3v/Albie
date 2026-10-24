# hub/ml (ML A)

The ML endpoints: PROJECT_CONTEXT section 7 plus ML_PLAN 7.4. Serves one project repo (`FORGE_ML_REPO`, default `ml-examples/cifar10`) from its `.forge/project/` and `.forge/runs/<id>/`. The hub never imports torch: missing graph/dataset files are computed by `python -m forge.run` in the repo's own Python, and `POST /ml/train` runs the repo's `train_cmd`.

```bash
# from hub/, in an env that has the repo's packages (for CIFAR: conda activate gatorhack-dubz)
uvicorn ml.dev_server:app --port 8000                  # /ml + /ws, until the router is mounted in main.py
python -m ml.make_mocks                                # .data/mocks/ml/{image_classification,regression}
FORGE_ML_REPO=.data/mocks/ml/image_classification uvicorn ml.dev_server:app --port 8000   # mock mode
pytest ml/tests
```

| Endpoint | Notes |
|---|---|
| `GET /ml/classes`, `/ml/graph`, `/ml/dataset`, `/ml/atlas` | dataset items get `thumb_url` and the relabel/remove state |
| `GET /ml/files/atlas.png`, `/ml/image/{id}` | full-size image if the dataset runner saved one, else the atlas cell |
| `GET /ml/sample/{id}` | detail card: metadata + latest prediction |
| `GET /ml/runs`, `/ml/results/{run}`, `/ml/confusion/{run}`, `/ml/summary/{run}` | results reflect relabel/remove (`relabeled`, `removed`, recomputed `correct`) |
| `POST /ml/train {epochs}` | runs the repo's train command with `FORGE_RUN_ID` + `FORGE_HUB`; 409 while one is running |
| `POST /ml/samples/{id}/relabel {label_idx}`, `/remove` | write `.forge/labels_override.json`, broadcast `ml.sample_updated` |
| `POST /ml/ingest {run_id, events}` | forge SDK → `/ws`, `ml.*` only |
| `POST /ml/replay {run_id, speed}` | stream a recorded run onto `/ws` |

**Mounting in `hub/main.py` (Person 5):** `from ml.router import router as ml_router` and `app.include_router(ml_router)`. It uses `app.state.bus`, which main.py already sets, and creates its service on first use.
