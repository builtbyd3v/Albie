"""Standalone app for the agent module (no CAD deps): /agent router + /ws/agent + /health.

Used by tests/agent and by `python -m agent`. The real hub (`main.py`) does not use
this app; it wires `router`, `handle_socket` and `health_fields()` into its own app.
"""

from __future__ import annotations

import os
from pathlib import Path

from dotenv import load_dotenv
from fastapi import FastAPI, WebSocket
from fastapi.middleware.cors import CORSMiddleware

from .bus import bus
from .router import router
from .ws import handle_socket

HUB_DIR = Path(__file__).resolve().parents[1]
WS_PATH = "/ws/agent"  # Hudson's EventBus owns /ws; unifying is Person 5's call


def load_env() -> None:
    """Load hub/.env (does not override variables already set in the shell)."""
    load_dotenv(HUB_DIR / ".env")


def health_fields() -> dict[str, object]:
    """Agent fields merged into /health (here and in main.py)."""
    return {
        "fake_agent": os.getenv("FAKE_AGENT", "0") == "1",
        "stt": os.getenv("STT_PROVIDER", "none"),
        "agent_sockets": bus.count,
    }


def mount_agent(app: FastAPI) -> None:
    """Add the /agent router and the /ws/agent socket to any FastAPI app."""
    app.include_router(router)

    @app.websocket(WS_PATH)
    async def agent_ws(websocket: WebSocket) -> None:
        await handle_socket(websocket)


def create_agent_app() -> FastAPI:
    load_env()
    app = FastAPI(title="Forge hub (agent only)")
    app.add_middleware(CORSMiddleware, allow_origins=["*"], allow_methods=["*"], allow_headers=["*"])
    mount_agent(app)

    @app.get("/health")
    def health() -> dict[str, object]:
        return {"ok": True, **health_fields()}

    return app
