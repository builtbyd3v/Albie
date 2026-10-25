"""ML state behind the /ml endpoints: one project repo, its .forge/project/ and .forge/runs/.

The hub never imports torch (ML_PLAN 11). Graph and dataset files are produced by the forge runners,
which run as subprocesses with the repo's own Python; training is the repo's own train command.

Env: FORGE_ML_REPO (default ml-examples/cifar10). Point it at hub/.data/mocks/ml/<archetype> for mock mode.
"""

from __future__ import annotations

import difflib
import hashlib
import json
import os
import re
import shlex
import shutil
import subprocess
import uuid
from pathlib import Path
from typing import Any

from .archetypes import ARCHETYPES, layout
from .models import VizCard
from .prescan import resolve_python

ROOT = Path(__file__).resolve().parents[2]
DEFAULT_REPO = ROOT / "ml-examples" / "cifar10"
FORGE_SDK = ROOT / "forge-sdk"
SAFE_ID = re.compile(r"^[A-Za-z0-9][A-Za-z0-9_.-]{0,79}$")
RUNNER_TIMEOUT_S = 180
RUNNER_JOBS = {"graph.json": "graph", "dataset.json": "dataset", "atlas.json": "dataset", "atlas.png": "dataset",
               "samples.json": "dataset"}


class NotFound(Exception):
    pass


class Conflict(Exception):
    pass


def safe_id(value: str, what: str = "id") -> str:
    """Ids end up in file paths: allow only plain names (no slashes, no `..`)."""
    if not SAFE_ID.match(value) or ".." in value:
        raise NotFound(f"bad {what}: {value!r}")
    return value


def _read_json(path: Path) -> Any:
    return json.loads(path.read_text())


