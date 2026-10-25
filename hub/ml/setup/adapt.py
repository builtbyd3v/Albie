"""Adapter: the LLM writes .forge/adapter.py + a patched training script; forge.check verifies (ML_PLAN 6.5).

Up to ROUNDS rounds: a failed check goes back to the LLM in the same conversation. Guardrails (6.6): the
LLM only returns text; the hub writes only inside .forge/ (the patch is a proposal in .forge/patch/, with
the original's hash so Accept can refuse if the file changed); patch targets must be existing .py files.
"""

from __future__ import annotations

import hashlib
import json
import os
import shutil
import subprocess
from dataclasses import dataclass, field
from pathlib import Path, PurePosixPath
from typing import Callable

from ..models import Profile
from .classify import files_block
from .llm import LLMResult, complete_json

PROMPTS = Path(__file__).resolve().parent / "prompts"
SYSTEM = (PROMPTS / "adapt.md").read_text()
ROOT = Path(__file__).resolve().parents[3]
FORGE_SDK = ROOT / "forge-sdk"
GOLDEN = ROOT / "ml-examples" / "cifar10"
ROUNDS = 3
TIMEOUT_S = 120
CHECK_TIMEOUT_S = 90

SCHEMA = {
    "type": "object",
    "properties": {
        "adapter_py": {"type": "string"},
        "patches": {
            "type": "array",
            "items": {"type": "object", "properties": {"path": {"type": "string"}, "content": {"type": "string"}},
                      "required": ["path", "content"], "additionalProperties": False},
        },
        "extra_viz_py": {"type": "string"},  # optional: ML_PLAN A7, only when profile.extra_viz is non-empty
        "notes": {"type": "string"},
    },
    "required": ["adapter_py", "patches", "notes"],
    "additionalProperties": False,
}


class GuardrailError(ValueError):
    pass


@dataclass
class AdaptResult:
    ok: bool
    rounds: int
    check: dict
    notes: str = ""
    patched: list[str] = field(default_factory=list)
    usage: list[dict] = field(default_factory=list)


