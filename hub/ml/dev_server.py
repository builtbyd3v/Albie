"""Standalone dev server for the ML router, with the hub's real /ws event bus.

Use it while the ml router isn't mounted in hub/main.py yet (Person 5 owns that file).
Run from hub/: uvicorn ml.dev_server:app --port 8000
"""

import logging
import os

from fastapi import FastAPI, WebSocket

from events import EventBus
from ml.router import router

log = logging.getLogger("forge.ml")


def _report_keys() -> None:
    """Say at startup which setup keys the hub sees: a missing key is the usual reason setup stops."""
    for name, needed_for in (("TYPESAFE_API_KEY", "classifying with Jev (else Claude classifies)"),
                             ("ANTHROPIC_API_KEY", "writing adapters for new repos")):
        value = os.environ.get(name, "")
        if not value:
            log.warning("%s is not set: needed for %s", name, needed_for)
        elif value.endswith("..."):
            log.warning("%s looks like a placeholder (%s): paste the real key", name, value)
        else:
            log.info("%s is set (%s...)", name, value[:10])

logging.basicConfig(level=logging.INFO, format="%(levelname)s:     %(message)s")
_report_keys()
app = FastAPI(title="forge-hub (ml dev)")
app.state.bus = EventBus()
app.include_router(router)


@app.websocket("/ws")
async def ws(websocket: WebSocket) -> None:
    await app.state.bus.serve(websocket)
