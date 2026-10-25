"""Albie improves the model: read the last run, propose a training-script change, apply it on a yes, retrain.

    diagnose(svc)   the latest run's numbers (accuracy, train/val gap, weakest classes, seconds per epoch)
    propose(svc)    Claude writes the change into .forge/improve/ (nothing in the repo changes yet)
    apply(svc)      copies it into the repo (keeps <file>.orig) and adds its flags to the profile's train_cmd
    undo(svc)       puts the originals and the old train_cmd back, and sets aside the runs trained with the change
                    (.forge/runs-undone/), so the desk and the next rehearsal start from the original score

Same guardrails as setup (ML_PLAN 6.6): the model only returns text, the hub writes only inside .forge/ until
the user says yes, patch targets must be existing .py files, every `# forge` line survives, and apply refuses
a file that changed after the proposal was written.

The golden CIFAR-10 example has a scripted change (improve_demo/cifar10/train.py: crops + flips, AdamW, one-cycle
LR, label smoothing, all 50k images; 60% -> 81% in about 90 s). FORGE_ML_IMPROVE picks the source:
auto (default) = Claude, the scripted change when Claude fails; scripted = the scripted change when the repo is
the golden example; llm = Claude only.
"""

from __future__ import annotations

import json
import os
import shlex
import shutil
import time
from pathlib import Path, PurePosixPath
from typing import Any, Callable

from .prescan import prescan, read_files
from .setup.adapt import GOLDEN, GuardrailError, safe_patch_path, sha256
from .setup.classify import files_block
from .setup.llm import LLMError, LLMResult, complete_json

PROMPT = Path(__file__).resolve().parent / "setup" / "prompts" / "improve.md"
DEMO = Path(__file__).resolve().parent / "improve_demo" / "cifar10"
TIMEOUT_S = 150
ROUNDS = 2
BUDGET_S = 120  # the improved run should finish in about two minutes: the demo watches it live

SCHEMA = {
    "type": "object",
    "properties": {
        "diagnosis": {"type": "string"},
        "changes": {"type": "array", "items": {
            "type": "object", "properties": {"title": {"type": "string"}, "why": {"type": "string"}},
            "required": ["title", "why"], "additionalProperties": False}},
        "patches": {"type": "array", "items": {
            "type": "object", "properties": {"path": {"type": "string"}, "content": {"type": "string"}},
            "required": ["path", "content"], "additionalProperties": False}},
        "train_args": {"type": "array", "items": {"type": "string"}},
        "epochs": {"type": "integer"},
        "expected": {"type": "string"},
    },
    "required": ["diagnosis", "changes", "patches", "train_args", "epochs", "expected"],
    "additionalProperties": False,
}


class ImproveError(RuntimeError):
    """Nothing to improve yet, no usable proposal, or apply/undo can't run. Safe to say to the user."""


def _dir(svc) -> Path:
    return svc.forge_dir / "improve"


def _read(path: Path) -> dict:
    return json.loads(path.read_text()) if path.exists() else {}


def plan(svc) -> dict:
    """The current proposal (.forge/improve/plan.json), or {}."""
    return _read(_dir(svc) / "plan.json")


# --- diagnose ---------------------------------------------------------------------------------------

def latest_run(svc) -> str | None:
    """The newest run with curves: a finished one first, else any with events."""
    runs = svc.runs()
    for r in runs:
        if r["status"] == "done":
            return r["run_id"]
    for r in runs:
        if (svc.run_dir(r["run_id"]) / "events.jsonl").exists():
            return r["run_id"]
    return None


def _num(value: Any) -> float | None:
    return float(value) if isinstance(value, (int, float)) else None


def diagnose(svc, run_id: str | None = None) -> dict:
    run_id = run_id or latest_run(svc)
    if run_id is None:
        raise ImproveError("there's no training run yet: train once, then I can look at it")
    history = svc.metrics_history(run_id)
    ticks = history["metrics"]
    if not ticks:
        raise ImproveError(f"run {run_id} has no metrics yet")
    last = ticks[-1]
    vals = [v for v in (_num(t.get("val_acc")) for t in ticks) if v is not None]
    out: dict[str, Any] = {"run_id": run_id, "epochs": len(ticks), "train_cmd": svc.profile().get("train_cmd"),
                           "archetype": svc.profile().get("archetype")}
    for key in ("train_loss", "val_loss", "train_acc", "val_acc"):
        if _num(last.get(key)) is not None:
            out[key] = round(float(last[key]), 4)
    if vals:
        out["best_val_acc"] = round(max(vals), 4)
    if "train_acc" in out and "val_acc" in out:
        out["train_val_gap"] = round(out["train_acc"] - out["val_acc"], 4)
    if history.get("overfit"):
        out["overfit"] = history["overfit"].get("reason") or True
    extra = {k: v for k, v in (last.get("extra") or {}).items() if _num(v) is not None}
    if extra:
        out["extra"] = extra
    events = [e for e in svc.replay_events(run_id) if e.get("type") == "ml.metrics"]
    if len(events) > 1:
        out["seconds_per_epoch"] = round((events[-1]["t"] - events[0]["t"]) / (len(events) - 1), 1)
    classes = svc.profile().get("classes") or []
    if history["class_acc"] and classes:
        per_class = history["class_acc"][-1].get("per_class") or []
        ranked = sorted(zip(classes, per_class), key=lambda p: p[1])
        out["weakest_classes"] = [{"class": c, "acc": round(a, 3)} for c, a in ranked[:3]]
    summary_path = svc.run_dir(run_id) / "summary.json"
    if summary_path.exists():
        summary = _read(summary_path)
        out["top_confusions"] = summary.get("top_confusions", [])[:3]
        out["confident_mistakes"] = len(summary.get("confident_mistakes", []))
        for key in ("r2", "mae", "rmse"):
            if summary.get(key) is not None:
                out[key] = summary[key]
    return out


