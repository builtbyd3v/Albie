"""ML endpoints against the make_mocks project repos (no torch needed) plus training / runner plumbing."""

import io
import json
import sys
import textwrap
import time
from pathlib import Path

import pytest
from fastapi import FastAPI, WebSocket
from fastapi.testclient import TestClient

from events import EventBus
from ml import make_mocks
from ml.models import (Atlas, Confusion, DatasetItem, Graph, RegressionResults, Results, RunInfo, SampleDetail,
                       Summary, VizCard)
from ml.router import router
from ml.service import MLService

pytestmark = pytest.mark.skipif(not make_mocks.UNITY_MOCKS.exists(), reason="Unity ML mocks missing")


def make_app(repo: Path) -> FastAPI:
    app = FastAPI()
    app.state.bus = EventBus()
    app.state.ml = MLService(repo)
    app.include_router(router)

    @app.websocket("/ws")
    async def ws(websocket: WebSocket):
        await app.state.bus.serve(websocket)

    return app


@pytest.fixture()
def image_repo(tmp_path) -> Path:
    make_mocks.image_classification(tmp_path / "img")
    return tmp_path / "img"


@pytest.fixture()
def client(image_repo):
    with TestClient(make_app(image_repo)) as c:
        yield c


def test_image_classification_endpoints_match_the_contract(client):
    classes = client.get("/ml/classes").json()
    assert len(classes) == 10 and classes[3] == "cat"
    items = [DatasetItem(**d) for d in client.get("/ml/dataset?split=val").json()]
    assert len(items) == 1000 and items[0].thumb_url == "/ml/image/val_0000" and not items[0].removed
    atlas = Atlas(**client.get("/ml/atlas").json())
    assert atlas.atlas_url.startswith("/ml/files/atlas.png")
    png = client.get(atlas.atlas_url)
    assert png.status_code == 200 and png.headers["content-type"] == "image/png"
    Graph(**client.get("/ml/graph").json())
    runs = [RunInfo(**r) for r in client.get("/ml/runs").json()]
    assert runs[0].run_id == "mock0001" and runs[0].status == "done" and runs[0].best_metric
    Results(**client.get("/ml/results/mock0001").json())
    Confusion(**client.get("/ml/confusion/mock0001").json())
    Summary(**client.get("/ml/summary/mock0001").json())


def test_image_is_cropped_from_the_atlas(client):
    from PIL import Image

    r = client.get("/ml/image/val_0007")
    assert r.status_code == 200
    assert Image.open(io.BytesIO(r.content)).size == (64, 64)


def test_sample_detail_includes_prediction(client):
    detail = SampleDetail(**client.get("/ml/sample/val_0007").json())
    assert detail.pred_idx is not None and len(detail.probs) == 10


def test_relabel_to_the_prediction_turns_the_dot_green_and_broadcasts(client, image_repo):
    wrong = next(s for s in client.get("/ml/results/mock0001").json()["samples"] if not s["correct"])
    with client.websocket_connect("/ws") as ws:
        r = client.post(f"/ml/samples/{wrong['id']}/relabel", json={"label_idx": wrong["pred_idx"]})
        assert r.status_code == 200
        assert ws.receive_json() == {"type": "ml.sample_updated",
                                     "payload": {"id": wrong["id"], "label_idx": wrong["pred_idx"], "removed": False}}
    fixed = next(s for s in client.get("/ml/results/mock0001").json()["samples"] if s["id"] == wrong["id"])
    assert fixed["correct"] and fixed["relabeled"] and fixed["true_idx"] == wrong["pred_idx"]
    item = next(d for d in client.get("/ml/dataset").json() if d["id"] == wrong["id"])
    assert item["label_idx"] == wrong["pred_idx"]
    saved = json.loads((image_repo / ".forge" / "labels_override.json").read_text())
    assert saved["relabel"] == {wrong["id"]: wrong["pred_idx"]}


def test_remove_marks_but_keeps_the_sample(client, image_repo):
    with client.websocket_connect("/ws") as ws:
        client.post("/ml/samples/val_0001/remove")
        assert ws.receive_json()["payload"]["removed"] is True
    item = next(d for d in client.get("/ml/dataset").json() if d["id"] == "val_0001")
    assert item["removed"] is True
    assert json.loads((image_repo / ".forge" / "labels_override.json").read_text())["remove"] == ["val_0001"]


def test_bad_requests(client):
    assert client.post("/ml/samples/val_0001/relabel", json={"label_idx": 99}).status_code == 409
    assert client.post("/ml/samples/nope_9999/remove").status_code == 404
    assert client.get("/ml/results/..%2F..%2Fsecrets").status_code == 404
    assert client.get("/ml/image/..%2Fprofile").status_code == 404
    assert client.get("/ml/results/missing").status_code == 404
    assert client.post("/ml/train", json={"epochs": 1}).status_code == 409  # mock has no train_cmd


def test_replay_streams_the_recording_onto_ws(client, image_repo):
    n = sum(1 for _ in (image_repo / ".forge" / "runs" / "mock0001" / "events.jsonl").open())
    with client.websocket_connect("/ws") as ws:
        r = client.post("/ml/replay", json={"run_id": "mock0001", "speed": 1000})
        assert r.json() == {"run_id": "mock0001", "events": n}
        got = [ws.receive_json() for _ in range(n)]
    assert got[-1]["payload"]["status"] == "done"
    assert any(e["type"] == "ml.overfit_alert" for e in got)


