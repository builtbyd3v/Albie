"""Hand-written extra charts for the CIFAR-10 golden example (ML_PLAN A7).

The setup step normally has an LLM write this file from `profile.extra_viz`. CIFAR-10 keeps a hand-written
copy so the feature is demoable without an LLM key and is the worked example the adapter prompt shows.

Each function is named `viz_<viz_id>` and receives `forge.viz.VizContext`. It runs once at the end of a
run, inside the training process, after evaluation: return a matplotlib figure (saved as a PNG) or a
VizCard dict (ML_PLAN 7.6). Import matplotlib inside the function, so a repo without it loses only that
chart instead of the whole file.
"""

from __future__ import annotations

import json
from pathlib import Path

REPO = Path(__file__).resolve().parents[1]


def _noisy_val_ids() -> set[str]:
    """The val samples whose label was flipped on purpose (noise.json, ML_PLAN 4.3)."""
    try:
        noise = json.loads((REPO / "noise.json").read_text())
        return {str(v["id"]) for v in noise.get("val", [])}
    except (OSError, ValueError, KeyError, TypeError):
        return set()


def viz_noise_accuracy(ctx):
    """The demo's point: the model is right about most of the images whose labels were flipped."""
    noisy = _noisy_val_ids()
    predicted = ctx.predicted()
    buckets = {"clean": [0, 0], "flipped": [0, 0]}  # [seen, correct]
    for i, sample_id in enumerate(ctx.ids):
        bucket = buckets["flipped" if sample_id in noisy else "clean"]
        bucket[0] += 1
        bucket[1] += int(predicted[i] == ctx.labels[i])
    accuracy = [correct / seen if seen else 0.0 for seen, correct in buckets.values()]
    return {
        "kind": "bar",
        "title": "Model vs the flipped labels",
        "subtitle": f"{buckets['flipped'][0]} of {len(ctx.ids)} validation labels were flipped on purpose",
        "x": ["other val images", "flipped labels"],
        "series": [{"name": "accuracy", "y": [round(v, 4) for v in accuracy]}],
    }


def viz_confidence_split(ctx):
    """Wrong predictions are usually very confident: that is where a bad label hides."""
    import matplotlib.pyplot as plt
    import numpy as np

    confidence = np.asarray(ctx.probs).max(axis=1)
    predicted = np.asarray(ctx.predicted())
    correct = predicted == np.asarray(ctx.labels)
    bins = np.linspace(0.0, 1.0, 11)
    fig, ax = plt.subplots(figsize=(5.2, 2.6), dpi=110)
    ax.hist(confidence[correct], bins=bins, alpha=.8, color="#22CF86", label="correct")
    ax.hist(confidence[~correct], bins=bins, alpha=.8, color="#FF6B4A", label="wrong")
    ax.set_xlabel("confidence in the predicted class")
    ax.set_ylabel("validation images")
    ax.set_title("How sure the model is when it is wrong")
    ax.legend(frameon=False, fontsize=8)
    ax.spines[["top", "right"]].set_visible(False)
    fig.tight_layout()
    return fig
