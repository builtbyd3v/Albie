# hub/ml (ML A)

The ML endpoints: PROJECT_CONTEXT section 7 plus ML_PLAN 7.4. Serves one project repo (`FORGE_ML_REPO`, default `ml-examples/cifar10`) from its `.forge/project/` and `.forge/runs/<id>/`. The hub never imports torch: missing graph/dataset files are computed by `python -m forge.run` in the repo's own Python, and `POST /ml/train` runs the repo's `train_cmd`.

```bash
# from hub/, in an env that has the repo's packages (for CIFAR: conda activate gatorhack-dubz)
uvicorn main:app --host 0.0.0.0 --port 8000
uvicorn main:app --host 0.0.0.0 --port 8000           # the one hub: /ml is mounted in main.py
uvicorn ml.dev_server:app --port 8000                  # ML only (/ml + /ws), no CAD/pet/Slack
python -m ml.make_mocks                                # .data/mocks/ml/{image_classification,regression}
FORGE_ML_REPO=.data/mocks/ml/image_classification uvicorn main:app --host 0.0.0.0 --port 8000   # mock mode
pytest ml/tests
```

| Endpoint | Notes |
|---|---|
| `GET /ml/classes`, `/ml/graph`, `/ml/dataset`, `/ml/atlas` | dataset items get `thumb_url` and the relabel/remove state |
| `GET /ml/files/atlas.png`, `/ml/image/{id}` | full-size image if the dataset runner saved one, else the atlas cell |
| `GET /ml/sample/{id}` | detail card: metadata + latest prediction |
| `GET /ml/runs`, `/ml/results/{run}`, `/ml/confusion/{run}`, `/ml/summary/{run}` | results reflect relabel/remove (`relabeled`, `removed`, recomputed `correct`) |
| `POST /ml/train {epochs}` | runs the repo's train command with `FORGE_RUN_ID` + `FORGE_HUB`; 409 while one is running |
| `POST /ml/github/import {url}` | inspect a public GitHub repo or selected folder through the Contents API; supports CIFAR-10 + SmallCNN |
| `POST /ml/github/train {import_id, epochs}` | runs the approved local CIFAR-10/SmallCNN worker for 5–10 epochs; GitHub source is never executed |
| `GET /ml/viz?run_id=` | chart cards: the archetype's built-ins plus `.forge/extra_viz.py` output (PNG or VizCard, A7) |
| `POST /ml/train {epochs}` | runs the repo's train command with `FORGE_RUN_ID` + `FORGE_HUB`; 409 while one is running. A script without forge calls still gets live curves (log tail, below) |
| `POST /ml/samples/{id}/relabel {label_idx}`, `/remove` | write `.forge/labels_override.json`, broadcast `ml.sample_updated` |
| `POST /ml/ingest {run_id, events}` | forge SDK → `/ws`, `ml.*` only |
| `POST /ml/replay {run_id, speed}` | stream a recorded run onto `/ws` |

The Training panel accepts a public GitHub repository URL or a folder URL such as
`https://github.com/Satyabratadas/forge-cifar10-demo` (public, the default "Forge CIFAR demo" preset).
Import inspects `train.py`, `model.py`, `data.py`, and `requirements.txt` from the selected folder and statically
checks for the supported `SmallCNN` + CIFAR-10
structure. It does not clone, import, or run repository code. A supported import selects the reviewed local
`ml-examples/cifar10` trainer and dataset. That worker records epoch, train/validation loss and accuracy, and
learning rate through Forge SDK; the SDK streams `ml.metrics`, `ml.class_acc`, and `ml.probe` events to the hub.
Set optional `GITHUB_TOKEN` in the hub environment to raise GitHub API rate limits for public repository reads.

**Mounted in `hub/main.py`:** the ML router uses `app.state.bus`, which main.py sets, and creates its service on first use.
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

## Albie improves the model (demo)

Voice tools in `tools.py` (registered in `main.py`), with the logic in `improve.py`:

| Say | Tool | What happens |
|---|---|---|
| "Albie, open the ML stuff I've been working on" | `ml_open` | Opens the last project (or one by name: `ml-examples/*`, `~/Desktop/ml-examples/*`, `FORGE_ML_PROJECTS`). The `ml.layout` carries `"open": true`, so the headset unfolds the ML desk (the user must be at the desk). Albie says the last run's score. |
| "What can I do better?" | `ml_improve` | Reads the last run (train/val gap, overfit alert, weakest classes, seconds per epoch). Claude writes a change into `.forge/improve/` within a 2-minute run budget, the diff opens in VS Code, and Albie says what he'd change about 40 s later. The repo isn't touched yet. |
| "Yes" | `ml_apply` | Copies the change in (keeps `<file>.orig`, refuses a file edited since), adds its flags to `train_cmd`, starts training. The curves stream live, and Albie says "went up from 60.6% to 79.0%" when it's done. |
| "Undo that" | `ml_undo` | Restores the code and `train_cmd`, and moves the runs trained with the change to `.forge/runs-undone/`, so the next rehearsal starts from the original score. |
| "Train it again" | `ml_train` | Same as the Train button, and the new score is spoken when it finishes. |

