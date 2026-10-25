import pytest

import forge
from forge import client


@pytest.fixture(autouse=True)
def isolated_forge(tmp_path, monkeypatch):
    """Each test: no active run, runs written to a temp dir, offline, warnings shown again."""
    monkeypatch.setenv("FORGE_RUN_DIR", str(tmp_path / "runs"))
    monkeypatch.setenv("FORGE_OFFLINE", "1")
    forge._end_run()
    client._warned.clear()
    yield
    forge._end_run()
