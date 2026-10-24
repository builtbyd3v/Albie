# hub/ml (ML A)

The ML endpoints: PROJECT_CONTEXT section 7 plus ML_PLAN 7.4. Serves one project repo (`FORGE_ML_REPO`, default `ml-examples/cifar10`) from its `.forge/project/` and `.forge/runs/<id>/`. The hub never imports torch: missing graph/dataset files are computed by `python -m forge.run` in the repo's own Python, and `POST /ml/train` runs the repo's `train_cmd`.

```bash
# from hub/, in an env that has the repo's packages (for CIFAR: conda activate gatorhack-dubz)
uvicorn main:app --host 0.0.0.0 --port 8000           # the one hub: /ml is mounted in main.py
uvicorn ml.dev_server:app --port 8000                  # ML only (/ml + /ws), no CAD/pet/Slack
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
| `GET /ml/viz?run_id=` | chart cards: the archetype's built-ins plus `.forge/extra_viz.py` output (PNG or VizCard, A7) |
| `POST /ml/train {epochs}` | runs the repo's train command with `FORGE_RUN_ID` + `FORGE_HUB`; 409 while one is running. A script without forge calls still gets live curves (log tail, below) |
| `POST /ml/samples/{id}/relabel {label_idx}`, `/remove` | write `.forge/labels_override.json`, broadcast `ml.sample_updated` |
| `POST /ml/ingest {run_id, events}` | forge SDK → `/ws`, `ml.*` only |
| `POST /ml/replay {run_id, speed}` | stream a recorded run onto `/ws` |

**Mounted in `hub/main.py`:** `app.include_router(ml_router)`; it uses `app.state.bus` and creates its service on first use (`app.state.ml`).

**Log tail (A6, `logtail.py`):** for a training script that doesn't call forge (the user picked Skip). While `POST /ml/train`'s process runs, the hub reads the repo's own logs every 2 s and sends them as `ml.run_state` / `ml.metrics` / `ml.overfit_alert`, then `done` when it exits; after Skip it keeps following, and each new log file is a new run. Reads HF `trainer_state.json`, Lightning `metrics.csv` and TensorBoard `events.out.tfevents.*` (own decoder, no tensorboard package). Tailed runs are saved to `.forge/runs/<id>/events.jsonl` (`run.json` has `source: logtail`), so replay and `/ml/metrics` work. If the forge SDK writes the run's `run.json`, the tail stands down.

**Extra charts (A7, `forge/viz.py`):** the classifier picks up to 3 project-specific charts in `profile.extra_viz`; setup asks the adapter LLM for `.forge/extra_viz.py` with one `viz_<viz_id>(ctx)` function per pick (`ctx`: model, adapter, classes, samples, ids, labels, probs, results, summary), and `forge.check` fails the round if one is missing or the file doesn't import. `forge.evaluate` runs them after evaluation and before `ml.results_ready`: a returned matplotlib figure becomes `runs/<id>/viz/<id>.png`, a returned dict becomes a VizCard in `runs/<id>/viz.json`. `GET /ml/viz` serves both next to the built-in cards (the dicts are validated against `models.VizCard`; PNGs were already picked up). CIFAR-10's hand-written `.forge/extra_viz.py` is the worked example.

## Open any ML repo (A5, ML_PLAN 6)

`POST /ml/project {path}` (or `python -m ml.open <path>`) runs: **pre-scan** (`prescan.py`, no LLM) → **classify** (one Claude call → `.forge/profile.json`; below 0.6 confidence it stops at `confirm_type` and waits for `POST /ml/project/type`) → **adapt** (Claude writes `.forge/adapter.py` + a patched training script, then `forge.check` runs in the repo's own Python; up to 3 rounds with the failures fed back) → **needs_approval** → `POST /ml/project/diff` (opens `code --diff`) and `POST /ml/project/patch {accept}` → **ready** + `ml.layout`. Each step is an `ml.project_state` event on `/ws`.

- A repo whose existing `.forge/profile.json` + `adapter.py` pass `forge.check` goes straight to ready with **no LLM call** (hand-written `.forge/`, or set up before). `POST /ml/project/rescan` forces a fresh setup (a hand-written adapter is kept as `adapter.prev.py`).
- **Guardrails:** the LLM only returns text (`setup/llm.py`, the one wrapper; structured outputs). The hub writes only inside `.forge/`; patch targets must be existing `.py` files in the repo. The patch is a proposal in `.forge/patch/` until Accept, which keeps `<file>.orig` and refuses if the file changed since. Setup never trains.
- **Classifier:** TypeSafe **Jev** (`setup/jev.py`, `setup/classify_jev.py`, the idea from PR #2) whenever `TYPESAFE_API_KEY` is set: typed choices over the 4 types (confidence + probabilities: below 0.6 the top 2 become `candidates`), the training file, model class, data loader and regression target, all picked from candidates the hub extracts from the code; class names come from the code too. ~0.4 s, ~$0.001 per repo. Without the key, or with `FORGE_CLASSIFIER=claude`, Claude classifies.
- **Adapter LLM:** Claude Opus 5.5 (`FORGE_LLM_MODEL` to override) via `anthropic` (`pip install -r ml/requirements.txt`): Jev can't write code. Needs `ANTHROPIC_API_KEY` in the hub's environment, never in git; without it, repos with a working `.forge/` still open, others stop after classifying with a clear error. Repo files are prompt-cached across the adapter rounds.
- **Interpreter:** the repo's `.venv`/`venv`/`env`, else the active conda env, else the hub's own Python; stored as `profile.python`.
- Tests (`ml/tests/test_setup.py`) use a fake LLM: no key, no cost.
