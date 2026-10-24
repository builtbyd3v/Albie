"""Standalone dev server for the ML router, with the hub's real /ws event bus.

Use it while the ml router isn't mounted in hub/main.py yet (Person 5 owns that file).
Run from hub/: uvicorn ml.dev_server:app --port 8000
"""

from fastapi import FastAPI, WebSocket

from events import EventBus
from ml.router import router

app = FastAPI(title="forge-hub (ml dev)")
app.state.bus = EventBus()
app.include_router(router)


@app.websocket("/ws")
async def ws(websocket: WebSocket) -> None:
    await app.state.bus.serve(websocket)
