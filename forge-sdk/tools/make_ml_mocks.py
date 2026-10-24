"""Generate the ML mock JSON (PROJECT_CONTEXT section 7) for the Unity panels.

Real CIFAR-10 val images and real SmallCNN layer shapes; simulated metrics and
predictions shaped to the demo script (overfit alert, weak "cat" class, ~3%
label noise the model disagrees with). Deterministic: rerunning gives the same
files and the same Unity GUIDs.

Run from repo root: python forge-sdk/tools/make_ml_mocks.py
"""

from __future__ import annotations

import argparse
import hashlib
import json
import math
import random
import sys
from pathlib import Path

import numpy as np
import torch
import torchvision

from forge import atlas as forge_atlas
from forge import graph as forge_graph

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "forge-sdk" / "examples" / "cifar10"))
from train import CLASSES, VAL_SIZE, SmallCNN  # noqa: E402
from torch.utils.data import Subset  # noqa: E402

OUT = ROOT / "unity" / "Assets" / "_Forge" / "Mocks" / "ML"
RUN_ID = "mock0001"
EPOCHS = 15
NOISE_RATE = 0.03
SEED = 7

# Per-class accuracy on clean labels; cat is the class the pet gets asked about.
CLASS_ACC = {"airplane": 0.82, "automobile": 0.88, "bird": 0.66, "cat": 0.52, "deer": 0.72,
             "dog": 0.64, "frog": 0.83, "horse": 0.80, "ship": 0.87, "truck": 0.85}
# Where wrong predictions go (weights over other classes); unlisted classes get weight 1.
CONFUSIONS = {"cat": {"dog": 12, "frog": 3, "bird": 2}, "dog": {"cat": 10, "horse": 3},
              "bird": {"airplane": 4, "deer": 3, "frog": 2}, "deer": {"horse": 4, "bird": 3},
              "automobile": {"truck": 8}, "truck": {"automobile": 8}, "airplane": {"ship": 6, "bird": 3},
              "ship": {"airplane": 6}, "horse": {"deer": 4, "dog": 3}, "frog": {"cat": 3, "bird": 2}}


def write_json(name: str, data) -> None:
    path = OUT / name
    path.write_text(json.dumps(data, indent=1) + "\n")
    write_meta(path, TEXT_META)


def guid_for(path: Path) -> str:
    return hashlib.md5(str(path.relative_to(ROOT)).encode()).hexdigest()


TEXT_META = """fileFormatVersion: 2
guid: {guid}
TextScriptImporter:
  externalObjects: {{}}
  userData:
  assetBundleName:
  assetBundleVariant:
"""

FOLDER_META = """fileFormatVersion: 2
guid: {guid}
folderAsset: yes
DefaultImporter:
  externalObjects: {{}}
  userData:
  assetBundleName:
  assetBundleVariant:
"""

# Point filter, no mips, clamp, uncompressed: atlas cells must not bleed into each other.
TEXTURE_META = """fileFormatVersion: 2
guid: {guid}
TextureImporter:
  internalIDToNameTable: []
  externalObjects: {{}}
  serializedVersion: 13
  mipmaps:
    enableMipMap: 0
  isReadable: 0
  textureSettings:
    serializedVersion: 2
    filterMode: 0
    aniso: 1
    mipBias: 0
    wrapU: 1
    wrapV: 1
    wrapW: 1
  maxTextureSize: 2048
  textureType: 0
  textureShape: 1
  platformSettings:
  - serializedVersion: 4
    buildTarget: DefaultTexturePlatform
    maxTextureSize: 2048
    textureFormat: -1
    textureCompression: 0
  userData:
  assetBundleName:
  assetBundleVariant:
"""


def write_meta(path: Path, template: str) -> None:
    Path(str(path) + ".meta").write_text(template.format(guid=guid_for(path)))


sample_id = forge_atlas.sample_id


def probs_for(pred: int, confidence: float, rng: random.Random) -> list[float]:
    """Probability vector with argmax == pred and roughly the given top probability."""
    rest = [rng.random() ** 2 for _ in CLASSES]
    rest[pred] = 0.0
    total = sum(rest) or 1.0
    probs = [(1 - confidence) * r / total for r in rest]
    probs[pred] = confidence
    probs = [round(p, 3) for p in probs]
    probs[pred] = round(1 - sum(p for k, p in enumerate(probs) if k != pred), 3)
    return probs


def wrong_class(true: int, rng: random.Random) -> int:
    weights = CONFUSIONS.get(CLASSES[true], {})
    choices = [k for k in range(len(CLASSES)) if k != true]
    return rng.choices(choices, [weights.get(CLASSES[k], 1) for k in choices])[0]


def make_results(clean_labels: list[int], rng: random.Random) -> tuple[list[dict], list[dict]]:
    noisy_idx = sorted(rng.sample(range(VAL_SIZE), round(VAL_SIZE * NOISE_RATE)))
    noise = []
    labels = list(clean_labels)
    for i in noisy_idx:
        labels[i] = wrong_class(clean_labels[i], rng)
        noise.append({"id": sample_id(i), "clean_idx": clean_labels[i], "noisy_idx": labels[i]})
    noisy = set(noisy_idx)

    samples = []
    for i in range(VAL_SIZE):
        clean = clean_labels[i]
        if i in noisy:
            # Model was right, data was wrong: confident prediction of the clean label.
            pred, conf = clean, rng.uniform(0.85, 0.97)
        elif rng.random() < CLASS_ACC[CLASSES[clean]]:
            pred, conf = clean, rng.uniform(0.55, 0.99)
        else:
            pred, conf = wrong_class(clean, rng), rng.uniform(0.35, 0.8)
        samples.append({"id": sample_id(i), "true_idx": labels[i], "pred_idx": pred,
                        "probs": probs_for(pred, conf, rng), "correct": pred == labels[i]})
    return samples, noise


