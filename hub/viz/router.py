"""`POST /viz/plan`: pick panels for a repo folder and push them as `viz.plan` (proposed contract addition)."""
from __future__ import annotations

import asyncio
import os
import urllib.error
from pathlib import Path

from fastapi import APIRouter, HTTPException, Request
from pydantic import BaseModel

from events import VizPlan
from viz import plan

router = APIRouter(prefix="/viz", tags=["viz"])


class PlanRequest(BaseModel):
    repo_path: str  # ponytail: any folder on the laptop; restrict to a workspace root if the hub leaves the hotspot


@router.post("/plan")
async def make_plan(body: PlanRequest, request: Request) -> VizPlan:
    root = Path(body.repo_path).expanduser().resolve()
    if not root.is_dir():
        raise HTTPException(400, f"Not a folder: {body.repo_path}")
    if not os.environ.get("TYPESAFE_API_KEY"):
        raise HTTPException(503, "TYPESAFE_API_KEY is not set")

    d = await asyncio.to_thread(plan.digest, root)
    try:
        res = await asyncio.to_thread(plan.call_jev, *plan.build_request(d))
        result = plan.parse(str(root), res)
    except urllib.error.HTTPError as e:
        raise HTTPException(502, f"Jev returned {e.code}: {e.read().decode(errors='ignore')[:300]}") from None
    except (urllib.error.URLError, TimeoutError) as e:
        raise HTTPException(502, f"Could not reach Jev: {e}") from None
    except (KeyError, TypeError):
        raise HTTPException(502, "Unexpected Jev response shape") from None
    await request.app.state.bus.publish("viz.plan", result)
    return result
