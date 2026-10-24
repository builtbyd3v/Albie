"""VS Code -> hub. The Forge Bit extension (`vscode-bit/`) posts here; the hub rebroadcasts on `/ws`
so the agent can make Bit speak up ("line 42 just broke", "training crashed").

Hub -> VS Code goes the other way, through `vscode/tools.py`.
"""
from __future__ import annotations

from fastapi import APIRouter, Request

from events import VscodeEvent

router = APIRouter(prefix="/vscode", tags=["vscode"])


@router.post("/event")
async def event(ev: VscodeEvent, request: Request) -> dict[str, bool]:
    await request.app.state.bus.publish("vscode.event", ev)
    return {"ok": True}