def make_events(final_val_acc: float, rng: random.Random) -> list[dict]:
    """Replayable event stream: [{t, type, payload}], t in seconds from start."""
    events = []
    t = 0.0

    def add(event_type: str, payload: dict, dt: float = 0.0) -> None:
        nonlocal t
        t = round(t + dt, 2)
        events.append({"t": t, "type": event_type, "payload": payload})

    add("ml.run_state", {"run_id": RUN_ID, "status": "training", "epoch": 0, "total_epochs": EPOCHS})
    add("ml.graph_ready", {}, 0.2)
    steps_per_epoch = 391  # 50,000 / batch 128
    prev = None
    rises = 0
    for e in range(1, EPOCHS + 1):
        train_loss = 1.75 * math.exp(-0.16 * (e - 1)) + 0.12 + rng.uniform(-0.02, 0.02)
        # Val loss bottoms out at epoch 9, then climbs: overfitting.
        val_loss = 0.86 + 0.012 * (e - 9) ** 2 + (0.03 * (e - 9) if e > 9 else 0) + rng.uniform(-0.01, 0.01)
        train_acc = min(0.97, 0.38 + 0.6 * (1 - math.exp(-0.2 * e)))
        val_acc = final_val_acc + 0.01 * (EPOCHS - e) if e > 9 else 0.36 + (final_val_acc + 0.06 - 0.36) * (1 - math.exp(-0.4 * e))
        if e == EPOCHS:
            val_acc = final_val_acc
        lr = 1e-3 if e <= 10 else 5e-4
        metrics = {"run_id": RUN_ID, "epoch": e, "step": e * steps_per_epoch, "train_loss": round(train_loss, 4),
                   "train_acc": round(train_acc, 4), "val_loss": round(val_loss, 4), "val_acc": round(val_acc, 4),
                   "lr": lr}
        add("ml.run_state", {"run_id": RUN_ID, "status": "training", "epoch": e, "total_epochs": EPOCHS}, 1.5)
        add("ml.metrics", metrics, 0.05)
        # Same rule as the SDK: val loss up 2 epochs in a row while train loss down.
        if prev is not None:
            rises = rises + 1 if val_loss > prev["val_loss"] and train_loss < prev["train_loss"] else 0
            if rises == 2:
                add("ml.overfit_alert", {"run_id": RUN_ID, "epoch": e,
                                         "reason": "val loss rose 2 epochs in a row while train loss fell"}, 0.05)
        prev = metrics
    add("ml.run_state", {"run_id": RUN_ID, "status": "evaluating", "epoch": EPOCHS, "total_epochs": EPOCHS}, 0.5)
    add("ml.results_ready", {"run_id": RUN_ID}, 1.0)
    add("ml.run_state", {"run_id": RUN_ID, "status": "done", "epoch": EPOCHS, "total_epochs": EPOCHS}, 0.05)
    return events


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--data", default=str(ROOT / "forge-sdk" / "data"))
    args = parser.parse_args()

    rng = random.Random(SEED)
    OUT.mkdir(parents=True, exist_ok=True)
    write_meta(OUT, FOLDER_META)

    val = torchvision.datasets.CIFAR10(args.data, train=False, download=True)
    clean_labels = [int(val.targets[i]) for i in range(VAL_SIZE)]

    write_json("ml_classes.json", CLASSES)
    # Same code paths forge.watch uses, so mocks and live runs can't drift apart.
    write_json("ml_graph.json", forge_graph.describe(SmallCNN(), torch.zeros(3, 32, 32)))

    image, atlas_meta, _ = forge_atlas.build(Subset(val, range(VAL_SIZE)))
    atlas_path = OUT / "ml_atlas_val.png"
    image.save(atlas_path)
    write_meta(atlas_path, TEXTURE_META)
    write_json("ml_atlas_val.json", {"atlas_url": "/ml/files/ml_atlas_val.png", **atlas_meta})

    samples, noise = make_results(clean_labels, rng)
    # Dataset shows the (possibly noisy) labels the model was trained against.
    write_json("ml_dataset_val.json", [{"id": s["id"], "label_idx": s["true_idx"],
                                        "thumb_url": f"/ml/image/{s['id']}"} for s in samples])
    write_json("ml_results.json", {"samples": samples})
    write_json("ml_noise.json", {"seed": SEED, "rate": NOISE_RATE, "flipped": noise})

    matrix = np.zeros((len(CLASSES), len(CLASSES)), dtype=int)
    for s in samples:
        matrix[s["true_idx"], s["pred_idx"]] += 1
    write_json("ml_confusion.json", {"labels": CLASSES, "matrix": matrix.tolist()})

    val_acc = sum(s["correct"] for s in samples) / VAL_SIZE
    write_json("ml_events.json", make_events(val_acc, rng))

    print(f"wrote mocks to {OUT.relative_to(ROOT)}: val_acc={val_acc:.3f}, flipped={len(noise)}")


if __name__ == "__main__":
    main()
