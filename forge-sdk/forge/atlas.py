"""Thumbnail atlas for `GET /ml/atlas` and the dataset list for `GET /ml/dataset`."""

from __future__ import annotations

import math
import warnings
from typing import Any

import numpy as np
import torch
from PIL import Image
from torch.utils.data import Dataset, Subset

MAX_TEXTURE = 2048  # Quest texture limit (CLAUDE.md)


def sample_id(i: int, split: str = "val") -> str:
    return f"{split}_{i:04d}"


def _unwrap(dataset: Dataset, i: int) -> tuple[Dataset, int]:
    """Follow Subset chains down to the base dataset and its index."""
    while isinstance(dataset, Subset):
        i = dataset.indices[i]
        dataset = dataset.dataset
    return dataset, i


def _raw_item(dataset: Dataset, i: int) -> tuple[Any, Any]:
    """The item before `transform` (e.g. Normalize) runs, so thumbnails keep their real colors."""
    base, j = _unwrap(dataset, i)
    if not hasattr(base, "transform"):
        return dataset[i]
    transform = base.transform
    base.transform = None
    try:
        return base[j]
    finally:
        base.transform = transform


def to_pil(img: Any) -> Image.Image:
    if isinstance(img, Image.Image):
        return img.convert("RGB")
    arr = img.detach().cpu().numpy() if isinstance(img, torch.Tensor) else np.asarray(img)
    if arr.ndim == 3 and arr.shape[0] in (1, 3) and arr.shape[-1] not in (1, 3):
        arr = arr.transpose(1, 2, 0)  # CHW -> HWC
    if arr.ndim == 3 and arr.shape[-1] == 1:
        arr = arr[..., 0]
    if arr.dtype != np.uint8:
        arr = arr.astype(np.float32)
        lo, hi = float(arr.min()), float(arr.max())
        if lo < 0 or hi > 1:  # still normalized: stretch to [0, 1]
            arr = (arr - lo) / (hi - lo or 1.0)
        arr = (arr * 255).round().astype(np.uint8)
    return Image.fromarray(arr).convert("RGB")


def _label(target: Any) -> int:
    return int(target.item() if isinstance(target, torch.Tensor) else target)


def build(dataset: Dataset, cell: int = 64, split: str = "val") -> tuple[Image.Image, dict, list[dict]]:
    """Returns (atlas image, {cell, cols, index}, [{id, label_idx}]).

    Cell `i` sits at column `i % cols`, row `i // cols` counted from the top. Datasets bigger than
    fit in a 2048 px atlas are truncated with a warning (64 px cells: 1,024 images, ML_PLAN 11).
    Small images are upscaled nearest-neighbor so CIFAR's 32 px stays crisp instead of blurry.
    """
    n = len(dataset)
    max_cells = (MAX_TEXTURE // cell) ** 2
    if n > max_cells:
        warnings.warn(f"forge: atlas holds {max_cells} images at {cell}px; showing the first {max_cells} of {n}")
        n = max_cells
    cols = math.ceil(math.sqrt(n))
    rows = math.ceil(n / cols)
    atlas = Image.new("RGB", (cols * cell, rows * cell))
    index: dict[str, int] = {}
    items: list[dict] = []
    for i in range(n):
        img, target = _raw_item(dataset, i)
        thumb = to_pil(img)
        if thumb.size != (cell, cell):
            upscale = thumb.width < cell and thumb.height < cell
            thumb = thumb.resize((cell, cell), Image.Resampling.NEAREST if upscale else Image.Resampling.BILINEAR)
        atlas.paste(thumb, ((i % cols) * cell, (i // cols) * cell))
        sid = sample_id(i, split)
        index[sid] = i
        items.append({"id": sid, "label_idx": _label(target)})
    return atlas, {"cell": [cell, cell], "cols": cols, "index": index}, items
