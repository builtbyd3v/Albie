"""Train SmallCNN on CIFAR-10 (10k-image subset by default, --full for all 50k).

    python train.py --epochs 10 --full

Improved by Albie: random crops + flips, AdamW with weight decay, a one-cycle learning rate and label
smoothing (the first run overfit: train accuracy far above validation).

Lines marked `# forge` are the patch the forge setup adds; the rest is a plain training script.
"""

from __future__ import annotations

import argparse
import json

import torch
import torch.nn.functional as F

import forge  # forge
from data import CLASSES, HERE, get_loaders
from model import SmallCNN

CHECKPOINT = HERE / "checkpoints" / "best.pt"
BEST = CHECKPOINT.with_suffix(".json")  # val acc of best.pt, so a weaker run never replaces it
LABEL_SMOOTHING = 0.1


def saved_best_acc() -> float:
    try:
        return float(json.loads(BEST.read_text())["val_acc"])
    except (OSError, ValueError, KeyError, TypeError):
        return 0.0


def augment(x: torch.Tensor) -> torch.Tensor:
    """Random crop (4 px reflect padding) and horizontal flip, on the device so loading stays fast."""
    x = F.pad(x, (4, 4, 4, 4), mode="reflect")
    i, j = torch.randint(0, 9, (2,)).tolist()
    x = x[:, :, i:i + 32, j:j + 32]
    flip = torch.rand(x.size(0), device=x.device) < 0.5
    return torch.where(flip[:, None, None, None], x.flip(3), x)


def run_epoch(model, loader, opt, device, sched=None) -> tuple[float, float]:
    training = opt is not None
    model.train(training)
    total_loss, correct, n = 0.0, 0, 0
    with torch.set_grad_enabled(training):
        for x, y in loader:
            x, y = x.to(device), y.to(device)
            out = model(augment(x) if training else x)
            loss = F.cross_entropy(out, y, label_smoothing=LABEL_SMOOTHING if training else 0.0)
            if training:
                opt.zero_grad()
                loss.backward()
                opt.step()
                sched.step()
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
    parser.add_argument("--lr", type=float, default=3e-3, help="peak of the one-cycle schedule")
    parser.add_argument("--weight-decay", type=float, default=5e-4)
    parser.add_argument("--seed", type=int, default=0)
    args = parser.parse_args()

    torch.manual_seed(args.seed)
    device = "cuda" if torch.cuda.is_available() else "mps" if torch.backends.mps.is_available() else "cpu"
    train_loader, val_loader = get_loaders(args.data, args.batch_size, args.full)

    model = SmallCNN(len(CLASSES)).to(device)
    opt = torch.optim.AdamW(model.parameters(), lr=args.lr, weight_decay=args.weight_decay)
    sched = torch.optim.lr_scheduler.OneCycleLR(opt, args.lr, epochs=args.epochs, steps_per_epoch=len(train_loader))
    forge.watch(model, val_loader.dataset, classes=CLASSES, epochs=args.epochs)  # forge

    best_acc = run_best = 0.0
    kept_acc = saved_best_acc()  # best.pt from earlier runs: only a better model replaces it
    for epoch in range(1, args.epochs + 1):
        train_loss, train_acc = run_epoch(model, train_loader, opt, device, sched)
        val_loss, val_acc = run_epoch(model, val_loader, None, device)
        print(f"epoch {epoch}/{args.epochs}  train {train_loss:.3f} / {train_acc:.3f}  val {val_loss:.3f} / {val_acc:.3f}")
        forge.log(epoch=epoch, train_loss=train_loss, train_acc=train_acc, val_loss=val_loss,  # forge
                  val_acc=val_acc, lr=opt.param_groups[0]["lr"], model=model)  # forge
        run_best = max(run_best, val_acc)
        if val_acc > max(best_acc, kept_acc):
            best_acc = val_acc
            CHECKPOINT.parent.mkdir(exist_ok=True)
            torch.save(model.state_dict(), CHECKPOINT)
            BEST.write_text(json.dumps({"val_acc": val_acc, "epoch": epoch, "epochs": args.epochs}))

    forge.evaluate(model, val_loader)  # forge
    if best_acc:
        print(f"best val acc {best_acc:.3f}, saved to {CHECKPOINT}")
    else:
        print(f"best val acc {run_best:.3f}; kept {CHECKPOINT} ({kept_acc:.3f} from an earlier run)")


if __name__ == "__main__":
    main()