class MLService:
    def __init__(self, repo: str | Path | None = None) -> None:
        self.repo = Path(repo or os.environ.get("FORGE_ML_REPO") or DEFAULT_REPO).resolve()
        self.train_proc: subprocess.Popen | None = None
        self.train_run_id: str | None = None
        self.project: dict[str, Any] = {"path": str(self.repo), "status": "idle"}

    # --- layout -------------------------------------------------------------------------------
    @property
    def forge_dir(self) -> Path:
        return self.repo / ".forge"

    @property
    def project_dir(self) -> Path:
        return self.forge_dir / "project"

    @property
    def runs_dir(self) -> Path:
        return self.forge_dir / "runs"

    @property
    def overrides_path(self) -> Path:
        return self.forge_dir / "labels_override.json"

    def profile(self) -> dict:
        path = self.forge_dir / "profile.json"
        return _read_json(path) if path.exists() else {}

    def python(self) -> str:
        """The repo's interpreter (profile.python), so torch never has to be in the hub's env."""
        return resolve_python(self.repo, self.profile().get("python"))

    # --- runs ---------------------------------------------------------------------------------
    def _run_dirs(self) -> list[Path]:
        if not self.runs_dir.is_dir():
            return []
        return sorted((p for p in self.runs_dir.iterdir() if p.is_dir()), key=lambda p: p.stat().st_mtime)

    def run_dir(self, run_id: str) -> Path:
        path = self.runs_dir / safe_id(run_id, "run id")
        if not path.is_dir():
            raise NotFound(f"no run {run_id!r} in {self.runs_dir}")
        return path

    def latest_run(self, with_file: str) -> Path | None:
        for path in reversed(self._run_dirs()):
            if (path / with_file).exists():
                return path
        return None

    def runs(self) -> list[dict]:
        out = []
        for path in reversed(self._run_dirs()):
            info = _read_json(path / "run.json") if (path / "run.json").exists() else {}
            summary = _read_json(path / "summary.json") if (path / "summary.json").exists() else {}
            if (path / "results.json").exists():
                status = "done"
            elif self.training_active() and path.name == self.train_run_id:
                status = "training"
            else:
                status = "incomplete"
            best = summary.get("best_val_acc") or summary.get("accuracy") or summary.get("r2")
            out.append({"run_id": path.name, "started_at": info.get("started_at"), "status": status,
                        "epochs": info.get("total_epochs"), "best_metric": best})
        return out

    def run_file(self, run_id: str, name: str) -> Any:
        path = self.run_dir(run_id) / name
        if not path.exists():
            raise NotFound(f"run {run_id} has no {name} yet")
        return _read_json(path)

    # --- project files (graph, dataset, atlas): runner output, else the latest run's copy ------
    def project_path(self, name: str, compute: bool = True) -> Path:
        path = self.project_dir / name
        if path.exists():
            return path
        run = self.latest_run(name)
        if run is not None:
            return run / name
        if compute and name in RUNNER_JOBS and (self.forge_dir / "adapter.py").exists():
            self.run_runner(RUNNER_JOBS[name])
            if path.exists():
                return path
        raise NotFound(f"no {name} for {self.repo.name}: train once, or run "
                       f"`python -m forge.run {RUNNER_JOBS.get(name, 'dataset')} --repo {self.repo}`")

    def run_runner(self, job: str) -> None:
        env = {**os.environ, "PYTHONPATH": os.pathsep.join(filter(None, [str(FORGE_SDK), os.environ.get("PYTHONPATH")]))}
        proc = subprocess.run([self.python(), "-m", "forge.run", job, "--repo", str(self.repo)], cwd=self.repo,
                              env=env, capture_output=True, text=True, timeout=RUNNER_TIMEOUT_S)
        if proc.returncode != 0:
            raise RuntimeError(f"forge.run {job} failed:\n{proc.stderr[-2000:]}")

    def classes(self) -> list[str]:
        try:
            return _read_json(self.project_path("classes.json", compute=False))
        except NotFound:
            if self.profile().get("classes"):
                return self.profile()["classes"]
            raise

    def graph(self) -> dict:
        return _read_json(self.project_path("graph.json"))

    def atlas(self) -> dict:
        meta = _read_json(self.project_path("atlas.json"))
        png = self.project_path("atlas.png")
        return {"atlas_url": f"/ml/files/atlas.png?v={int(png.stat().st_mtime)}", **meta}

    def atlas_png(self) -> Path:
        return self.project_path("atlas.png")

    def dataset(self) -> list[dict]:
        relabel, remove = self.overrides()
        items = []
        for item in _read_json(self.project_path("dataset.json")):
            sid = item["id"]
            if sid in relabel:
                item["label_idx"] = relabel[sid]
            item["removed"] = sid in remove
            if item.get("kind") == "image":
                item["thumb_url"] = f"/ml/image/{sid}"
            items.append(item)
        return items

    def image(self, sample_id: str) -> bytes | Path:
        """The full-size image if the dataset runner saved one, else its cell cropped out of the atlas."""
        safe_id(sample_id, "sample id")
        full = self.project_dir / "images" / f"{sample_id}.png"
        if full.exists():
            return full
        meta = _read_json(self.project_path("atlas.json"))
        if sample_id not in meta["index"]:
            raise NotFound(f"no sample {sample_id!r}")
        from io import BytesIO

        from PIL import Image  # pillow ships with the hub env (matplotlib, via rocketpy)

        cell, cols, i = meta["cell"][0], meta["cols"], meta["index"][sample_id]
        x, y = (i % cols) * cell, (i // cols) * cell
        with Image.open(self.project_path("atlas.png")) as atlas:
            crop = atlas.crop((x, y, x + cell, y + cell))
        buf = BytesIO()
        crop.save(buf, format="PNG")
        return buf.getvalue()

    # --- results with relabel/remove applied ----------------------------------------------------
    def results(self, run_id: str) -> dict:
        results = self.run_file(run_id, "results.json")
        relabel, remove = self.overrides()
        for s in results["samples"]:
            if "true_idx" not in s:  # regression: nothing to relabel
                continue
            if s["id"] in relabel:
                s["true_idx"], s["relabeled"] = relabel[s["id"]], True
                s["correct"] = s["pred_idx"] == s["true_idx"]
            s["removed"] = s["id"] in remove
        return results

    def sample(self, sample_id: str) -> dict:
        safe_id(sample_id, "sample id")
        try:
            samples = _read_json(self.project_path("samples.json", compute=False))
        except NotFound:
            samples = {}
        detail = samples.get(sample_id)
        if detail is None:
            item = next((d for d in self.dataset() if d["id"] == sample_id), None)
            if item is None:
                raise NotFound(f"no sample {sample_id!r}")
            detail = {"id": sample_id, "name": item.get("name", sample_id), "split": "val",
                      "label_idx": item.get("label_idx"), "y": item.get("y"), "meta": {}}
        relabel, remove = self.overrides()
        if sample_id in relabel:
            detail["label_idx"], detail["relabeled"] = relabel[sample_id], True
        detail["removed"] = sample_id in remove
        run = self.latest_run("results.json")
        if run is not None:
            pred = next((s for s in _read_json(run / "results.json")["samples"] if s["id"] == sample_id), None)
            if pred and "pred_idx" in pred:
                detail.update(pred_idx=pred["pred_idx"], probs=pred["probs"])
        return detail

    # --- chart cards (ML_PLAN 7.6): this archetype's viz_ids from the run files, plus runs/<id>/viz/*.png ---
    def viz(self, run_id: str | None = None) -> list[dict]:
        if run_id is None:
            latest = self.latest_run("summary.json")
            if latest is None:
                return []
            run_id = latest.name
        run = self.run_dir(run_id)
        summary = _read_json(run / "summary.json") if (run / "summary.json").exists() else {}
        archetype = summary.get("archetype") or self.profile().get("archetype") or "generic"
        wanted = list(ARCHETYPES.get(archetype, ARCHETYPES["generic"])["viz_ids"])
        wanted += [v["viz_id"] for v in self.profile().get("extra_viz", []) if v.get("viz_id") not in wanted]
        cards = []
        for viz_id in wanted:
            build = getattr(self, f"_viz_{viz_id}", None)
            card = build(run, summary) if build else None
            if card:
                cards.append({"viz_id": viz_id, "run_id": run_id, "panel": "ml-viz", **card})
        pngs = sorted((run / "viz").glob("*.png")) if (run / "viz").is_dir() else []
        for png in pngs:
            if SAFE_ID.match(png.stem) and png.stem not in {c["viz_id"] for c in cards}:
                cards.append({"viz_id": png.stem, "run_id": run_id, "panel": "ml-viz", "kind": "image",
                              "title": png.stem.replace("_", " ").capitalize(), "png_url": f"/ml/viz/{run_id}/{png.stem}.png",
                              "alt": png.stem.replace("_", " ")})
        cards += self._extra_cards(run, run_id, {c["viz_id"] for c in cards})
        return cards

    def _extra_cards(self, run: Path, run_id: str, seen: set[str]) -> list[dict]:
        """Charts from `.forge/extra_viz.py` (ML_PLAN A7), written to the run's viz.json. Figures are already
        picked up as PNG cards above; here only dict VizCards are merged, validated against models.VizCard."""
        path = run / "viz.json"
        if not path.exists():
            return []
        try:
            raw = _read_json(path)
        except (OSError, ValueError):
            return []
        cards = []
        for item in raw if isinstance(raw, list) else []:
            try:
                card = VizCard.model_validate(item).model_dump(exclude_none=True)
                card["run_id"] = run_id
                card["panel"] = card.get("panel") or "ml-viz"
                if card["kind"] == "image":
                    png = run / "viz" / f"{safe_id(card['viz_id'], 'viz id')}.png"
                    if not png.exists():
                        continue  # an image card with no file behind it would draw empty
                    card["png_url"] = f"/ml/viz/{run_id}/{card['viz_id']}.png"
            except Exception:  # noqa: BLE001 - one bad extra card never breaks /ml/viz
                continue
            if card["viz_id"] in seen:
                continue
            seen.add(card["viz_id"])
            cards.append(card)
        return cards

    def viz_png(self, run_id: str, viz_id: str) -> Path:
        path = self.run_dir(run_id) / "viz" / f"{safe_id(viz_id, 'viz id')}.png"
        if not path.exists():
            raise NotFound(f"run {run_id} has no chart {viz_id!r}")
        return path

    def _viz_per_class_accuracy(self, run: Path, summary: dict) -> dict | None:
        per_class = summary.get("per_class") or []
        if not per_class:
            return None
        return {"title": "Accuracy per class", "subtitle": f"overall {summary.get('accuracy', 0):.1%}", "kind": "bar",
                "x": [c["name"] for c in per_class], "series": [{"name": "accuracy", "y": [c["accuracy"] for c in per_class]}]}

    def _viz_confusion_matrix(self, run: Path, summary: dict) -> dict | None:
        if not (run / "confusion.json").exists():
            return None
        confusion = _read_json(run / "confusion.json")
        return {"title": "Confusion matrix", "subtitle": "rows: true, columns: predicted", "kind": "heatmap",
                "cells": confusion["matrix"], "x_labels": confusion["labels"], "y_labels": confusion["labels"]}

    def _regression_samples(self, run: Path) -> list[dict]:
        if not (run / "results.json").exists():
            return []
        return [s for s in _read_json(run / "results.json").get("samples", []) if "y_pred" in s]

    def _viz_pred_vs_actual(self, run: Path, summary: dict) -> dict | None:
        samples = self._regression_samples(run)[:1000]
        if not samples:
            return None
        r2 = summary.get("r2")
        return {"title": "Predicted vs actual", "subtitle": f"R² {r2:.3f}" if r2 is not None else None, "kind": "scatter",
                "points": [{"x": s["y_true"], "y": s["y_pred"], "id": s["id"]} for s in samples],
                "x_label": "actual", "y_label": "predicted"}

    def _viz_residual_hist(self, run: Path, summary: dict) -> dict | None:
        residuals = [s.get("residual", s["y_pred"] - s["y_true"]) for s in self._regression_samples(run)]
        if not residuals:
            return None
        lo, hi, bins = min(residuals), max(residuals), 12
        width = (hi - lo) / bins or 1.0
        counts = [0] * bins
        for r in residuals:
            counts[min(bins - 1, int((r - lo) / width))] += 1
        mae = summary.get("mae")
        return {"title": "Residuals", "subtitle": f"MAE {mae:.3g}" if mae is not None else None, "kind": "bar",
                "x": [f"{lo + (i + .5) * width:.3g}" for i in range(bins)], "series": [{"name": "count", "y": counts}]}

    def _viz_feature_importance(self, run: Path, summary: dict) -> dict | None:
        features = summary.get("feature_importance") or []  # [{name, value}] when the eval runner provides it
        if not features:
            return None
        features = sorted(features, key=lambda f: abs(f["value"]), reverse=True)[:12]
        return {"title": "Feature importance", "kind": "bar", "x": [f["name"] for f in features],
                "series": [{"name": "importance", "y": [f["value"] for f in features]}]}

    def _viz_final_metrics(self, run: Path, summary: dict) -> dict | None:
        rows = [[k.replace("_", " "), round(v, 4) if isinstance(v, float) else v] for k, v in summary.items()
                if isinstance(v, (int, float)) and not isinstance(v, bool)]
        return {"title": "Final metrics", "kind": "table", "columns": ["metric", "value"], "rows": rows} if rows else None

    # --- relabel / remove -> .forge/labels_override.json (applied by the repo's data code) -------
    def overrides(self) -> tuple[dict[str, int], set[str]]:
        if not self.overrides_path.exists():
            return {}, set()
        data = _read_json(self.overrides_path)
        return {k: int(v) for k, v in data.get("relabel", {}).items()}, set(data.get("remove", []))

    def _write_overrides(self, relabel: dict[str, int], remove: set[str]) -> None:
        self.forge_dir.mkdir(parents=True, exist_ok=True)
        tmp = self.overrides_path.with_suffix(".tmp")
        tmp.write_text(json.dumps({"relabel": relabel, "remove": sorted(remove)}, indent=1))
        tmp.replace(self.overrides_path)  # atomic: a crash never leaves a half-written file

    def _current_label(self, sample_id: str) -> int:
        item = next((d for d in self.dataset() if d["id"] == sample_id), None)
        if item is None:
            raise NotFound(f"no sample {sample_id!r}")
        return int(item.get("label_idx") if item.get("label_idx") is not None else -1)

    def relabel(self, sample_id: str, label_idx: int) -> dict:
        safe_id(sample_id, "sample id")
        n = len(self.classes())
        if not 0 <= label_idx < n:
            raise Conflict(f"label_idx {label_idx} out of range (0..{n - 1})")
        self._current_label(sample_id)  # 404 for unknown ids
        relabel, remove = self.overrides()
        relabel[sample_id] = label_idx
        self._write_overrides(relabel, remove)
        return {"id": sample_id, "label_idx": label_idx, "removed": sample_id in remove}

    def remove(self, sample_id: str) -> dict:
        safe_id(sample_id, "sample id")
        label = self._current_label(sample_id)
        relabel, remove = self.overrides()
        remove.add(sample_id)
        self._write_overrides(relabel, remove)
        return {"id": sample_id, "label_idx": label, "removed": True}

    # --- training -------------------------------------------------------------------------------
    def training_active(self) -> bool:
        return self.train_proc is not None and self.train_proc.poll() is None

    def start_training(self, epochs: int, hub_url: str) -> str:
        if self.training_active():
            raise Conflict(f"run {self.train_run_id} is still training")
        cmd = self.profile().get("train_cmd")
        if not cmd:
            raise Conflict(f"no train_cmd in {self.forge_dir / 'profile.json'}")
        argv = shlex.split(cmd)
        if argv[0] in ("python", "python3"):
            argv[0] = self.python()
        # Only a command that already takes --epochs gets the requested count; appending it to one that
        # doesn't would break scripts that read their own argv (e.g. a CSV path as the first argument).
        if "--epochs" in argv[:-1]:
            argv[argv.index("--epochs") + 1] = str(epochs)
        run_id = uuid.uuid4().hex[:8]
        env = {k: v for k, v in os.environ.items() if k not in ("FORGE_OFFLINE", "FORGE_RUN_DIR")}
        env.update(FORGE_HUB=hub_url.rstrip("/"), FORGE_RUN_ID=run_id,
                   PYTHONPATH=os.pathsep.join(filter(None, [str(FORGE_SDK), os.environ.get("PYTHONPATH")])))
        self.forge_dir.mkdir(parents=True, exist_ok=True)
        log = open(self.forge_dir / f"train-{run_id}.log", "w")  # noqa: SIM115 - the child process owns it
        self.train_proc = subprocess.Popen(argv, cwd=self.repo, env=env, stdout=log, stderr=subprocess.STDOUT)
        self.train_run_id = run_id
        return run_id

    def replay_events(self, run_id: str) -> list[dict]:
        path = self.run_dir(run_id) / "events.jsonl"
        if not path.exists():
            raise NotFound(f"run {run_id} has no events.jsonl")
        events = [json.loads(line) for line in path.read_text().splitlines() if line.strip()]
        return sorted(events, key=lambda e: e.get("t", 0.0))

    def metrics_history(self, run_id: str) -> dict:
        """A run's ml.metrics ticks and ml.class_acc rows (last per epoch), total epochs, overfit alert and
        latest ml.probe, from events.jsonl, so panels opened after training still draw the live graphs."""
        by_epoch: dict[int, dict] = {}
        class_acc: dict[int, dict] = {}
        total, overfit, probe = 0, None, None
        for e in self.replay_events(run_id):
            payload = e.get("payload") or {}
            if e.get("type") == "ml.metrics":
                by_epoch[int(payload.get("epoch", 0))] = payload
            elif e.get("type") == "ml.class_acc":
                class_acc[int(payload.get("epoch", 0))] = payload
            elif e.get("type") == "ml.probe":
                probe = payload
            elif e.get("type") == "ml.run_state":
                total = max(total, int(payload.get("total_epochs") or 0))
            elif e.get("type") == "ml.overfit_alert" and overfit is None:
                overfit = payload
        metrics = [by_epoch[k] for k in sorted(by_epoch)]
        return {"run_id": run_id, "total_epochs": total or len(metrics), "metrics": metrics, "overfit": overfit,
                "class_acc": [class_acc[k] for k in sorted(class_acc)], "probe": probe}


    # --- project setup (ML_PLAN 6, 7.4) -----------------------------------------------------------
    def open_project(self, path: str) -> Path:
        repo = Path(path).expanduser().resolve()
        if not repo.is_dir():
            raise NotFound(f"no folder at {path!r}")
        self.repo = repo
        self.project = {"path": str(repo), "status": "scanning"}
        return repo

    def update_project(self, status: str, **fields: Any) -> tuple[dict, dict | None]:
        """Record one setup step. Returns (ml.project_state payload, ml.layout payload or None)."""
        profile = fields.pop("profile", None)
        state: dict[str, Any] = {"path": str(self.repo), "status": status}
        state.update({k: v for k, v in fields.items() if v is not None})
        if profile is not None:
            state["profile"] = profile.model_dump(by_alias=True)
        elif self.project.get("profile") and status not in ("scanning", "classifying"):
            state["profile"] = self.project["profile"]
        new_layout = None
        if status in ("ready", "failed"):
            extra = [v["viz_id"] for v in state.get("profile", {}).get("extra_viz", [])]
            new_layout = layout(state.get("archetype", "generic"), extra)
            state["layout"] = new_layout
        self.project = state
        event = {k: state[k] for k in ("path", "status", "archetype", "candidates", "summary", "error") if k in state}
        return event, new_layout

    def _manifest(self) -> dict:
        path = self.forge_dir / "patch" / "manifest.json"
        return _read_json(path) if path.exists() else {}

    def patch_files(self) -> list[dict]:
        """The proposed patch as unified diffs, for GET /ml/project."""
        out = []
        for rel in self._manifest():
            original, patched = self.repo / rel, self.forge_dir / "patch" / rel
            def lines(path: Path) -> list[str]:  # a missing final newline would glue two diff lines
                return [line if line.endswith("\n") else line + "\n" for line in path.read_text().splitlines(True)]

            diff = difflib.unified_diff(lines(original), lines(patched), fromfile=f"a/{rel}", tofile=f"b/{rel}")
            out.append({"path": rel, "diff": "".join(diff)})
        return out

    def project_state(self) -> dict:
        state = dict(self.project)
        if state.get("status") == "needs_approval":
            state["patch"] = {"files": self.patch_files()}
        return state

    def open_diff(self) -> dict:
        """Open the proposed patch in the laptop's real VS Code (`code --diff`), else return the diff text."""
        files = list(self._manifest())
        if not files:
            raise NotFound("no proposed patch")
        code = shutil.which("code")
        if code is None:
            return {"ok": True, "opened": False, "files": self.patch_files()}
        for rel in files:
            subprocess.Popen([code, "--diff", str(self.repo / rel), str(self.forge_dir / "patch" / rel)])
        return {"ok": True, "opened": True}

    def apply_patch(self, accept: bool) -> list[str]:
        """The only way setup changes the user's code (ML_PLAN 6.6). Keeps <file>.orig in .forge/patch/ and
        refuses if a file changed after the patch was written. accept=False is Skip: nothing is written."""
        if self.project.get("status") != "needs_approval":
            raise Conflict(f"nothing to approve (project is {self.project.get('status')})")
        manifest = self._manifest()
        applied = []
        if accept:
            for rel, info in manifest.items():
                target = self.repo / rel
                if hashlib.sha256(target.read_bytes()).hexdigest() != info["sha256"]:
                    raise Conflict(f"{rel} changed after the patch was written; rescan the project")
            for rel in manifest:
                target, patched = self.repo / rel, self.forge_dir / "patch" / rel
                shutil.copyfile(target, patched.with_name(patched.name + ".orig"))
                shutil.copyfile(patched, target)
                applied.append(rel)
        return applied