def headline(d: dict) -> str:
    """'60.5% validation accuracy after 10 epochs' or the run's own metrics."""
    if "best_val_acc" in d:
        return f"{d['best_val_acc']:.1%} validation accuracy after {d['epochs']} epochs"
    if d.get("r2") is not None:
        return f"R squared {d['r2']:.2f} after {d['epochs']} epochs"
    if d.get("extra"):
        name, value = next(iter(d["extra"].items()))
        return f"{name.replace('_', ' ')} {value:.3g} after {d['epochs']} epochs"
    return f"{d['epochs']} epochs"


# --- propose ----------------------------------------------------------------------------------------

def _mode() -> str:
    mode = os.environ.get("FORGE_ML_IMPROVE", "auto").strip().lower()
    return mode if mode in ("auto", "scripted", "llm") else "auto"


def _train_script(svc) -> str:
    return (svc.profile().get("entry_points") or {}).get("train") or "train.py"


def scripted_available(svc) -> bool:
    """The repo's training script is the golden CIFAR-10 one, so the scripted change applies cleanly."""
    script = svc.repo / _train_script(svc)
    golden = GOLDEN / "train.py"
    return script.is_file() and golden.is_file() and script.read_bytes() == golden.read_bytes()


def scripted(d: dict) -> dict:
    gap = d.get("train_val_gap")
    seen = (f"It's overfitting: {d['train_acc']:.0%} on training but {d['val_acc']:.0%} on validation."
            if gap is not None and gap > 0.08 else f"It tops out at {d.get('best_val_acc', 0):.0%} on validation.")
    return {
        "diagnosis": seen + " It memorizes 10,000 images instead of learning from all 50,000, with no augmentation "
                            "and a flat learning rate.",
        "changes": [
            {"title": "Random crops and flips", "why": "every epoch sees slightly different images, so it can't memorize"},
            {"title": "One-cycle learning rate with AdamW", "why": "warms up, then anneals: faster and steadier"},
            {"title": "Label smoothing", "why": "3% of the labels are wrong; it stops chasing them"},
            {"title": "All 50,000 training images", "why": "five times the data for the same 10 epochs"},
        ],
        "patches": [{"path": "train.py", "content": (DEMO / "train.py").read_text()}],
        "train_args": ["--full"],
        "epochs": 10,
        "expected": "around 80% validation accuracy in about a minute and a half",
    }


def _forge_lines(text: str) -> list[str]:
    return [line.strip() for line in text.splitlines() if line.rstrip().endswith("# forge")]


def check_proposal(repo: Path, data: dict) -> list[tuple[str, Path, str]]:
    """Guardrails on the model's reply. Returns (rel, target, content) per patched file."""
    if not data.get("patches"):
        raise GuardrailError("no patches: send the full new content of at least one existing .py file")
    out = []
    for p in data["patches"]:
        rel = PurePosixPath(p["path"].replace("\\", "/")).as_posix()
        target = safe_patch_path(repo, rel)
        try:
            compile(p["content"], rel, "exec")
        except SyntaxError as exc:
            raise GuardrailError(f"{rel} doesn't parse: line {exc.lineno}: {exc.msg}") from exc
        kept = {line.strip() for line in p["content"].splitlines()}
        lost = [line for line in _forge_lines(target.read_text()) if line not in kept]
        if lost:
            raise GuardrailError(f"{rel} dropped Forge lines (keep them unchanged): {lost[:3]}")
        out.append((rel, target, p["content"]))
    patched_text = "\n".join(c for _, _, c in out)
    for arg in data.get("train_args", []):
        flag = arg.split("=", 1)[0]
        if flag.startswith("-") and flag not in patched_text:
            raise GuardrailError(f"train_args flag {flag} isn't defined in the patched script")
    if not 1 <= int(data.get("epochs") or 0) <= 100:
        raise GuardrailError("epochs must be between 1 and 100")
    return out


def _context(svc, d: dict) -> str:
    hints = prescan(svc.repo)
    files = read_files(svc.repo, hints["files_sent"])
    return (f"<run_diagnosis>\n{json.dumps(d, indent=1)}\n</run_diagnosis>\n\n"
            f"<profile>\n{json.dumps(svc.profile(), indent=1)}\n</profile>\n\n"
            f"<time_budget seconds=\"{BUDGET_S}\">The improved run must finish within about {BUDGET_S} seconds on "
            f"this machine. The last run took {d.get('seconds_per_epoch', '?')} s per epoch.</time_budget>\n\n"
            f"<repo_files>\n{files_block(files)}\n</repo_files>")


