"""REST endpoints for the agent module (mounted at /agent). Asking Bit anything goes over /ws (agent.ask)."""

from __future__ import annotations

from fastapi import APIRouter

from .config import PublicProject, public_projects

router = APIRouter(prefix="/agent", tags=["agent"])


@router.get("/projects", response_model=list[PublicProject])
async def agent_projects() -> list[PublicProject]:
    return public_projects()
