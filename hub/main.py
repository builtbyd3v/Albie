"""Forge hub: one FastAPI process, a router per module, one `/ws` event bus.

Modules: CAD, agent (pet), VS Code push, messaging (Slack), screens (the screen-bridge child process), desk
(timers, notes, movie). The ML router is still to come.
`/ws` is the one socket: the EventBus publishes to it and the agent (pet) session reads it, since Unity talks to
the hub through one ForgeNet socket. See README "Agent module (pet)".
Run from hub/:  uvicorn main:app --host 0.0.0.0 --port 8000   (no --reload: it would restart the CAD worker)

Env: FORGE_DATA_DIR (default hub/.data), FORGE_CAD_SCRIPT (default hub/cad/parts/rocket.py), SLACK_* (messaging/settings.py),
SCREEN_BRIDGE=1|0 and SCREEN_BRIDGE_DIR (screens/service.py).
"""
from __future__ import annotations

import asyncio
import contextlib
import logging
import os
from pathlib import Path

from fastapi import FastAPI, WebSocket
from fastapi.middleware.cors import CORSMiddleware

from agent import handle_socket as agent_socket, router as agent_router
from agent import speech, tools as agent_tools
from agent import ws as agent_ws
from agent.app import health_fields as agent_health, load_env
from agent.watcher import watcher as proactive
from cad.router import router as cad_router
from cad.service import CadService, watch_script
from desk import DeskService
from desk import tools as desk_tools
from desk.router import router as desk_router
from events import EventBus
from messaging import MessageStore, SlackService, SlackSettings
from messaging import tools as messaging_tools
from messaging.router import router as messaging_router
from screens import ScreenBridgeService
from screens import router as screens_router
from vscode.router import router as vscode_router

HUB_DIR = Path(__file__).resolve().parent


def create_app(*, data_dir: Path | None = None, cad_script: Path | None = None, watch: bool = True) -> FastAPI:
    data_dir = Path(data_dir or os.environ.get("FORGE_DATA_DIR") or HUB_DIR / ".data")
    cad_script = Path(cad_script or os.environ.get("FORGE_CAD_SCRIPT") or HUB_DIR / "cad/parts/rocket.py").resolve()

    @contextlib.asynccontextmanager
    async def lifespan(app: FastAPI):
        bus = EventBus()
        cad = CadService(data_dir / "cad", cad_script, bus)
        app.state.bus, app.state.cad = bus, cad
        agent_tools.register_cad(cad, bus)  # Bit's CAD tools for Grok and the Claude runner
        slack = SlackService(SlackSettings.from_env(), MessageStore(data_dir / "messages"), bus, speech.speak, speech.note)
        app.state.slack = slack
        agent_ws.client_handlers["messages.focus"] = slack.focus_from_ws  # "Reply with Bit" from a message card
        agent_ws.client_handlers["messages.opened"] = slack.opened_from_ws  # Unity's answer to messages.open
        agent_tools.register(messaging_tools.TOOLS, lambda name, args: messaging_tools.call_tool(name, args, slack))
        desk = DeskService(data_dir, bus, speech.speak)  # timer, notes, movie: .data/desk, .data/media
        app.state.desk = desk
        agent_tools.register(desk_tools.TOOLS, lambda name, args: desk_tools.call_tool(name, args, desk))
        await cad.start()
        await slack.start()
        screens = ScreenBridgeService(bus)  # npm start in screen-bridge/, killed with the hub (SCREEN_BRIDGE=0 to skip)
        app.state.screens = screens
        await screens.start()
        proactive.cad_script = str(cad_script)  # proactive Bit, after start: nobody hears a boot failure
        cad.on_regen_failed = lambda failure: proactive.notify("cad.regen_failed", failure)
        watcher = asyncio.create_task(watch_script(cad)) if watch else None
        try:
            yield
        finally:
            if watcher:
                watcher.cancel()
                with contextlib.suppress(asyncio.CancelledError):
                    await watcher
            desk.stop()
            await screens.stop()
            await slack.stop()
            await cad.stop()

    app = FastAPI(title="Forge hub", lifespan=lifespan)
    app.add_middleware(CORSMiddleware, allow_origins=["*"], allow_methods=["*"], allow_headers=["*"])
    app.include_router(cad_router)
    app.include_router(agent_router)
    app.include_router(vscode_router)
    app.include_router(messaging_router)
    app.include_router(screens_router)
    app.include_router(desk_router)

    @app.websocket("/ws")
    async def ws(websocket: WebSocket):
        # One socket per Unity client (ForgeNet): the EventBus publishes CAD/... events to it, and the agent
        # session reads it (hello, agent.*, stt.*, tts.stop, binary mic audio; other types are ignored).
        await websocket.accept()
        try:
            await app.state.bus.attach(websocket)
            await agent_socket(websocket, accept=False)
        finally:
            app.state.bus.detach(websocket)

    @app.get("/health")
    def health():
        return {"ok": True, "cad_version": app.state.cad.head, "slack": app.state.slack.health(),
                "screen": app.state.screens.health(), "desk": app.state.desk.health(), **agent_health()}

    return app


load_env()  # hub/.env (agent settings: FAKE_AGENT, ANTHROPIC_API_KEY, ...)
logging.basicConfig(level=logging.INFO, format="%(asctime)s %(name)s %(levelname)s %(message)s")
app = create_app()

if __name__ == "__main__":
    import uvicorn

    uvicorn.run(app, host="0.0.0.0", port=8000)
