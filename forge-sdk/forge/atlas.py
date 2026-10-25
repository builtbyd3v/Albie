"""Thumbnail atlas for `GET /ml/atlas` and the dataset list for `GET /ml/dataset`."""

from __future__ import annotations

import math
import warnings
from typing import Any, Sequence

import numpy as np
from PIL import Image

try:  # torch is optional: Keras / plain-list datasets work too
    import torch
    from torch.utils.data import Subset
except ImportError:  # pragma: no cover - exercised in torch-free envs
    torch = None
    Subset = ()  # isinstance(x, ()) is always False

MAX_TEXTURE = 2048  # Quest texture limit (CLAUDE.md)


def sample_id(i: int, split: str = "val") -> str:
    return f"{split}_{i:04d}"


def _unwrap(dataset: Any, i: int) -> tuple[Any, int]:
    """Follow Subset chains down to the base dataset and its index."""
    while isinstance(dataset, Subset):
        i = dataset.indices[i]
        dataset = dataset.dataset
    return dataset, i


def _raw_item(dataset: Any, i: int) -> tuple[Any, Any]:
    """The item with the base dataset's `transform` (e.g. Normalize) switched off, so thumbnails keep
    their real colors. Read through the top-level dataset, so Subset wrappers that relabel still apply."""
    base, _ = _unwrap(dataset, i)
    if not hasattr(base, "transform"):
        return dataset[i]
    transform = base.transform
    base.transform = None
    try:
        return dataset[i]
    finally:
        base.transform = transform


def to_pil(img: Any) -> Image.Image:
    if isinstance(img, Image.Image):
        return img.convert("RGB")
    arr = img.detach().cpu().numpy() if torch is not None and isinstance(img, torch.Tensor) else np.asarray(img)
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
    return int(target.item() if hasattr(target, "item") else target)


def build(dataset: Any, cell: int = 64, split: str = "val") -> tuple[Image.Image, dict, list[dict]]:
    """Returns (atlas image, {cell, cols, index}, [{id, name, kind, label_idx}]).

    Ids come from `dataset.ids` when the dataset has them (stable across removals), else `val_0000`...
    Names (file names for the picked-image card) come from `dataset.names` when present, else the id.

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
    ids = getattr(dataset, "ids", None)
    names = getattr(dataset, "names", None)
    index: dict[str, int] = {}
    items: list[dict] = []
    for i in range(n):
        img, target = _raw_item(dataset, i)
        thumb = to_pil(img)
        if thumb.size != (cell, cell):
            upscale = thumb.width < cell and thumb.height < cell
            thumb = thumb.resize((cell, cell), Image.Resampling.NEAREST if upscale else Image.Resampling.BILINEAR)
        atlas.paste(thumb, ((i % cols) * cell, (i // cols) * cell))
        sid = ids[i] if ids is not None else sample_id(i, split)
        index[sid] = i
        items.append({"id": sid, "name": names[i] if names is not None else sid, "kind": "image",
                      "label_idx": _label(target)})
    return atlas, {"cell": [cell, cell], "cols": cols, "index": index}, items


TEXT_CHARS = 2000  # dataset.json keeps this much of each text (the detail card shows it)


def _font(size: int) -> Any:
    from PIL import ImageFont

    try:
        return ImageFont.load_default(size=size)  # Pillow >= 10.1: scalable
    except TypeError:  # pragma: no cover - older Pillow: the fixed bitmap font
        return ImageFont.load_default()


def text_card(text: str, cell: int) -> Image.Image:
    """One text sample as a square card: the first words, wrapped, light on dark, so the image panels (stack,
    mistakes gallery, probe strip) show text where they would show a photo."""
    from PIL import ImageDraw

    size = max(8, cell // 9)
    font, pad = _font(size), max(3, cell // 16)
    card = Image.new("RGB", (cell, cell), (28, 34, 46))
    draw = ImageDraw.Draw(card)
    lines, line, width = [], "", cell - 2 * pad
    for word in " ".join(str(text).split()).split(" "):
        candidate = f"{line} {word}".strip()
        if draw.textlength(candidate, font=font) <= width:
            line = candidate
            continue
        if line:
            lines.append(line)
        line = word
        if (len(lines) + 1) * (size + 2) > cell - 2 * pad:
            break
    if line and (len(lines) + 1) * (size + 2) <= cell - 2 * pad:
        lines.append(line)
    for k, row in enumerate(lines):
        draw.text((pad, pad + k * (size + 2)), row, fill=(226, 232, 240), font=font)
    return card


def build_text(texts: Sequence[str], labels: Sequence[int], ids: Sequence[str] | None = None,
               names: Sequence[str] | None = None, split: str = "val") -> tuple[Image.Image, dict, list[dict]]:
    """Like `build` for a text dataset: a card per sample (the biggest square cell, 64 to 128 px, that fits
    them all in one 2048 atlas) and items `{id, name, kind: "text", label_idx, text}` (ML_PLAN 7.4)."""
    n = len(texts)
    max_cells = (MAX_TEXTURE // 64) ** 2
    if n > max_cells:
        warnings.warn(f"forge: atlas holds {max_cells} texts; showing the first {max_cells} of {n}")
        n = max_cells
    cols = max(1, math.ceil(math.sqrt(n)))
    cell = max(64, min(128, MAX_TEXTURE // cols))
    rows = max(1, math.ceil(n / cols))
    atlas = Image.new("RGB", (cols * cell, rows * cell))
    index: dict[str, int] = {}
    items: list[dict] = []
    for i in range(n):
        atlas.paste(text_card(texts[i], cell), ((i % cols) * cell, (i // cols) * cell))
        sid = ids[i] if ids is not None else sample_id(i, split)
        index[sid] = i
        items.append({"id": sid, "name": names[i] if names is not None else sid, "kind": "text",
                      "label_idx": _label(labels[i]), "text": str(texts[i])[:TEXT_CHARS]})
    return atlas, {"cell": [cell, cell], "cols": cols, "index": index}, items


def dataset_texts(dataset: Any) -> list[str] | None:
    """A text dataset's raw strings: `dataset.texts` (through Subset wrappers), else None (an image dataset)."""
    texts = getattr(dataset, "texts", None)
    if texts is None and isinstance(dataset, Subset):
        base_texts = dataset_texts(dataset.dataset)
        texts = None if base_texts is None else [base_texts[i] for i in dataset.indices]
    return None if texts is None else [str(t) for t in texts]
