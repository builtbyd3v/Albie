"""The full hub's /ws is shared: CAD EventBus events and the agent session ride one socket (Unity's ForgeNet)."""

from __future__ import annotations

import json
from pathlib import Path
from typing import Any

import pytest
from fastapi.testclient import TestClient

HUB = Path(__file__).resolve().parents[1]
ROCKET = HUB / "cad" / "parts" / "rocket.py"


@pytest.fixture()
def client(tmp_path: Path, monkeypatch: pytest.MonkeyPatch):
    repo = tmp_path / "repo"
    repo.mkdir()
    cfg = tmp_path / "projects.yaml"
    cfg.write_text(f'projects:\n  demo:\n    path: "{repo.as_posix()}"\n    default_mode: "qa"\n')
    for k, v in {"FAKE_AGENT": "1", "FAKE_AGENT_DELAY": "0.01", "PROJECTS_FILE": str(cfg), "DEFAULT_PROJECT": "demo",
                 "STT_PROVIDER": "none", "VOICE_PROVIDER": "fake", "FAKE_VOICE_PACE": "0", "SCREEN_BRIDGE": "0"}.items():
        monkeypatch.setenv(k, v)
    from agent.config import load_projects

    load_projects.cache_clear()
    from main import create_app

    with TestClient(create_app(data_dir=tmp_path / "data", cad_script=ROCKET, watch=False)) as c:
        yield c
    load_projects.cache_clear()


def _recv(ws: Any) -> tuple[str, Any]:
    msg = ws.receive()
    if msg.get("bytes") is not None:
        return "bytes", msg["bytes"]
    data = json.loads(msg["text"])
    return data["type"], data["payload"]


def test_cad_and_voice_share_one_socket(client: TestClient) -> None:
    with client.websocket_connect("/ws") as ws:
        assert _recv(ws)[0] == "cad.model_updated"  # sticky CAD state still replayed first
        ws.send_json({"type": "hello", "payload": {"client": "unity", "pet_id": "bit", "project_id": "demo"}})
        assert _recv(ws) == ("voice.ready", {"pet_id": "bit", "provider": "fake", "sample_rate": 24000})

        ws.send_json({"type": "stt.start", "payload": {"pet_id": "bit"}})
        ws.send_bytes(b"\x00\x00" * 1600)  # mic audio up the shared socket
        ws.send_json({"type": "stt.stop", "payload": {"pet_id": "bit"}})
        seen: list[str] = []
        audio = 0
        while "tts.end" not in seen:
            t, p = _recv(ws)
            seen.append(t)
            if t == "bytes":
                audio += len(p)
        assert seen.index("stt.final") < seen.index("tts.start") < seen.index("tts.end")
        assert audio > 0  # speech audio down the shared socket

        # CAD publishes still reach the same socket.
        assert client.post("/cad/view", json={"explode": True}).status_code == 200
        t = None
        for _ in range(20):
            t, _p = _recv(ws)
            if t == "cad.view":
                break
        assert t == "cad.view"


def test_health_reports_the_screen_bridge(client: TestClient) -> None:
    """One process: /health carries the screen bridge too (off here, so no child is spawned in tests)."""
    body = client.get("/health").json()
    assert body["screen"] == {"enabled": False, "state": "stopped", "source": None}
    assert client.get("/screens/status").json()["enabled"] is False
