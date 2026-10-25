"""Hand-written forge adapter for the CIFAR-10 example.

The golden example: the adapter prompt gets this file as its worked example, tests compare against it,
and it's the stage backup if the LLM misbehaves. It imports the repo's model and data, never copies them.
"""

from __future__ import annotations

from pathlib import Path

import numpy as np
import torch
from PIL import Image

from data import CLASSES, TRANSFORM, get_dataset
from forge.adapter import Adapter, Sample
from model import SmallCNN

REPO = Path(__file__).resolve().parents[1]
CHECKPOINT = REPO / "checkpoints" / "best.pt"


class Cifar10Adapter(Adapter):
    archetype = "image_classification"

    def build_model(self) -> SmallCNN:
        return SmallCNN(len(CLASSES))

    def load_model(self) -> SmallCNN | None:
        if not CHECKPOINT.exists():
            return None
        model = SmallCNN(len(CLASSES))
        model.load_state_dict(torch.load(CHECKPOINT, map_location="cpu"))
        return model.eval()

    def example_input(self) -> torch.Tensor:
        return torch.zeros(1, 3, 32, 32)

    def classes(self) -> list[str]:
        return list(CLASSES)

    def samples(self, split: str = "val", limit: int = 1000) -> list[Sample]:
        ds = get_dataset(split, REPO / "data", download=False)
        out = []
        for i in range(min(limit, len(ds))):
            base_index = ds.indices[i]
            out.append(Sample(
                id=ds.ids[i],
                item=Image.fromarray(ds.dataset.data[base_index]),
                label=ds.labels[i],
                name=f"{ds.ids[i]}.png",
                meta={"split": split, "cifar_index": base_index, "size": [32, 32], "channels": 3},
            ))
        return out

    def _batch(self, model: torch.nn.Module, items: list[Image.Image]) -> torch.Tensor:
        device = next(model.parameters()).device
        return torch.stack([TRANSFORM(im) for im in items]).to(device)

    def predict(self, model: torch.nn.Module, items: list[Image.Image]) -> np.ndarray:
        was_training = model.training
        model.eval()
        with torch.no_grad():
            probs = torch.softmax(model(self._batch(model, items)), dim=1)
        model.train(was_training)
        return probs.cpu().numpy()

    def features(self, model: torch.nn.Module, items: list[Image.Image]) -> np.ndarray:
        was_training = model.training
        model.eval()
        with torch.no_grad():
            feats = model.head[:-1](model.features(self._batch(model, items)))
        model.train(was_training)
        return feats.cpu().numpy()
