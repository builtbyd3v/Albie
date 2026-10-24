import json
import threading
from http.server import BaseHTTPRequestHandler, HTTPServer

import pytest

from vscode import tools


@pytest.fixture
def fake_extension(monkeypatch):
    """Stands in for the vscode-bit extension: records requests, refuses /run like the allowlist does."""
    seen = []

    class Handler(BaseHTTPRequestHandler):
        def reply(self):
            length = int(self.headers.get("Content-Length") or 0)
            body = json.loads(self.rfile.read(length)) if length else None
            seen.append((self.command, self.path, body))
            status, out = (400, {"error": "command not allowed: x"}) if self.path == "/run" else (200, {"ok": True})
            self.send_response(status)
            self.send_header("Content-Type", "application/json")
            self.end_headers()
            self.wfile.write(json.dumps(out).encode())

        do_GET = do_POST = reply

        def log_message(self, *_):
            pass

    server = HTTPServer(("127.0.0.1", 0), Handler)
    threading.Thread(target=server.serve_forever, daemon=True).start()
    monkeypatch.setattr(tools, "BASE", f"http://127.0.0.1:{server.server_port}")
    yield seen
    server.shutdown()


def test_errors_path_is_quoted(fake_extension):
    tools.get_errors("my dir/train.py")
    assert fake_extension == [("GET", "/errors?path=my%20dir/train.py", None)]


def test_highlight_defaults_end_to_start(fake_extension):
    tools.highlight("train.py", 40, note="lr too high")
    assert fake_extension[0][2] == {"path": "train.py", "start": 40, "end": 40, "note": "lr too high", "ms": 4000}


def test_refused_command_raises_with_extension_message(fake_extension):
    with pytest.raises(RuntimeError, match="command not allowed"):
        tools.run_command("x")
