"""A2: events.jsonl, the background poster, never raising, the overfit rule, replay, and hub ingest."""

import json
import sys
from pathlib import Path

import pytest
import torch
import torch.nn as nn

import forge
from forge import client
from forge.replay import load_events, replay


def run_dir(tmp_path) -> Path:
    return next((tmp_path / "runs").iterdir())


def log_epochs(losses):
    for e, (train, val) in enumerate(losses, start=1):
        forge.log(epoch=e, train_loss=train, train_acc=0.5, val_loss=val, val_acc=0.5, lr=1e-3)


def test_every_event_lands_in_events_jsonl_with_increasing_time(tmp_path, capsys):
    log_epochs([(1.0, 1.1), (0.9, 1.0)])
    forge.evaluate(None, None)
    events = load_events(run_dir(tmp_path))
    assert [e["type"] for e in events] == ["ml.run_state", "ml.metrics", "ml.run_state", "ml.metrics",
                                           "ml.run_state", "ml.results_ready", "ml.run_state"]
    assert [e["t"] for e in events] == sorted(e["t"] for e in events)
    assert events[-1]["payload"]["status"] == "done"


def test_metrics_accept_tensors():
    forge.log(epoch=1, train_loss=torch.tensor(0.5), train_acc=torch.tensor(0.8), val_loss=0.6, val_acc=0.7, lr=1e-3)
    forge.evaluate(None, None)


def test_overfit_alert_fires_once_after_two_rising_val_epochs(tmp_path):
    log_epochs([(1.0, 1.0), (0.8, 0.9), (0.7, 0.95), (0.6, 1.0), (0.5, 1.1)])
    alerts = [e for e in load_events(run_dir(tmp_path)) if e["type"] == "ml.overfit_alert"]
    assert len(alerts) == 1 and alerts[0]["payload"]["epoch"] == 4


def test_no_overfit_alert_when_train_loss_rises_too(tmp_path):
    log_epochs([(1.0, 1.0), (1.1, 1.1), (1.2, 1.2), (1.3, 1.3)])
    assert not [e for e in load_events(run_dir(tmp_path)) if e["type"] == "ml.overfit_alert"]


def test_overfit_alert_rearms_after_val_improves(tmp_path):
    log_epochs([(1.0, 1.0), (0.9, 1.1), (0.8, 1.2), (0.7, 0.8), (0.6, 0.9), (0.5, 1.0)])
    alerts = [e for e in load_events(run_dir(tmp_path)) if e["type"] == "ml.overfit_alert"]
    assert [a["payload"]["epoch"] for a in alerts] == [3, 6]


class Exploding(nn.Module):
    def forward(self, x):
        raise RuntimeError("boom in forward")


def test_sdk_never_raises_into_training(capsys):
    dataset = [(torch.zeros(3, 8, 8), 0)]
    run_id = forge.watch(Exploding(), dataset, classes=["a"])
    assert run_id is not None  # the run still started; only the graph failed
    forge.log(epoch=1, train_loss="not a number", train_acc=0, val_loss=0, val_acc=0, lr=0)
    forge.evaluate(None, None)
    err = capsys.readouterr().err
    assert "forge: model graph failed: RuntimeError: boom in forward (training continues)" in err
    assert "forge: log failed" in err


def test_poster_batches_by_run_and_survives_a_dead_hub(capsys):
    sent = []
    poster = client.Poster(post=lambda url, body: sent.append(body))
    for i in range(3):
        poster.put("run_a", {"type": "ml.metrics", "payload": {"i": i}})
    poster.put("run_b", {"type": "ml.graph_ready", "payload": {}})
    assert poster.flush()
    assert {b["run_id"]: len(b["events"]) for b in sent} == {"run_a": 3, "run_b": 1}
    assert sent[0]["events"][0]["payload"] == {"i": 0}

    def dead(url, body):
        raise ConnectionError("refused")

    down = client.Poster(post=dead)
    for _ in range(5):
        down.put("run_a", {"type": "ml.metrics", "payload": {}})
    assert down.flush()
    assert capsys.readouterr().err.count("unreachable") == 1  # one warning line, not one per event


def test_replay_resends_events_with_scaled_timing(tmp_path):
    log_epochs([(1.0, 1.0), (0.9, 0.9)])
    forge.evaluate(None, None)
    rd = run_dir(tmp_path)
    lines = (rd / "events.jsonl").read_text().splitlines()
    retimed = [json.dumps({**json.loads(l), "t": i * 2.0}) for i, l in enumerate(lines)]
    (rd / "events.jsonl").write_text("\n".join(retimed) + "\n")

    sent, slept = [], []
    n = replay(rd, speed=4, max_gap=0.4, send=lambda run_id, e: sent.append((run_id, e)), sleep=slept.append)
    assert n == len(lines) == len(sent)
    assert {run_id for run_id, _ in sent} == {rd.name}
    assert slept == [0.4] * (n - 1)  # 2 s gaps / 4 = 0.5 s, capped at 0.4


@pytest.fixture()
def hub_client():
    from fastapi.testclient import TestClient

    sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "hub"))
    from ml.dev_server import app

    with TestClient(app) as c:
        yield c


def test_hub_ingest_rebroadcasts_on_ws(hub_client):
    with hub_client.websocket_connect("/ws") as ws:
        r = hub_client.post("/ml/ingest", json={"run_id": "r1", "events": [
            {"type": "ml.metrics", "payload": {"epoch": 1}}, {"type": "ml.overfit_alert", "payload": {"epoch": 1}}]})
        assert r.json() == {"ok": True, "count": 2}
        assert ws.receive_json() == {"type": "ml.metrics", "payload": {"epoch": 1}}
        assert ws.receive_json()["type"] == "ml.overfit_alert"


def test_hub_ingest_rejects_non_ml_events(hub_client):
    r = hub_client.post("/ml/ingest", json={"run_id": "r1", "events": [{"type": "pet.say", "payload": {}}]})
    assert r.status_code == 400
