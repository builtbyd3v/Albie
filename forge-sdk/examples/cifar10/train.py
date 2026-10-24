"""CIFAR-10 demo: small CNN, 1,000-image val subset, ~3% injected label noise.

Run: FORGE_OFFLINE=1 python forge-sdk/examples/cifar10/train.py --epochs 2
"""

from __future__ import annotations

import argparse

import torch
import torch.nn as nn
import torch.nn.functional as F
import torchvision
import torchvision.transforms as T
from torch.utils.data import DataLoader, Subset

import forge

CLASSES = ["airplane", "automobile", "bird", "cat", "deer", "dog", "frog", "horse", "ship", "truck"]
VAL_SIZE = 1000


class SmallCNN(nn.Module):
    """Every layer is a module (no F.relu) so forward hooks see it and Model3D shows a block for it."""

    def __init__(self) -> None:
        super().__init__()
        self.net = nn.Sequential(
            nn.Conv2d(3, 32, 3, padding=1),
            nn.BatchNorm2d(32),
            nn.ReLU(),
            nn.MaxPool2d(2),
            nn.Conv2d(32, 64, 3, padding=1),
            nn.BatchNorm2d(64),
            nn.ReLU(),
            nn.MaxPool2d(2),
            nn.Flatten(),
            nn.Linear(64 * 8 * 8, 128),
            nn.ReLU(),
            nn.Linear(128, 10),
        )

    def forward(self, x: torch.Tensor) -> torch.Tensor:
        return self.net(x)


def run_epoch(model, loader, opt, device) -> tuple[float, float]:
    training = opt is not None
    model.train(training)
    total_loss, correct, n = 0.0, 0, 0
    with torch.set_grad_enabled(training):
        for x, y in loader:
            x, y = x.to(device), y.to(device)
            out = model(x)
            loss = F.cross_entropy(out, y)
            if training:
                opt.zero_grad()
                loss.backward()
                opt.step()
            total_loss += loss.item() * len(y)
            correct += (out.argmax(1) == y).sum().item()
            n += len(y)
    return total_loss / n, correct / n


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--epochs", type=int, default=10)
    parser.add_argument("--data", default="forge-sdk/data")
    parser.add_argument("--runs", default="forge-sdk/runs")
    args = parser.parse_args()

    device = "mps" if torch.backends.mps.is_available() else "cpu"
    tf = T.Compose([T.ToTensor(), T.Normalize((0.5,) * 3, (0.5,) * 3)])
    train_set = torchvision.datasets.CIFAR10(args.data, train=True, download=True, transform=tf)
    test_set = torchvision.datasets.CIFAR10(args.data, train=False, download=True, transform=tf)
    val_set = Subset(test_set, range(VAL_SIZE))
    # TODO: inject ~3% label noise into val_set (seeded), save the flipped ids to demo/fallback/noise.json
    # TODO: apply labels_override.json (relabel/remove from the hub) before training

    train_loader = DataLoader(train_set, batch_size=128, shuffle=True)
    val_loader = DataLoader(val_set, batch_size=256)

    model = SmallCNN().to(device)
    opt = torch.optim.Adam(model.parameters(), lr=1e-3)

    forge.watch(model, val_set, classes=CLASSES, epochs=args.epochs, run_dir=args.runs)
    for e in range(1, args.epochs + 1):
        train_loss, train_acc = run_epoch(model, train_loader, opt, device)
        val_loss, val_acc = run_epoch(model, val_loader, None, device)
        forge.log(epoch=e, train_loss=train_loss, train_acc=train_acc, val_loss=val_loss, val_acc=val_acc,
                  lr=opt.param_groups[0]["lr"])
    forge.evaluate(model, val_loader)


if __name__ == "__main__":
    main()
