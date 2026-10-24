"""Bit's fast tools: one registry over the module tools (cad/tools.py, vscode/tools.py) for both brains.

Grok calls them directly (voice.py), next to ``delegate``. The Claude runner gets the same tools as an
in-process MCP server named ``forge`` (runner.py). While a tool runs, Bit walks to its panel
(``pet.state`` working + target_panel). Errors come back as ``{"error": ...}`` so the model can explain.

VS Code tools are always registered. CAD tools need the hub's CadService, so main.py calls
``register_cad`` at startup; the standalone agent app (``python -m agent``) has no CAD.
"""

from __future__ import annotations

import json
import logging
from collections.abc import Awaitable, Callable
from typing import Any

from claude_agent_sdk import McpSdkServerConfig, SdkMcpTool, create_sdk_mcp_server, tool

from vscode import tools as vscode_tools

from . import events as ev
from .bus import bus

log = logging.getLogger(__name__)

CallTool = Callable[[str, dict[str, Any]], Awaitable[dict[str, Any]]]
SERVER = "forge"
PANELS = {"cad": "cad-viewer", "vscode": "screen-main", "messages": "messages"}  # tool prefix -> panel Bit walks to while working

_tools: dict[str, tuple[dict[str, Any], CallTool]] = {}


def register(defs: list[dict[str, Any]], call_tool: CallTool) -> None:
    """Add tools in the cad/tools.py shape: {name, description, parameters (JSON schema)}."""
    for d in defs:
        _tools[d["name"]] = (d, call_tool)


def register_cad(cad: Any, event_bus: Any) -> None:
    from cad import tools as cad_tools

    register(cad_tools.TOOLS, lambda name, args: cad_tools.call_tool(name, args, cad, event_bus))


def has(name: str) -> bool:
    return name in _tools


def definitions() -> list[dict[str, Any]]:
    return [d for d, _ in _tools.values()]


def grok_definitions() -> list[dict[str, Any]]:
    return [{"type": "function", **d} for d in definitions()]


def mcp_names() -> list[str]:
    """allowed_tools entries for the runner (permission_mode dontAsk denies anything not listed)."""
    return [f"mcp__{SERVER}__{name}" for name in _tools]


async def call(name: str, args: dict[str, Any], pet_id: str) -> dict[str, Any]:
    entry = _tools.get(name)
    if entry is None:
        return {"error": f"unknown tool {name!r}"}
    await bus.broadcast(ev.pet_state(pet_id, "working", target_panel=PANELS.get(name.split("_", 1)[0])))
    try:
        return await entry[1](name, args)
    except Exception as e:  # a crashing tool must come back to the model, not kill the turn
        log.exception("tool %s failed", name)
        return {"error": str(e)}


def _mcp_tools(pet_id: str) -> list[SdkMcpTool[Any]]:
    def wrap(name: str, d: dict[str, Any]) -> SdkMcpTool[Any]:
        async def handler(args: dict[str, Any]) -> dict[str, Any]:
            out = await call(name, args, pet_id)
            return {"content": [{"type": "text", "text": json.dumps(out, default=str)}], "is_error": "error" in out}

        return tool(name, d["description"], d["parameters"])(handler)

    return [wrap(name, d) for name, (d, _) in _tools.items()]


def mcp_server(pet_id: str) -> McpSdkServerConfig:
    return create_sdk_mcp_server(SERVER, tools=_mcp_tools(pet_id))


register(vscode_tools.TOOLS, vscode_tools.call_tool)
