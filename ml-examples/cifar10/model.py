"""Small CNN for CIFAR-10: three conv blocks, then a two-layer head."""

import torch
import torch.nn as nn


def conv_block(cin: int, cout: int) -> list[nn.Module]:
    return [nn.Conv2d(cin, cout, 3, padding=1), nn.BatchNorm2d(cout), nn.ReLU(), nn.MaxPool2d(2)]


class SmallCNN(nn.Module):
    def __init__(self, num_classes: int = 10) -> None:
        super().__init__()
        self.features = nn.Sequential(*conv_block(3, 32), *conv_block(32, 64), *conv_block(64, 128), nn.Flatten())
        self.head = nn.Sequential(nn.Linear(128 * 4 * 4, 128), nn.ReLU(), nn.Linear(128, num_classes))

    def forward(self, x: torch.Tensor) -> torch.Tensor:
        return self.head(self.features(x))