def sha256(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def golden_example() -> str:
    """CIFAR-10's hand-written .forge as the worked example (ML_PLAN 6.5)."""
    parts = []
    for rel in (".forge/profile.json", ".forge/adapter.py", "train.py"):
        path = GOLDEN / rel
        if path.exists():
            parts.append(f'<file path="{rel}">\n{path.read_text()}\n</file>')
    return "\n\n".join(parts)


SKIP_PATCH_DIRS = (".forge", ".venv", "venv", ".git", "site-packages")  # never patched, at any depth


def safe_patch_path(repo: Path, rel: str) -> Path:
    p = PurePosixPath(rel.replace("\\", "/"))
    if (p.is_absolute() or ".." in p.parts or not p.parts or set(p.parts) & set(SKIP_PATCH_DIRS)
            or p.suffix != ".py"):
        raise GuardrailError(f"patch path {rel!r} must be a relative .py path inside the repo, outside "
                             f"{', '.join(SKIP_PATCH_DIRS)}")
    target = repo / Path(*p.parts)
    if not target.resolve().is_relative_to(Path(repo).resolve()):  # a symlink pointing out of the repo
        raise GuardrailError(f"patch path {rel!r} resolves outside the repo")
    if not target.is_file():
        raise GuardrailError(f"patch path {rel!r} is not an existing file; only existing files can be patched")
    return target


def write_outputs(repo: Path, data: dict) -> list[str]:
    """Write the adapter, the proposed patch and the extra charts inside .forge/ only.

    Returns the patched repo paths.
    """
    forge = repo / ".forge"
    forge.mkdir(exist_ok=True)
    if not (forge / ".gitignore").exists():
        (forge / ".gitignore").write_text("*\n")  # keeps all of .forge out of the user's git (ML_PLAN 6.2)
    targets = [(p["path"], safe_patch_path(repo, p["path"]), p["content"]) for p in data["patches"]]
    for rel, _, content in targets:
        try:
            compile(content, rel, "exec")
        except SyntaxError as exc:
            raise GuardrailError(f"patched {rel} doesn't parse: line {exc.lineno}: {exc.msg}") from exc
    extras = (data.get("extra_viz_py") or "").strip()
    if extras:
        try:
            compile(extras, ".forge/extra_viz.py", "exec")
        except SyntaxError as exc:
            raise GuardrailError(f"extra_viz.py doesn't parse: line {exc.lineno}: {exc.msg}") from exc
    adapter = forge / "adapter.py"
    if adapter.exists() and not (forge / "adapter.generated").exists():
        shutil.copyfile(adapter, forge / "adapter.prev.py")  # never silently replace a hand-written adapter
    adapter.write_text(data["adapter_py"])
    (forge / "adapter.generated").write_text("written by forge setup; delete to mark adapter.py hand-written\n")
    if extras:
        (forge / "extra_viz.py").write_text(extras)  # ML_PLAN A7: the classifier's project-specific charts
    patch_dir = forge / "patch"
    shutil.rmtree(patch_dir, ignore_errors=True)
    manifest = {}
    for rel, target, content in targets:
        rel = PurePosixPath(rel.replace("\\", "/")).as_posix()
        out = patch_dir / rel
        out.parent.mkdir(parents=True, exist_ok=True)
        out.write_text(content)
        manifest[rel] = {"sha256": sha256(target)}
    if manifest:
        (patch_dir / "manifest.json").write_text(json.dumps(manifest, indent=1))
    return list(manifest)


def run_check(repo: Path, python: str) -> dict:
    """`<repo python> -m forge.check <repo>` with forge-sdk on PYTHONPATH; never trains (8 samples, 60 s)."""
    env = {**os.environ, "PYTHONPATH": os.pathsep.join(filter(None, [str(FORGE_SDK), os.environ.get("PYTHONPATH")]))}
    check_file = repo / ".forge" / "check.json"
    check_file.unlink(missing_ok=True)
    try:
        proc = subprocess.run([python, "-m", "forge.check", str(repo)], cwd=repo, env=env, capture_output=True,
                              text=True, timeout=CHECK_TIMEOUT_S)
    except subprocess.TimeoutExpired:
        return {"ok": False, "checks": [{"name": "time", "ok": False, "detail": f"check ran over {CHECK_TIMEOUT_S}s"}]}
    if check_file.exists():
        return json.loads(check_file.read_text())
    return {"ok": False, "checks": [{"name": "forge.check", "ok": False,
                                     "detail": f"exit {proc.returncode}:\n{(proc.stderr or proc.stdout)[-3000:]}"}]}


def feedback(check: dict) -> str:
    failed = [c for c in check.get("checks", []) if not c["ok"]]
    lines = [f"- {c['name']}: {c['detail'][-2500:]}" for c in failed]
    return ("forge.check failed:\n" + "\n".join(lines) +
            "\nWrite corrected versions of adapter_py, extra_viz_py and patches, in full.")


def adapt(repo: str | Path, profile: Profile, files: dict[str, str], python: str,
          llm: Callable[..., LLMResult] = complete_json,
          check: Callable[[Path, str], dict] = run_check,
          on_round: Callable[[int], None] | None = None) -> AdaptResult:
    repo = Path(repo).resolve()
    adapter_src = (FORGE_SDK / "forge" / "adapter.py").read_text()
    context = (f"<forge_adapter_base path=\"forge/adapter.py\">\n{adapter_src}\n</forge_adapter_base>\n\n"
               f"<worked_example repo=\"cifar10\">\n{golden_example()}\n</worked_example>\n\n"
               f"<profile>\n{profile.model_dump_json(by_alias=True, indent=1)}\n</profile>\n\n"
               f"<repo_files>\n{files_block(files)}\n</repo_files>")
    messages: list[dict] = [{"role": "user", "content": [
        {"type": "text", "text": context},
        {"type": "text", "text": "Write the adapter and the training-script patch for this repo."},
    ]}]
    result_check: dict = {}
    usage, notes, patched = [], "", []
    for round_no in range(1, ROUNDS + 1):
        if on_round:
            on_round(round_no)
        result = llm(SYSTEM, messages, SCHEMA, timeout=TIMEOUT_S, effort="high")
        usage.append(result.usage)
        notes = result.data.get("notes", "")
        try:
            patched = write_outputs(repo, result.data)
            result_check = check(repo, python)
        except GuardrailError as exc:
            result_check = {"ok": False, "checks": [{"name": "patches", "ok": False, "detail": str(exc)}]}
        if result_check.get("ok"):
            return AdaptResult(True, round_no, result_check, notes, patched, usage)
        messages += [{"role": "assistant", "content": result.content},
                     {"role": "user", "content": feedback(result_check)}]
    return AdaptResult(False, ROUNDS, result_check, notes, patched, usage)
