# ML Mode Plan: open any ML repo, get its visuals

> Companion to `PROJECT_CONTEXT.md` sections 8.3 and 8.4. Written for the two ML people (ML A: Python side, ML B: Unity side) and their coding agents.
> Contract changes in section 7 are **proposals** until the team agrees (PROJECT_CONTEXT section 12).

**Status 2026-10-24:** A1–A5, A8, B1–B6 built; L1 started with the live dot wall (`forge.log(..., model=model)` → `ml.epoch_preds`). Unity panels load the hub's data when Live, relabel/remove go through the hub, `/ml/viz` serves chart cards, the ML sandbox runs in the Meta XR Simulator. Open: teammate repos for A5's cold-open test, A6, A7, the `ml` router in `hub/main.py` (Person 5), ML prefabs in Main (Hudson), Bit's setup cards (Person 5), a Quest build.

**Decided 2026-10-23** with Hudson:
- A **classifier** (an LLM call) reads the repo's key files and says what type of project it is: image classifier, regression, and so on. Each type has a **fixed list of what to show** (section 5). A second step writes the glue code so those visuals work with this particular repo.
- The setup only ever writes inside a `.forge/` folder in the repo. Changes to the user's training script are a **proposed patch the user approves**.
- **Every training run is saved automatically** and can be replayed, so the demo never depends on training behaving live.
- **CIFAR-10** (section 4.3) is test repo #0 and the stage backup. Your own repos are the real targets.
- Extra charts reach Unity as **chart cards** (`VizCard`): one prefab draws line/bar/heatmap/scatter/table charts and also shows a PNG for anything custom.

**Open, and yours to decide (ML team):**
- **Which LLM** does the classifying and adapting (section 6.7).
- **Which graphs are best to watch during training** (section 8).

**Open, Hudson decides:** how VS Code connects to the app (section 3). It doesn't block you.

---

## 0. Your first hour

Both of you, together:
1. Read PROJECT_CONTEXT sections 1, 2, 7, 8.3 and 8.4, then this whole file. Section 5.3 is the picture of what we're building.
2. Each pick **1 or 2 of your own ML repos** (rules in section 4). Ideally one image classifier, plus a regression or NLP project if you have one. Post the names and one line each in chat.
3. Clean them up into `ml-examples/<name>/` (section 4.2). ML A also starts `ml-examples/cifar10/` (section 4.3).
4. Go through section 7 together and agree on the schemas. After that they're frozen; changes go through chat.

Then split: ML A takes the **A track** (section 9), ML B the **B track**.

---

## 1. What we're building

The user opens an ML repo. Within a minute, the room shows the right visuals for *that* project, with no hand-wiring:

1. **Open.** The hub learns which repo is open (section 3).
2. **Pre-scan** (plain Python, no LLM, about 1 s): framework, likely train/model/data files, loss, checkpoints, existing logs.
3. **Classify** (one LLM call): the hub sends the key files, and the LLM answers with the project type from a fixed list (section 5) plus details like the class names. The hub saves that as `.forge/profile.json`.
4. **Adapt** (LLM, up to 3 rounds): the LLM writes `.forge/adapter.py`, a small class that loads *this* repo's model and data, plus a patched copy of the training script with the forge calls added. The hub writes the files; the LLM never touches the disk.
5. **Check** (plain Python): `forge.check` runs the adapter on 8 samples. If it fails, the error goes back to the LLM for another round.
6. **Approve.** Bit says "I can hook up your training with 4 lines" and shows Show diff / Accept / Skip. Nothing touches the user's code before Accept.
7. **Unfold.** The hub sends `ml.layout` (this type's panels). The dataset and the model appear right away.
8. **Train.** The patched script streams metrics while it trains, and the room shows it learning live (section 8). When it ends, the results appear (section 5.3).

The result is cached. Re-opening the same repo skips steps 2 to 5 and goes straight to step 7 in a few seconds.

```
 LAPTOP                                                                                    UNITY
 VS Code opens repo ──► POST /ml/project { path }
                                 │
   hub/ml                        ▼
     prescan ──► classifier ──► adapter ──────► forge.check ──► needs_approval
     hints.json  (1 LLM call)   (LLM, ≤ 3       8 samples        │  Bit: Show diff / Accept / Skip
                 profile.json   rounds)                          ▼
                                                               ready ── ml.layout ─────────────► this type's panels

 train.py + forge calls ── POST /ml/ingest ──► hub ── /ws: ml.metrics + live events ───────────► live training visuals
   └─ always writes .forge/runs/<id>/ (replayable)    REST: results, confusion, atlas, viz ─────► results, dot wall, VizCards
```

---

## 2. Pieces, files, owners

| Piece | Where | What it does | Owner |
|---|---|---|---|
| Example repos | `ml-examples/` | CIFAR-10 + your cleaned repos. Test inputs for everything | A + B |
| `forge` SDK | `forge-sdk/forge/` | `watch` / `log` / `evaluate` for training scripts, `Adapter` base class, `check`, `replay`, runners | A |
| Pre-scan | `hub/ml/prescan.py` | Heuristics → `.forge/hints.json` | A |
| Setup | `hub/ml/setup/` | Classifier + adapter prompts, LLM wrapper, check loop, cache, guardrails | A |
| Types | `hub/ml/archetypes.py` | The "if it's X, show Y" list (section 5) | A |
| Hub router | `hub/ml/router.py`, `service.py`, `models.py` | Section 7 endpoints + events, run storage, mocks | A |
| Unity panels | `unity/Assets/_Forge/ML/` | Section 5.3 for image classification, `VizCard`, live training panel, layout switching | B |

The A track is heavier. If ML B finishes early, B takes A6 and A7 (section 9).

```
forge-sdk/                      pip install -e forge-sdk
  forge/__init__.py             watch, log, evaluate
  forge/adapter.py              Adapter, Sample (7.2)
  forge/check.py                python -m forge.check <repo>
  forge/run.py                  python -m forge.run <job> --repo <path>   (the hub calls this with the repo's python)
  forge/replay.py               python -m forge.replay <run dir> --speed 4
  forge/runners/                graph, dataset, eval, live, regression
hub/ml/
  router.py service.py models.py archetypes.py prescan.py open.py make_mocks.py
  setup/classify.py  setup/adapt.py  setup/llm.py  setup/prompts/classify.md  setup/prompts/adapt.md
  tests/
ml-examples/
  cifar10/  <your-repo-1>/  <your-repo-2>/  README.md
```

---

## 3. VS Code: still open, and why it doesn't block you

We haven't decided how the app connects to VS Code yet (screen streaming per PROJECT_CONTEXT 8.2, maybe plus an extension or MCP). For ML it doesn't matter, because whatever we pick ends up making **one call**:

```
POST /ml/project { "path": "C:/Users/you/code/my-classifier" }
```

Until it's decided, make that call yourself with `curl`, or run `python -m ml.open <path>` (a 10-line helper in `hub/ml/open.py`).

| Option (Hudson decides) | How the hub learns the repo |
|---|---|
| Small VS Code extension | Posts the path on workspace open; can also add a "Forge: Train" command |
| MCP server in the hub | VS Code's AI agent calls a `forge_open_project` tool. Only fires when the user talks to that agent |
| Streaming only | The user picks the repo from a list in XR |
| CLI | `python -m ml.open .` in the VS Code terminal (what we use for now) |

Showing the patch doesn't depend on this either. VS Code runs on the laptop, so the hub runs `code --diff train.py .forge/patch/train.py` and the diff opens in the real VS Code, which is what the main screen shows.

---

## 4. Test repos (step 1)

### 4.1 What makes a good one
- Trains in **under about 5 minutes on your laptop** (subset the data if needed), or ships a checkpoint.
- **PyTorch first.** sklearn is fine for the regression one. Keras only if it's all you have.
- The dataset downloads by script or is small. Nothing behind a login.
- Small enough to read: roughly under 2,000 lines of Python.
- Has a story: some classes it's bad at, a plausibly mislabeled sample, a feature that matters.

### 4.2 Cleanup rules
Clean it **a little**. The system has to handle normal messy repos, so don't rewrite yours into our shape.
- **Keep:** the original structure, file names, training loop, and model code.
- **Remove:** dead experiments, notebooks that duplicate scripts, hard-coded absolute paths, required W&B or cloud logins (make them optional), giant config systems where a few argparse flags would do.
- **Make sure:** the entry point (`python train.py` or whatever it is) runs from the repo root in a fresh venv after `pip install -r requirements.txt`; seeds are fixed; there's an `--epochs` flag; a val split exists.
- **Never commit:** datasets, checkpoints over about 20 MB, `.venv/`, `runs/`. Put the download step in the README.
- Add `ml-examples/<name>/README.md`: what it is, setup, how long an epoch takes, what's interesting in the results.

Don't hand-write a `.forge/` folder for your own repos. That's the setup step's job, and your repos are how we test it. CIFAR-10 is the one exception.

### 4.3 CIFAR-10: test repo #0 and stage backup (ML A)
CIFAR-10 is a standard dataset of 60,000 32x32 photos in 10 classes (airplane, automobile, bird, cat, deer, dog, frog, horse, ship, truck), loaded with `torchvision.datasets.CIFAR10`.
- `ml-examples/cifar10/`: `train.py`, `model.py` (small CNN, 3 conv blocks), `data.py`. A 10,000-image train subset by default (`--full` for all of it), and a fixed **1,000-image val subset** (the one shown in XR).
- **Flip about 3% of the labels on purpose**, in train and in the val subset, and record which ones in `noise.json`. They show up as confident mistakes on the dot wall even though the model is right: "the model was right, the data was wrong" (demo beat 3).
- This repo gets a **hand-written `.forge/`** (`profile.json`, `adapter.py`) with the patch already applied. It's the worked example the adapter prompt gets, the golden output for tests, and the backup if the LLM misbehaves on stage.

---

## 5. Project types: "if it's X, show Y"

The classifier picks exactly one type. Each type is one entry in `hub/ml/archetypes.py`: the panels to unfold, the visuals to compute, and the adapter methods it needs. Adding a type means adding an entry and its runners. Unity needs no new code for charts, because it just reads `ml.layout` and new charts are VizCards.

### 5.1 The list

| Type | Typical signals | Tier |
|---|---|---|
| `image_classification` | torchvision, `ImageFolder` / CIFAR / MNIST, conv layers, `CrossEntropyLoss` | MUST |
| `regression` | `read_csv`, `MSELoss` / `L1Loss`, sklearn `LinearRegression` / `Ridge`, `XGBRegressor`, one numeric target | SHOULD |
| `text_classification` | `transformers`, a tokenizer, `load_dataset`, text columns + `CrossEntropyLoss` | STRETCH |
| `generic` | anything else, or the classifier isn't sure | MUST (fallback) |

`generic` makes sure every repo shows something: training curves, the model if it's PyTorch, and any extra charts.

### 5.2 What each type shows

| Visual | image_classification | regression | text_classification | generic |
|---|---|---|---|---|
| Dataset | stack of images (5.3) | rows + feature histograms | stack of text cards | no |
| 3D model, input shape on top, output shape on the bottom | yes | yes; linear models get a coefficient bar chart instead | yes (collapsed per block) | if PyTorch |
| Live training graphs (section 8) | yes | yes (loss, R², MAE) | yes | yes |
| Results summary | accuracy, per-class accuracy, confusion matrix | R², MAE, residual histogram, feature importance | accuracy, per-class accuracy, confusion matrix | final metrics |
| Per-sample results | dot wall + mistakes gallery (5.3) | predicted-vs-actual scatter; click a point → its row | dot wall + mistakes gallery with text cards | no |
| Repo-specific extras (LLM-written PNGs, A7) | up to 3 | up to 3 | up to 3 | up to 3 |

### 5.3 Image classification, step by step

This is the reference for how a type should look and feel. Positions are a starting point; the final slots are decided in Hudson's `Workspace`.

```
                    ┌──────────────── live training graphs (above) ────────────────┐
 ┌ dataset ┐ ┌ picked  ┐                                         ┌ results ───┐ ┌ dot wall ───────────┐
 │ stack   │ │ image + │          ┌──── VS Code ────┐            │ accuracy   │ │ ● ● ● ● ● ● ● ● ● ● │ [Show mistakes]
 │ (top    │ │ metadata│          │                 │            │ per class  │ │ ● ● ● ● ● ● ● ● ● ● │ [Most confident]
 │  left)  │ └─────────┘          └─────────────────┘            │ confusion  │ │ ● ● ● ● ● ● ● ● ● ● │ [By class]
 └─────────┘     ┌ 3D model on the desk: input shape on top, output on the bottom ┐                     [Confused pairs]
```

1. **Open.** The classifier decides it's `image_classification`, and this layout unfolds.

2. **Dataset stack (top left).** The images as a stack of paper, seen from the side.
   - Point at it (controller ray, hand ray or mouse) and move up and down the stack. The sheet under the pointer lifts a little and a label shows its **file name** (and class). The thumbstick or scroll wheel also scrubs.
   - **Click** (trigger, pinch or left click): the sheet slides out and **opens to the right of the stack**, big, with its metadata:
     - the image, file name and path
     - class label and split (train/val)
     - image size, channels, file size
     - the model's prediction and confidence, if a trained model exists
   - Press **X** to put it back in the pile (B on the right controller, X on the desktop keyboard, or the card's **Put back** button when using hands).
   - The stack shows the fixed 1,000-image val subset, with a train/val toggle if there's time.

3. **3D model (on the desk, center left).** Shown as soon as the repo has a model. It's built from the code, so no trained weights are needed.
   - Clean, good-looking blocks, one per layer. Color by layer type (conv, pool, linear, norm, activation), size from the layer's output shape.
   - **Input shape above** the top block (`3 x 32 x 32`), **output shape below** the bottom one (`10 classes`).
   - Hover a block: layer name, type, parameter count, in/out shape.
   - During training a pulse runs down it on every update.

4. **Live training (above).** Graphs that update while it trains. Which graphs is **your call** (section 8).

5. **Results (right), when training ends.** A panel pops up with how well it did: overall accuracy, val loss, best epoch, per-class accuracy bars, and the confusion matrix. Clicking a confusion matrix cell does the same as Confused pairs below.

6. **Dot wall (the big panel on the right).** One dot per val image: **green = right, red = wrong.** It's dots, not pictures, so all 1,000 fit on one screen and you see the pattern at a glance.
   - Rows are grouped by true class, so a class with a lot of red stands out.
   - Hover a dot: a small thumbnail. Click it: that image's detail card (step 8).
   - Options next to the wall:
     - **Show mistakes:** a gallery of every wrong image.
     - **Most confident mistakes:** wrong images where the model was very sure. This is where mislabeled data hides.
     - **By class:** pick a class, see only its mistakes.
     - **Confused pairs:** the biggest mix-ups ("cat ↔ dog: 41"); click one to see those images.

7. **Mistakes gallery.** A grid of thumbnails (about 24 per page; page with the thumbstick or scroll). Click one and it gets big (step 8). X goes back to the dot wall.

8. **Detail card.**
   - The image, large.
   - **What the model said:** label and confidence.
   - **What it should have been:** the true label.
   - **Probability bars for every class.**
   - Actions:
     - **Relabel** (class picker): the label was wrong.
     - **Remove:** the image is junk.
     - **Looks fine:** the model is wrong; leave the image alone.
     - **Ask Bit** (with Person 5): "why did it think this was a dog?"
   - Relabel and Remove are saved to `.forge/labels_override.json`. The dot updates (it gets a small marker, and a relabel that now matches the prediction turns green).

Data each piece needs (all section 7 of PROJECT_CONTEXT, plus 7.4 here):

| Piece | Data |
|---|---|
| Dataset stack + picked image | `GET /ml/dataset`, `GET /ml/atlas`, `GET /ml/sample/{id}`, `GET /ml/image/{id}` |
| 3D model | `GET /ml/graph` |
| Live training | `ml.metrics` + whichever live events section 8 picks |
| Results, dot wall, gallery, detail card | `GET /ml/results/{run_id}`, `GET /ml/confusion/{run_id}`, `GET /ml/viz`, atlas thumbnails |
| Relabel / Remove | `POST /ml/samples/{id}/relabel`, `POST /ml/samples/{id}/remove` |

### 5.4 The other types
Regression and text get the same step-by-step write-up once someone has a repo of that type, using 5.3 as the template: what's where, what you can point at, what clicking does, and what data each piece needs.

---

## 6. Setup: classifier + adapter

### 6.1 When it runs
- On `POST /ml/project`. The cache key is a hash of every `.py`, `.yaml`, `.toml`, `.json` and `requirements*.txt` file in the repo, skipping `.forge/`, data and venvs. If the key matches and `forge.check` passes, the project is `ready` in seconds.
- `POST /ml/project/rescan` ignores the cache ("Bit, look again").
- Budget: cold under 2 minutes, warm under 5 seconds. On stage we open a warm repo.
- Bit narrates it from the `ml.project_state` events ("ooh, new project, one sec", then the summary card). Person 5 maps those events to `pet.state` / `pet.say`.

### 6.2 What it writes
```
<user repo>/.forge/
  .gitignore             contains "*", so none of this shows up in the user's git
  hints.json             pre-scan output
  profile.json           classifier output (7.1)
  adapter.py             adapter output (7.2)
  extra_viz.py           optional (A7)
  patch/train.py         the proposed patched script; train.py.orig is kept once it's applied
  check.json             last forge.check result
  cache_key
  labels_override.json   relabel/remove decisions
  runs/<run_id>/         events.jsonl, graph.json, results.json, confusion.json, atlas.png, viz/*.png
```

### 6.3 Pre-scan (`hub/ml/prescan.py`, no LLM)
It walks the repo (skipping `.git`, `.forge`, venvs, data folders and files over 1 MB) and records:
- imports: `torch`, `torchvision`, `lightning`, `tensorflow`/`keras`, `sklearn`, `xgboost`, `transformers`
- likely entry points: `train*.py`, `main.py`, files with `if __name__ == "__main__":` and an optimizer
- loss and dataset hints: `CrossEntropyLoss`, `MSELoss`, `ImageFolder`, `read_csv`, `load_dataset`
- checkpoints (`*.pt`, `*.pth`, `*.ckpt`, `*.joblib`, `*.pkl`) and logs (`runs/`, `lightning_logs/`, `events.out.tfevents.*`, `trainer_state.json`)
- the Python interpreter: `.venv`, `venv`, `env`, or an active conda env. If none is found, setup fails with "which python?" and the user sets it.
- **which files to send to the LLM:** entry point, model, data loading and configs first, up to a fixed size budget. Small repos (section 4.1) fit almost whole.

### 6.4 Classifier (one LLM call)
- **In:** `hints.json` + the contents of the files the pre-scan picked.
- **Out:** JSON matching `profile.json` (7.1). The type must come from the list in 5.1. The hub validates it with pydantic; on error it asks once more with the validation message.
- **Not sure?** Below a confidence threshold (start at 0.6), Bit asks instead of guessing: "Looks like an image classifier, or is it regression?" The answer goes to `POST /ml/project/type`. If nobody answers, it's `generic`.
- **Prompt outline** (`setup/prompts/classify.md`): "Here are the key files of an ML repo. Say which type it is, from this list: [...]. Fill in this JSON: [...]. Pick up to 3 extra visuals that would matter for *this* project, and say why. If you're unsure, say `generic` and explain in `notes`."

### 6.5 Adapter (LLM, up to 3 rounds)
- **In:** `profile.json`, the same key files, the `forge.Adapter` source, and CIFAR-10's `.forge/adapter.py` as a worked example.
- **Out:** JSON with the full text of `adapter.py`, the full text of the patched training script, and optionally `extra_viz.py` (A7). The hub writes them to `.forge/` and `.forge/patch/` and builds the diff itself.
- **Rules in the prompt:** the adapter **imports the repo's own model and data code; it doesn't copy it.** The patch adds the forge calls (7.3) and changes nothing else.
- **Check loop:** the hub runs `<repo python> -m forge.check <repo>`. That calls every adapter method on 8 samples and checks the results:
  - probabilities sum to 1
  - `len(classes)` matches the output width
  - sample ids are unique and identical across two calls
  - the whole check finishes within 60 s

  On failure, the traceback and the failed check go back to the LLM in the same conversation. After **3 rounds** the project is `failed` with the last error, and the UI falls back to `generic`.

### 6.6 Guardrails
- **The LLM never touches the disk and never runs anything.** It only returns text; the hub decides what to write and only writes inside `.forge/`. That keeps the LLM swappable and makes this rule easy to enforce.
- **Only `POST /ml/project/patch {accept: true}` applies the patch**, and only after the user has seen the diff (PROJECT_CONTEXT rule: no irreversible action without a confirmation step). The hub keeps `<file>.orig` and refuses if the file changed after the patch was written.
- **Never trains during setup.** The check uses 8 samples, `torch.no_grad()` and `num_workers=0`.
- Repo contents (READMEs, comments) are data for the LLM, not instructions.
- Timeouts: classifier 60 s, adapter 120 s per round, check 60 s.

### 6.7 Which LLM: your call
Not decided. Pick one before starting A5, then post the pick and why in chat. What it needs:
- Reliable JSON output (a JSON mode or structured outputs).
- Good at reading Python, with enough context for a few thousand lines of code.
- Under about 30 s per call.
- An API key that lives in the hub's environment and never in git.

Put every call behind one wrapper, `hub/ml/setup/llm.py` (for example `complete_json(system, messages, schema) -> dict`), so switching models is a one-file change. If you'd rather use an agent framework that explores the repo on its own, that's fine too, as long as the rules in 6.6 still hold: it writes only in `.forge/`, and it gets no shell and no web access.

### 6.8 Without the LLM
If there's no key, or the LLM fails on stage, a hand-written `.forge/` (like CIFAR-10's) works exactly the same: the hub only cares that `profile.json` validates and `forge.check` passes. That's also the way to debug: write the adapter for a repo by hand once, then get the LLM to produce something equivalent.

---

## 7. Contracts (proposed additions to PROJECT_CONTEXT section 7)

### 7.1 `.forge/profile.json`
```json
{
  "schema": 1,
  "archetype": "image_classification",
  "confidence": 0.9,
  "framework": "pytorch",
  "python": ".venv/Scripts/python.exe",
  "entry_points": { "train": "train.py", "model": "model.py:SmallCNN", "data": "data.py:get_loaders" },
  "train_cmd": "python train.py --epochs 10",
  "classes": ["airplane", "automobile", "bird", "cat", "deer", "dog", "frog", "horse", "ship", "truck"],
  "target": null,
  "input_shape": [3, 32, 32],
  "checkpoint": "checkpoints/best.pt",
  "existing_logs": [],
  "extra_viz": [{ "viz_id": "class_balance", "why": "train set has 3x more dogs than cats" }],
  "summary": "Image classifier, 10 classes, small CNN on CIFAR-10.",
  "notes": ""
}
```
- `archetype` (the project type): `image_classification | regression | text_classification | generic`
- `framework`: `pytorch | lightning | sklearn | keras | hf_transformers | other`
- Paths are relative to the repo root, with forward slashes.
- `classes` is for classification only; `target` (the column being predicted) is for regression only.
- `summary` is one sentence Bit can say out loud.

### 7.2 `forge.Adapter` (`forge-sdk/forge/adapter.py`)
```python
class Adapter:
    archetype: str                                          # must match profile.json
    def build_model(self): ...                              # untrained model, for the 3D model before any training
    def load_model(self): ...                               # trained model from the latest checkpoint, or None
    def example_input(self): ...                            # one batch the model accepts (graph + shapes)
    def classes(self) -> list[str]: ...                     # classification only
    def samples(self, split="val", limit=1000) -> list[Sample]: ...
        # Sample(id: str, item: PIL.Image | str | dict, label: int | float, name: str, meta: dict)
        # ids stable across calls and runs; name = file name; meta = path, size, etc. for the picked-image card
    def predict(self, model, items) -> np.ndarray: ...      # [N, C] probabilities, or [N] values for regression
    def features(self, model, items) -> np.ndarray | None: ...  # [N, D] penultimate layer; optional (embedding cloud, section 8)
```
For sklearn, `build_model` / `load_model` return the estimator and `example_input` returns `None`. There's no layer graph, so the hub shows coefficients or feature importances instead.

### 7.3 What the training patch adds
These are the same calls as PROJECT_CONTEXT 8.3, but they need fewer arguments because the adapter supplies the dataset and classes:
```python
import forge                                         # top of the file
forge.watch(model)                                   # after the model is built: graph + dataset
    forge.log(epoch=e, train_loss=tl, train_acc=ta, val_loss=vl, val_acc=va, lr=lr, model=model)   # end of each epoch
forge.evaluate(model)                                # after training: results, confusion, extras
```
- `model=model` on `log` turns on the live visuals that need the model (section 8). Leave it off and you still get the curves.
- The SDK **never raises into the training script**. Every error becomes one warning line.
- With the hub off it still writes `.forge/runs/<run_id>/` (`events.jsonl` + result files), so **every run is a replayable recording.**
- The hub address comes from `FORGE_HUB` (default `http://127.0.0.1:8000`). Events are batched on a background thread and posted to `POST /ml/ingest`.
- Explicit arguments still work without an adapter (`forge.watch(model, val_dataset, classes=CLASSES)`), so a hand-instrumented script needs no `.forge/`.

### 7.4 REST
```
POST /ml/project         { path }            -> { status }                    start setup, or reuse the cache
GET  /ml/project                             -> { path, status, profile?, layout?, patch?: { files: [{ path, diff }] }, error? }
POST /ml/project/rescan                      -> { status }                    ignore the cache
POST /ml/project/type    { archetype }       -> { status }                    user confirms or overrides the type
POST /ml/project/diff                        -> { ok }                        opens `code --diff` on the laptop
POST /ml/project/patch   { accept: bool }    -> { applied: string[] }         the only way the user's code changes
GET  /ml/sample/{id}                         -> { id, name, path, split, label_idx?, y?, meta: {...}, pred_idx?, probs? }   picked-image / detail card
POST /ml/ingest          { run_id, events: [{ type, payload }] } -> { ok }    forge SDK → hub; the hub rebroadcasts on /ws
GET  /ml/runs                                -> [{ run_id, started_at, status, epochs, best_metric? }]
POST /ml/replay          { run_id, speed }   -> { run_id }                    stream a recorded run as if it were live
GET  /ml/viz?run_id=                         -> VizCard[]
GET  /ml/viz/{run_id}/{viz_id}.png           -> PNG                           for kind "image" cards

changed:
GET  /ml/dataset?split=val -> [{ id, name, kind: "image"|"text"|"row", label_idx?, y?, thumb_url?, text? }]
GET  /ml/results/{run_id}  -> classification unchanged; regression: { samples: [{ id, y_true, y_pred, residual }] }
```

### 7.5 WebSocket events
```
ml.project_state { path, status: "scanning"|"classifying"|"confirm_type"|"adapting"|"checking"|"needs_approval"|"ready"|"failed",
                   archetype?, candidates?: string[], summary?, error? }
ml.layout        { archetype, title, panels: string[], viz_ids: string[] }
ml.viz           VizCard (7.6)

only if picked in section 8:
ml.epoch_preds   { run_id, epoch, pred_idx: int[] }                                         same order as /ml/dataset, N ≤ 1000
ml.probe         { run_id, epoch, samples: [{ id, pred_idx?, probs?: float[], y_pred? }] }
ml.embedding     { run_id, epoch, ids: string[], label_idx: int[], points: float[][] }      N x 3, N ≤ 1000
ml.layer_stats   { run_id, epoch, layers: [{ name, update_ratio }] }
```
- Existing `ml.*` events are unchanged.
- Big payloads (results, the atlas, embeddings over 1,000 points) go over REST; the event only says they're ready.
- New panel IDs: `ml-viz` (the VizCard stack), `ml-dots` (dot wall + mistakes gallery), `ml-live` (live training extras).

### 7.6 VizCard
One spec, one Unity prefab. Keep each card under about 50 KB.
```
{
  viz_id, run_id?, title, subtitle?, panel: "ml-viz" | "ml-training",
  kind: "line" | "bar" | "heatmap" | "scatter" | "table" | "image" | "text",
  x?: (string|number)[], series?: [{ name, y: number[] }],          line, bar
  cells?: number[][], x_labels?: string[], y_labels?: string[],     heatmap
  points?: [{ x, y, id?, color_idx? }], x_label?, y_label?,         scatter (clicking a point with an id opens that sample)
  columns?: string[], rows?: (string|number)[][],                   table
  png_url?, alt?,                                                   image (matplotlib from the hub or extra_viz.py)
  text?                                                             text (a short plain paragraph)
}
```

---

## 8. Watching it learn: you decide which graphs

Loss and accuracy curves (train vs val) are the baseline. Plan on them, along with the overfit banner (val loss up 2 epochs in a row while train loss falls) and the pulse on the 3D model.

Beyond that, **which graphs are best to watch while it trains is your call (ML A + B together)**:
1. Try the candidates below on CIFAR-10 and on your own repo.
2. Keep the 3 or 4 that are actually readable from a meter away and tell you something.
3. Post the pick in chat, then build those (task L1 in section 9).

| Candidate | What you'd see | How |
|---|---|---|
| Live dot wall | the dot wall from 5.3, filling in during training: red dots turn green epoch by epoch | one pass over the 1,000 val samples per epoch → `ml.epoch_preds` |
| Per-class accuracy over time | a class × epoch heatmap; you see which classes learn late | same pass as the live dot wall |
| Probe strip | 16 fixed val images (2 or 3 of them mislabeled) whose prediction bars change every epoch: "cat?" turns into "cat!" | `adapter.predict` on 16 items → `ml.probe` |
| Embedding cloud | the val images as points in a 3D volume, colored by class, pulling apart into clusters as it learns | `adapter.features` → PCA to 3D, aligned to the previous epoch (orthogonal Procrustes) so it morphs instead of jumping → `ml.embedding` |
| Layer activity | 3D model blocks glow by how much each layer changed this epoch (`‖ΔW‖ / ‖W‖`); frozen or dead layers stay dark | weight snapshot per epoch → `ml.layer_stats` |
| Learning rate, epoch time, ETA | small line or number readouts | already in `ml.metrics`, plus timestamps |

Rules for whatever you pick:
- It all runs in `forge.log(..., model=model)` at the end of each epoch, inside the training process.
- Together it costs under 5% of epoch time.
- Use `torch.no_grad()` and eval mode (switch back to train mode afterwards), on the model's own device.
- For the embedding cloud, use PCA rather than UMAP or t-SNE: it's fast, deterministic and stable from epoch to epoch, which keeps the animation readable.

---

## 9. Tasks

### A track (ML A: Python)
| # | Task | Done when |
|---|---|---|
| A1 | `ml-examples/cifar10/` + its hand-written `.forge/` (4.3) | `python train.py --epochs 3` runs; `python -m forge.check ml-examples/cifar10` passes |
| A2 | `forge-sdk`: `watch` / `log` / `evaluate`, run folder + `events.jsonl`, background poster, never raises, `python -m forge.replay <run dir> --speed 4` | with the hub off, a run leaves a complete `.forge/runs/<id>/`; with it on, the same run replays into `/ws` |
| A3 | Runners (`forge/runners/`): graph (forward hooks on `example_input`), dataset + thumbnail atlas + per-sample metadata, eval (results, confusion, per-class); regression versions | every output validates against the pydantic models in `hub/ml/models.py` |
| A4 | `hub/ml/` router + service + `make_mocks.py` + tests. Include the router in `hub/main.py` (Person 5 owns that file, so tell them first) | `python -m ml.make_mocks` writes `hub/.data/mocks/ml/` for image_classification and regression; the endpoints serve a real CIFAR run |
| A5 | Pick the LLM (6.7). Pre-scan + classifier + adapter + check loop + guardrails (section 6). The cache can wait | a cold open on each teammate repo reaches `needs_approval` or `ready` with no hand edits on at least 2 of them; nothing written outside `.forge/` |
| A6 | Log-tail fallback for "Skip" (no patch): TensorBoard event files, Lightning `metrics.csv`, HF `trainer_state.json` → `ml.metrics` | a repo that already logs to TensorBoard shows live curves without the patch |
| A7 | `extra_viz`: the adapter also writes `.forge/extra_viz.py` (up to 3 functions, each returning a matplotlib figure or a VizCard dict) for the extras the classifier picked | at least one teammate repo gets a project-specific chart that makes sense |
| A8 | Recordings: a full CIFAR run and the best teammate-repo run, copied to `demo/fallback/` | `forge.replay` of each drives the whole ML mode with no training running |

Order: A1 → A2 → A3 → A4 → A5 → A8 → A6/A7. ML B is blocked on A4's mocks, so if A4 slips, hand-write a few mock JSON files on day 1.

### B track (ML B: Unity, in `Assets/_Forge/ML/`, sandbox `MLSandbox.unity`, hub off, mocks in `Assets/_Forge/Mocks/ml/`)
Everything works with controller ray, hand ray/poke and mouse (PROJECT_CONTEXT 12).

| # | Task | Done when |
|---|---|---|
| B1 | Dataset stack (5.3 step 2): sheets, hover lift + file name, scrub, click → picked-image card with metadata to the right, X / Put back | in the sandbox you can scrub 1,000 mock images, open any one and put it back, with mouse and in the simulator |
| B2 | 3D model (5.3 step 3): blocks per layer, input shape on top, output shape on the bottom, hover info, pulse | the CIFAR mock graph looks clean and reads correctly at 1 m |
| B3 | Results panel + dot wall + options + mistakes gallery + detail card with Relabel / Remove / Looks fine (5.3 steps 5 to 8). Dots are one instanced mesh with per-dot color | the whole flow works on the CIFAR mock run: dot wall → Most confident mistakes → detail card → Relabel → the dot updates |
| B4 | `VizCard` prefab (a `ForgePanel` variant) that renders all 7 kinds from mock JSON | the sandbox cycles through every mock card; readable at 1 m |
| B5 | Layout: on `ml.layout`, show only that type's panels (with Hudson: the Workspace needs a per-mode panel list it can change at runtime) | swapping between the image and regression mock layouts swaps the panels without reloading the scene |
| B6 | Regression results: predicted-vs-actual scatter VizCard, click a point → row card | works on the regression mocks |

### Both
| # | Task | Done when |
|---|---|---|
| L1 | Pick the live training graphs (section 8), then build them: A does the Python side, B the panel | the picked graphs animate during a replayed CIFAR run |

### Needs from other people
- **Hudson (Core):** decide the VS Code connection (section 3). Make the Workspace take a runtime panel list per mode (B5) and give slots for 5.3's layout. Approve the new panel IDs and VizCard as section 7 contracts.
- **Person 5 (hub + Bit's brain):** turn `ml.project_state` into Bit's states and cards: the summary, the "which type is it?" question, and Show diff → `POST /ml/project/diff`, Accept / Skip → `POST /ml/project/patch`. Add an `ml_get_project` tool so Bit can answer "what is this repo?", and the Ask Bit button on the detail card. Agree on the `ml` router going into `main.py`.

### Tiers
- **MUST:** A1 to A5 for image_classification, A8, the `generic` fallback, B1 to B3, B5, the baseline curves.
- **SHOULD:** the regression type end to end (B6), the setup cache, A6, B4, L1.
- **STRETCH:** text_classification, A7, Grad-CAM heatmaps on the detail card, Lightning/Keras callbacks.

---

## 10. Demo beat 3, revised opening

This replaces the first line of PROJECT_CONTEXT section 2, beat 3, once A5 works. Until then the old version stands (CIFAR-10 already instrumented).

1. VS Code on the main screen opens the project (a teammate's classifier, or CIFAR-10). Bit: "Ooh, new project. One sec." Bit dives into the screen.
2. Bit pops back out with a card: "Image classifier, 10 classes, small CNN. I can hook up your training with 4 lines." **Show diff** opens the diff in VS Code. **Accept.**
3. The layout from 5.3 unfolds: the dataset stack top left, the 3D model on the desk. The user flips through the stack, opens an image, puts it back. Then they hit Train.
4. Live: the curves draw, the model pulses, plus whichever live graphs we picked (section 8).
5. Training ends. Results pop up on the right and the dot wall fills in. **Most confident mistakes** → a "dog" the model calls a cat at 97% → it really is a cat → **Relabel**. Then on to "Hey Bit, why is my model so bad at cats?" (beat 4).

On stage: open the repo once before the demo so the cache is warm, and have `forge.replay` ready in case live training misbehaves.

---

## 11. Gotchas

| Gotcha | What to do |
|---|---|
| Two Python environments: the hub's (3.11, FastAPI, build123d) and the repo's (torch etc.) | Runners and `forge.check` run as subprocesses with the **repo's** interpreter (`profile.python`), and the hub puts `forge-sdk` on their `PYTHONPATH`. For training the user starts themselves, run `pip install -e <path to forge-sdk>` once in the repo env (part of the Accept step). Never install torch into the hub env |
| On Windows, a DataLoader with `num_workers > 0` needs an `if __name__ == "__main__":` guard | The check and the runners always use `num_workers=0` |
| Sample ids must stay stable across runs, or relabel/remove breaks | id = split + index in the fixed subset, or a hash of the file path |
| Quest texture limit is 2048 | 64x64 thumbnails (CIFAR's 32x32 upscaled nearest-neighbor); one 2048 atlas holds 1,024, which is why val is 1,000. The detail card loads the full image from `/ml/image/{id}` |
| 1,000 dots or sheets as separate objects will tank Quest frame rate | One instanced mesh with per-instance color/offset (PROJECT_CONTEXT 12) |
| Big WS messages stall the headset | Events carry ids and small arrays; results, the atlas and big embeddings go over REST |
| Live visuals slowing training down | Keep them under 5% of epoch time (section 8) |
| Relabel/remove | Writes `.forge/labels_override.json`, which `adapter.samples` and `forge.evaluate` apply. Making the next *training* run honor it is per repo: done for CIFAR-10, stretch elsewhere |
| LLM API key | Lives only in the hub's environment. Thanks to the cache, each repo version gets classified cold only once |
| Repo has no val split | The adapter makes one (fixed seed, 10%) and says so in `notes` |

## Not doing
- Training on the Quest. Training always runs on the laptop.
- Supporting every framework. PyTorch first, sklearn for regression; everything else is `generic`.
- Generating Unity code at runtime. New charts are VizCards.
- Changing the user's code before they've seen the diff and pressed Accept.
