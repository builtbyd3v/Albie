"""Constrained worker for imported repositories matching the approved CIFAR-10/SmallCNN format.

This worker deliberately selects only checked-in training code. It does not import code downloaded from GitHub.
The existing Forge CIFAR-10 trainer emits ml.metrics, ml.class_acc, ml.probe and result events through forge-sdk.
"""

from __future__ import annotations

import argparse
import runpy
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
TRAINING_REPO = ROOT / "ml-examples" / "cifar10"
FORGE_SDK = ROOT / "forge-sdk"


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--dataset", choices=("cifar10",), required=True)
    parser.add_argument("--model", choices=("smallcnn",), required=True)
    parser.add_argument("--epochs", type=int, choices=range(5, 11), required=True)
    args = parser.parse_args()
    if not (TRAINING_REPO / "train.py").is_file():
        parser.error("the approved CIFAR-10 trainer is missing")
    sys.path.insert(0, str(FORGE_SDK))
    sys.path.insert(0, str(TRAINING_REPO))
    sys.argv = [str(TRAINING_REPO / "train.py"), "--epochs", str(args.epochs)]
    runpy.run_path(str(TRAINING_REPO / "train.py"), run_name="__main__")


if __name__ == "__main__":
    main()