`FORGE_ML_IMPROVE` picks the source of the change:
- `auto` (default): Claude writes it. If Claude fails, the golden CIFAR-10 example falls back to the scripted change.
- `scripted`: for CIFAR-10, the hand-written `improve_demo/cifar10/train.py` (crops + flips, AdamW, one-cycle LR, label smoothing, `--full`): measured **60.5% → 81.0%** in about 90 s. Use it when the demo needs the same number every time.
- `llm`: Claude only.

Measured with Claude: 60.6% → 79.0% (8 epochs on all 50k images) and 60.6% → 72.7% (30 epochs on 10k). Claude's plan varies from run to run.

Rehearse on a copy (for example `cp -R ml-examples/cifar10 ~/Desktop/ml-examples/`), not the golden example: the setup adapter uses the golden `train.py` as its worked example.

## Neural Net Lab (TensorFlow Playground on the ML desk)

A small network learning a 2D dataset live, for the "turn off a neuron, ask Albie why" demo. It trains in the headset
(Unity `ML/NeuralLab/NeuralLabSim`, 30 epochs a second), so it works without a training process or GPU.

- **Open it:** the **Neural Net Lab** button on the Training panel (or "Albie, show me the neural network"). The ML
  desk swaps to the Lab, big and in the middle; **◀ ML project** swaps back.
- **On the panel:** Play (stops by itself once test accuracy reaches 92%; Play again keeps going) / Step / Reset; dataset (circle, XOR, Gauss, spiral), activation, learning rate, noise and
  layers. Tap an input to use it. **Tap a neuron to switch it off**: the boundary and test accuracy change at once.
  Each neuron shows what it has learned; links are blue (positive) or orange (negative), thicker = larger.
- **In 3D on the desk** (`ML/NeuralLab/NeuralNet3D`, volume `ml-neural-3d`): inputs → layers → output left to right,
  each layer a ring of neurons, links blue / orange by weight. Every second a data point flows through and the
  neurons light up with their activation. **Touch a hidden neuron with your fingertip** (or point a ray, or click) to
  switch it off and on (`NeuronTouch`, the same input rules as CAD's Expand ring); it goes dark with its links.
- **Albie switches the views:** "open the neural net" (`nn_open`) from any view, "back to my ML project" (`nn_close`).
- **Albie (`ml/nn_lab.py`):** `nn_open`, `nn_control` (play / pause / step / reset / restore_all, dataset, activation,
  learning_rate, hidden, features, noise), `nn_neuron {neuron: "L1N3", enabled}`, and `nn_status`. `nn_status` gives
  every neuron's id, what it detects (x, y, distance from the center, x·y) and `test_accuracy_if_toggled`, plus the last
  changes with accuracy before and after, so "why did it drop?" gets a real answer.
- **Proposed contract additions (ML_PLAN 7.4):** `ml.nn_command` (hub → Unity, `{seq, action?, dataset?, …}`),
  `POST /ml/nn/state` (Unity → hub, `applied_seq` + the state) and `GET /ml/nn/state`.
- **Prefab:** Forge > ML > Build Neural Lab Prefab (`ML/Editor/NeuralLabBuilder.cs`).

- **"Albie, fix it" (`nn_improve` → Apply card or a spoken yes → `nn_apply`):** Albie says what's holding the network
  back (inputs it lacks, layers too small, a linear activation, a wild learning rate, neurons left off) and proposes the
  measured recipe for the dataset (`RECIPES` in `ml/nn_lab.py`; measured with Forge > ML > Check Neural Lab Recipes).
  On Apply it trains live, pauses once test accuracy stops climbing and says "Test accuracy went from 52% to 100%".
  Measured: spiral with x, y [4, 2] stalls at 52–60% → x, y, x², y², sin x, sin y [8, 8] reaches 97% at 4 s, 100% at 6 s.

Demo script (fix it): Spiral → Play (stuck around 55%) → "Albie, why is it bad? Fix it." → Apply → it climbs to ~100%.

Demo script (neurons): open the Lab → Play on **circle** (about 99% in 5 s) → tap the most important neuron (layer 1, neuron 3 in
our runs: 99.6% → 69%) → "Albie, why did the accuracy drop?" → tap it again (back to 99.6%).
