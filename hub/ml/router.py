"""ML router (PROJECT_CONTEXT section 7, ML_PLAN 7.4). Mount in hub/main.py with `app.include_router(ml_router)`.

Needs `app.state.bus` (the /ws EventBus). The MLService is created on first use from FORGE_ML_REPO.
"""

from __future__ import annotations

import asyncio
from pathlib import Path

from fastapi import APIRouter, HTTPException, Request
from fastapi.responses import FileResponse, Response

from .models import (Atlas, Confusion, DatasetItem, Graph, IngestRequest, RelabelRequest, ReplayRequest,
                     RunInfo, SampleDetail, SampleUpdated, Summary, TrainRequest, TrainResponse)
from .service import Conflict, MLService, NotFound

router = APIRouter(prefix="/ml", tags=["ml"])


def _svc(request: Request) -> MLService:
    if getattr(request.app.state, "ml", None) is None:
        request.app.state.ml = MLService()
    return request.app.state.ml


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
def train(req: TrainRequest, request: Request) -> TrainResponse:
    run_id = _call(_svc(request).start_training, req.epochs, str(request.base_url))
    return TrainResponse(run_id=run_id)


@router.get("/results/{run_id}")
def get_results(run_id: str, request: Request) -> dict:
    # dict, not Results: regression runs have {id, y_true, y_pred, residual} samples (ML_PLAN 7.4)
    return _call(_svc(request).results, run_id)


@router.get("/confusion/{run_id}")
def get_confusion(run_id: str, request: Request) -> Confusion:
    return _call(_svc(request).run_file, run_id, "confusion.json")


@router.get("/summary/{run_id}")
def get_summary(run_id: str, request: Request) -> Summary:
    """Proposed: results panel data (accuracy, per-class, confused pairs, confident mistakes)."""
    return _call(_svc(request).run_file, run_id, "summary.json")


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
    return {"ok": True, "count": len(req.events)}


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

    state.ml_replay = asyncio.create_task(play())
    return {"run_id": req.run_id, "events": len(events)}
