"""CAD REST endpoints (PROJECT_CONTEXT section 7, plus the CAD_PLAN section 5 proposals).

Omit `v` to get the current version (not cached); pass `v` for an immutable, cacheable file.
"""
from __future__ import annotations

from typing import Any

from fastapi import APIRouter, Body, Depends, HTTPException, Request
from fastapi.responses import FileResponse

from cad.part_api import ParamError
from cad.service import CadService, RegenError, VersionRecord
from events import CadView

router = APIRouter(prefix="/cad", tags=["cad"])


def service(request: Request) -> CadService:
    return request.app.state.cad


def record(cad: CadService, v: int | None) -> VersionRecord:
    try:
        return cad.get(v)
    except KeyError:
        raise HTTPException(404, f"No CAD version {v}") from None


def file(cad: CadService, v: int | None, name: str, media_type: str) -> FileResponse:
    r = record(cad, v)
    cache = "public, max-age=31536000, immutable" if v is not None else "no-store"
    return FileResponse(cad.path(r.version, name), media_type=media_type,
                        headers={"Cache-Control": cache, "X-Forge-Version": str(r.version)})


def regen_error(e: RegenError) -> HTTPException:
    return HTTPException(422 if e.failure.kind == "params" else 500, e.failure.model_dump())


def summary(r: VersionRecord) -> dict[str, Any]:
    return {"version": r.version, "clamped": r.clamped, "analysis": r.analysis}


@router.get("/params")
def get_params(cad: CadService = Depends(service)):
    r = cad.current
    return {"part": r.part, "version": r.version, "params": r.param_schema}


@router.post("/params")
async def post_params(changes: dict[str, Any] = Body(...), cad: CadService = Depends(service)):
    try:
        return summary(await cad.set_params(changes))
    except ParamError as e:
        raise HTTPException(422, str(e)) from None
    except RegenError as e:
        raise regen_error(e) from None


@router.post("/undo")
async def undo(cad: CadService = Depends(service)):
    try:
        return summary(await cad.undo())
    except LookupError as e:
        raise HTTPException(409, str(e)) from None


@router.post("/reset")
async def reset(cad: CadService = Depends(service)):
    try:
        return summary(await cad.reset())
    except RegenError as e:
        raise regen_error(e) from None


@router.get("/model.glb")
def model_glb(v: int | None = None, cad: CadService = Depends(service)):
    return file(cad, v, "model.glb", "model/gltf-binary")


@router.get("/metadata.json")
def metadata(v: int | None = None, cad: CadService = Depends(service)):
    return file(cad, v, "metadata.json", "application/json")


@router.get("/analysis")
def analysis(v: int | None = None, cad: CadService = Depends(service)):
    return record(cad, v).analysis


@router.get("/model.stl")
async def model_stl(v: int | None = None, cad: CadService = Depends(service)):
    r = record(cad, v)
    try:
        path = await cad.stl(r.version)
    except LookupError as e:
        raise HTTPException(409, str(e)) from None
    except RegenError as e:
        raise regen_error(e) from None
    return FileResponse(path, media_type="model/stl", filename=f"{r.part}_v{r.version}.stl")


@router.get("/flight")
async def flight(v: int | None = None, cad: CadService = Depends(service)):
    """Simulated launch to apogee on an Estes C6: apogee, speeds, and a trajectory for the room arc."""
    r = record(cad, v)
    try:
        return {"version": r.version, **await cad.flight(r.version)}
    except LookupError as e:
        raise HTTPException(409, str(e)) from None
    except RegenError as e:
        raise regen_error(e) from None


@router.get("/versions")
def versions(cad: CadService = Depends(service)):
    return [{"version": r.version, "parent": r.parent, "source": r.source, "created_at": r.created_at,
             "head": r.version == cad.head, "params": r.params,
             "margin_cal": r.analysis.get("margin_cal"), "stability": r.analysis.get("stability")}
            for r in cad.versions()]


@router.post("/view")
async def view(body: CadView, request: Request, cad: CadService = Depends(service)):
    """UI-only: explode the assembly or focus a part. Doesn't change the design."""
    if body.focus_id is not None and body.focus_id not in cad.current.fingerprints:
        raise HTTPException(404, f"No object {body.focus_id!r}; objects: {sorted(cad.current.fingerprints)}")
    await request.app.state.bus.publish("cad.view", body)
    return {"ok": True}
