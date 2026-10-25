"""AG News (4 news topics) as padded word ids: a 12,000-headline train subset, a fixed 500-headline val subset,
~3% noisy val labels (the "the model was right, the data was wrong" moment, as in the CIFAR-10 example).

`python data.py` downloads the AG News CSVs (~31 MB) into data/. Each sample is "title. description".
Relabel/remove decisions from the hub live in .forge/labels_override.json and apply on the next load:
    {"relabel": {"val_0007": 3}, "remove": ["train_01234"]}
"""

from __future__ import annotations

import csv
import html
import json
import re
import urllib.request
from collections import Counter
from pathlib import Path

import numpy as np
import torch
from torch.utils.data import DataLoader, Dataset

HERE = Path(__file__).resolve().parent
CLASSES = ["World", "Sports", "Business", "Sci/Tech"]
MIRROR = "https://raw.githubusercontent.com/mhjabreel/CharCnn_Keras/master/data/ag_news_csv"
FILES = {"train": "train.csv", "val": "test.csv"}  # val comes from AG News' own test split
TRAIN_PER_CLASS = 3000
VAL_PER_CLASS = 125
NOISE_RATE = 0.03
MAX_LEN = 48
VOCAB_SIZE = 20_000
PAD, UNK = 0, 1
SEED = 7
OVERRIDES = HERE / ".forge" / "labels_override.json"
TOKEN = re.compile(r"[a-z0-9']+")
ENTITY = re.compile(r"(?<!&)#(\d+);")
TAG = re.compile(r"<[^>]+>")


def download(root: Path = HERE / "data") -> None:
    root.mkdir(parents=True, exist_ok=True)
    for name in FILES.values():
        target = root / name
        if not target.exists():
            partial = target.with_suffix(".partial")
            urllib.request.urlretrieve(f"{MIRROR}/{name}", partial)
            partial.rename(target)


def read_rows(path: Path) -> list[tuple[int, str]]:
    """[(class index, "title. description")] in file order."""
    rows = []
    with open(path, newline="", encoding="utf-8") as f:
        for label, title, description in csv.reader(f):
            text = f"{title.strip()}. {description.strip()}".replace("\\", " ")
            text = html.unescape(ENTITY.sub(r"&#\1;", text))  # AG News keeps escapes like "#39;" and "&lt;"
            text = TAG.sub("", text)
            rows.append((int(label) - 1, " ".join(text.split())))
    return rows


def pick(rows: list[tuple[int, str]], per_class: int, seed: int) -> list[int]:
    """The first `per_class` rows of each class after a seeded shuffle: the same subset on every machine."""
    order = np.random.default_rng(seed).permutation(len(rows))
    taken, counts = [], Counter()
    for i in order:
        label = rows[i][0]
        if counts[label] < per_class:
            counts[label] += 1
            taken.append(int(i))
    return sorted(taken)


def flip_labels(labels: list[int]) -> dict[int, int]:
    """Deterministic 3% label flips on the val subset: {position: noisy label}, always a different class."""
    rng = np.random.default_rng(SEED + 1)
    picked = rng.choice(len(labels), round(len(labels) * NOISE_RATE), replace=False)
    offsets = rng.integers(1, len(CLASSES), size=len(picked))
    return {int(i): (labels[i] + int(o)) % len(CLASSES) for i, o in zip(picked, offsets)}


def tokenize(text: str) -> list[str]:
    return TOKEN.findall(text.lower())


class Vocab:
    """The VOCAB_SIZE most common train words (ties by first appearance); 0 is padding, 1 an unknown word."""

    def __init__(self, texts: list[str]) -> None:
        counts = Counter(word for text in texts for word in tokenize(text))
        self.words = [w for w, _ in counts.most_common(VOCAB_SIZE - 2)]
        self.index = {w: i + 2 for i, w in enumerate(self.words)}

    def __len__(self) -> int:
        return len(self.words) + 2

    def encode(self, text: str) -> torch.Tensor:
        ids = [self.index.get(w, UNK) for w in tokenize(text)][:MAX_LEN]
        return torch.tensor(ids + [PAD] * (MAX_LEN - len(ids)), dtype=torch.long)


class TextDataset(Dataset):
    """(word ids [MAX_LEN], label) per item. `texts`, `labels`, `ids` and `names` are what forge shows."""

    def __init__(self, texts: list[str], labels: list[int], ids: list[str], vocab: Vocab) -> None:
        self.texts, self.labels, self.ids, self.vocab = texts, labels, ids, vocab
        self.names = [f"{i}.txt" for i in ids]
        self.encoded = [vocab.encode(t) for t in texts]

    def __len__(self) -> int:
        return len(self.texts)

    def __getitem__(self, i: int) -> tuple[torch.Tensor, int]:
        return self.encoded[i], self.labels[i]


def _overrides(path: Path | None) -> tuple[dict[str, int], set[str]]:
    if path is None or not path.exists():
        return {}, set()
    data = json.loads(path.read_text())
    return {k: int(v) for k, v in data.get("relabel", {}).items()}, set(data.get("remove", []))


def _split(root: Path, split: str) -> tuple[list[str], list[int], list[str]]:
    rows = read_rows(root / FILES[split])
    chosen = pick(rows, TRAIN_PER_CLASS if split == "train" else VAL_PER_CLASS, SEED + (0 if split == "train" else 2))
    texts = [rows[i][1] for i in chosen]
    labels = [rows[i][0] for i in chosen]
    if split == "val":
        for position, noisy in flip_labels(labels).items():
            labels[position] = noisy
    ids = [f"train_{i:05d}" if split == "train" else f"val_{i:04d}" for i in chosen]
    return texts, labels, ids


def get_datasets(root: str | Path = HERE / "data", download_missing: bool = True,
                 overrides: Path | None = OVERRIDES) -> tuple[TextDataset, TextDataset, Vocab]:
    root = Path(root)
    if download_missing:
        download(root)
    train_texts, train_labels, train_ids = _split(root, "train")
    vocab = Vocab(train_texts)  # from the train texts only, before overrides: the same vocab on every load
    relabel, remove = _overrides(overrides)
    out = []
    for texts, labels, ids in ((train_texts, train_labels, train_ids), _split(root, "val")):
        keep = [k for k, sid in enumerate(ids) if sid not in remove]
        out.append(TextDataset([texts[k] for k in keep], [relabel.get(ids[k], labels[k]) for k in keep],
                               [ids[k] for k in keep], vocab))
    return out[0], out[1], vocab


def get_loaders(root: str | Path = HERE / "data", batch_size: int = 64) -> tuple[DataLoader, DataLoader, Vocab]:
    train, val, vocab = get_datasets(root)
    return (DataLoader(train, batch_size=batch_size, shuffle=True),
            DataLoader(val, batch_size=256, shuffle=False), vocab)


if __name__ == "__main__":
    download()
    train, val, vocab = get_datasets(download_missing=False)
    print(f"train {len(train)}, val {len(val)} ({round(VAL_PER_CLASS * len(CLASSES) * NOISE_RATE)} noisy labels), vocab {len(vocab)}")
