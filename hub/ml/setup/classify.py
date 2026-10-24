"""Classifier: one LLM call -> .forge/profile.json (ML_PLAN 6.4). On a validation error, asks once more."""

from __future__ import annotations

import json
from pathlib import Path
from typing import Callable

from pydantic import ValidationError

from ..models import Profile
from .llm import LLMResult, complete_json

PROMPTS = Path(__file__).resolve().parent / "prompts"
SYSTEM = (PROMPTS / "classify.md").read_text()
CONFIDENCE_THRESHOLD = 0.6
TIMEOUT_S = 60

_nullable_str = {"anyOf": [{"type": "string"}, {"type": "null"}]}
_archetype = {"type": "string", "enum": ["image_classification", "regression", "text_classification", "generic"]}

SCHEMA = {
    "type": "object",
    "properties": {
        "archetype": _archetype,
        "confidence": {"type": "number"},
        "candidates": {"type": "array", "items": _archetype},
        "framework": {"type": "string", "enum": ["pytorch", "lightning", "sklearn", "keras", "hf_transformers", "other"]},
        "entry_points": {
            "type": "object",
            "properties": {"train": {"type": "string"}, "model": _nullable_str, "data": _nullable_str},
            "required": ["train", "model", "data"],
            "additionalProperties": False,
        },
        "train_cmd": _nullable_str,
        "classes": {"anyOf": [{"type": "array", "items": {"type": "string"}}, {"type": "null"}]},
        "target": _nullable_str,
        "input_shape": {"anyOf": [{"type": "array", "items": {"type": "integer"}}, {"type": "null"}]},
        "checkpoint": _nullable_str,
        "extra_viz": {
            "type": "array",
            "items": {"type": "object", "properties": {"viz_id": {"type": "string"}, "why": {"type": "string"}},
                      "required": ["viz_id", "why"], "additionalProperties": False},
        },
        "summary": {"type": "string"},
        "notes": {"type": "string"},
    },
    "required": ["archetype", "confidence", "candidates", "framework", "entry_points", "train_cmd", "classes",
                 "target", "input_shape", "checkpoint", "extra_viz", "summary", "notes"],
    "additionalProperties": False,
}


class SetupError(RuntimeError):
    """Setup can't continue; the message is shown to the user (and Bit)."""


def files_block(files: dict[str, str]) -> str:
    """Repo files as tagged data. Deterministic order, so the cached prefix stays identical across calls."""
    return "\n\n".join(f'<file path="{rel}">\n{text}\n</file>' for rel, text in sorted(files.items()))


def context_text(hints: dict, files: dict[str, str]) -> str:
    return (f"<prescan>\n{json.dumps(hints, indent=1, sort_keys=True)}\n</prescan>\n\n"
            f"<repo_files>\n{files_block(files)}\n</repo_files>")


def _check(data: dict, repo: Path, hints: dict) -> Profile:
    profile = Profile(**{**data, "schema": 1, "python": hints.get("python"),
                         "existing_logs": hints.get("existing_logs", [])})
    problems = []
    train = profile.entry_points.train
    if not (repo / train).is_file():
        problems.append(f"entry_points.train {train!r} is not a file in the repo")
    if profile.archetype in ("image_classification", "text_classification") and not profile.classes:
        problems.append("a classification profile needs `classes`")
    if profile.archetype == "regression" and not profile.target:
        problems.append("a regression profile needs `target`")
    if problems:
        raise ValueError("; ".join(problems))
    return profile


def classify(repo: str | Path, hints: dict, files: dict[str, str],
             llm: Callable[..., LLMResult] = complete_json) -> tuple[Profile, list[dict]]:
    """Returns (profile, usage per call). Raises SetupError if two answers in a row don't validate."""
    repo = Path(repo).resolve()
    messages = [{"role": "user", "content": [
        {"type": "text", "text": context_text(hints, files)},
        {"type": "text", "text": "Classify this repo and fill in its profile."},
    ]}]
    usage = []
    for attempt in (1, 2):
        result = llm(SYSTEM, messages, SCHEMA, timeout=TIMEOUT_S, effort="medium")
        usage.append(result.usage)
        try:
            return _check(result.data, repo, hints), usage
        except (ValidationError, ValueError) as exc:
            if attempt == 2:
                raise SetupError(f"the classifier's profile didn't validate twice: {exc}") from exc
            messages += [{"role": "assistant", "content": result.content},
                         {"role": "user", "content": f"That profile failed validation: {exc}\nFix it and answer again."}]
    raise AssertionError("unreachable")


def needs_confirmation(profile: Profile) -> bool:
    return profile.confidence < CONFIDENCE_THRESHOLD
