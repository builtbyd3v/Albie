"""REST endpoints for the agent module (mounted at /agent)."""

from __future__ import annotations

import logging

from fastapi import APIRouter, HTTPException
from pydantic import BaseModel

from .bus import Emitter
from .config import ProjectNotFound, PublicProject, public_projects, resolve_project
from .events import Event
from .runner import get_runner

log = logging.getLogger(__name__)
router = APIRouter(prefix="/agent", tags=["agent"])


class TextRequest(BaseModel):
    pet_id: str = "bit"
    text: str
    project_id: str | None = None  # additive: defaults to DEFAULT_PROJECT


class TextReply(BaseModel):
    reply: str


@router.post("/text", response_model=TextReply)
async def agent_text(req: TextRequest) -> TextReply:
    """Typed fallback: run one qa turn, return the final answer.

    Events still stream over /ws so the pet animates while this request waits.
    """
    try:
        project = resolve_project(req.project_id)
    except ProjectNotFound as exc:
        raise HTTPException(status_code=404, detail=str(exc)) from exc
    bus_emit = Emitter(req.pet_id, redact=[str(project.path)])
    error: str | None = None

    async def emit(event: Event) -> None:
        nonlocal error
        if event.type == "agent.error":
            error = event.payload.get("message")
        await bus_emit(event)

    reply = await get_runner()(project, req.text, "qa", emit, pet_id=req.pet_id)
    if error:
        raise HTTPException(status_code=502, detail=error)
    return TextReply(reply=reply)


@router.get("/projects", response_model=list[PublicProject])
async def agent_projects() -> list[PublicProject]:
    return public_projects()
