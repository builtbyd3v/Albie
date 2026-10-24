"""Screens module: the hub supervises Dev's screen bridge so `uvicorn main:app` is the only process to start.

`screen-bridge/start.js` (npm start) installs the VS Code extension, starts signaling.py (:8766) and stream.py
(:8765) and launches Chrome sharing the VS Code window over WebRTC. `ScreenBridgeService` spawns it as a child
process group, logs its output as `[screen-bridge]`, publishes `screen.status` on /ws (sticky) and kills the whole
group with the hub. Env: SCREEN_BRIDGE=1|0, SCREEN_BRIDGE_DIR. REST: GET /screens/status.
"""

from .router import router
from .service import ScreenBridgeService

__all__ = ["ScreenBridgeService", "router"]
