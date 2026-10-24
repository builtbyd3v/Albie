from __future__ import annotations

from collections.abc import Iterator
from pathlib import Path

import pytest
from fastapi.testclient import TestClient


@pytest.fixture()
def client(tmp_path: Path, monkeypatch: pytest.MonkeyPatch) -> Iterator[TestClient]:
    """App in fake mode against a throwaway project dir."""
    repo = tmp_path / "repo"
    repo.mkdir()
    (repo / "hello.py").write_text("def greet():\n    return 'hi'\n")
    cfg = tmp_path / "projects.yaml"
    cfg.write_text(f'projects:\n  demo:\n    path: "{repo}"\n    default_mode: "qa"\n')
    monkeypatch.setenv("FAKE_AGENT", "1")
    monkeypatch.setenv("FAKE_AGENT_DELAY", "0.01")
    monkeypatch.setenv("PROJECTS_FILE", str(cfg))
    monkeypatch.setenv("DEFAULT_PROJECT", "demo")
    monkeypatch.setenv("STT_PROVIDER", "none")
    monkeypatch.setenv("AGENT_EMIT_PET_SAY", "0")

    from agent.config import load_projects

    load_projects.cache_clear()
    from agent.app import create_agent_app

    app = create_agent_app()

    with TestClient(app) as c:
        yield c
    load_projects.cache_clear()
