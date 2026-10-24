"""Let Bit drive VS Code without touching the user's keyboard or mouse.

Talks to the Forge Bit extension (`vscode-bit/`), which serves a tiny HTTP API
on 127.0.0.1 from inside the VS Code window. The hub's agent can import these
functions as Bit's tools.

Setup: `ln -s "$PWD/vscode-bit" ~/.vscode/extensions/forge.forge-bit-0.1.0`,
then relaunch VS Code with the project open.

    python bit_vscode.py open README.md 12
    python bit_vscode.py highlight train.py 40 52 "lr is 10x too high"
    python bit_vscode.py errors
    python bit_vscode.py context
    python bit_vscode.py run workbench.action.togglePanel
    python bit_vscode.py train
"""

import argparse
import json
import os
import urllib.error
import urllib.parse
import urllib.request

BASE = f"http://127.0.0.1:{os.environ.get('BIT_VSCODE_PORT', '8770')}"


def call(method, route, body=None):
    data = json.dumps(body).encode() if body is not None else None
    req = urllib.request.Request(BASE + route, data=data, method=method,
                                 headers={"Content-Type": "application/json"})
    try:
        with urllib.request.urlopen(req, timeout=5) as res:
            return json.load(res)
    except urllib.error.HTTPError as e:
        raise RuntimeError(json.load(e).get("error", str(e))) from None


def open_file(path, line=1):
    """Open a workspace file at a line, centered."""
    return call("POST", "/open", {"path": path, "line": line})


def highlight(path, start, end=None, note=None, ms=4000):
    """Make lines glow for a few seconds, with an optional inline note. Bit 'points' at code."""
    return call("POST", "/highlight", {"path": path, "start": start, "end": end or start, "note": note, "ms": ms})


def get_errors(path=None):
    """Errors and warnings from VS Code's language servers: [{file, line, severity, message, source}]."""
    return call("GET", "/errors" + (f"?path={urllib.parse.quote(path)}" if path else ""))


def get_context():
    """What the engineer is looking at: file, cursor line, selection, visible lines, error count."""
    return call("GET", "/context")


def run_command(command, *args):
    """Run a VS Code command id from the extension's allowlist (forgeBit.allowedCommands)."""
    return call("POST", "/run", {"command": command, "args": list(args)})


def train():
    """Type forgeBit.trainCommand into VS Code's "Bit" terminal, so the run shows on the streamed screen."""
    return call("POST", "/train", {})


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
