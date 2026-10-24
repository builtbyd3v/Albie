"""WebSocket event bus (`/ws`) and payload models. Mirror of ForgeEvents.cs.

Every message is {"type": ..., "payload": {...}}. Minimal skeleton: only the CAD events and ui.highlight
exist so far; add the other modules' models from PROJECT_CONTEXT section 7 next to these.
"""
from __future__ import annotations

import asyncio
import logging
from typing import Any

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


class UiHighlight(BaseModel):
    panel: str
    item_ids: list[str] | None = None
    class_idx: int | None = None


class VizPlan(BaseModel):
    """Proposed contract addition: which panels fit the repo open in VS Code (picked by Jev)."""
    repo_path: str
    repo_kind: str                    # ml_training | cad | xr_app | web_app | data_pipeline | other
    entry_file: str | None = None
    panels: list[str]                 # panel IDs to show, highest score first
    scores: dict[str, float]          # every catalog panel, 0..1
    model: str


class EventBus:
    STICKY = {"cad.model_updated", "viz.plan"}  # replayed to clients that connect later

    def __init__(self) -> None:
        self._clients: set[WebSocket] = set()
        self._last: dict[str, dict[str, Any]] = {}

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

    async def serve(self, ws: WebSocket) -> None:
        await ws.accept()
        self._clients.add(ws)
        try:
            for message in list(self._last.values()):
                await ws.send_json(message)
            while True:
                await ws.receive_text()  # clients don't publish yet; keep the socket open
        except (WebSocketDisconnect, asyncio.CancelledError):
            pass
        finally:
            self._clients.discard(ws)
