"""ML router (PROJECT_CONTEXT section 7, ML_PLAN 7.4). Mount in hub/main.py with `app.include_router(ml_router)`.

Needs `app.state.bus` (the /ws EventBus). The MLService is created on first use from FORGE_ML_REPO.
"""

from __future__ import annotations

import asyncio
import os
import sys
from pathlib import Path
from typing import Any

from fastapi import APIRouter, HTTPException, Request
from fastapi.responses import FileResponse, Response
from starlette.datastructures import State

from .models import (Atlas, Confusion, DatasetItem, Graph, IngestRequest, PatchRequest, ProjectRequest,
                     GitHubImportRequest, GitHubImportResponse, GitHubTrainRequest, RelabelRequest, ReplayRequest,
                     RunInfo, SampleDetail, SampleUpdated, Summary, TrainRequest, TrainResponse, TypeRequest, VizCard)
from .github_import import GitHubImportError
from . import logtail, nn_lab
from .service import Conflict, MLService, NotFound
from .setup import pipeline

router = APIRouter(prefix="/ml", tags=["ml"])


def hub_url() -> str:
    """Where a training run posts back: FORGE_HUB_SELF, else this machine on uvicorn's --port, else :8000.
    The run is a child process on the hub's machine, so localhost always reaches it, request or not."""
    if os.environ.get("FORGE_HUB_SELF"):
        return os.environ["FORGE_HUB_SELF"]
    port, argv = os.environ.get("UVICORN_PORT", "8000"), sys.argv
    for i, arg in enumerate(argv):
        if arg == "--port" and i + 1 < len(argv):
            port = argv[i + 1]
        elif arg.startswith("--port="):
            port = arg.split("=", 1)[1]
    return f"http://127.0.0.1:{port}"


def service(state) -> MLService:
    """The app's MLService, created on first use (routes, and Albie's tools in ml/tools.py)."""
    if getattr(state, "ml", None) is None:
        state.ml = MLService()
    return state.ml


def _svc(request: Request) -> MLService:
    return service(request.app.state)


def _call(fn, *args):
    try:
        return fn(*args)
    except NotFound as exc:
        raise HTTPException(status_code=404, detail=str(exc)) from exc
    except Conflict as exc:
        raise HTTPException(status_code=409, detail=str(exc)) from exc
    except RuntimeError as exc:  # a forge runner failed in the repo's environment
        raise HTTPException(status_code=500, detail=str(exc)) from exc


@router.get("/classes")
def get_classes(request: Request) -> list[str]:
    return _call(_svc(request).classes)


@router.get("/dataset")
def get_dataset(request: Request, split: str = "val") -> list[DatasetItem]:
    return _call(_svc(request).dataset)


@router.get("/atlas")
def get_atlas(request: Request, split: str = "val") -> Atlas:
    return _call(_svc(request).atlas)


@router.get("/files/atlas.png")
def get_atlas_png(request: Request) -> FileResponse:
    return FileResponse(_call(_svc(request).atlas_png), media_type="image/png")


@router.get("/image/{id}")
def get_image(id: str, request: Request) -> Response:
    image = _call(_svc(request).image, id)
    if isinstance(image, Path):
        return FileResponse(image, media_type="image/png")
    return Response(content=image, media_type="image/png")


@router.get("/sample/{id}")
def get_sample(id: str, request: Request) -> SampleDetail:
    return _call(_svc(request).sample, id)


@router.get("/graph")
def get_graph(request: Request) -> Graph:
    return _call(_svc(request).graph)


@router.get("/runs")
def get_runs(request: Request) -> list[RunInfo]:
    return _call(_svc(request).runs)


@router.post("/train")
async def train(req: TrainRequest, request: Request) -> TrainResponse:
    _svc(request)
    return TrainResponse(run_id=_call(start_run, request.app.state, req.epochs))


