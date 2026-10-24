"""Pydantic models for the ML REST contract (PROJECT_CONTEXT section 7)."""

from __future__ import annotations

from typing import Any

from pydantic import BaseModel


class DatasetItem(BaseModel):
    id: str
    label_idx: int
    thumb_url: str


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


class Results(BaseModel):
    samples: list[Sample]


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
