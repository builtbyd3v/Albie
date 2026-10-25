"""Pre-scan an ML repo with plain Python, no LLM, about a second (ML_PLAN 6.3) -> .forge/hints.json.

Finds the framework, likely entry points, loss/dataset/model hints, checkpoints and logs, the repo's
Python interpreter, and picks which files to send to the classifier (most useful first, within a budget).
"""

from __future__ import annotations

import os
import re
import shutil
import sys
from pathlib import Path

SKIP_DIRS = {".git", ".forge", ".venv", "venv", "env", "__pycache__", "node_modules", "data", "datasets",
             "checkpoints", "wandb", "mlruns", "lightning_logs", "runs", "outputs", ".ipynb_checkpoints",
             ".mypy_cache", ".pytest_cache", ".ruff_cache", "build", "dist", ".idea", ".vscode", "site-packages"}
MAX_FILE_BYTES = 1_000_000
SEND_BUDGET_CHARS = 150_000  # ~40k tokens: small repos (ML_PLAN 4.1) fit almost whole
TEXT_EXTS = {".py", ".yaml", ".yml", ".toml", ".json", ".cfg", ".ini", ".txt", ".md"}

FRAMEWORKS = {"torch": "torch", "torchvision": "torchvision", "lightning": "lightning",
              "pytorch_lightning": "lightning", "tensorflow": "tensorflow", "keras": "keras", "sklearn": "sklearn",
              "xgboost": "xgboost", "lightgbm": "lightgbm", "transformers": "transformers", "datasets": "datasets",
              "pandas": "pandas", "numpy": "numpy", "yfinance": "yfinance"}
LOSS_HINTS = ["CrossEntropyLoss", "cross_entropy", "NLLLoss", "BCEWithLogitsLoss", "MSELoss", "mse_loss", "L1Loss",
              "HuberLoss", "mean_squared_error", "log_loss"]
DATA_HINTS = ["ImageFolder", "CIFAR10", "CIFAR100", "MNIST", "FashionMNIST", "read_csv", "load_dataset",
              "DataLoader", "train_test_split", "TimeSeriesSplit", "yf.download", "load_iris", "fetch_"]
MODEL_HINTS = ["nn.Module", "nn.Conv2d", "nn.Linear", "LinearRegression", "Ridge", "Lasso", "LogisticRegression",
               "RandomForest", "XGBRegressor", "XGBClassifier", "LGBMRegressor", "LGBMClassifier",
               "AutoModelForSequenceClassification", "keras.Sequential"]
TRAIN_SIGNALS = re.compile(r"\.backward\(|optim\.|\.fit\(|trainer\.fit|model\.train\(")
IMPORT_RE = re.compile(r"^\s*(?:from|import)\s+([A-Za-z_][\w]*)", re.MULTILINE)
CHECKPOINT_EXTS = {".pt", ".pth", ".ckpt", ".joblib", ".pkl", ".h5", ".keras", ".safetensors"}
LOG_NAMES = {"metrics.csv", "trainer_state.json"}


def _walk(repo: Path):
    for root, dirs, files in os.walk(repo):
        dirs[:] = sorted(d for d in dirs if d not in SKIP_DIRS and not d.startswith("."))
        for name in sorted(files):
            yield Path(root) / name


def _find_artifacts(repo: Path) -> tuple[list[str], list[str]]:
    """Checkpoints and logs, including inside the folders the text walk skips (only names, never read)."""
    checkpoints, logs = [], []
    for root, dirs, files in os.walk(repo):
        dirs[:] = [d for d in dirs if d not in {".git", ".venv", "venv", "env", "node_modules", "__pycache__",
                                                 "site-packages", ".forge"}]
        rel_root = Path(root).relative_to(repo)
        if rel_root.name in {"lightning_logs", "runs", "wandb", "mlruns"}:
            logs.append(rel_root.as_posix())
        for name in files:
            rel = (rel_root / name).as_posix()
            if Path(name).suffix in CHECKPOINT_EXTS:
                checkpoints.append(rel)
            elif name in LOG_NAMES or name.startswith("events.out.tfevents"):
                logs.append(rel)
    return sorted(checkpoints)[:50], sorted(set(logs))[:50]


