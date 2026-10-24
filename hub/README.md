# Forge hub

FastAPI on the laptop: REST + one `/ws` event bus. Unity is a view; the hub owns the state.
So far only the **CAD** module exists (see `../CAD_PLAN.md`). `main.py` and `events.py` are a minimal skeleton for Person 5 to grow.

## Setup (Python 3.11)

```powershell
cd hub
py -3.11 -m venv .venv
.venv\Scripts\activate
pip install -r requirements-dev.txt   # build123d, rocketpy, fastapi, pytest...
```

Never `pip install cadquery` into this env: its OCP build breaks build123d's imports.

## Run

```powershell
uvicorn main:app --host 0.0.0.0 --port 8000     # no --reload (it restarts the CAD worker)
```

- `GET /health` once the first rocket is built (a few seconds on startup).
- Open `cad/parts/rocket.py` in VS Code: **saving it regenerates the rocket** and pushes `cad.model_updated`. A broken save keeps the last good model and emits `cad.regen_failed` with the error line.
- Try it: `curl -X POST localhost:8000/cad/params -H "Content-Type: application/json" -d "{\"fin_count\": 4}"`
- Data (versions, GLBs) lives in `hub/.data/` (gitignored). Delete `.data/cad` to start fresh. Env: `FORGE_DATA_DIR`, `FORGE_CAD_SCRIPT`.

## Test

```powershell
pytest            # about 1 min: geometry, RocketPy cross-check, export validity, API, watcher, timeouts
```

## Mocks for Unity (hub off)

```powershell
python -m cad.make_mocks       # -> hub/.data/mocks/cad/v1..v4 + regen_failed.json
```

Copy that folder to `unity/Assets/_Forge/Mocks/cad/`. The four states are the demo story: green 1.57 → "smaller fins" red 0.84 → "fourth fin" amber 1.25 → "20% longer" green 1.60.

## CAD module map

| File | What |
|---|---|
| `cad/parts/rocket.py` | The part: `PARAMS` + `build(params)`. The file on the main screen. |
| `cad/part_api.py` | The only Forge types a part script imports (`Param`, masses, `FinSet`, `RocketSpec`, `BuildResult`). |
| `cad/export.py` | GLB + cad-vision schema 1.0 metadata, validated with cad-vision's loader rules. |
| `cad/analysis.py` | Mass, CG, Barrowman CP, static margin, markers for Unity. |
| `cad/flight.py` | RocketPy launch-to-apogee sim on an Estes C6 (`cad/data/estes_c6.eng`). |
| `cad/worker.py` | Build process with timeout and respawn; reloads the part script on every job. |
| `cad/service.py` | Version tree on disk, one regen at a time, events, undo/reset, file watcher. |
| `cad/router.py` | `/cad/*` endpoints (CAD_PLAN section 5). |
| `cad/tools.py` | Bit's CAD tools (JSON schemas + `call_tool`) for the agent module. |

## Viz module (`viz/`): pick panels for a repo

`POST /viz/plan {"repo_path": "<folder>"}` digests the folder (file tree + truncated source) and asks TypeSafe Jev 1.14 (a decision model, about 0.4 s per call) which panels fit. Panels scoring 0.7 or more come back highest first, and the same result goes out on `/ws` as `viz.plan` (sticky).

- Env: `TYPESAFE_API_KEY` (503 without it). The digest is sent to `api.typesafe.ai`.
- Repos over about 60k tokens add the experimental `TypeSafe-Danger-Zone-Long-Context: true` header (262k limit).
- Try it: `curl -X POST localhost:8000/viz/plan -H "Content-Type: application/json" -d "{\"repo_path\": \"C:/path/to/repo\"}"`

**Proposed contract addition (not in PROJECT_CONTEXT section 7 yet, needs team OK):**

```
POST /viz/plan { repo_path }  -> VizPlan
viz.plan { repo_path, repo_kind, entry_file?, panels[], scores{panel_id: 0..1}, model }
```

`panels` uses the section 7 panel IDs. Unity does not consume it yet; the idea is that `Workspace` unfolds these panels when you sit down at a repo.
