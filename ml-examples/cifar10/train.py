"""Train SmallCNN on CIFAR-10 (10k-image subset by default, --full for all 50k).

    python train.py --epochs 10

Lines marked `# forge` are the patch the forge setup adds; the rest is a plain training script.
"""

from __future__ import annotations

import argparse

import torch
import torch.nn.functional as F

import forge  # forge
from data import CLASSES, HERE, get_loaders
from model import SmallCNN

CHECKPOINT = HERE / "checkpoints" / "best.pt"


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
    parser.add_argument("--full", action="store_true", help="train on all 50k images instead of 10k")
    parser.add_argument("--data", default=str(HERE / "data"))
    parser.add_argument("--batch-size", type=int, default=128)
    parser.add_argument("--lr", type=float, default=1e-3)
    parser.add_argument("--seed", type=int, default=0)
    args = parser.parse_args()

    torch.manual_seed(args.seed)
    device = "cuda" if torch.cuda.is_available() else "mps" if torch.backends.mps.is_available() else "cpu"
    train_loader, val_loader = get_loaders(args.data, args.batch_size, args.full)

    model = SmallCNN(len(CLASSES)).to(device)
    opt = torch.optim.Adam(model.parameters(), lr=args.lr)
    forge.watch(model, val_loader.dataset, classes=CLASSES, epochs=args.epochs)  # forge

    best_acc = 0.0
    for epoch in range(1, args.epochs + 1):
        train_loss, train_acc = run_epoch(model, train_loader, opt, device)
        val_loss, val_acc = run_epoch(model, val_loader, None, device)
        print(f"epoch {epoch}/{args.epochs}  train {train_loss:.3f} / {train_acc:.3f}  val {val_loss:.3f} / {val_acc:.3f}")
        forge.log(epoch=epoch, train_loss=train_loss, train_acc=train_acc, val_loss=val_loss,  # forge
                  val_acc=val_acc, lr=opt.param_groups[0]["lr"])  # forge
        if val_acc > best_acc:
            best_acc = val_acc
            CHECKPOINT.parent.mkdir(exist_ok=True)
            torch.save(model.state_dict(), CHECKPOINT)

    forge.evaluate(model, val_loader)  # forge
    print(f"best val acc {best_acc:.3f}, saved to {CHECKPOINT}")


if __name__ == "__main__":
    main()