# Preset buttons on the Training panel, so nobody types a URL in the headset. FORGE_ML_GITHUB_PRESETS overrides
# them without a rebuild: "Label|https://github.com/...;Label|https://github.com/...".
DEFAULT_GITHUB_PRESETS = [
    {"label": "Forge CIFAR demo", "url": "https://github.com/Satyabratadas/forge-cifar10-demo"},
    {"label": "PyTorch MNIST", "url": "https://github.com/pytorch/examples/tree/main/mnist"},
]


def github_presets() -> list[dict]:
    raw = os.environ.get("FORGE_ML_GITHUB_PRESETS", "").strip()
    if not raw:
        return DEFAULT_GITHUB_PRESETS
    presets = []
    for item in raw.split(";"):
        label, _, url = item.partition("|")
        if label.strip() and url.strip().startswith("https://github.com/"):
            presets.append({"label": label.strip(), "url": url.strip()})
    return presets[:4]


@router.post("/nn/state")
async def post_nn_state(state: dict, request: Request) -> dict:
    """The headset's Neural Net Lab state (Unity NeuralLabSim), for Albie's nn_* tools (ml/nn_lab.py)."""
    nn_lab.lab(request.app.state).report(state)
    return {"ok": True}


@router.get("/nn/state")
def get_nn_state(request: Request) -> dict:
    return nn_lab.lab(request.app.state).status()


@router.get("/github/presets")
def get_github_presets() -> list[dict]:
    return github_presets()


@router.post("/github/import", response_model=GitHubImportResponse)
async def import_github(req: GitHubImportRequest, request: Request) -> GitHubImportResponse:
    """The CIFAR-10 preset layout (read statically) is ready at once and trains the checked-in copy. Any other
    public repo is cloned and set up like `POST /ml/project`: ml.project_state on /ws until ready (or failed)."""
    svc = _svc(request)
    running = getattr(request.app.state, "ml_setup", None)
    if running is not None and not running.done():
        raise HTTPException(status_code=409, detail="project setup already running")
    try:
        result = await asyncio.to_thread(svc.import_github, req.url)
    except GitHubImportError as exc:
        raise HTTPException(status_code=422, detail=str(exc)) from exc
    except Conflict as exc:
        raise HTTPException(status_code=409, detail=str(exc)) from exc
    if result.get("mode") == "clone":
        _stop_tail(request.app.state)
        _start_setup(request)
    return GitHubImportResponse(**result)


@router.post("/github/train", response_model=TrainResponse)
def train_github(req: GitHubTrainRequest, request: Request) -> TrainResponse:
    """A cloned import: its own train_cmd once setup is ready. The preset: the checked-in CIFAR-10 worker."""
    run_id = _call(_svc(request).start_github_training, req.import_id, req.epochs, str(request.base_url))
    return TrainResponse(run_id=run_id)


@router.get("/results/{run_id}")
def get_results(run_id: str, request: Request) -> dict:
    # dict, not Results: regression runs have {id, y_true, y_pred, residual} samples (ML_PLAN 7.4)
    return _call(_svc(request).results, run_id)


@router.get("/confusion/{run_id}")
def get_confusion(run_id: str, request: Request) -> Confusion:
    return _call(_svc(request).run_file, run_id, "confusion.json")


@router.get("/metrics/{run_id}")
def get_metrics(run_id: str, request: Request) -> dict:
    """Proposed: a run's live-graph history {run_id, total_epochs, metrics: [ml.metrics...], overfit,
    class_acc: [ml.class_acc...], probe: last ml.probe} (ML_PLAN 7.4)."""
    return _call(_svc(request).metrics_history, run_id)


@router.get("/summary/{run_id}")
def get_summary(run_id: str, request: Request) -> Summary:
    """Proposed: results panel data (accuracy, per-class, confused pairs, confident mistakes)."""
    return _call(_svc(request).run_file, run_id, "summary.json")


@router.get("/viz")
def get_viz(request: Request, run_id: str | None = None) -> list[VizCard]:
    """Chart cards for a run (default: the latest with results). ML_PLAN 7.6."""
    return _call(_svc(request).viz, run_id)


