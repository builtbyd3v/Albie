import pytest
from fastapi import FastAPI, WebSocket
from fastapi.testclient import TestClient

from events import EventBus
from viz import plan
from viz.router import router


def write(root, rel, text="x = 1\n"):
    p = root / rel
    p.parent.mkdir(parents=True, exist_ok=True)
    p.write_text(text)


def test_digest_skips_junk_and_lists_entry_files(tmp_path):
    write(tmp_path, "train.py", "import torch\n")
    write(tmp_path, "src/model.py")
    write(tmp_path, "README.md", "# demo\n")
    write(tmp_path, ".git/config")
    write(tmp_path, "node_modules/pkg/index.js")
    write(tmp_path, ".venv/lib/site.py")

    d = plan.digest(tmp_path)

    assert "import torch" in d.state and "# demo" in d.state
    assert ".git" not in d.state and "node_modules" not in d.state and ".venv" not in d.state
    assert d.entries == ["src/model.py", "train.py"]


def test_digest_caps_total_size(tmp_path, monkeypatch):
    monkeypatch.setattr(plan, "MAX_TOTAL_CHARS", 1_000)
    for i in range(20):
        write(tmp_path, f"f{i:02}.py", "y" * 500)

    assert len(plan.digest(tmp_path).state) <= 1_000


def test_long_context_header_only_for_big_repos():
    small = plan.Digest(state="a" * 100, entries=[])
    big = plan.Digest(state="a" * (plan.DEFAULT_LIMIT_TOKENS * 4 + 4), entries=[])

    assert plan.LONG_CONTEXT_HEADER not in plan.build_request(small)[1]
    assert plan.build_request(big)[1][plan.LONG_CONTEXT_HEADER] == "true"


def test_entry_question_only_when_there_are_entries():
    assert "entry_file" not in plan.build_request(plan.Digest("s", []))[0]["questions"]
    q = plan.build_request(plan.Digest("s", ["a.py", "b.py"]))[0]["questions"]["entry_file"]
    assert list(q["criteria"]) == ["a.py", "b.py"]


def jev_response(scores, kind="ml_training", entry="train.py"):
    answers = {k.replace("-", "_"): {"type": "noul", "noul": v} for k, v in scores.items()}
    answers["repo_kind"] = {"type": "choice", "choice": kind, "confidence": 0.9}
    if entry:
        answers["entry_file"] = {"type": "choice", "choice": entry, "confidence": 0.8}
    return {"model": "jev-1.14.0", "answers": answers}


def test_parse_keeps_panels_over_threshold_highest_first():
    res = jev_response({"ml-training": 0.8, "ml-model3d": 0.95, "ml-datastack": 0.69,
                        "ml-results": 0.1, "cad-viewer": 0.0})

    p = plan.parse("/repo", res)

    assert p.panels == ["ml-model3d", "ml-training"]
    assert p.scores["ml-datastack"] == 0.69
    assert (p.repo_kind, p.entry_file, p.model) == ("ml_training", "train.py", "jev-1.14.0")


def test_parse_without_entry_file():
    res = jev_response({pid: 0.0 for pid in plan.PANELS}, kind="other", entry=None)
    assert plan.parse("/repo", res).entry_file is None


@pytest.fixture
def client(monkeypatch):
    calls = []

    def fake_call(body, headers):
        calls.append(body)
        if "broken.py" in body["state"]:
            return {"model": "jev-1.14.0", "answers": {}}
        return jev_response({**{pid: 0.0 for pid in plan.PANELS}, "cad-viewer": 0.96}, kind="cad")

    monkeypatch.setenv("TYPESAFE_API_KEY", "test")
    monkeypatch.setattr(plan, "call_jev", fake_call)
    app = FastAPI()
    app.state.bus = EventBus()
    app.include_router(router)

    @app.websocket("/ws")
    async def ws(websocket: WebSocket):
        await app.state.bus.serve(websocket)

    with TestClient(app) as c:
        yield c, calls


def test_plan_endpoint_returns_and_publishes(client, tmp_path):
    c, calls = client
    write(tmp_path, "rocket.py", "from build123d import *\n")

    with c.websocket_connect("/ws") as ws:
        r = c.post("/viz/plan", json={"repo_path": str(tmp_path)})
        event = ws.receive_json()

    assert r.status_code == 200 and r.json()["panels"] == ["cad-viewer"]
    assert event == {"type": "viz.plan", "payload": r.json()}
    assert "build123d" in calls[0]["state"]


def test_plan_endpoint_rejects_missing_folder(client, tmp_path):
    c, calls = client
    r = c.post("/viz/plan", json={"repo_path": str(tmp_path / "nope")})
    assert r.status_code == 400 and not calls


def test_plan_endpoint_needs_api_key(client, tmp_path, monkeypatch):
    c, calls = client
    monkeypatch.delenv("TYPESAFE_API_KEY")
    assert c.post("/viz/plan", json={"repo_path": str(tmp_path)}).status_code == 503 and not calls


def test_plan_endpoint_rejects_bad_jev_response(client, tmp_path):
    c, _ = client
    write(tmp_path, "broken.py")
    assert c.post("/viz/plan", json={"repo_path": str(tmp_path)}).status_code == 502