def test_metrics_history_feeds_curves_after_training(client):
    h = client.get("/ml/metrics/mock0001").json()
    epochs = [m["epoch"] for m in h["metrics"]]
    assert epochs == sorted(set(epochs)) and len(epochs) == h["total_epochs"] == 15
    assert h["overfit"] is not None
    assert client.get("/ml/metrics/missing").status_code == 404


def test_regression_mock(tmp_path):
    make_mocks.regression(tmp_path / "reg")
    with TestClient(make_app(tmp_path / "reg")) as c:
        rows = [DatasetItem(**d) for d in c.get("/ml/dataset").json()]
        assert rows[0].kind == "row" and rows[0].y is not None and rows[0].thumb_url is None
        RegressionResults(**c.get("/ml/results/mockreg1").json())
        assert 0 < Summary(**c.get("/ml/summary/mockreg1").json()).r2 < 1
        assert c.get("/ml/classes").status_code == 404
        assert SampleDetail(**c.get("/ml/sample/row_0003").json()).meta["rooms"] >= 1


def test_viz_cards_for_image_classification(client, image_repo):
    cards = [VizCard(**c) for c in client.get("/ml/viz").json()]
    by_id = {c.viz_id: c for c in cards}
    assert by_id["per_class_accuracy"].kind == "bar" and len(by_id["per_class_accuracy"].series[0].y) == 10
    heat = by_id["confusion_matrix"]
    assert heat.kind == "heatmap" and len(heat.cells) == len(heat.x_labels) == 10
    assert all(c.run_id == "mock0001" and c.panel == "ml-viz" for c in cards)
    assert client.get("/ml/viz", params={"run_id": "mock0001"}).json() == client.get("/ml/viz").json()


def test_viz_png_extras_become_image_cards(client, image_repo):
    viz = image_repo / ".forge" / "runs" / "mock0001" / "viz"
    viz.mkdir()
    (viz / "class_balance.png").write_bytes(b"\x89PNG\r\n\x1a\n")
    card = next(c for c in client.get("/ml/viz").json() if c["viz_id"] == "class_balance")
    assert card["kind"] == "image" and card["png_url"] == "/ml/viz/mock0001/class_balance.png"
    r = client.get(card["png_url"])
    assert r.status_code == 200 and r.headers["content-type"] == "image/png"
    assert client.get("/ml/viz/mock0001/missing.png").status_code == 404
    assert client.get("/ml/viz/..%2F..%2Fsecrets/x.png").status_code == 404


def test_viz_cards_for_regression(tmp_path):
    make_mocks.regression(tmp_path / "reg")
    with TestClient(make_app(tmp_path / "reg")) as c:
        by_id = {v.viz_id: v for v in (VizCard(**x) for x in c.get("/ml/viz").json())}
    assert by_id["pred_vs_actual"].kind == "scatter" and by_id["pred_vs_actual"].points[0].id
    assert by_id["residual_hist"].kind == "bar" and sum(by_id["residual_hist"].series[0].y) == len(by_id["pred_vs_actual"].points)


def test_train_launches_the_repo_command_with_run_id_and_hub(tmp_path):
    repo = tmp_path / "repo"
    (repo / ".forge").mkdir(parents=True)
    (repo / ".forge" / "profile.json").write_text(json.dumps(
        {"archetype": "generic", "python": sys.executable, "train_cmd": "python train.py --epochs 10"}))
    (repo / "train.py").write_text(textwrap.dedent("""
        import json, os, sys
        json.dump({"argv": sys.argv[1:], "run_id": os.environ["FORGE_RUN_ID"], "hub": os.environ["FORGE_HUB"]},
                  open("ran.json", "w"))
    """))
    svc = MLService(repo)
    run_id = svc.start_training(3, "http://127.0.0.1:8765/")
    svc.train_proc.wait(timeout=30)
    ran = json.loads((repo / "ran.json").read_text())
    assert ran == {"argv": ["--epochs", "3"], "run_id": run_id, "hub": "http://127.0.0.1:8765"}


REGRESSION_ADAPTER = '''
import numpy as np
from forge.adapter import Adapter, Sample

class Rows(Adapter):
    archetype = "regression"
    def build_model(self):
        return 1.0
    def samples(self, split="val", limit=1000):
        return [Sample(id=f"r{i}", item=float(i), label=float(i), name=f"row {i}") for i in range(5)]
    def predict(self, model, items):
        return np.array(items)
'''


def test_missing_dataset_is_computed_by_the_runner_in_the_repo_python(tmp_path):
    repo = tmp_path / "repo"
    (repo / ".forge").mkdir(parents=True)
    (repo / ".forge" / "profile.json").write_text(json.dumps({"archetype": "regression", "python": sys.executable}))
    (repo / ".forge" / "adapter.py").write_text(REGRESSION_ADAPTER)
    with TestClient(make_app(repo)) as c:
        start = time.monotonic()
        rows = c.get("/ml/dataset").json()
        assert [r["id"] for r in rows] == ["r0", "r1", "r2", "r3", "r4"]
        assert time.monotonic() - start < 60
    assert (repo / ".forge" / "project" / "dataset.json").exists()