@router.get("/viz/{run_id}/{viz_id}.png")
def get_viz_png(run_id: str, viz_id: str, request: Request) -> FileResponse:
    return FileResponse(_call(_svc(request).viz_png, run_id, viz_id), media_type="image/png")


async def _publish_sample(request: Request, update: dict) -> SampleUpdated:
    event = SampleUpdated(**update)
    await request.app.state.bus.publish("ml.sample_updated", event)
    return event


@router.post("/samples/{id}/relabel")
async def relabel(id: str, req: RelabelRequest, request: Request) -> SampleUpdated:
    return await _publish_sample(request, _call(_svc(request).relabel, id, req.label_idx))


@router.post("/samples/{id}/remove")
async def remove(id: str, request: Request) -> SampleUpdated:
    return await _publish_sample(request, _call(_svc(request).remove, id))


@router.post("/ingest")
async def ingest(req: IngestRequest, request: Request) -> dict:
    """forge SDK -> hub: rebroadcast each event on /ws. Only ml.* events, so a training script
    can't pose as the pet or another module."""
    bad = sorted({e.type for e in req.events if not e.type.startswith("ml.")})
    if bad:
        raise HTTPException(status_code=400, detail=f"only ml.* events can be ingested, got {bad}")
    bus = request.app.state.bus
    for e in req.events:
        await bus.publish(e.type, e.payload)
        _tell_albie(request.app.state, e.type, e.payload)
    return {"ok": True, "count": len(req.events)}


def _tell_albie(state: State, type_: str, payload: Any) -> None:
    """Proactive Albie (main.py sets state.proactive): an ml.overfit_alert makes him speak up; other events: nothing."""
    proactive = getattr(state, "proactive", None)
    if proactive is not None:
        proactive.notify(type_, payload)


@router.post("/replay")
async def replay(req: ReplayRequest, request: Request) -> dict:
    """Stream a recorded run onto /ws as if it were live (the demo safety net). Replaces any running replay."""
    if req.speed <= 0:
        raise HTTPException(status_code=400, detail="speed must be > 0")
    events = _call(_svc(request).replay_events, req.run_id)
    state = request.app.state
    previous = getattr(state, "ml_replay", None)
    if previous is not None and not previous.done():
        previous.cancel()

    async def play() -> None:
        prev_t = None
        for e in events:
            t = e.get("t", 0.0)
            if prev_t is not None:
                await asyncio.sleep(max(0.0, t - prev_t) / req.speed)
            prev_t = t
            await state.bus.publish(e["type"], e["payload"])
            _tell_albie(state, e["type"], e["payload"])  # the demo replay's overfit alert too

    state.ml_replay = asyncio.create_task(play())
    return {"run_id": req.run_id, "events": len(events)}


def start_run(state, epochs: int) -> str:
    """Start the profile's train_cmd. Raises Conflict while a run is still training."""
    svc = service(state)
    run_id = svc.start_training(epochs, hub_url())
    # A6: if the script doesn't call forge, its own logs (TensorBoard / Lightning / HF) become the live curves.
    # A script that does use forge writes the run's run.json first, and the tail stands down.
    _start_tail(state, logtail.LogTail(svc.repo, svc.runs_dir, run_id, total_epochs=epochs), svc.train_proc)
    return run_id


def _start_tail(state, tail: logtail.LogTail, proc=None) -> None:
    """Poll the log tail every POLL_S and publish what it finds. With a training process, stop when it exits
    (after one last read) with ml.run_state done; without one (after Skip), follow until replaced."""
    _stop_tail(state)

    async def follow() -> None:
        while True:
            ended = proc is not None and proc.poll() is not None
            events = await asyncio.to_thread(tail.poll)
            if ended:
                events += tail.finish()
            for event_type, payload in events:
                await state.bus.publish(event_type, payload)
                _tell_albie(state, event_type, payload)
            if ended or (tail.stopped and proc is None):  # an SDK run still gets closed when its process exits
                return
            await asyncio.sleep(logtail.POLL_S)

    state.ml_tail = asyncio.create_task(follow())


