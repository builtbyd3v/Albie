"""WebSocket event bus (`/ws`) and payload models. Mirror of ForgeEvents.cs.

Every message is {"type": ..., "payload": {...}}. Minimal skeleton: only the CAD events and ui.highlight
exist so far; add the other modules' models from PROJECT_CONTEXT section 7 next to these.
"""
from __future__ import annotations

import asyncio
import logging
from typing import Any, Literal

from fastapi import WebSocket, WebSocketDisconnect
from pydantic import BaseModel

log = logging.getLogger("forge.events")


class CadAnalysis(BaseModel):
    mass_g: float
    cg_gltf: list[float]
    length_mm: float | None = None
    diameter_mm: float | None = None
    cg_mm: float | None = None        # from the nose tip
    cp_mm: float | None = None        # from the nose tip
    margin_cal: float | None = None
    stable: bool | None = None
    stability: str | None = None      # unstable | marginal | stable | overstable
    cna_per_rad: float | None = None
    motor: str | None = None
    markers_gltf: dict[str, list[float]] | None = None
    breakdown: list[dict[str, Any]] = []
    assumptions: list[str] = []


class CadModelUpdated(BaseModel):
    version: int
    params: dict[str, Any]
    previous_version: int | None = None
    changed_ids: list[str] = []
    removed_ids: list[str] = []
    source: str = "api"               # api | script | undo | reset | startup
    analysis: CadAnalysis | None = None


class CadRegenFailed(BaseModel):
    source: str
    kind: str                         # params | build | timeout | crash
    error: str
    error_line: int | None = None     # line in the part script, when the script raised
    kept_version: int | None = None


class CadView(BaseModel):
    explode: bool | None = None
    focus_id: str | None = None


class VscodeEvent(BaseModel):
    """Pushed by the Forge Bit VS Code extension so Bit can speak up on its own."""
    kind: Literal["errors", "command_finished"]
    file: str | None = None           # workspace-relative
    line: int | None = None           # 1-based, first error
    message: str | None = None        # first error
    count: int | None = None          # errors in the saved file; 0 = just fixed
    command: str | None = None        # command_finished: the command line
    exit_code: int | None = None


class UiHighlight(BaseModel):
    panel: str
    item_ids: list[str] | None = None
    class_idx: int | None = None


class EventBus:
    STICKY = {"cad.model_updated", "cad.view"}  # replayed to clients that connect later

    def __init__(self) -> None:
        self._clients: set[WebSocket] = set()
        self._last: dict[str, dict[str, Any]] = {}

    def latest(self, prefix: str = "") -> dict[str, dict[str, Any]]:
        """Last sticky payload per type: lets a client without a WebSocket poll for state."""
        return {t: m["payload"] for t, m in self._last.items() if t.startswith(prefix)}

    async def publish(self, type_: str, payload: BaseModel | dict[str, Any]) -> None:
        body = payload.model_dump() if isinstance(payload, BaseModel) else payload
        message = {"type": type_, "payload": body}
        if type_ in self.STICKY:
            self._last[type_] = message
        dead = []
        for ws in list(self._clients):
            try:
                await ws.send_json(message)
            except Exception:  # noqa: BLE001 - a dropped client must not break the publisher
                dead.append(ws)
        self._clients.difference_update(dead)

    async def attach(self, ws: WebSocket) -> None:
        """Start publishing to an accepted socket (replays sticky state). Someone else owns its receive loop."""
        self._clients.add(ws)
        for message in list(self._last.values()):
            await ws.send_json(message)

    def detach(self, ws: WebSocket) -> None:
        self._clients.discard(ws)

    async def serve(self, ws: WebSocket) -> None:
        await ws.accept()
        try:
            await self.attach(ws)
            while True:
                await ws.receive_text()  # clients don't publish yet; keep the socket open
        except (WebSocketDisconnect, asyncio.CancelledError):
            pass
        finally:
            self.detach(ws)
