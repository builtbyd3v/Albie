"""Turn predictions into results.json, confusion.json and summary.json (shared by forge.evaluate and runners).

Shapes match hub/ml/models.py: Results / Confusion / RegressionResults / Summary.
"""

from __future__ import annotations

from typing import Sequence

import numpy as np

TOP_CONFUSIONS = 10
CONFIDENT_MISTAKES = 50
PROB_DECIMALS = 4


def classification(ids: Sequence[str], labels: Sequence[int], probs: np.ndarray, classes: Sequence[str],
                   archetype: str = "image_classification") -> tuple[dict, dict, dict]:
    """probs: [N, C] probabilities in dataset order. Returns (results, confusion, summary)."""
    probs = np.asarray(probs, dtype=np.float64)
    labels = np.asarray(labels, dtype=np.int64)
    n, c = probs.shape
    if len(ids) != n or len(labels) != n:
        raise ValueError(f"{len(ids)} ids, {len(labels)} labels, {n} prediction rows")
    if len(classes) != c:
        raise ValueError(f"{len(classes)} classes but predictions have {c} columns")
    preds = probs.argmax(axis=1)
    correct = preds == labels

    results = {"samples": [
        {"id": str(ids[i]), "true_idx": int(labels[i]), "pred_idx": int(preds[i]),
         "probs": [round(float(p), PROB_DECIMALS) for p in probs[i]], "correct": bool(correct[i])}
        for i in range(n)]}

    matrix = np.zeros((c, c), dtype=np.int64)
    np.add.at(matrix, (labels, preds), 1)
    confusion = {"labels": list(classes), "matrix": matrix.tolist()}

    per_class = []
    for k in range(c):
        count = int(matrix[k].sum())
        per_class.append({"idx": k, "name": classes[k], "count": count, "correct": int(matrix[k, k]),
                          "accuracy": round(matrix[k, k] / count, 4) if count else None})
    off = [(int(matrix[t, p]), t, p) for t in range(c) for p in range(c) if t != p and matrix[t, p]]
    top = [{"true_idx": t, "pred_idx": p, "count": cnt} for cnt, t, p in sorted(off, reverse=True)[:TOP_CONFUSIONS]]
    wrong = np.flatnonzero(~correct)
    confidence = probs[wrong, preds[wrong]]
    mistakes = [{"id": str(ids[i]), "true_idx": int(labels[i]), "pred_idx": int(preds[i]),
                 "confidence": round(float(conf), PROB_DECIMALS)}
                for i, conf in sorted(zip(wrong, confidence), key=lambda x: -x[1])[:CONFIDENT_MISTAKES]]

    summary = {"archetype": archetype, "num_samples": n, "accuracy": round(float(correct.mean()), 4) if n else None,
               "per_class": per_class, "top_confusions": top, "confident_mistakes": mistakes}
    return results, confusion, summary


def regression(ids: Sequence[str], y_true: Sequence[float], y_pred: Sequence[float]) -> tuple[dict, dict]:
    """Returns (results, summary) for a regression model."""
    y_true = np.asarray(y_true, dtype=np.float64).ravel()
    y_pred = np.asarray(y_pred, dtype=np.float64).ravel()
    if not len(ids) == len(y_true) == len(y_pred):
        raise ValueError(f"{len(ids)} ids, {len(y_true)} targets, {len(y_pred)} predictions")
    residual = y_true - y_pred
    results = {"samples": [{"id": str(i), "y_true": float(t), "y_pred": float(p), "residual": float(r)}
                           for i, t, p, r in zip(ids, y_true, y_pred, residual)]}
    ss_tot = float(((y_true - y_true.mean()) ** 2).sum())
    summary = {"archetype": "regression", "num_samples": len(y_true),
               "r2": round(1 - float((residual ** 2).sum()) / ss_tot, 4) if ss_tot else None,
               "mae": round(float(np.abs(residual).mean()), 6),
               "rmse": round(float(np.sqrt((residual ** 2).mean())), 6)}
    return results, summary