def find_python(repo: Path) -> str | None:
    """The repo's own interpreter: a venv inside it, else the active conda env, else None (ask the user)."""
    for venv in (".venv", "venv", "env"):
        for exe in ("bin/python", "Scripts/python.exe"):
            if (repo / venv / exe).exists():
                return f"{venv}/{exe}"
    conda = os.environ.get("CONDA_PREFIX")
    if conda and (Path(conda) / "bin" / "python").exists():
        return str(Path(conda) / "bin" / "python")
    return None


def resolve_python(repo: Path, configured: str | None) -> str:
    """A profile's `python` as something runnable: a path inside the repo, else the repo's own venv (.venv/,
    venv/, env/) for a bare "python", else that name on PATH, else the hub's own interpreter. A hand-written
    profile may just say "python", and PATH's python (the hub's) lacks the repo's torch."""
    configured = configured or "python"
    candidate = Path(repo) / configured
    if candidate.exists():
        return str(candidate)
    if Path(configured).name in ("python", "python3"):
        venv = find_python(Path(repo))
        if venv is not None:
            return str(Path(repo) / venv)  # an absolute conda path stays absolute
    return shutil.which(configured) or sys.executable


def _priority(rel: str, text: str, entry_points: set[str]) -> int:
    name = Path(rel).name.lower()
    if rel in entry_points:
        return 0
    if name.endswith(".py") and ("model" in name or "net" in name or "arch" in name or "nn.Module" in text):
        return 1
    if name.endswith(".py") and ("data" in name or "dataset" in name or "loader" in name):
        return 2
    if name.endswith((".yaml", ".yml", ".toml", ".cfg", ".ini")):
        return 3
    if name.startswith("requirements") or name in {"environment.yml", "pyproject.toml", "setup.py"}:
        return 4
    if name.endswith(".py"):
        return 5
    if name.lower().startswith("readme"):
        return 6
    return 9


def prescan(repo: str | Path) -> dict:
    repo = Path(repo).resolve()
    texts: dict[str, str] = {}
    notebooks: list[str] = []
    for path in _walk(repo):
        rel = path.relative_to(repo).as_posix()
        if path.suffix == ".ipynb":
            notebooks.append(rel)
            continue
        if path.suffix not in TEXT_EXTS or path.stat().st_size > MAX_FILE_BYTES:
            continue
        if path.suffix in {".json", ".txt", ".md"} and not (path.name.startswith("requirements")
                                                            or path.name.lower().startswith("readme")
                                                            or path.suffix == ".json" and path.stat().st_size < 20_000):
            continue
        try:
            texts[rel] = path.read_text(errors="replace")
        except OSError:
            continue

    py = {rel: t for rel, t in texts.items() if rel.endswith(".py")}
    imports = sorted({m for t in py.values() for m in IMPORT_RE.findall(t)})
    frameworks = sorted({FRAMEWORKS[i] for i in imports if i in FRAMEWORKS})

    def files_with(needles: list[str]) -> dict[str, list[str]]:
        found = {n: sorted(rel for rel, t in py.items() if n in t) for n in needles}
        return {n: files for n, files in found.items() if files}

    entry_points = sorted(rel for rel, t in py.items()
                          if Path(rel).name.startswith("train") or Path(rel).name == "main.py"
                          or ('if __name__ == "__main__"' in t or "if __name__ == '__main__'" in t)
                          and TRAIN_SIGNALS.search(t))
    checkpoints, logs = _find_artifacts(repo)

    ordered = sorted(texts, key=lambda rel: (_priority(rel, texts[rel], set(entry_points)), rel))
    sent, skipped, used = [], [], 0
    for rel in ordered:
        if used + len(texts[rel]) <= SEND_BUDGET_CHARS:
            sent.append(rel)
            used += len(texts[rel])
        else:
            skipped.append(rel)

    return {
        "repo": repo.name,
        "frameworks": frameworks,
        "imports": imports,
        "entry_points": entry_points,
        "loss_hints": files_with(LOSS_HINTS),
        "dataset_hints": files_with(DATA_HINTS),
        "model_hints": files_with(MODEL_HINTS),
        "checkpoints": checkpoints,
        "existing_logs": logs,
        "notebooks": notebooks,
        "python": find_python(repo),
        "py_lines": sum(t.count("\n") + 1 for t in py.values()),
        "files_sent": sent,
        "files_skipped": skipped,
    }


def read_files(repo: str | Path, rels: list[str]) -> dict[str, str]:
    repo = Path(repo).resolve()
    return {rel: (repo / rel).read_text(errors="replace") for rel in rels}
