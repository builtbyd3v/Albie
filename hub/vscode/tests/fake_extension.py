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
