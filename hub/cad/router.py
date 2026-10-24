"""CAD REST endpoints (PROJECT_CONTEXT section 7, plus the CAD_PLAN section 5 proposals).

Everything acts on the open design (`POST /cad/part` switches it; `GET /cad/parts` lists the library).
Omit `v` to get the current version (not cached); pass `v` for an immutable, cacheable file.
"""
from __future__ import annotations

from typing import Any

from fastapi import APIRouter, Body, Depends, HTTPException, Request
from fastapi.responses import FileResponse
from pydantic import BaseModel

from cad.part_api import DEFAULT_GLYPH, ParamError, param_groups, primary_metric
from cad.service import CadService, RegenError, VersionRecord, version_label
from events import CadView

router = APIRouter(prefix="/cad", tags=["cad"])


class OpenPart(BaseModel):
    part: str


class ApplyPreset(BaseModel):
    name: str


def service(request: Request) -> CadService:
    return request.app.state.cad


def record(cad: CadService, v: int | None) -> VersionRecord:
    try:
        return cad.get(v)
    except KeyError:
        raise HTTPException(404, f"No CAD version {v}") from None


def file_headers(v: int | None, version: int) -> dict[str, str]:
    """`?v=N` names an immutable file; without it the answer is whatever is current, so never cache it."""
    return {"Cache-Control": "public, max-age=31536000, immutable" if v is not None else "no-store",
            "X-Forge-Version": str(version)}


def file(cad: CadService, v: int | None, name: str, media_type: str) -> FileResponse:
    r = record(cad, v)
    return FileResponse(cad.path(r.version, name), media_type=media_type, headers=file_headers(v, r.version))


def regen_error(e: RegenError) -> HTTPException:
    return HTTPException(422 if e.failure.kind == "params" else 500, e.failure.model_dump())


def summary(r: VersionRecord) -> dict[str, Any]:
    return {"part": r.part, "version": r.version, "clamped": r.clamped, "analysis": r.analysis}


def preset_list(presets: dict[str, dict[str, Any]]) -> list[dict[str, Any]]:
    """PRESETS as an ordered list (JSON clients find a list easier than an object keyed by name)."""
    return [{"name": name, "params": params} for name, params in presets.items()]


@router.get("/parts")
def parts(cad: CadService = Depends(service)):
    """The design library: every part script in hub/cad/parts, and which one is open."""
    return {"active": cad.active,
            "parts": [{**info, "presets": preset_list(info["presets"])} for info in cad.part_infos()]}


@router.post("/part")
async def open_part(body: OpenPart, cad: CadService = Depends(service)):
    try:
        return summary(await cad.open_part(body.part))
    except LookupError as e:
        raise HTTPException(404, str(e)) from None
    except RegenError as e:
        raise regen_error(e) from None


@router.get("/params")
def get_params(v: int | None = None, cad: CadService = Depends(service)):
    """The design's parameters (each with its group, step, label, help, default, the parts it shapes and the
    dimension it changes), presets and features; `groups` lists the parameter groups in panel order; `glyph` is the
    design's icon."""
    r = record(cad, v)
    return {"part": r.part, "title": r.title, "description": r.description, "version": r.version,
            "params": r.param_schema, "presets": preset_list(r.presets), "features": r.features,
            "groups": param_groups(r.param_schema), "glyph": r.glyph or DEFAULT_GLYPH}


@router.post("/params")
async def post_params(changes: dict[str, Any] = Body(...), cad: CadService = Depends(service)):
    try:
        return summary(await cad.set_params(changes))
    except ParamError as e:
        raise HTTPException(422, str(e)) from None
    except RegenError as e:
        raise regen_error(e) from None


@router.post("/preset")
async def apply_preset(body: ApplyPreset, cad: CadService = Depends(service)):
    try:
        return summary(await cad.apply_preset(body.name))
    except LookupError as e:
        raise HTTPException(404, str(e)) from None
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


async def drawing_file(cad: CadService, v: int | None, fmt: str, media_type: str) -> FileResponse:
    r = record(cad, v)
    try:
        path = await cad.drawing(r.version, fmt)
    except LookupError as e:
        raise HTTPException(409, str(e)) from None
    except RegenError as e:
        raise regen_error(e) from None
    return FileResponse(path, media_type=media_type, headers=file_headers(v, r.version))


@router.get("/drawing.png")
async def drawing_png(v: int | None = None, cad: CadService = Depends(service)):
    """Engineering drawing of a version (2048 x 1448, A3 landscape): front, top, right and iso views, overall
    dimensions, parts list, title block; parts changed from the previous version in orange. Made on first request
    (about a second), then cached. 409 if the version was built from an older part script."""
    return await drawing_file(cad, v, "png", "image/png")


@router.get("/drawing.svg")
async def drawing_svg(v: int | None = None, cad: CadService = Depends(service)):
    """The same drawing as SVG (made in the same job as the PNG)."""
    return await drawing_file(cad, v, "svg", "image/svg+xml")


@router.get("/flight")
async def flight(v: int | None = None, cad: CadService = Depends(service)):
    """Simulated flight on an Estes C6 (rocket designs): apogee, speeds, a trajectory for the room arc, and the
    whole flight to landing as fixed-rate samples plus events (liftoff ... landing) for the Fly animation."""
    r = record(cad, v)
    try:
        return {"version": r.version, **await cad.flight(r.version)}
    except LookupError as e:
        raise HTTPException(409, str(e)) from None
    except RegenError as e:
        raise regen_error(e) from None


@router.get("/motion")
async def motion(v: int | None = None, cad: CadService = Depends(service)):
    """The Walk demo's frames (designs with simulate_motion: the humanoid): joint angles per sample about each
    joint's pivot, and the whole model's path, in the model frame (CAD_PLAN section 5). 409 for other designs."""
    r = record(cad, v)
    try:
        return {"version": r.version, **await cad.motion(r.version)}
    except LookupError as e:
        raise HTTPException(409, str(e)) from None
    except RegenError as e:
        raise regen_error(e) from None


@router.get("/versions")
def versions(cad: CadService = Depends(service)):
    """The open design's versions. `label`: the version in words (Bit's request, a preset, "Saved rocket.py", the
    change); `primary`: the design's main metric for that version {id, label, value, display, status}."""
    return [{"version": r.version, "part": r.part, "parent": r.parent, "source": r.source, "created_at": r.created_at,
             "head": r.version == cad.head, "params": r.params,
             "margin_cal": r.analysis.get("margin_cal"), "stability": r.analysis.get("stability"),
             "label": version_label(r), "primary": primary_metric(r.analysis.get("metrics", []))}
            for r in cad.versions()]


@router.get("/latest")
def latest(request: Request):
    """{"cad.model_updated": payload, "cad.view": payload}: the WS state, for clients that poll instead."""
    return request.app.state.bus.latest("cad.")


@router.post("/view")
async def view(body: CadView, request: Request, cad: CadService = Depends(service)):
    """UI-only: explode the assembly or focus a part. Doesn't change the design."""
    if body.focus_id is not None and body.focus_id not in cad.current.fingerprints:
        raise HTTPException(404, f"No object {body.focus_id!r}; objects: {sorted(cad.current.fingerprints)}")
    await request.app.state.bus.publish("cad.view", body)
    return {"ok": True}
