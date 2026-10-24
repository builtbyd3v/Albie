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

NO_ARGS = {"type": "object", "properties": {}}

TOOLS: list[dict[str, Any]] = [
    {"name": "cad_list_parts",
     "description": "List the designs in the CAD library (model rocket, quadcopter, gearbox, ...) with their "
                    "descriptions and named presets, and which one is open on the desk.",
     "parameters": NO_ARGS},
    {"name": "cad_open_part",
     "description": "Open another design from the library on the desk (use the `part` id from cad_list_parts). "
                    "Each design keeps its own history, so switching back resumes where it was.",
     "parameters": {"type": "object", "properties": {"part": {"type": "string"}}, "required": ["part"]}},
    {"name": "cad_get_params",
     "description": "The open design: every parameter with its value, range and unit; its metrics, each with a "
                    "status (ok, warn or bad; for the rocket, static margin in calibers: below 1.0 is unstable, "
                    "1.5 to 2.5 is good); its presets; and its part IDs.",
     "parameters": NO_ARGS},
    {"name": "cad_set_params",
     "description": "Change parameters of the open design; it regenerates in about a second. Pass absolute values "
                    "(work out '20% longer' from cad_get_params first). Out-of-range values are clamped. Returns "
                    "the new metrics: if one went bad, tell the user and suggest a fix.",
     "parameters": {"type": "object", "properties": {
         "changes": {"type": "object", "description": "parameter name -> new value, e.g. {\"fin_count\": 4}",
                     "additionalProperties": {"type": ["number", "string"]}}},
         "required": ["changes"]}},
    {"name": "cad_apply_preset",
     "description": "Apply one of the open design's named presets (from cad_get_params), on top of its current "
                    "values. Returns the new metrics, like cad_set_params.",
     "parameters": {"type": "object", "properties": {"name": {"type": "string"}}, "required": ["name"]}},
    {"name": "cad_undo", "description": "Undo the last change to the open design.", "parameters": NO_ARGS},
    {"name": "cad_reset", "description": "Put the open design back to its defaults (undo still works afterwards).",
     "parameters": NO_ARGS},
    {"name": "cad_view",
     "description": "Explode the open design into its parts, put it back together, or focus one part. View only; "
                    "doesn't change the design. Part IDs come from cad_get_params (e.g. NOSE_CONE, FIN_1 on the "
                    "rocket).",
     "parameters": {"type": "object", "properties": {
         "explode": {"type": "boolean"}, "focus_id": {"type": "string"}}}},
    {"name": "cad_simulate_flight",
     "description": "Rocket designs only: simulate a flight on an Estes C6-5: apogee in metres, time to apogee, "
                    "speeds, when the parachute opens, descent rate and landing time and distance (events), and a "
                    "trajectory Unity draws as an arc. Takes about a second (longer the first time). If the result has "
                    "a warning, mention it.",
     "parameters": NO_ARGS},
    {"name": "cad_get_context",
     "description": "For deeper reasoning (delegate): the open design's parameters, analysis, recent version "
                    "history and its part script source.",
     "parameters": NO_ARGS},
]


def _brief(analysis: dict[str, Any]) -> dict[str, Any]:
    """The analysis for the voice model: what Unity draws by coordinates (explode vectors, marker positions, the
    demo's spin axes, dimension end points) left out; the numbers stay."""
    out = {k: v for k, v in analysis.items() if k not in ("explode", "markers")}
    if "dimensions" in out:
        out["dimensions"] = [{k: d.get(k) for k in ("id", "label", "value_mm")} for d in out["dimensions"]]
    if isinstance(out.get("demo"), dict):
        out["demo"] = {k: v for k, v in out["demo"].items() if k not in ("spin_parts", "axis")}
    return out


def _design(cad: CadService) -> dict[str, Any]:
    r = cad.current
    return {"part": r.part, "title": r.title, "version": r.version, "params": r.param_schema,
            "presets": list(r.presets), "analysis": _brief(r.analysis), "part_ids": sorted(r.fingerprints)}


def _changed(cad: CadService, r) -> dict[str, Any]:
    return {"part": r.part, "version": r.version, "params": r.params, "clamped": r.clamped,
            "analysis": _brief(r.analysis), "changed_ids": cad.diff(r.parent, r.version)[0]}


async def call_tool(name: str, args: dict[str, Any], cad: CadService, bus: EventBus) -> dict[str, Any]:
    """Run one tool. Errors come back as {"error": ...} so the model can recover and explain."""
    try:
        if name == "cad_list_parts":
            return {"active": cad.active, "parts": [
                {k: info[k] for k in ("part", "title", "description", "features", "active")} | {
                    "presets": list(info["presets"])} for info in cad.part_infos()]}
        if name == "cad_open_part":
            await cad.open_part(str(args.get("part", "")))
            return _design(cad)
        if name == "cad_get_params":
            return _design(cad)
        if name == "cad_set_params":
            return _changed(cad, await cad.set_params(args.get("changes") or {}, source="pet"))
        if name == "cad_apply_preset":
            return _changed(cad, await cad.apply_preset(str(args.get("name", "")), source="pet"))
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
            return {k: v for k, v in flight.items() if k not in ("trajectory_m", "samples")}  # for Unity, not the model
        if name == "cad_get_context":
            history = [{"version": r.version, "source": r.source, "params": r.params,
                        "metrics": {m["id"]: m["display"] for m in r.analysis.get("metrics", [])}}
                       for r in cad.versions()[-8:]]
            return {**_design(cad), "history": history, "script_path": str(cad.script),
                    "script": cad.script.read_text(encoding="utf-8")}
    except (ParamError, LookupError) as e:
        return {"error": str(e)}
    except RegenError as e:
        return {"error": e.failure.error, "kind": e.failure.kind, "kept_version": e.failure.kept_version}
    return {"error": f"Unknown CAD tool {name!r}"}
