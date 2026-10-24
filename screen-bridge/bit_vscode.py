"""Let Bit drive VS Code without touching the user's keyboard or mouse.

Wraps the VSCode MCP Bridge extension (YuTengjing.vscode-mcp-bridge) through
its CLI, plus the official `code` CLI for jumping to a line. The hub's agent
can import these functions as Bit's tools later.

Setup: install the extension in VS Code and open the workspace in a window.

    python bit_vscode.py open README.md 12
    python bit_vscode.py errors
    python bit_vscode.py run workbench.action.togglePanel
"""

import argparse
import json
import os
import subprocess

WORKSPACE = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
CLI = ["npx", "-y", "@vscode-mcp/vscode-mcp-cli@4.9.5"]


def bridge(*args):
    out = subprocess.run([*CLI, *args, "--workspace", WORKSPACE],
                         capture_output=True, text=True, check=True)
    return out.stdout


def open_file(path, line=1):
    """Open a workspace file at a line in the existing window."""
    full = os.path.join(WORKSPACE, path)
    subprocess.run(["code", "--reuse-window", "--goto", f"{full}:{line}"], check=True)


def get_errors(path=None):
    """Errors and warnings from VS Code's language servers, as text."""
    # No path means every git-modified file.
    return bridge("get-diagnostics", *(["--not-recommend-file-paths", path] if path else []))


def run_command(command, *args):
    """Run any VS Code command id. ponytail: no allowlist yet; the hub must confirm risky ones with the user."""
    extra = ["--args", json.dumps(list(args))] if args else []
    return bridge("execute-command", "--command", command, *extra)


if __name__ == "__main__":
    p = argparse.ArgumentParser()
    sub = p.add_subparsers(dest="cmd", required=True)
    o = sub.add_parser("open")
    o.add_argument("path")
    o.add_argument("line", type=int, nargs="?", default=1)
    e = sub.add_parser("errors")
    e.add_argument("path", nargs="?")
    r = sub.add_parser("run")
    r.add_argument("command")
    a = p.parse_args()
    if a.cmd == "open":
        open_file(a.path, a.line)
    elif a.cmd == "errors":
        print(get_errors(a.path))
    else:
        print(run_command(a.command))
