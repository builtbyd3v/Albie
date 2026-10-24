"""Setup flow for one repo (ML_PLAN 1, 6): scan -> classify -> (confirm type) -> adapt + check -> approve.

`emit(status, **fields)` reports each ml.project_state step; the caller turns it into /ws events and the
GET /ml/project state. A repo whose existing .forge/profile.json + adapter.py pass forge.check goes straight
to ready without any LLM call (6.8: hand-written .forge, or a repo set up before). Never trains.
"""

from __future__ import annotations

import json
import os
import sys
from pathlib import Path
from typing import Callable

from pydantic import ValidationError

from ..models import Profile
from ..prescan import prescan, read_files
from . import jev
from .adapt import adapt, run_check
from .classify import SetupError, classify, needs_confirmation
from .classify_jev import classify_jev
from .llm import LLMError, LLMResult, complete_json

Emit = Callable[..., None]


def classifier_backend() -> str:
    """FORGE_CLASSIFIER=jev|claude; by default Jev when TYPESAFE_API_KEY is set (cheap, typed), else Claude."""
    choice = os.environ.get("FORGE_CLASSIFIER", "").lower()
    if choice in ("jev", "claude"):
        return choice
    return "jev" if jev.available() else "claude"


def _forge_dir(repo: Path) -> Path:
    forge = repo / ".forge"
    forge.mkdir(exist_ok=True)
    if not (forge / ".gitignore").exists():
        (forge / ".gitignore").write_text("*\n")
    return forge


def _load_profile(path: Path) -> Profile | None:
    try:
        return Profile(**json.loads(path.read_text()))
    except (OSError, ValueError, ValidationError):
        return None


def run_setup(repo: str | Path, emit: Emit, *, force: bool = False, archetype: str | None = None,
              llm: Callable[..., LLMResult] = complete_json, check: Callable[[Path, str], dict] = run_check,
              jev_ask: Callable[..., dict] = jev.ask, backend: str | None = None) -> None:
    """`llm` writes the adapter (and classifies when the backend is claude); `jev_ask` classifies with Jev."""
    repo = Path(repo).resolve()
    try:
        _run(repo, emit, force, archetype, llm, check, jev_ask, backend or classifier_backend())
    except (LLMError, SetupError) as exc:
        emit("failed", error=str(exc))
    except Exception as exc:  # noqa: BLE001 - report every failure to the user instead of dying silently
        emit("failed", error=f"{type(exc).__name__}: {exc}")


def _run(repo: Path, emit: Emit, force: bool, archetype: str | None, llm, check, jev_ask, backend: str) -> None:
    forge = _forge_dir(repo)
    emit("scanning")
    hints = prescan(repo)
    (forge / "hints.json").write_text(json.dumps(hints, indent=1))
    python = hints["python"] or sys.executable

    existing = _load_profile(forge / "profile.json")
    if not force and archetype is None and existing and (forge / "adapter.py").exists():
        if check(repo, existing.python or python).get("ok"):
            emit("ready", archetype=existing.archetype, summary=existing.summary, profile=existing)
            return

    files = read_files(repo, hints["files_sent"])
    draft_path = forge / "profile.draft.json"
    if archetype is not None:
        draft = _load_profile(draft_path)
        if draft is None:
            raise SetupError("no classifier result to confirm; open the project again")
        profile = draft.model_copy(update={"archetype": archetype, "confidence": 1.0, "candidates": []})
    else:
        emit("classifying")
        if backend == "jev":
            profile, _ = classify_jev(repo, hints, files, jev_ask)
        else:
            profile, _ = classify(repo, hints, files, llm)
        if needs_confirmation(profile):
            draft_path.write_text(profile.model_dump_json(by_alias=True, indent=1))
            emit("confirm_type", archetype=profile.archetype, candidates=profile.candidates or [profile.archetype],
                 summary=profile.summary)
            return

    profile = profile.model_copy(update={"python": profile.python or python})
    (forge / "profile.json").write_text(profile.model_dump_json(by_alias=True, indent=1))
    draft_path.unlink(missing_ok=True)

    emit("adapting", archetype=profile.archetype, summary=profile.summary)

    def on_round(n: int) -> None:
        if n > 1:  # a retry after a failed check
            emit("checking", archetype=profile.archetype, round=n)

    result = adapt(repo, profile, files, profile.python, llm, check, on_round=on_round)
    if not result.ok:
        failed = [c for c in result.check.get("checks", []) if not c["ok"]]
        reason = "; ".join(f"{c['name']}: {c['detail'].strip().splitlines()[-1] if c['detail'].strip() else ''}"
                           for c in failed) or "forge.check failed"
        emit("failed", archetype="generic", summary=profile.summary,
             error=f"adapter still failing after {result.rounds} rounds: {reason}"[:1000])
        return
    if result.patched:
        emit("needs_approval", archetype=profile.archetype, summary=profile.summary, profile=profile,
             patch=result.patched, notes=result.notes)
    else:
        emit("ready", archetype=profile.archetype, summary=profile.summary, profile=profile, notes=result.notes)
