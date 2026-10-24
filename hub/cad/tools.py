"""Bit's CAD tools: JSON-schema definitions plus handlers, for the agent module to register.

Handlers go through the same CadService calls as the REST endpoints, so the pet and the UI change the
world the same way. The agent should also emit pet.state (working, target_panel=cad-viewer) around
cad_set_params so Bit walks over and tinkers; that's the agent module's job.

Tools that change what the user sees open the CAD view first through `show` (agent/workspace.py ensure, passed in
by agent/tools.py), so "open the quadcopter" works with nothing open. Opening the view never blocks the change itself:
if it fails (the user isn't at the desk) the result carries `view_error` for Bit to mention. Buttons only Unity has
(Expand, the demo, markers, dimensions, the panel's tabs) are pressed with `cad.action` (Addendum H), which is not
sticky: it means "now", and Unity applies it once the version it names is on screen.
"""
from __future__ import annotations

import itertools
import re
from collections.abc import Awaitable, Callable
from typing import Any

from cad.part_api import ParamError, primary_metric
from cad.service import CadService, RegenError, version_label
from events import Attachment, CadAction, CadView, EventBus

NO_ARGS = {"type": "object", "properties": {}}
TABS = ["overview", "parameters", "analysis", "history"]
VERSIONS_LISTED = 12
GLYPH_WORDS = {"drone": "drone", "quad": "drone", "copter": "drone", "gears": "gears", "gear": "gears",
               "transmission": "gears", "reducer": "gears", "rocket": "rocket"}

Show = Callable[[str], Awaitable[dict[str, Any]]]
_seq = itertools.count(1)

