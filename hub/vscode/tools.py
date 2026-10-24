"""Hub -> VS Code: Bit's tools for driving the editor without touching the user's keyboard or mouse.

Talks to the Forge Bit extension (`vscode-bit/`), which serves a tiny HTTP API
on 127.0.0.1 from inside the VS Code window. The agent registers these
functions as Bit's tools: `from vscode import tools`.

The calls block (urllib, 5 s timeout). From async code use `await asyncio.to_thread(tools.get_errors)`.

Setup: `ln -s "$PWD/vscode-bit" ~/.vscode/extensions/forge.forge-bit-0.1.0`,
then relaunch VS Code with the project open. Try it from hub/:

    python -m vscode.tools open README.md 12
    python -m vscode.tools highlight train.py 40 52 "lr is 10x too high"
    python -m vscode.tools errors
    python -m vscode.tools context
    python -m vscode.tools run workbench.action.togglePanel
    python -m vscode.tools train
"""
from __future__ import annotations

import argparse
import asyncio
import json
import os
import urllib.error
import urllib.parse
import urllib.request
from typing import Any

BASE = f"http://127.0.0.1:{os.environ.get('BIT_VSCODE_PORT', '8770')}"


def call(method: str, route: str, body: dict[str, Any] | None = None) -> Any:
    data = json.dumps(body).encode() if body is not None else None
    req = urllib.request.Request(BASE + route, data=data, method=method,
                                 headers={"Content-Type": "application/json"})
    try:
        with urllib.request.urlopen(req, timeout=5) as res:
            return json.load(res)
    except urllib.error.HTTPError as e:
        raise RuntimeError(json.load(e).get("error", str(e))) from None


def open_file(path: str, line: int = 1) -> Any:
    """Open a workspace file at a line, centered."""
    return call("POST", "/open", {"path": path, "line": line})


def highlight(path: str, start: int, end: int | None = None, note: str | None = None, ms: int = 4000) -> Any:
    """Make lines glow for a few seconds, with an optional inline note. Bit 'points' at code."""
    return call("POST", "/highlight", {"path": path, "start": start, "end": end or start, "note": note, "ms": ms})


def get_errors(path: str | None = None) -> Any:
    """Errors and warnings from VS Code's language servers: [{file, line, severity, message, source}]."""
    return call("GET", "/errors" + (f"?path={urllib.parse.quote(path)}" if path else ""))


def get_context() -> Any:
    """What the engineer is looking at: file, cursor line, selection, visible lines, error count."""
    return call("GET", "/context")


def run_command(command: str, *args: Any) -> Any:
    """Run a VS Code command id from the extension's allowlist (forgeBit.allowedCommands)."""
    return call("POST", "/run", {"command": command, "args": list(args)})


def train() -> Any:
    """Type forgeBit.trainCommand into VS Code's "Albie" terminal, so the run shows on the streamed screen."""
    return call("POST", "/train", {})


# For the agent module, same shape as cad/tools.py. Prefix "vscode" -> panel "screen-main" in the brain's PANELS.
TOOLS: list[dict[str, Any]] = [
    {"name": "vscode_get_context",
     "description": "What the engineer is looking at in VS Code right now: file, cursor line, selected text, "
                    "visible lines, error count. Call this first when they say 'this', 'here' or 'my code'.",
     "parameters": {"type": "object", "properties": {}}},
    {"name": "vscode_get_errors",
     "description": "Errors and warnings VS Code shows, with file, line and message. Omit path for the whole "
                    "workspace.",
     "parameters": {"type": "object", "properties": {
         "path": {"type": "string", "description": "workspace-relative file, e.g. train.py"}}}},
    {"name": "vscode_open_file",
     "description": "Open a file at a line in VS Code, centered. The engineer sees it on the main screen.",
     "parameters": {"type": "object", "properties": {
         "path": {"type": "string"}, "line": {"type": "integer", "minimum": 1}},
         "required": ["path"]}},
    {"name": "vscode_highlight",
     "description": "Point at code: lines glow for a few seconds with an optional short note at the end, e.g. "
                    "'lr is 10x too high'. Use it whenever you talk about specific lines.",
     "parameters": {"type": "object", "properties": {
         "path": {"type": "string"}, "start": {"type": "integer", "minimum": 1},
         "end": {"type": "integer", "minimum": 1}, "note": {"type": "string", "maxLength": 60}},
         "required": ["path", "start"]}},
    {"name": "vscode_run_command",
     "description": "Run a VS Code command. Only these work: workbench.action.togglePanel, "
                    "workbench.action.files.save, editor.action.formatDocument, workbench.actions.view.problems, "
                    "workbench.action.terminal.toggleTerminal, workbench.action.zoomIn, workbench.action.zoomOut, "
                    "workbench.action.zoomReset.",
     "parameters": {"type": "object", "properties": {"command": {"type": "string"}}, "required": ["command"]}},
    {"name": "vscode_train",
     "description": "Start the training script in VS Code's 'Albie' terminal, so the run shows on the main screen. "
                    "If it crashes, you'll get a vscode.event with the exit code.",
     "parameters": {"type": "object", "properties": {}}},
]

_HANDLERS = {
    "vscode_get_context": lambda a: get_context(),
    "vscode_get_errors": lambda a: {"errors": get_errors(a.get("path"))},
    "vscode_open_file": lambda a: open_file(a["path"], a.get("line", 1)),
    "vscode_highlight": lambda a: highlight(a["path"], a["start"], a.get("end"), a.get("note")),
    "vscode_run_command": lambda a: run_command(a["command"]),
    "vscode_train": lambda a: train(),
}


async def call_tool(name: str, args: dict[str, Any]) -> dict[str, Any]:
    """Run one tool off the event loop. Errors come back as {"error": ...} so the model can recover and explain."""
    handler = _HANDLERS.get(name)
    if handler is None:
        return {"error": f"Unknown tool {name!r}"}
    try:
        return await asyncio.to_thread(handler, args)
    except urllib.error.URLError as e:
        return {"error": f"VS Code isn't reachable (is the Forge Albie extension running?): {e.reason}"}
    except (RuntimeError, KeyError) as e:
        return {"error": str(e)}


if __name__ == "__main__":
    p = argparse.ArgumentParser()
    sub = p.add_subparsers(dest="cmd", required=True)
    o = sub.add_parser("open")
    o.add_argument("path")
    o.add_argument("line", type=int, nargs="?", default=1)
    h = sub.add_parser("highlight")
    h.add_argument("path")
    h.add_argument("start", type=int)
    h.add_argument("end", type=int, nargs="?")
    h.add_argument("note", nargs="?")
    e = sub.add_parser("errors")
    e.add_argument("path", nargs="?")
    sub.add_parser("context")
    r = sub.add_parser("run")
    r.add_argument("command")
    sub.add_parser("train")
    a = p.parse_args()
    if a.cmd == "open":
        out = open_file(a.path, a.line)
    elif a.cmd == "highlight":
        out = highlight(a.path, a.start, a.end, a.note)
    elif a.cmd == "errors":
        out = get_errors(a.path)
    elif a.cmd == "context":
        out = get_context()
    elif a.cmd == "run":
        out = run_command(a.command)
    else:
        out = train()
    print(json.dumps(out, indent=2))
