"""Text datasets (`dataset.texts`): forge.watch writes text items and a text-card atlas, evaluate a text summary."""

import json
import os
from pathlib import Path

import torch
import torch.nn as nn
from torch.utils.data import DataLoader, Dataset, Subset

import forge
from forge import atlas

TOPICS = ["sports", "markets"]


class Headlines(Dataset):
    """Like the AG News example: `texts`, `labels`, `ids`; items are (word-id tensor, label)."""

    def __init__(self, n: int) -> None:
        self.texts = [f"Headline {i}: the {TOPICS[i % 2]} story of the day, told at some length" for i in range(n)]
        self.labels = [i % 2 for i in range(n)]
        self.ids = [f"val_{i:04d}" for i in range(n)]

    def __len__(self) -> int:
        return len(self.texts)

    def __getitem__(self, i: int):
        return torch.full((6,), self.labels[i] + 1, dtype=torch.long), self.labels[i]


class Bag(nn.Module):
    def __init__(self) -> None:
        super().__init__()
        self.embedding = nn.Embedding(3, 4)
        self.out = nn.Linear(4, 2)

    def forward(self, ids):
        return self.out(self.embedding(ids).mean(1))


def test_build_text_sizes_cells_to_fit_and_keeps_the_text():
    image, meta, items = atlas.build_text(["a short one", "x " * 1500], [0, 1], ids=["t_0", "t_1"])
    assert meta["cell"] == [128, 128] and image.size == (256, 128)  # two cards: the biggest cell
    assert len(set(image.crop((0, 0, 128, 128)).getdata())) > 2  # the card has text drawn on it
    assert items[0] == {"id": "t_0", "name": "t_0", "kind": "text", "label_idx": 0, "text": "a short one"}
    assert len(items[1]["text"]) == atlas.TEXT_CHARS
    _, meta, _ = atlas.build_text(["t"] * 1000, [0] * 1000)
    assert meta["cell"] == [64, 64] and meta["cols"] * 64 <= atlas.MAX_TEXTURE


def test_texts_found_through_subsets():
    ds = Headlines(6)
    assert atlas.dataset_texts(Subset(ds, [4, 1])) == [ds.texts[4], ds.texts[1]]
    assert atlas.dataset_texts([1, 2]) is None


def test_watch_and_evaluate_a_text_dataset():
    ds, model = Headlines(20), Bag()
    run_id = forge.watch(model, ds, classes=TOPICS, epochs=1)
    run = Path(os.environ["FORGE_RUN_DIR"]) / run_id
    items = json.loads((run / "dataset.json").read_text())
    assert [i["kind"] for i in items] == ["text"] * 20 and items[3]["text"] == ds.texts[3]
    assert [i["label_idx"] for i in items] == ds.labels
    assert json.loads((run / "atlas.json").read_text())["index"]["val_0003"] == 3
    assert {layer["type"] for layer in json.loads((run / "graph.json").read_text())["layers"]} >= {"embedding", "linear"}
    forge.log(epoch=1, train_loss=0.5, val_loss=0.4, val_acc=0.5, model=model)
    forge.evaluate(model, DataLoader(ds, batch_size=8))
    summary = json.loads((run / "summary.json").read_text())
    assert summary["archetype"] == "text_classification" and summary["num_samples"] == 20
