"""Issue #71: ml-examples/text-classification end to end, offline, on a tiny AG News stand-in.

The example is copied without its data; a few made-up headlines per topic stand in for the AG News CSVs. Then:
open it (its hand-written .forge/ passes the real forge.check, no LLM), train one epoch with the real script, and
read back what the panels read: the text layout, text dataset items, a text-card atlas, per-class accuracy, results.
"""

import csv
import os
import shutil
import subprocess
import sys
from pathlib import Path

import pytest
from fastapi.testclient import TestClient

from ml import prescan as prescan_mod
from ml.service import FORGE_SDK, ROOT
from ml.tests.test_api import make_app

pytest.importorskip("torch")  # train.py runs on this python

EXAMPLE = ROOT / "ml-examples" / "text-classification"
WORDS = {
    1: ["election", "minister", "troops", "peace", "embassy", "border"],
    2: ["coach", "season", "goal", "playoff", "striker", "medal"],
    3: ["shares", "profit", "merger", "investors", "quarter", "bank"],
    4: ["software", "chip", "internet", "browser", "space", "researchers"],
}


def fake_ag_news(path: Path, rows_per_class: int, offset: int) -> None:
    with open(path, "w", newline="", encoding="utf-8") as f:
        writer = csv.writer(f)
        for i in range(rows_per_class):
            for label, words in WORDS.items():
                a, b = words[(i + offset) % len(words)], words[(i + offset + 1) % len(words)]
                writer.writerow([label, f"The {a} news #39;today", f"A story about the {a} and the {b}, number {i}."])


@pytest.fixture()
def text_repo(tmp_path, monkeypatch):
    repo = tmp_path / "text-classification"
    shutil.copytree(EXAMPLE, repo, ignore=shutil.ignore_patterns("data", "checkpoints", "__pycache__", "runs",
                                                                "project", "*.log", "check.json", "hints.json"))
    (repo / "data").mkdir()
    fake_ag_news(repo / "data" / "train.csv", 40, 0)
    fake_ag_news(repo / "data" / "test.csv", 30, 3)
    monkeypatch.setattr(prescan_mod, "find_python", lambda repo: sys.executable)
    return repo


def test_text_example_opens_trains_and_serves_text(text_repo):
    app = make_app(text_repo)
    with TestClient(app) as client, client.websocket_connect("/ws") as ws:
        assert client.post("/ml/project", json={"path": str(text_repo)}).status_code == 200
        events = []
        while not events or events[-1]["payload"].get("status") not in ("ready", "failed", "needs_approval"):
            event = ws.receive_json()
            if event["type"] in ("ml.project_state", "ml.layout"):
                events.append(event)
        state = events[-1]["payload"]
        assert state["status"] == "ready", state  # the hand-written .forge/ passed the real forge.check
        assert state["archetype"] == "text_classification"
        layout = ws.receive_json()
        assert layout["type"] == "ml.layout" and layout["payload"]["archetype"] == "text_classification"
        assert "ml-results" in layout["payload"]["panels"] and "ml-dots" in layout["payload"]["panels"]

        # One real epoch of the example's own train.py, events recorded to the run folder.
        env = {**os.environ, "FORGE_OFFLINE": "1", "FORGE_RUN_DIR": str(text_repo / ".forge" / "runs"),
               "PYTHONPATH": str(FORGE_SDK)}
        done = subprocess.run([sys.executable, "train.py", "--epochs", "1"], cwd=text_repo, env=env,
                              capture_output=True, text=True, timeout=300)
        assert done.returncode == 0, done.stderr[-2000:]

        items = client.get("/ml/dataset").json()
        assert len(items) == 120 and {i["kind"] for i in items} == {"text"}
        assert items[0]["text"].startswith("The ") and "#39;" not in items[0]["text"]  # cleaned: "The election news 'today"
        atlas = client.get("/ml/atlas").json()
        assert 64 <= atlas["cell"][0] <= 128 and len(atlas["index"]) == 120  # a text card per sample
        assert client.get(atlas["atlas_url"]).status_code == 200

        run = next(r for r in client.get("/ml/runs").json() if r["status"] == "done")
        summary = client.get(f"/ml/summary/{run['run_id']}").json()
        assert summary["archetype"] == "text_classification" and summary["num_samples"] == 120
        history = client.get(f"/ml/metrics/{run['run_id']}").json()
        assert history["metrics"][0]["val_acc"] is not None  # live metrics
        assert len(history["class_acc"][0]["per_class"]) == 4  # per-class accuracy each epoch
        assert len(history["probe"]["samples"]) == 16
        results = client.get(f"/ml/results/{run['run_id']}").json()
        ids = {i["id"] for i in items}
        assert {s["id"] for s in results["samples"]} == ids  # results are the same text samples
