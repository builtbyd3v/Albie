"""A3: forge.evaluate, the shared evaluation maths, and the forge.run runners (validated against hub models)."""

import json
import sys
from pathlib import Path

import numpy as np
import pytest
import torch
import torch.nn as nn
from torch.utils.data import DataLoader

import forge
from forge import evaluation
from forge.run import main as run_main

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "hub"))
from ml.models import (Atlas, Confusion, DatasetItem, Graph, RegressionResults, Results,  # noqa: E402
                       Summary)

CIFAR = ROOT / "ml-examples" / "cifar10"
needs_data = pytest.mark.skipif(not (CIFAR / "data" / "cifar-10-batches-py").exists(),
                                reason="run `python data.py` in ml-examples/cifar10 to download CIFAR-10")


def latest_run(root: Path) -> Path:
    return max((p for p in root.iterdir() if p.is_dir()), key=lambda p: p.stat().st_mtime)


class EchoModel(nn.Module):
    """Predicts class (first input value % 3) with high confidence: each row's prediction reveals its input."""

    def __init__(self):
        super().__init__()
        self.dummy = nn.Parameter(torch.zeros(1))

    def forward(self, x):
        return nn.functional.one_hot(x[:, 0].long() % 3, 3).float() * 10 + self.dummy


class Indexed(torch.utils.data.Dataset):
    def __init__(self, n):
        self.n = n
        self.ids = [f"val_{i:04d}" for i in range(n)]

    def __len__(self):
        return self.n

    def __getitem__(self, i):
        return torch.full((2,), float(i)), (i % 3 if i % 5 else (i + 1) % 3)  # every 5th label is wrong


def test_evaluate_keeps_dataset_order_even_with_a_shuffled_loader(tmp_path):
    model = EchoModel().train()
    loader = DataLoader(Indexed(40), batch_size=7, shuffle=True)
    forge.log(epoch=1, train_loss=1.0, train_acc=0.5, val_loss=0.9, val_acc=0.6, lr=1e-3)
    forge.log(epoch=2, train_loss=0.8, train_acc=0.7, val_loss=0.8, val_acc=0.7, lr=1e-3)
    forge.evaluate(model, loader, classes=["a", "b", "c"])
    assert model.training  # restored

    run = latest_run(tmp_path / "runs")
    results = Results(**json.loads((run / "results.json").read_text()))
    for s in results.samples:
        i = int(s.id[-4:])
        assert s.pred_idx == i % 3  # the prediction came from this id's own input
        assert s.correct == bool(i % 5)
    summary = Summary(**json.loads((run / "summary.json").read_text()))
    assert summary.accuracy == 0.8 and summary.best_epoch == 2 and summary.val_loss == 0.8
    Confusion(**json.loads((run / "confusion.json").read_text()))
    types = [json.loads(l)["type"] for l in (run / "events.jsonl").read_text().splitlines()]
    assert types[-2:] == ["ml.results_ready", "ml.run_state"]


def test_classification_maths():
    probs = np.array([[0.9, 0.1], [0.2, 0.8], [0.7, 0.3], [0.4, 0.6]])
    results, confusion, summary = evaluation.classification(["a", "b", "c", "d"], [0, 1, 1, 0], probs, ["x", "y"])
    assert confusion["matrix"] == [[1, 1], [1, 1]]
    assert [m["id"] for m in summary["confident_mistakes"]] == ["c", "d"]  # 0.7 before 0.6
    assert summary["per_class"][0] == {"idx": 0, "name": "x", "count": 2, "correct": 1, "accuracy": 0.5}
    assert {(p["true_idx"], p["pred_idx"]) for p in summary["top_confusions"]} == {(0, 1), (1, 0)}
    with pytest.raises(ValueError):
        evaluation.classification(["a"], [0], probs[:1], ["x", "y", "z"])


def test_regression_maths():
    results, summary = evaluation.regression(["a", "b", "c"], [1.0, 2.0, 3.0], [1.0, 2.0, 4.0])
    assert results["samples"][2]["residual"] == -1.0
    assert summary["mae"] == pytest.approx(1 / 3, abs=1e-6) and summary["r2"] == 0.5
    RegressionResults(**results)
    Summary(**summary)


@needs_data
def test_cifar10_runners_validate_against_hub_models(tmp_path, capsys):
    out = tmp_path / "project"
    run_main(["graph", "--repo", str(CIFAR), "--out", str(out)])
    run_main(["dataset", "--repo", str(CIFAR), "--out", str(out), "--limit", "100"])
    Graph(**json.loads((out / "graph.json").read_text()))
    items = [DatasetItem(**d) for d in json.loads((out / "dataset.json").read_text())]
    assert len(items) == 100 and items[0].kind == "image" and items[0].name == "val_0000.png"
    Atlas(atlas_url="/hub/adds/this", **json.loads((out / "atlas.json").read_text()))
    meta = json.loads((out / "samples.json").read_text())
    assert meta["val_0000"]["meta"]["size"] == [32, 32]

    run_main(["eval", "--repo", str(CIFAR), "--limit", "100"])
    run = Path(capsys.readouterr().out.strip().splitlines()[-1].split("-> ")[-1])
    try:
        Results(**json.loads((run / "results.json").read_text()))
        Confusion(**json.loads((run / "confusion.json").read_text()))
        assert Summary(**json.loads((run / "summary.json").read_text())).num_samples == 100
    finally:
        import shutil
        shutil.rmtree(run)  # don't leave test runs in the example repo


REGRESSION_ADAPTER = '''
import numpy as np
from forge.adapter import Adapter, Sample

class Doubler(Adapter):
    archetype = "regression"
    def build_model(self):
        return 2.0
    def load_model(self):
        return 2.0
    def samples(self, split="val", limit=1000):
        return [Sample(id=f"row_{i}", item=float(i), label=2.0 * i + (1 if i == 3 else 0), name=f"row {i}")
                for i in range(min(limit, 10))]
    def predict(self, model, items):
        return np.array(items) * model
'''


def test_regression_runners(tmp_path, capsys):
    repo = tmp_path / "reg"
    (repo / ".forge").mkdir(parents=True)
    (repo / ".forge" / "adapter.py").write_text(REGRESSION_ADAPTER)
    run_main(["dataset", "--repo", str(repo)])
    items = [DatasetItem(**d) for d in json.loads((repo / ".forge" / "project" / "dataset.json").read_text())]
    assert items[3].kind == "row" and items[3].y == 7.0
    run_main(["eval", "--repo", str(repo)])
    run = latest_run(repo / ".forge" / "runs")
    results = RegressionResults(**json.loads((run / "results.json").read_text()))
    assert [s.residual for s in results.samples if s.residual] == [1.0]
    assert Summary(**json.loads((run / "summary.json").read_text())).mae == 0.1
    with pytest.raises(ValueError, match="no layer graph"):
        run_main(["graph", "--repo", str(repo)])