def _stop_tail(state) -> None:
    previous = getattr(state, "ml_tail", None)
    if previous is not None and not previous.done():
        previous.cancel()


# --- project setup: open any ML repo (ML_PLAN 6, 7.4) -----------------------------------------------

def _start_setup(request: Request, **kwargs) -> dict:
    _svc(request)
    try:
        return start_setup(request.app.state, **kwargs)
    except Conflict as exc:
        raise HTTPException(status_code=409, detail=str(exc)) from exc


def start_setup(state, *, open_desk: bool = False, **kwargs) -> dict:
    """Run the setup flow in a worker thread; each step becomes ml.project_state (and ml.layout) on /ws.
    open_desk (Albie opened the project): the layout also asks the headset to open the ML desk."""
    svc = service(state)
    running = getattr(state, "ml_setup", None)
    if running is not None and not running.done():
        raise Conflict("project setup already running")
    loop = asyncio.get_running_loop()
    repo = svc.repo

    def emit(status: str, **fields) -> None:
        event, new_layout = svc.update_project(status, **fields)
        asyncio.run_coroutine_threadsafe(state.bus.publish("ml.project_state", event), loop).result(timeout=10)
        if new_layout is not None:
            payload = {**new_layout, "open": True} if open_desk else new_layout
            asyncio.run_coroutine_threadsafe(state.bus.publish("ml.layout", payload), loop).result(timeout=10)

    # tests inject fakes: state.ml_llm (adapter / Claude classifier), state.ml_jev (+ state.ml_backend)
    extra = {k: v for k, v in (("llm", getattr(state, "ml_llm", None)), ("jev_ask", getattr(state, "ml_jev", None)),
                               ("backend", getattr(state, "ml_backend", None))) if v is not None}
    state.ml_setup = asyncio.ensure_future(asyncio.to_thread(pipeline.run_setup, repo, emit, **kwargs, **extra))
    return {"status": svc.project["status"]}


@router.post("/project")
async def open_project(req: ProjectRequest, request: Request) -> dict:
    """VS Code (or `python -m ml.open`) says which repo is open: set up, or reuse a working .forge/."""
    _call(_svc(request).open_project, req.path)
    _stop_tail(request.app.state)
    return _start_setup(request)


@router.get("/project")
def get_project(request: Request) -> dict:
    return _call(_svc(request).project_state)


@router.post("/project/rescan")
async def rescan_project(request: Request) -> dict:
    """"Bit, look again": ignore the existing .forge/ and classify + adapt from scratch."""
    return _start_setup(request, force=True)


@router.post("/project/type")
async def confirm_type(req: TypeRequest, request: Request) -> dict:
    """The user picks the type after a low-confidence classification (status confirm_type)."""
    if _svc(request).project.get("status") != "confirm_type":
        raise HTTPException(status_code=409, detail="the project isn't waiting for a type")
    return _start_setup(request, archetype=req.archetype)


@router.post("/project/diff")
def open_diff(request: Request) -> dict:
    return _call(_svc(request).open_diff)


@router.post("/project/patch")
async def apply_patch(req: PatchRequest, request: Request) -> dict:
    """Accept (apply the patch) or Skip. The only way setup changes the user's code."""
    svc = _svc(request)
    applied = _call(svc.apply_patch, req.accept)
    profile = svc.project.get("profile", {})
    event, new_layout = svc.update_project("ready", archetype=profile.get("archetype"),
                                           summary=profile.get("summary"), applied=applied)
    await request.app.state.bus.publish("ml.project_state", event)
    await request.app.state.bus.publish("ml.layout", new_layout)
    if not req.accept:
        # Skip: no forge calls in the script, so follow its own logs for runs started outside the hub (A6).
        _start_tail(request.app.state, logtail.LogTail(svc.repo, svc.runs_dir))
    return {"applied": applied}