TOOLS: list[dict[str, Any]] = [
    {"name": "cad_list_parts",
     "description": "List the designs in the CAD library (model rocket, quadcopter, gearbox, ...) with their "
                    "descriptions and named presets, and which one is open on the desk.",
     "parameters": NO_ARGS},
    {"name": "cad_open_part",
     "description": "Open a design from the library on the desk and show it in the CAD view (opens the view if "
                    "needed). `part` is an id from cad_list_parts (rocket, quadcopter, gearbox); a title or a word "
                    "like 'drone' works too. Each design keeps its own history, so switching back resumes where it was.",
     "parameters": {"type": "object", "properties": {"part": {"type": "string"}}, "required": ["part"]}},
    {"name": "cad_get_params",
     "description": "The open design: every parameter with its value, range and unit; its metrics, each with a "
                    "status (ok, warn or bad; for the rocket, static margin in calibers: below 1.0 is unstable, "
                    "1.5 to 2.5 is good); its presets; its demo; and its part IDs.",
     "parameters": NO_ARGS},
    {"name": "cad_set_params",
     "description": "Change parameters of the open design; it regenerates in about a second, in the CAD view. Pass "
                    "absolute values (work out '10% larger' from cad_get_params first). Match the user's words to the "
                    "closest parameter (on the rocket the body tube is the 'shaft' or 'body'). Out-of-range values are "
                    "clamped. Returns the new metrics: if one went bad, tell the user and suggest a fix.",
     "parameters": {"type": "object", "properties": {
         "changes": {"type": "object", "description": "parameter name -> new value, e.g. {\"fin_count\": 4}",
                     "additionalProperties": {"type": ["number", "string"]}},
         "request": {"type": "string", "description": "What the user asked for, in a few of their words, e.g. "
                                                       "\"Make the fins smaller\"; it names the version in the "
                                                       "design's history."}},
         "required": ["changes"]}},
    {"name": "cad_apply_preset",
     "description": "Apply one of the open design's named presets (from cad_get_params), on top of its current "
                    "values. Returns the new metrics, like cad_set_params.",
     "parameters": {"type": "object", "properties": {
         "name": {"type": "string"},
         "request": {"type": "string", "description": "What the user asked for, in a few of their words (for the "
                                                       "history; the preset's name if left out)."}},
         "required": ["name"]}},
    {"name": "cad_undo", "description": "Undo the last change to the open design.", "parameters": NO_ARGS},
    {"name": "cad_reset", "description": "Put the open design back to its defaults (undo still works afterwards).",
     "parameters": NO_ARGS},
    {"name": "cad_view",
     "description": "Change how the open design is shown (view only; the design doesn't change). Pass only what the "
                    "user asked for. expand: true brings the model up big in front of the user, where they can pull "
                    "parts off and turn them ('expand it', 'let me look at it', 'bring it closer'); false puts it back "
                    "on the desk. explode: true spreads the parts out, false puts them back together (also snaps "
                    "pulled-off parts back). focus_id: highlight one part (IDs from cad_get_params, e.g. NOSE_CONE, "
                    "FIN_1); empty string clears it. markers: the center of gravity and other markers on the model. "
                    "dimensions: dimension callouts on the model. tab: the design panel's tab.",
     "parameters": {"type": "object", "properties": {
         "expand": {"type": "boolean"}, "explode": {"type": "boolean"}, "focus_id": {"type": "string"},
         "markers": {"type": "boolean"}, "dimensions": {"type": "boolean"},
         "tab": {"type": "string", "enum": TABS}}}},
    {"name": "cad_demo",
     "description": "Run the open design's demo in the CAD view: the rocket flies its simulated flight across the "
                    "room, the quadcopter hovers, the gearbox turns at its ratio ('show me it flying', 'make it hover', "
                    "'run it'). run=false stops it. For the rocket it also returns the flight numbers (apogee in "
                    "metres, speeds, landing): mention one. If the demo is blocked, say why.",
     "parameters": {"type": "object", "properties": {"run": {"type": "boolean", "description": "default true"}}}},
    {"name": "cad_simulate_flight",
     "description": "Rocket designs only: the flight numbers on an Estes C6-5 without showing anything: apogee in "
                    "metres, time to apogee, speeds, when the parachute opens, descent rate and landing time and "
                    "distance (events). To show it flying, use cad_demo. If the result has a warning, mention it.",
     "parameters": NO_ARGS},
    {"name": "cad_list_versions",
     "description": "The open design's recent versions, oldest first: number, what made it (the request, preset or "
                    "change), its main metric, and which one is current.",
     "parameters": NO_ARGS},
    {"name": "cad_restore_version",
     "description": "Go back to an earlier version (a number from cad_list_versions): its values become a new "
                    "current version, so undo still works. Use cad_undo for just the last change.",
     "parameters": {"type": "object", "properties": {"version": {"type": "integer"}}, "required": ["version"]}},
    {"name": "cad_export_files",
     "description": "Make the open design's files for a supplier or a print shop: the CAD model (STEP), a mesh for 3D "
                    "printing (STL), the engineering drawing (PDF) and the parts list (CSV). To send them to someone, "
                    "use messages_send with attach instead; this only makes them and says what they are.",
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
    params = [{k: v for k, v in p.items() if k != "parts"} for p in r.param_schema]   # part lists are for Unity
    return {"part": r.part, "title": r.title, "version": r.version, "params": params,
            "presets": list(r.presets), "analysis": _brief(r.analysis), "part_ids": sorted(r.fingerprints)}


def _changed(cad: CadService, r) -> dict[str, Any]:
    return {"part": r.part, "version": r.version, "params": r.params, "clamped": r.clamped,
            "analysis": _brief(r.analysis), "changed_ids": cad.diff(r.parent, r.version)[0]}


def _words(text: str) -> str:
    return re.sub(r"[^a-z0-9]+", "", text.lower())


def find_part(cad: CadService, name: str) -> str:
    """A design id from what the user called it: the id, the title, part of either ("quad"), or its kind ("drone")."""
    want = _words(name)
    infos = cad.part_infos()
    if not want:
        raise LookupError(f"Which design? The library has {[i['part'] for i in infos]}")
    for match in (lambda i: want in (_words(i["part"]), _words(i["title"])),
                  lambda i: want in _words(i["part"]) or want in _words(i["title"]) or _words(i["part"]) in want,
                  lambda i: any(w in want and g == i.get("glyph") for w, g in GLYPH_WORDS.items())):
        hits = [i["part"] for i in infos if match(i)]
        if len(hits) == 1:
            return hits[0]
    raise LookupError(f"No design {name!r}; the library has {[i['part'] for i in infos]}")


async def _show(show: Show | None) -> dict[str, Any]:
    """Open the CAD view; a failure comes back as {"view_error": ...} and the tool goes on."""
    if show is None:
        return {}
    shown = await show("cad")
    return {"view_error": shown["error"]} if "error" in shown else {}


async def _press(bus: EventBus, cad: CadService, action: str, **fields: Any) -> None:
    r = cad.current
    await bus.publish("cad.action", CadAction(action=action, part=r.part, version=r.version, seq=next(_seq), **fields))


async def _view(args: dict[str, Any], cad: CadService, bus: EventBus, show: Show | None) -> dict[str, Any]:
    if not any(k in args for k in ("expand", "explode", "focus_id", "markers", "dimensions", "tab")):
        return {"error": "Nothing to change: pass expand, explode, focus_id, markers, dimensions or tab"}
    if "focus_id" in args:
        focus = str(args["focus_id"] or "").strip().upper() or None
        if focus is not None and focus not in cad.current.fingerprints:
            return {"error": f"No part {focus!r}", "parts": sorted(cad.current.fingerprints)}
    if "tab" in args and args["tab"] not in TABS:
        return {"error": f"No tab {args['tab']!r}; the tabs are {TABS}"}
    view = await _show(show)
    if "view_error" in view:
        return {"error": view["view_error"]}  # everything below is only on screen
    done: list[str] = []
    if "explode" in args or "focus_id" in args:
        await bus.publish("cad.view", CadView(explode=args.get("explode"),
                                              focus_id=str(args.get("focus_id") or "").strip().upper() or None))
        done += [k for k in ("explode", "focus_id") if k in args]
        if args.get("explode") is False:
            await _press(bus, cad, "reassemble")  # "put it back together" also means parts pulled off in Expand
    for key in ("expand", "markers", "dimensions"):
        if key in args:
            await _press(bus, cad, key, on=bool(args[key]))
            done.append(key)
    if "tab" in args:
        await _press(bus, cad, "tab", tab=args["tab"])
        done.append("tab")
    return {"ok": True, "shown": done}


async def _demo(args: dict[str, Any], cad: CadService, bus: EventBus, show: Show | None) -> dict[str, Any]:
    r = cad.current
    demo = r.analysis.get("demo") or {}
    run = args.get("run", True) is not False
    if run and demo.get("kind", "none") == "none":
        return {"error": f"{r.title} has no demo."}
    if run and demo.get("blocked"):
        return {"error": f"The demo can't run: {demo['blocked']}"}
    out: dict[str, Any] = {"part": r.part, "version": r.version, "demo": demo.get("label") or demo.get("kind")}
    if run and demo.get("kind") == "launch":
        flight = await cad.flight(None)  # cached for Unity's GET /cad/flight too, so the launch starts at once
        out["flight"] = {k: v for k, v in flight.items() if k not in ("trajectory_m", "samples")}
    elif run:
        out["note"] = demo.get("note", "")
        out["readouts"] = [m.get("display") or m.get("value") for m in demo.get("readouts", [])]
    view = await _show(show)
    if "view_error" in view:
        return {"error": view["view_error"], **{k: v for k, v in out.items() if k == "flight"}}
    await _press(bus, cad, "demo", on=run)
    return {"ok": True, "running": run, **out}


def _versions(cad: CadService) -> dict[str, Any]:
    rows = [{"version": r.version, "label": version_label(r), "source": r.source, "current": r.version == cad.head,
             "primary": (primary_metric(r.analysis.get("metrics", [])) or {}).get("display")}
            for r in cad.versions()[-VERSIONS_LISTED:]]
    return {"part": cad.active, "versions": rows}


async def _restore(args: dict[str, Any], cad: CadService) -> dict[str, Any]:
    try:
        target = cad.get(int(args["version"]))
    except (KeyError, TypeError, ValueError):
        return {"error": f"No version {args.get('version')!r}", **_versions(cad)}
    if target.part != cad.active:
        await cad.open_part(target.part)
    if target.version == cad.head:
        return {"ok": True, "already_current": True, "version": target.version}
    known = {p["name"] for p in cad.current.param_schema}
    values = {k: v for k, v in target.params.items() if k in known}
    return _changed(cad, await cad.set_params(values, source="pet", label=f"Back to v{target.version}"))


async def _files(cad: CadService) -> dict[str, Any]:
    r = cad.current
    files = await cad.package(None)
    return {"part": r.part, "title": r.title, "version": r.version,
            "files": [{k: f[k] for k in ("kind", "name", "title", "bytes")} for f in files]}


async def attachments(cad: CadService, kinds: list[str]) -> list[Attachment]:
    """The open design's current files as draft attachments (messages_send attach): "all" or some of step / stl /
    drawing / parts, in the package's order."""
    from cad.package import KINDS

    want = set(KINDS) if "all" in kinds else set(kinds)
    unknown = sorted(want - set(KINDS))
    if unknown:
        raise LookupError(f"No file kind {unknown}; the kinds are {list(KINDS)} or all")
    r = cad.current
    return [Attachment(id=f"{r.part}-v{r.version}-{f['kind']}", name=f["name"], title=f["title"], kind=f["kind"],
                       bytes=f["bytes"], source=f"cad {r.part} v{r.version}", path=f["path"])
            for f in await cad.package(None) if f["kind"] in want]


async def call_tool(name: str, args: dict[str, Any], cad: CadService, bus: EventBus,
                    show: Show | None = None) -> dict[str, Any]:
    """Run one tool. Errors come back as {"error": ...} so the model can recover and explain. `show(view)` opens a
    workspace view (agent/workspace.py); None (tests, scripts) leaves the screen alone."""
    try:
        if name == "cad_list_parts":
            return {"active": cad.active, "parts": [
                {k: info[k] for k in ("part", "title", "description", "features", "active")} | {
                    "presets": list(info["presets"])} for info in cad.part_infos()]}
        if name == "cad_open_part":
            await cad.open_part(find_part(cad, str(args.get("part", ""))))
            return {**_design(cad), **await _show(show)}  # design first: the view opens on it, not the old one
        if name == "cad_get_params":
            return _design(cad)
        if name == "cad_set_params":
            view = await _show(show)  # view first: the user sees it regenerate
            return {**_changed(cad, await cad.set_params(args.get("changes") or {}, source="pet",
                                                         label=args.get("request") or None)), **view}
        if name == "cad_apply_preset":
            view = await _show(show)
            return {**_changed(cad, await cad.apply_preset(str(args.get("name", "")), source="pet",
                                                           label=args.get("request") or None)), **view}
        if name == "cad_undo":
            view = await _show(show)
            await cad.undo()
            return {**_design(cad), **view}
        if name == "cad_reset":
            view = await _show(show)
            await cad.reset()
            return {**_design(cad), **view}
        if name == "cad_view":
            return await _view(args, cad, bus, show)
        if name == "cad_demo":
            return await _demo(args, cad, bus, show)
        if name == "cad_simulate_flight":
            flight = await cad.flight(None)
            return {k: v for k, v in flight.items() if k not in ("trajectory_m", "samples")}  # for Unity, not the model
        if name == "cad_list_versions":
            return _versions(cad)
        if name == "cad_restore_version":
            view = await _show(show)
            return {**await _restore(args, cad), **view}
        if name == "cad_export_files":
            return await _files(cad)
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
