"""Project registry: hub/config/projects.yaml -> ProjectConfig.

The resolved filesystem path stays server-side; clients only ever see project ids.
"""

from __future__ import annotations

import logging
import os
from functools import lru_cache
from pathlib import Path
from typing import Literal

import yaml
from pydantic import BaseModel, Field

log = logging.getLogger(__name__)

HUB_DIR = Path(__file__).resolve().parent.parent
DEFAULT_PROJECTS_FILE = HUB_DIR / "config" / "projects.yaml"


class ProjectConfig(BaseModel):
    id: str
    path: Path  # absolute, expanded; never serialize to clients
    default_mode: Literal["qa", "code", "auto"] = "qa"

    @property
    def exists(self) -> bool:
        return self.path.is_dir()

    @property
    def is_git(self) -> bool:
        return (self.path / ".git").exists()


class PublicProject(BaseModel):
    """What GET /agent/projects returns (no paths)."""

    id: str
    default_mode: str
    available: bool
    git: bool


class _RawProject(BaseModel):
    path: str
    default_mode: Literal["qa", "code", "auto"] = "qa"


class _RawFile(BaseModel):
    projects: dict[str, _RawProject] = Field(default_factory=dict)


def projects_file() -> Path:
    raw = os.getenv("PROJECTS_FILE")
    if not raw:
        return DEFAULT_PROJECTS_FILE
    p = Path(raw).expanduser()
    return p if p.is_absolute() else HUB_DIR / p


@lru_cache(maxsize=1)
def load_projects() -> dict[str, ProjectConfig]:
    """Parse projects.yaml once. Missing repo paths are logged, not fatal."""
    path = projects_file()
    data = yaml.safe_load(path.read_text()) or {}
    parsed = _RawFile.model_validate(data)
    out: dict[str, ProjectConfig] = {}
    for pid, raw in parsed.projects.items():
        cfg = ProjectConfig(
            id=pid, path=Path(raw.path).expanduser().resolve(), default_mode=raw.default_mode
        )
        if not cfg.exists:
            log.warning("project %s: path does not exist (%s)", pid, cfg.path)
        out[pid] = cfg
    log.info("loaded %d project(s) from %s", len(out), path)
    return out


class ProjectNotFound(Exception):
    """Raised with a client-safe message."""


def resolve_project(project_id: str | None) -> ProjectConfig:
    """project_id (or DEFAULT_PROJECT, or the first entry) -> validated ProjectConfig."""
    projects = load_projects()
    pid = project_id or os.getenv("DEFAULT_PROJECT") or next(iter(projects), None)
    if pid is None or pid not in projects:
        raise ProjectNotFound(f"Unknown project '{pid}'.")
    cfg = projects[pid]
    if not cfg.exists:
        raise ProjectNotFound(f"Project '{pid}' is not available on this hub.")
    return cfg


def public_projects() -> list[PublicProject]:
    return [
        PublicProject(id=p.id, default_mode=p.default_mode, available=p.exists, git=p.is_git)
        for p in load_projects().values()
    ]
