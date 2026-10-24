"""CIFAR-10 data: a 10k train subset (or all 50k), a fixed 1,000-image val subset, ~3% noisy labels.

Noise is deliberate: those images show up as confident mistakes even though the model is right
("the model was right, the data was wrong"). `python data.py` writes noise.json.

Relabel/remove decisions from the hub live in .forge/labels_override.json and apply on the next load:
    {"relabel": {"val_0007": 3}, "remove": ["train_01234"]}
"""

from __future__ import annotations

import json
from pathlib import Path

import numpy as np
import torchvision
import torchvision.transforms as T
from torch.utils.data import DataLoader, Dataset, Subset

HERE = Path(__file__).resolve().parent
CLASSES = ["airplane", "automobile", "bird", "cat", "deer", "dog", "frog", "horse", "ship", "truck"]
TRAIN_SUBSET = 10_000
VAL_SIZE = 1000
NOISE_RATE = 0.03
SEED = 7
OVERRIDES = HERE / ".forge" / "labels_override.json"

TRANSFORM = T.Compose([T.ToTensor(), T.Normalize((0.4914, 0.4822, 0.4465), (0.2470, 0.2435, 0.2616))])


class LabeledSubset(Subset):
    """A Subset with its own labels (noise + overrides) and stable sample ids."""

    def __init__(self, dataset: Dataset, indices: list[int], labels: list[int], ids: list[str]) -> None:
        super().__init__(dataset, indices)
        self.labels = labels
        self.ids = ids

    def __getitem__(self, i: int):
        img, _ = self.dataset[self.indices[i]]
        return img, self.labels[i]

    def __getitems__(self, indices: list[int]) -> list:
        # torch >= 2.x: the DataLoader batches through this, bypassing __getitem__ unless overridden
        return [self[i] for i in indices]


def sample_id(split: str, base_index: int) -> str:
    return f"train_{base_index:05d}" if split == "train" else f"val_{base_index:04d}"


def flip_labels(targets: list[int], split: str) -> dict[int, int]:
    """Deterministic 3% label flips: {base index: noisy label}. Train: over all 50k, so the same images
    are noisy with or without --full. Val: over the fixed 1,000-image subset, so exactly 30."""
    labels = targets[:VAL_SIZE] if split == "val" else targets
    rng = np.random.default_rng(SEED + (0 if split == "train" else 1))
    n = len(labels)
    picked = rng.choice(n, round(n * NOISE_RATE), replace=False)
    offsets = rng.integers(1, len(CLASSES), size=len(picked))
    return {int(i): (labels[i] + int(o)) % len(CLASSES) for i, o in zip(sorted(picked), offsets)}


def load_overrides(path: Path | None = OVERRIDES) -> tuple[dict[str, int], set[str]]:
    """None (or a missing file) means no overrides."""
    if path is None or not path.exists():
        return {}, set()
    data = json.loads(path.read_text())
    return {k: int(v) for k, v in data.get("relabel", {}).items()}, set(data.get("remove", []))


def _base(split: str, root: Path | str, download: bool) -> torchvision.datasets.CIFAR10:
    return torchvision.datasets.CIFAR10(str(root), train=split == "train", transform=TRANSFORM, download=download)


def _indices(split: str, full: bool) -> list[int]:
    if split == "val":
        return list(range(VAL_SIZE))
    if full:
        return list(range(50_000))
    return sorted(int(i) for i in np.random.default_rng(SEED).choice(50_000, TRAIN_SUBSET, replace=False))


def get_dataset(split: str = "val", root: Path | str = HERE / "data", full: bool = False,
                download: bool = True, overrides: Path | None = OVERRIDES) -> LabeledSubset:
    base = _base(split, root, download)
    targets = [int(t) for t in base.targets]
    noise = flip_labels(targets, split)
    relabel, remove = load_overrides(overrides)
    indices, labels, ids = [], [], []
    for i in _indices(split, full):
        sid = sample_id(split, i)
        if sid in remove:
            continue
        indices.append(i)
        labels.append(relabel.get(sid, noise.get(i, targets[i])))
        ids.append(sid)
    return LabeledSubset(base, indices, labels, ids)


def get_loaders(root: Path | str = HERE / "data", batch_size: int = 128, full: bool = False):
    train = get_dataset("train", root, full)
    val = get_dataset("val", root)
    return DataLoader(train, batch_size=batch_size, shuffle=True), DataLoader(val, batch_size=256)


def noise_report(root: Path | str = HERE / "data") -> dict:
    """Every flipped label, for noise.json. Train covers all 50k so it holds for --full too."""
    report: dict = {"seed": SEED, "rate": NOISE_RATE}
    for split in ("train", "val"):
        targets = [int(t) for t in _base(split, root, download=True).targets]
        report[split] = [{"id": sample_id(split, i), "clean_idx": targets[i], "noisy_idx": noisy}
                         for i, noisy in flip_labels(targets, split).items()]
    return report


if __name__ == "__main__":
    report = noise_report()
    (HERE / "noise.json").write_text(json.dumps(report, indent=1) + "\n")
    print(f"noise.json: {len(report['train'])} train + {len(report['val'])} val labels flipped")
