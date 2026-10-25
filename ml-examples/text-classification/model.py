from __future__ import annotations

import torch
import torch.nn as nn


class MeanPool(nn.Module):
    """Average of the word vectors, skipping padding (id 0)."""

    def forward(self, vectors: torch.Tensor, ids: torch.Tensor) -> torch.Tensor:
        mask = (ids != 0).unsqueeze(-1).float()
        return (vectors * mask).sum(1) / mask.sum(1).clamp(min=1)


class BagOfWords(nn.Module):
    """Word embeddings, averaged, then a small MLP: a fastText-style classifier that trains in seconds on a CPU."""

    def __init__(self, vocab_size: int, num_classes: int, dim: int = 64, hidden: int = 64) -> None:
        super().__init__()
        self.embedding = nn.Embedding(vocab_size, dim, padding_idx=0)
        self.pool = MeanPool()
        self.head = nn.Sequential(nn.Linear(dim, hidden), nn.ReLU(), nn.Dropout(0.2), nn.Linear(hidden, num_classes))

    def forward(self, ids: torch.Tensor) -> torch.Tensor:
        return self.head(self.pool(self.embedding(ids), ids))
