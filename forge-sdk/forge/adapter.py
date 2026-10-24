"""`forge.Adapter`: loads one repo's model and data for the forge runners (ML_PLAN 7.2).

Each repo gets a `.forge/adapter.py` with one `Adapter` subclass, hand-written or LLM-written.
It imports the repo's own model and data code rather than copying it.
"""

from __future__ import annotations

import importlib.util
import inspect
import sys
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any

import numpy as np

ARCHETYPES = ("image_classification", "regression", "text_classification", "generic")


@dataclass
class Sample:
    id: str                       # stable across calls and runs
    item: Any                     # PIL.Image | str | dict
    label: int | float | None
    name: str                     # file name, for the picked-image card
    meta: dict = field(default_factory=dict)


class Adapter:
    archetype: str = "generic"

    def build_model(self) -> Any:
        """Untrained model, for the 3D model before any training."""
        raise NotImplementedError

    def load_model(self) -> Any | None:
        """Trained model from the latest checkpoint, or None."""
        return None

    def example_input(self) -> Any | None:
        """One batch the model accepts (graph + shapes). None for sklearn."""
        return None

    def classes(self) -> list[str]:
        """Classification only."""
        return []

    def samples(self, split: str = "val", limit: int = 1000) -> list[Sample]:
        raise NotImplementedError

    def predict(self, model: Any, items: list[Any]) -> np.ndarray:
        """[N, C] probabilities, or [N] values for regression."""
        raise NotImplementedError

    def features(self, model: Any, items: list[Any]) -> np.ndarray | None:
        """[N, D] penultimate-layer features; optional (embedding cloud)."""
        return None


def load(repo: str | Path) -> Adapter:
    """Import `<repo>/.forge/adapter.py` (with the repo on sys.path) and instantiate its Adapter subclass."""
    repo = Path(repo).resolve()
    path = repo / ".forge" / "adapter.py"
    if not path.exists():
        raise FileNotFoundError(f"no adapter at {path}")
    if str(repo) not in sys.path:
        sys.path.insert(0, str(repo))
    spec = importlib.util.spec_from_file_location("forge_repo_adapter", path)
    assert spec and spec.loader
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    found = [c for _, c in inspect.getmembers(module, inspect.isclass)
             if issubclass(c, Adapter) and c is not Adapter and c.__module__ == module.__name__]
    if len(found) != 1:
        raise TypeError(f"{path} must define exactly one Adapter subclass, found {len(found)}")
    return found[0]()
