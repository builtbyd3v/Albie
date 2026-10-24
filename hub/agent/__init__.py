"""Pet agent module: FastAPI router (/agent/*) + /ws/agent handler (standalone app: agent.app).

Mount in any hub app:

    from agent import router as agent_router, handle_socket
    app.include_router(agent_router)
    @app.websocket("/ws/agent")
    async def ws(ws: WebSocket): await handle_socket(ws)
"""

from .router import router
from .ws import handle_socket

__all__ = ["router", "handle_socket"]
