"""Bit's CAD tools: JSON-schema definitions plus handlers, for the agent module to register.

Handlers go through the same CadService calls as the REST endpoints, so the pet and the UI change the
world the same way. The agent should also emit pet.state (working, target_panel=cad-viewer) around
cad_set_params so Bit walks over and tinkers; that's the agent module's job.
"""
from __future__ import annotations

from typing import Any

from cad.part_api import ParamError
from cad.service import CadService, RegenError
from events import CadView, EventBus

TOOLS: list[dict[str, Any]] = [
    {"name": "cad_get_params",
     "description": "Current rocket design: every parameter with its value, range and unit, plus the stability "
                    "analysis (static margin in calibers; below 1.0 is unstable, 1.5 to 2.5 is good).",
     "parameters": {"type": "object", "properties": {}}},
    {"name": "cad_set_params",
     "description": "Change design parameters; the rocket regenerates in about a second. Pass absolute values "
                    "(work out '20% longer' from cad_get_params first). Out-of-range values are clamped. Returns "
                    "the new stability analysis: if it went unstable, tell the user and suggest a fix.",
     "parameters": {"type": "object", "properties": {
         "changes": {"type": "object", "description": "parameter name -> new value, e.g. {\"fin_count\": 4}",
                     "additionalProperties": {"type": ["number", "string"]}}},
         "required": ["changes"]}},
    {"name": "cad_undo", "description": "Undo the last design change.",
     "parameters": {"type": "object", "properties": {}}},
    {"name": "cad_reset", "description": "Go back to the default rocket design (undo still works afterwards).",
     "parameters": {"type": "object", "properties": {}}},
    {"name": "cad_view",
     "description": "Explode the rocket into its parts, put it back together, or focus one part. View only; "
                    "doesn't change the design. Part IDs: NOSE_CONE, BODY_TUBE, MOTOR_MOUNT, FIN_SET, FIN_1...",
     "parameters": {"type": "object", "properties": {
         "explode": {"type": "boolean"}, "focus_id": {"type": "string"}}}},
    {"name": "cad_simulate_flight",
     "description": "Simulate a launch of the current rocket on an Estes C6: apogee in metres, time to apogee, "
                    "speeds, and a trajectory Unity draws as an arc. Takes about a second (longer the first time). "
                    "If the result has a warning, mention it.",
     "parameters": {"type": "object", "properties": {}}},
    {"name": "cad_get_context",
     "description": "For deeper reasoning (delegate): parameters, analysis, recent version history and the "
                    "part script source.",
     "parameters": {"type": "object", "properties": {}}},
]


def _design(cad: CadService) -> dict[str, Any]:
    r = cad.current
    return {"version": r.version, "params": r.param_schema, "analysis": r.analysis}


async def call_tool(name: str, args: dict[str, Any], cad: CadService, bus: EventBus) -> dict[str, Any]:
    """Run one tool. Errors come back as {"error": ...} so the model can recover and explain."""
    try:
        if name == "cad_get_params":
            return _design(cad)
        if name == "cad_set_params":
            r = await cad.set_params(args.get("changes") or {}, source="pet")
            return {"version": r.version, "params": r.params, "clamped": r.clamped, "analysis": r.analysis,
                    "changed_ids": cad.diff(r.parent, r.version)[0]}
        if name == "cad_undo":
            await cad.undo()
            return _design(cad)
        if name == "cad_reset":
            await cad.reset()
            return _design(cad)
        if name == "cad_view":
            view = CadView(explode=args.get("explode"), focus_id=args.get("focus_id"))
            if view.focus_id is not None and view.focus_id not in cad.current.fingerprints:
                return {"error": f"No part {view.focus_id!r}", "parts": sorted(cad.current.fingerprints)}
            await bus.publish("cad.view", view)
            return {"ok": True}
        if name == "cad_simulate_flight":
            flight = await cad.flight(None)
            return {k: v for k, v in flight.items() if k != "trajectory_m"}  # the arc is for Unity, not the model
        if name == "cad_get_context":
            history = [{"version": r.version, "source": r.source, "params": r.params,
                        "margin_cal": r.analysis.get("margin_cal")} for r in cad.versions()[-8:]]
            return {**_design(cad), "history": history, "script_path": str(cad.script),
                    "script": cad.script.read_text(encoding="utf-8")}
    except (ParamError, LookupError) as e:
        return {"error": str(e)}
    except RegenError as e:
        return {"error": e.failure.error, "kind": e.failure.kind, "kept_version": e.failure.kept_version}
    return {"error": f"Unknown CAD tool {name!r}"}