def ask_claude(svc, d: dict, llm: Callable[..., LLMResult] = complete_json) -> dict:
    messages: list[dict] = [{"role": "user", "content": [
        {"type": "text", "text": _context(svc, d)},
        {"type": "text", "text": "Improve this model's validation score. Write the change."}]}]
    error = ""
    for _ in range(ROUNDS):
        result = llm(PROMPT.read_text(), messages, SCHEMA, timeout=TIMEOUT_S, effort="high")
        try:
            check_proposal(svc.repo, result.data)
            return result.data
        except GuardrailError as exc:
            error = str(exc)
            messages += [{"role": "assistant", "content": result.content},
                         {"role": "user", "content": f"That reply broke a rule: {error}. Send the whole reply again, fixed."}]
    raise ImproveError(f"Claude's change didn't pass the checks: {error}")


def propose(svc, llm: Callable[..., LLMResult] = complete_json) -> dict:
    """Write a proposal into .forge/improve/ and return plan.json. Never touches the repo's own files."""
    if plan(svc).get("applied"):
        raise ImproveError("an improvement is already applied: undo it first, or train again")
    d = diagnose(svc)
    mode, data, source = _mode(), None, "claude"
    if mode == "scripted" and scripted_available(svc):
        data, source = scripted(d), "scripted"
    else:
        try:
            data = ask_claude(svc, d, llm)
        except (LLMError, ImproveError) as exc:
            if mode == "llm" or not scripted_available(svc):
                raise ImproveError(str(exc)) from exc
            data, source = scripted(d), "scripted"
    targets = check_proposal(svc.repo, data)
    out = _dir(svc)
    shutil.rmtree(out, ignore_errors=True)
    manifest = {}
    for rel, target, content in targets:
        dest = out / rel
        dest.parent.mkdir(parents=True, exist_ok=True)
        dest.write_text(content)
        manifest[rel] = {"sha256": sha256(target)}
    result = {"diagnosis": data["diagnosis"], "changes": data["changes"], "train_args": data["train_args"],
              "epochs": int(data["epochs"]), "expected": data["expected"], "source": source,
              "files": manifest, "baseline": {"run_id": d["run_id"], "headline": headline(d),
                                              "best_val_acc": d.get("best_val_acc")},
              "applied": False}
    (out / "plan.json").write_text(json.dumps(result, indent=1))
    return result


# --- apply / undo -----------------------------------------------------------------------------------

def with_args(train_cmd: str, args: list[str]) -> str:
    argv = shlex.split(train_cmd)
    for arg in args:
        if arg not in argv:
            argv.append(arg)
    return shlex.join(argv)


def apply(svc) -> dict:
    current = plan(svc)
    if not current:
        raise ImproveError("there's no proposed change: ask me what to improve first")
    if current.get("applied"):
        return current
    out = _dir(svc)
    for rel, info in current["files"].items():
        if sha256(svc.repo / rel) != info["sha256"]:
            raise ImproveError(f"{rel} changed after I wrote the proposal; ask me again")
    for rel in current["files"]:
        target = svc.repo / rel
        shutil.copyfile(target, out / f"{rel}.orig")
        shutil.copyfile(out / rel, target)
    profile_path = svc.forge_dir / "profile.json"
    shutil.copyfile(profile_path, out / "profile.json.orig")
    profile = json.loads(profile_path.read_text())
    if current["train_args"] and profile.get("train_cmd"):
        profile["train_cmd"] = with_args(profile["train_cmd"], current["train_args"])
        profile_path.write_text(json.dumps(profile, indent=2) + "\n")
    current["applied"], current["applied_at"] = True, time.time()
    (out / "plan.json").write_text(json.dumps(current, indent=1))
    return current


def undo(svc) -> list[str]:
    """Put back what apply replaced. Returns the restored paths ([] when nothing was applied)."""
    current, out = plan(svc), _dir(svc)
    if not current.get("applied"):
        return []
    restored = []
    for rel in current["files"]:
        orig = out / f"{rel}.orig"
        if orig.exists():
            shutil.copyfile(orig, svc.repo / rel)
            restored.append(rel)
    if (out / "profile.json.orig").exists():
        shutil.copyfile(out / "profile.json.orig", svc.forge_dir / "profile.json")
    applied_at = current.get("applied_at", float("inf"))
    for run in svc._run_dirs():
        started = _read(run / "run.json").get("started_at") or run.stat().st_mtime
        if started >= applied_at:  # trained with the code that's now gone: kept, out of the desk's way
            (svc.forge_dir / "runs-undone").mkdir(exist_ok=True)
            shutil.move(str(run), str(svc.forge_dir / "runs-undone" / run.name))
    shutil.rmtree(out, ignore_errors=True)
    return restored
