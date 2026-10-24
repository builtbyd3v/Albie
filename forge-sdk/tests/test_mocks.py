"""The Unity ML mocks must match the section 7 contract (hub/ml/models.py)."""

import json
import sys
from pathlib import Path

import pytest

ROOT = Path(__file__).resolve().parents[2]
MOCKS = ROOT / "unity" / "Assets" / "_Forge" / "Mocks" / "ml"
sys.path.insert(0, str(ROOT / "hub"))
from ml.models import Atlas, Confusion, DatasetItem, Graph, Results  # noqa: E402

pytestmark = pytest.mark.skipif(not MOCKS.exists(), reason="run forge-sdk/tools/make_ml_mocks.py first")

EVENT_KEYS = {
    "ml.run_state": {"run_id", "status", "epoch", "total_epochs"},
    "ml.metrics": {"run_id", "epoch", "step", "train_loss", "train_acc", "val_loss", "val_acc", "lr"},
    "ml.overfit_alert": {"run_id", "epoch", "reason"},
    "ml.graph_ready": set(),
    "ml.results_ready": {"run_id"},
}


def load(name: str):
    return json.loads((MOCKS / name).read_text())


def test_rest_mocks_match_contract():
    classes = load("ml_classes.json")
    assert len(classes) == 10
    dataset = [DatasetItem(**d) for d in load("ml_dataset_val.json")]
    atlas = Atlas(**load("ml_atlas_val.json"))
    Graph(**load("ml_graph.json"))
    results = Results(**load("ml_results.json"))
    confusion = Confusion(**load("ml_confusion.json"))

    ids = {d.id for d in dataset}
    assert ids == set(atlas.index) == {s.id for s in results.samples}
    assert confusion.labels == classes
    assert sum(map(sum, confusion.matrix)) == len(results.samples)
    for s in results.samples:
        assert len(s.probs) == len(classes)
        assert abs(sum(s.probs) - 1) < 0.01
        assert s.probs.index(max(s.probs)) == s.pred_idx
        assert s.correct == (s.pred_idx == s.true_idx)


def test_atlas_fits_quest_texture_limit():
    from PIL import Image

    atlas = Atlas(**load("ml_atlas_val.json"))
    w, h = Image.open(MOCKS / "ml_atlas_val.png").size
    assert w <= 2048 and h <= 2048
    assert w == atlas.cols * atlas.cell[0]


def test_events_match_contract_and_demo_script():
    events = load("ml_events.json")
    times = [e["t"] for e in events]
    assert times == sorted(times)
    for e in events:
        assert set(e["payload"]) == EVENT_KEYS[e["type"]], e
    types = [e["type"] for e in events]
    assert "ml.overfit_alert" in types
    assert types[-1] == "ml.run_state" and events[-1]["payload"]["status"] == "done"


def test_noisy_labels_are_confident_misses():
    noise = load("ml_noise.json")["flipped"]
    samples = {s["id"]: s for s in load("ml_results.json")["samples"]}
    assert len(noise) == 30
    for n in noise:
        s = samples[n["id"]]
        assert s["true_idx"] == n["noisy_idx"] and s["pred_idx"] == n["clean_idx"] and not s["correct"]
