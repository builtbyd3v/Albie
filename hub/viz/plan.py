"""Pick Forge panels for a repo: digest the folder, ask TypeSafe Jev typed questions, keep the confident yeses.

Jev cannot read files, so the digest (file tree + truncated source) is the `state`. Docs: docs.typesafe.ai/api.
Env: TYPESAFE_API_KEY.
"""
from __future__ import annotations

import json
import os
import urllib.request
from dataclasses import dataclass
from pathlib import Path
from typing import Any

from events import VizPlan

JEV_URL = "https://api.typesafe.ai/v1/systemone"
JEV_MODEL = "jev-1.14.0"
LONG_CONTEXT_HEADER = "TypeSafe-Danger-Zone-Long-Context"  # experimental: 64k -> 262k tokens

SKIP_DIRS = {"node_modules", "venv", "__pycache__", "Library", "Temp", "Logs", "obj", "Build", "Builds", "build",
             "dist", "target", "out", "UserSettings", "data", "datasets", "checkpoints"}
TEXT_EXT = {".py", ".ipynb", ".ts", ".tsx", ".js", ".cs", ".scad", ".md", ".toml", ".txt", ".yaml", ".yml",
            ".json", ".html", ".cfg"}
ENTRY_EXT = {".py", ".ipynb", ".ts", ".js"}
MAX_FILE_CHARS = 6_000
MAX_FILE_BYTES = 2_000_000
MAX_TOTAL_CHARS = 800_000       # about 200k tokens, under the 262k long-context limit
DEFAULT_LIMIT_TOKENS = 60_000   # under Jev's 64k default, leaves room for the questions
THRESHOLD = 0.7

# Panel IDs from PROJECT_CONTEXT section 7. Each asks for evidence in code, not plans in docs:
# "would this panel be useful" scored everything about 0.5 when the docs described unbuilt panels.
PANELS = {
    "ml-training": "a training loop that runs over epochs or steps and computes a loss",
    "ml-model3d": "a neural network architecture definition (torch nn.Module subclass, Keras layers)",
    "ml-datastack": "code that loads an image or sample dataset (Dataset class, DataLoader, torchvision.datasets)",
    "ml-results": "code that evaluates a classifier on a validation or test set (predictions vs labels)",
    "cad-viewer": "code that builds 3D geometry (build123d, CadQuery, OpenSCAD) or exports meshes",
}
REPO_KINDS = {
    "ml_training": "Trains or fine-tunes ML models.",
    "cad": "Generates mechanical / CAD geometry.",
    "xr_app": "A Unity or XR application.",
    "web_app": "A web frontend or backend service.",
    "data_pipeline": "ETL, analytics, or notebooks over tabular data.",
    "other": "None of the above.",
}


@dataclass
class Digest:
    state: str
    entries: list[str]


def digest(root: Path) -> Digest:
    tree, bodies, entries, total = [], [], [], 0
    for dirpath, dirnames, filenames in os.walk(root):
        dirnames[:] = sorted(d for d in dirnames if d not in SKIP_DIRS and not d.startswith("."))
        for name in sorted(filenames):
            p = Path(dirpath) / name
            rel = p.relative_to(root).as_posix()
            tree.append(rel)
            if p.suffix not in TEXT_EXT or p.stat().st_size > MAX_FILE_BYTES or total > MAX_TOTAL_CHARS:
                continue
            text = p.read_text(errors="ignore")[:MAX_FILE_CHARS]
            bodies.append(f"\n=== {rel} ===\n{text}")
            total += len(text)
            if p.suffix in ENTRY_EXT:
                entries.append(rel)
    state = "FILE TREE:\n" + "\n".join(tree[:3000]) + "\n\nFILES:" + "".join(bodies)
    return Digest(state[:MAX_TOTAL_CHARS], sorted(entries)[:255])  # choice criteria cap is 255


def build_request(d: Digest) -> tuple[dict[str, Any], dict[str, str]]:
    questions: dict[str, Any] = {
        "repo_kind": {"type": "choice", "instructions": "What kind of project is this repo?", "criteria": REPO_KINDS},
    }
    for pid, evidence in PANELS.items():
        questions[pid.replace("-", "_")] = {
            "type": "noul",
            "instructions": f"The source code files (.py, .ts, .js, .cs, .scad, .ipynb) in `state` contain {evidence}.",
            "criteria": {"true": "Real, runnable code for this exists in the repo.",
                         "false": "It is absent, or only described in docs/markdown as a plan."},
        }
    if d.entries:
        questions["entry_file"] = {"type": "choice",
                                   "instructions": "Which file is the main entry point a developer runs?",
                                   "criteria": {e: e for e in d.entries}}
    body = {"model": JEV_MODEL, "state": d.state, "questions": questions}
    est_tokens = len(json.dumps(body)) // 4  # ponytail: 4 chars per token, no tokenizer
    return body, {LONG_CONTEXT_HEADER: "true"} if est_tokens > DEFAULT_LIMIT_TOKENS else {}


def call_jev(body: dict[str, Any], headers: dict[str, str]) -> dict[str, Any]:
    req = urllib.request.Request(JEV_URL, data=json.dumps(body).encode(), headers={
        **headers, "Content-Type": "application/json",
        "Authorization": f"Bearer {os.environ['TYPESAFE_API_KEY']}"})
    with urllib.request.urlopen(req, timeout=60) as r:
        return json.load(r)


def parse(repo_path: str, res: dict[str, Any]) -> VizPlan:
    a = res["answers"]
    scores = {pid: a[pid.replace("-", "_")]["noul"] for pid in PANELS}
    return VizPlan(
        repo_path=repo_path,
        repo_kind=a["repo_kind"]["choice"],
        entry_file=a.get("entry_file", {}).get("choice"),
        panels=sorted((pid for pid, s in scores.items() if s >= THRESHOLD), key=lambda pid: -scores[pid]),
        scores=scores,
        model=res["model"],
    )
