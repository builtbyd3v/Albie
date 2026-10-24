"""GET /screens/status: what the screen bridge is doing (same fields as /health.screen plus detail and restarts)."""
from __future__ import annotations

from fastapi import APIRouter, Request

router = APIRouter(prefix="/screens", tags=["screens"])


@router.get("/status")
async def status(request: Request) -> dict:
    return request.app.state.screens.status()
