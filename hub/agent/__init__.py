"""Pet agent module: the /agent router and the session handler main.py runs on the shared /ws socket.

    from agent import router as agent_router, handle_socket
    app.include_router(agent_router)
    ...
    await bus.attach(websocket); await handle_socket(websocket, accept=False)   # main.py's /ws
"""

from .router import router
from .ws import handle_socket

__all__ = ["router", "handle_socket"]
