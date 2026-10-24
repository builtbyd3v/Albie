from __future__ import annotations

from collections.abc import Iterator
from pathlib import Path

import pytest
from fastapi import FastAPI, WebSocket
from fastapi.testclient import TestClient


def agent_only_app() -> FastAPI:
    """The agent's part of main.py's app, without CAD / Slack / screens: /agent, /health and the /ws session."""
    from agent import handle_socket, router
    from agent.app import health_fields

    app = FastAPI(title="Forge hub (agent only, tests)")
    app.include_router(router)

    @app.websocket("/ws")
    async def ws(websocket: WebSocket) -> None:
        await handle_socket(websocket)

    @app.get("/health")
    def health() -> dict[str, object]:
        return {"ok": True, **health_fields()}

    return app


@pytest.fixture()
def client(tmp_path: Path, monkeypatch: pytest.MonkeyPatch) -> Iterator[TestClient]:
    """App in fake mode against a throwaway project dir."""
    repo = tmp_path / "repo"
    repo.mkdir()
    (repo / "hello.py").write_text("def greet():\n    return 'hi'\n")
    cfg = tmp_path / "projects.yaml"
    cfg.write_text(f'projects:\n  demo:\n    path: "{repo.as_posix()}"\n    default_mode: "qa"\n')
    monkeypatch.setenv("FAKE_AGENT", "1")
    monkeypatch.setenv("FAKE_AGENT_DELAY", "0.01")
    monkeypatch.setenv("PROJECTS_FILE", str(cfg))
    monkeypatch.setenv("DEFAULT_PROJECT", "demo")
    monkeypatch.setenv("STT_PROVIDER", "none")
    monkeypatch.setenv("AGENT_EMIT_PET_SAY", "0")

    from agent.config import load_projects

    load_projects.cache_clear()
    with TestClient(agent_only_app()) as c:
        yield c
    load_projects.cache_clear()
