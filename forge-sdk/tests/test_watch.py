import json
import sys
from pathlib import Path

import pytest
import torch
import torch.nn as nn
import torchvision.transforms as T
from PIL import Image
from torch.utils.data import Dataset, Subset

import forge
from forge import atlas, graph

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "ml-examples" / "cifar10"))
from model import SmallCNN  # noqa: E402


class FakeVision(Dataset):
    """Behaves like a torchvision dataset: PIL images, optional `transform`."""

    def __init__(self, n: int, size: int = 32, transform=None) -> None:
        self.n, self.size, self.transform = n, size, transform

    def __len__(self) -> int:
        return self.n

    def __getitem__(self, i: int):
        img = Image.new("RGB", (self.size, self.size), (i % 256, 100, 200))
        return (self.transform(img) if self.transform else img), i % 10


NORMALIZED = T.Compose([T.ToTensor(), T.Normalize((0.5,) * 3, (0.5,) * 3)])


def test_graph_has_shapes_without_batch_dim_and_keeps_train_mode():
    model = SmallCNN().train()
    layers = graph.describe(model, torch.zeros(3, 32, 32))["layers"]
    assert model.training
    assert layers[0] == {"name": "features.0", "type": "conv", "in_shape": [3, 32, 32], "out_shape": [32, 32, 32],
                         "params": 896}
    assert layers[-1]["out_shape"] == [10]
    assert {l["type"] for l in layers} == {"conv", "norm", "activation", "pool", "reshape", "linear"}


def test_graph_single_channel_input():
    model = nn.Sequential(nn.Conv2d(1, 4, 3), nn.Flatten(), nn.Linear(4 * 26 * 26, 10))
    layers = graph.describe(model, torch.zeros(1, 28, 28))["layers"]
    assert layers[0]["in_shape"] == [1, 28, 28]


def test_atlas_uses_raw_colors_through_subset_and_normalize():
    base = FakeVision(50, transform=NORMALIZED)
    image, meta, items = atlas.build(Subset(base, range(10, 30)))
    assert base.transform is NORMALIZED  # restored
    assert meta["cols"] == 5 and meta["cell"] == [64, 64] and image.size == (5 * 64, 4 * 64)
    assert items[0] == {"id": "val_0000", "name": "val_0000", "kind": "image", "label_idx": 0}  # base 10 -> 0
    assert image.getpixel((0, 0)) == (10, 100, 200)  # un-normalized pixel from base index 10
    assert meta["index"]["val_0019"] == 19


def test_atlas_resizes_and_caps_at_2048():
    image, meta, items = atlas.build(FakeVision(3, size=64), cell=32)
    assert image.getpixel((0, 0)) == (0, 100, 200)
    with pytest.warns(UserWarning):
        image, meta, items = atlas.build(FakeVision(4100, size=8), cell=32)
    assert len(items) == 4096 and max(image.size) <= 2048


def test_atlas_upscales_nearest_neighbor():
    checker = Image.new("RGB", (2, 2), (0, 0, 0))
    checker.putpixel((1, 0), (255, 255, 255))

    class Tiny(Dataset):
        def __len__(self):
            return 1

        def __getitem__(self, i):
            return checker, 0

    image, _, _ = atlas.build(Tiny(), cell=4)
    # each source pixel becomes a crisp 2x2 block, no blended greys
    assert {image.getpixel((x, y)) for x in range(2) for y in range(2)} == {(0, 0, 0)}
    assert {image.getpixel((x, y)) for x in range(2, 4) for y in range(2)} == {(255, 255, 255)}


def test_atlas_1000_images_fill_one_2048_texture():
    image, meta, items = atlas.build(FakeVision(1000))
    assert image.size == (2048, 2048) and meta["cols"] == 32 and len(items) == 1000


def test_to_pil_handles_tensors():
    assert atlas.to_pil(torch.zeros(1, 4, 4)).size == (4, 4)
    assert atlas.to_pil(torch.full((3, 4, 4), -1.0)).mode == "RGB"


def test_watch_writes_run_dir_and_emits_events(tmp_path, capsys):
    run_id = forge.watch(SmallCNN(), FakeVision(20, transform=NORMALIZED), classes=[str(i) for i in range(10)],
                         epochs=3, run_dir=tmp_path)
    run = tmp_path / run_id
    assert {p.name for p in run.iterdir()} == {"atlas.png", "atlas.json", "dataset.json", "classes.json",
                                               "graph.json", "run.json", "events.jsonl"}
    assert len(json.loads((run / "dataset.json").read_text())) == 20
    assert json.loads((run / "graph.json").read_text())["layers"][0]["type"] == "conv"
    events = [json.loads(line) for line in capsys.readouterr().out.splitlines()]
    assert [e["type"] for e in events] == ["ml.run_state", "ml.graph_ready"]
    assert events[0]["payload"] == {"run_id": run_id, "status": "training", "epoch": 0, "total_epochs": 3}


def test_log_with_model_sends_live_predictions_and_keeps_train_mode(tmp_path, capsys):
    model = SmallCNN()
    run_id = forge.watch(model, FakeVision(12, transform=NORMALIZED), classes=[str(i) for i in range(10)],
                         epochs=2, run_dir=tmp_path)
    capsys.readouterr()
    model.train()
    forge.log(epoch=1, train_loss=1.0, val_loss=1.1, model=model)
    assert model.training
    events = [json.loads(line) for line in capsys.readouterr().out.splitlines()]
    assert [e["type"] for e in events] == ["ml.run_state", "ml.metrics", "ml.epoch_preds"]
    preds = events[-1]["payload"]
    assert preds["run_id"] == run_id and preds["epoch"] == 1
    assert len(preds["pred_idx"]) == 12 and all(0 <= p < 10 for p in preds["pred_idx"])
    assert "model" not in events[1]["payload"].get("extra", {})


def test_live_predictions_cover_only_the_atlas_samples(tmp_path, capsys, monkeypatch):
    monkeypatch.setattr(atlas, "MAX_TEXTURE", 64 * 2)  # a 2x2 atlas: 4 cells
    model = SmallCNN()
    forge.watch(model, FakeVision(9, transform=NORMALIZED), classes=[str(i) for i in range(10)], run_dir=tmp_path)
    forge.log(epoch=1, model=model)
    events = [json.loads(line) for line in capsys.readouterr().out.splitlines()]
    assert len(next(e for e in events if e["type"] == "ml.epoch_preds")["payload"]["pred_idx"]) == 4
