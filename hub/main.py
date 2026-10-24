"""Forge hub: one FastAPI process, a router per module, one `/ws` event bus.

Minimal skeleton (CAD only so far); Person 5 owns the hub and adds the ml, agent and messaging routers.
Run from hub/:  uvicorn main:app --host 0.0.0.0 --port 8000   (no --reload: it would restart the CAD worker)

Env: FORGE_DATA_DIR (default hub/.data), FORGE_CAD_SCRIPT (default hub/cad/parts/rocket.py).
"""
from __future__ import annotations

import asyncio
import contextlib
import logging
import os
from pathlib import Path

from fastapi import FastAPI, WebSocket
from fastapi.middleware.cors import CORSMiddleware

from agent.brain import Brain
from agent.router import router as agent_router
from cad import tools as cad_tools
from cad.router import router as cad_router
from cad.service import CadService, watch_script
from events import EventBus

HUB_DIR = Path(__file__).resolve().parent


def create_app(*, data_dir: Path | None = None, cad_script: Path | None = None, watch: bool = True) -> FastAPI:
    data_dir = Path(data_dir or os.environ.get("FORGE_DATA_DIR") or HUB_DIR / ".data")
    cad_script = Path(cad_script or os.environ.get("FORGE_CAD_SCRIPT") or HUB_DIR / "cad/parts/rocket.py").resolve()

    @contextlib.asynccontextmanager
    async def lifespan(app: FastAPI):
        bus = EventBus()
        cad = CadService(data_dir / "cad", cad_script, bus)
        app.state.bus, app.state.cad = bus, cad
        app.state.brain = Brain(bus, cad_tools.TOOLS, lambda name, args: cad_tools.call_tool(name, args, cad, bus))
        await cad.start()
        watcher = asyncio.create_task(watch_script(cad)) if watch else None
        try:
            yield
        finally:
            if watcher:
                watcher.cancel()
                with contextlib.suppress(asyncio.CancelledError):
                    await watcher
            await cad.stop()

    app = FastAPI(title="Forge hub", lifespan=lifespan)
    app.add_middleware(CORSMiddleware, allow_origins=["*"], allow_methods=["*"], allow_headers=["*"])
    app.include_router(cad_router)
    app.include_router(agent_router)

    @app.websocket("/ws")
    async def ws(websocket: WebSocket):
        await app.state.bus.serve(websocket)

    @app.get("/health")
    def health():
        return {"ok": True, "cad_version": app.state.cad.head}

    return app


logging.basicConfig(level=logging.INFO, format="%(asctime)s %(name)s %(levelname)s %(message)s")
app = create_app()

if __name__ == "__main__":
    import uvicorn

    uvicorn.run(app, host="0.0.0.0", port=8000)
