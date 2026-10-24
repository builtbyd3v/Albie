import pytest
from fastapi import FastAPI, WebSocket
from fastapi.testclient import TestClient

from events import EventBus
from vscode.router import router


@pytest.fixture
def client():
    # Just the bus and this router: no CAD worker needed.
    app = FastAPI()
    app.state.bus = EventBus()
    app.include_router(router)

    @app.websocket("/ws")
    async def ws(websocket: WebSocket):
        await app.state.bus.serve(websocket)

    with TestClient(app) as c:
        yield c


def test_event_is_broadcast_on_ws(client):
    with client.websocket_connect("/ws") as ws:
        r = client.post("/vscode/event", json={
            "kind": "errors", "file": "train.py", "line": 42, "message": "NameError: lr", "count": 1,
        })
        assert r.status_code == 200
        assert ws.receive_json() == {"type": "vscode.event", "payload": {
            "kind": "errors", "file": "train.py", "line": 42, "message": "NameError: lr",
            "count": 1, "command": None, "exit_code": None,
        }}


def test_command_finished_carries_exit_code(client):
    with client.websocket_connect("/ws") as ws:
        client.post("/vscode/event", json={"kind": "command_finished", "command": "python train.py", "exit_code": 1})
        payload = ws.receive_json()["payload"]
        assert payload["kind"] == "command_finished" and payload["exit_code"] == 1


def test_unknown_kind_is_rejected(client):
    assert client.post("/vscode/event", json={"kind": "saved"}).status_code == 422
