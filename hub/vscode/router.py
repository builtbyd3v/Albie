"""VS Code -> hub. The Forge Bit extension (`vscode-bit/`) posts here; the hub rebroadcasts on `/ws`
so the agent can make Bit speak up ("line 42 just broke", "training crashed").

Hub -> VS Code goes the other way, through `vscode/tools.py`. GET /vscode/context passes the extension's
/context through, so Unity can put the open file and error count on the VS Code panel.
"""
from __future__ import annotations

import asyncio
from typing import Any

from fastapi import APIRouter, HTTPException, Request

from agent.watcher import watcher
from events import VscodeEvent
from vscode import tools

router = APIRouter(prefix="/vscode", tags=["vscode"])


@router.post("/event")
async def event(ev: VscodeEvent, request: Request) -> dict[str, bool]:
    await request.app.state.bus.publish("vscode.event", ev)
    watcher.notify("vscode.event", ev)  # Bit may speak up (agent/watcher.py)
    return {"ok": True}


@router.get("/context")
async def context() -> Any:
    try:
        return await asyncio.to_thread(tools.get_context)
    except (OSError, RuntimeError) as e:  # URLError is an OSError: VS Code closed or the extension not loaded
        raise HTTPException(503, f"VS Code not reachable: {e}") from None
