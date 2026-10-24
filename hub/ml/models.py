"""Pydantic models for the ML REST contract (PROJECT_CONTEXT section 7, ML_PLAN 7.4)."""

from __future__ import annotations

from typing import Any, Literal

from pydantic import BaseModel


class DatasetItem(BaseModel):
    """GET /ml/dataset item. ML_PLAN 7.4: `name` + `kind`; the rest depends on the kind."""
    id: str
    name: str
    kind: Literal["image", "text", "row"]
    label_idx: int | None = None      # classification
    y: float | None = None            # regression target
    thumb_url: str | None = None      # images; the hub adds it
    text: str | None = None           # text
    removed: bool = False             # proposed: marked Remove (stays listed so the dot keeps its marker)


class Atlas(BaseModel):
    atlas_url: str
    cell: list[int]
    cols: int
    index: dict[str, int]


class Layer(BaseModel):
    name: str
    type: str
    in_shape: list[int]
    out_shape: list[int]
    params: int


class Graph(BaseModel):
    layers: list[Layer]


class TrainRequest(BaseModel):
    epochs: int


class TrainResponse(BaseModel):
    run_id: str


class Sample(BaseModel):
    id: str
    true_idx: int
    pred_idx: int
    probs: list[float]
    correct: bool
    relabeled: bool = False           # proposed: true_idx comes from a Relabel; correct is recomputed
    removed: bool = False             # proposed: marked Remove


class Results(BaseModel):
    samples: list[Sample]


class RegressionSample(BaseModel):
    id: str
    y_true: float
    y_pred: float
    residual: float


class RegressionResults(BaseModel):
    samples: list[RegressionSample]


class ClassStat(BaseModel):
    idx: int
    name: str
    count: int
    correct: int
    accuracy: float | None             # None when the class has no samples


class ConfusedPair(BaseModel):
    true_idx: int
    pred_idx: int
    count: int


class ConfidentMistake(BaseModel):
    id: str
    true_idx: int
    pred_idx: int
    confidence: float


class Summary(BaseModel):
    """summary.json (proposed): the results panel and the dot wall's option buttons (ML_PLAN 5.3)."""
    archetype: str
    num_samples: int
    accuracy: float | None = None
    val_loss: float | None = None
    best_epoch: int | None = None
    best_val_acc: float | None = None
    per_class: list[ClassStat] = []
    top_confusions: list[ConfusedPair] = []
    confident_mistakes: list[ConfidentMistake] = []
    r2: float | None = None            # regression
    mae: float | None = None
    rmse: float | None = None


class Confusion(BaseModel):
    labels: list[str]
    matrix: list[list[int]]


class RelabelRequest(BaseModel):
    label_idx: int


class IngestEvent(BaseModel):
    type: str
    payload: dict[str, Any]


class IngestRequest(BaseModel):
    """POST /ml/ingest (ML_PLAN 7.4): a batch of events from the forge SDK or forge.replay."""
    run_id: str
    events: list[IngestEvent]


class RunInfo(BaseModel):
    """GET /ml/runs item (ML_PLAN 7.4)."""
    run_id: str
    started_at: float | None = None
    status: str                       # training | done | incomplete
    epochs: int | None = None
    best_metric: float | None = None  # best val acc (classification) or R2 (regression)


class SampleDetail(BaseModel):
    """GET /ml/sample/{id} (ML_PLAN 7.4): the picked-image / detail card."""
    id: str
    name: str
    split: str
    label_idx: int | None = None
    y: float | None = None
    meta: dict[str, Any] = {}
    pred_idx: int | None = None
    probs: list[float] | None = None
    relabeled: bool = False
    removed: bool = False


class ReplayRequest(BaseModel):
    run_id: str
    speed: float = 1.0


class SampleUpdated(BaseModel):
    """ml.sample_updated event (PROJECT_CONTEXT section 7)."""
    id: str
    label_idx: int
    removed: bool
