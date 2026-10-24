"""Classify a repo with Jev (typed choices) instead of a text LLM. Same output as classify.py: a Profile.

Jev only picks from options, so the hub builds the options from the code (ast): model classes, data
loaders, dataframe columns, class-name lists. Questions ask about evidence in the code, not usefulness
(PR #2's lesson: "would this help" scored everything ~0.5). Class names come from the code, not Jev.
"""

from __future__ import annotations

import ast
import re
import shlex
from pathlib import Path
from typing import Any, Callable

from ..models import Profile
from . import jev
from .classify import CONFIDENCE_THRESHOLD, SetupError, context_text

NONE = "none"
ARCHETYPES = {
    "image_classification": "The code trains or evaluates a model that predicts one class label per image "
                            "(image datasets such as ImageFolder, CIFAR or MNIST, convolutional layers, "
                            "cross-entropy loss).",
    "regression": "The code fits a model that predicts one numeric value per row or time step (MSE or L1 loss, "
                  "sklearn regressors such as Ridge or LinearRegression, XGBoost or LightGBM regressors).",
    "text_classification": "The code trains a model that predicts one class label per piece of text (tokenizers, "
                           "transformers, text columns, cross-entropy loss).",
    "generic": "The code trains some other kind of model, or the repo has no model-training code.",
}
FRAMEWORKS = {
    "pytorch": "The model is built and trained with plain PyTorch (torch.nn, a manual training loop).",
    "lightning": "Training uses PyTorch Lightning (LightningModule, Trainer).",
    "sklearn": "The model is a scikit-learn, XGBoost or LightGBM estimator trained with .fit().",
    "keras": "The model is built with Keras or TensorFlow.",
    "hf_transformers": "The model comes from Hugging Face transformers (AutoModel, Trainer).",
    "other": "None of the above.",
}
TITLES = {"image_classification": "Image classifier", "regression": "Regression model",
          "text_classification": "Text classifier", "generic": "ML project"}
KNOWN_CLASSES = {
    "CIFAR10": ["airplane", "automobile", "bird", "cat", "deer", "dog", "frog", "horse", "ship", "truck"],
    "FashionMNIST": ["T-shirt/top", "Trouser", "Pullover", "Dress", "Coat", "Sandal", "Shirt", "Sneaker", "Bag",
                     "Ankle boot"],
    "MNIST": [str(d) for d in range(10)],
}
CLASS_LIST_NAMES = {"CLASSES", "classes", "CLASS_NAMES", "class_names", "LABELS", "labels", "LABEL_NAMES"}
MODEL_BASES = re.compile(r"(Module|Model|Sequential|LightningModule|Network)$")
DATA_NAMES = re.compile(r"(load|data|loader|dataset|fetch|read)", re.IGNORECASE)


def _base_name(node: ast.expr) -> str:
    return node.attr if isinstance(node, ast.Attribute) else node.id if isinstance(node, ast.Name) else ""


def extract(files: dict[str, str]) -> dict[str, Any]:
    """Candidates for Jev's choices, plus facts Jev can't produce (class names)."""
    models, loaders, columns, classes = [], [], {}, None
    dataset_names = set()
    for rel, text in sorted(files.items()):
        if not rel.endswith(".py"):
            continue
        try:
            tree = ast.parse(text)
        except SyntaxError:
            continue
        for node in tree.body:
            if isinstance(node, ast.ClassDef) and any(MODEL_BASES.search(_base_name(b)) for b in node.bases):
                models.append(f"{rel}:{node.name}")
            if isinstance(node, (ast.FunctionDef, ast.ClassDef)) and DATA_NAMES.search(node.name):
                loaders.append(f"{rel}:{node.name}")
            if (isinstance(node, ast.Assign) and classes is None and isinstance(node.value, (ast.List, ast.Tuple))
                    and any(isinstance(t, ast.Name) and t.id in CLASS_LIST_NAMES for t in node.targets)
                    and node.value.elts and all(isinstance(e, ast.Constant) and isinstance(e.value, str)
                                                for e in node.value.elts)):
                classes = [e.value for e in node.value.elts]
        for node in ast.walk(tree):
            if isinstance(node, ast.Subscript) and isinstance(node.slice, ast.Constant) \
                    and isinstance(node.slice.value, str):
                columns[node.slice.value] = columns.get(node.slice.value, 0) + 1
            if isinstance(node, ast.Attribute) and node.attr in KNOWN_CLASSES:
                dataset_names.add(node.attr)
    if classes is None:
        for name in ("CIFAR10", "FashionMNIST", "MNIST"):  # most specific first
            if name in dataset_names:
                classes = KNOWN_CLASSES[name]
                break
    ranked_columns = sorted(columns, key=lambda c: (-columns[c], c))
    return {"models": models, "loaders": loaders, "columns": ranked_columns, "classes": classes}


def _choice(instructions: str, options: list[str], allow_none: bool) -> dict:
    options = options[: jev.MAX_CRITERIA - (1 if allow_none else 0)]
    criteria = {o: o for o in options}
    if allow_none:
        criteria[NONE] = "None of these."
    return {"type": "choice", "instructions": instructions, "criteria": criteria}


def build_questions(hints: dict, files: dict[str, str], found: dict) -> dict[str, Any]:
    py_files = [rel for rel in files if rel.endswith(".py")]
    train_options = list(dict.fromkeys(hints.get("entry_points", []) + py_files))
    questions: dict[str, Any] = {
        "archetype": {"type": "choice", "criteria": ARCHETYPES,
                      "instructions": "Which statement describes what the source code in `state` actually does?"},
        "framework": {"type": "choice", "criteria": FRAMEWORKS,
                      "instructions": "Which statement matches how the source code builds and trains its model?"},
    }
    if train_options:
        questions["train_file"] = _choice(
            "Which file, when run with python, trains or fits the model? Judge by the code, not the file name.",
            train_options, allow_none=False)
    if found["models"]:
        questions["model_entry"] = _choice("Which class in the source code defines the model that gets trained?",
                                           found["models"], allow_none=True)
    if found["loaders"]:
        questions["data_entry"] = _choice("Which function or class in the source code loads the training data?",
                                          found["loaders"], allow_none=True)
    if found["columns"]:
        questions["target"] = _choice(
            "If the code fits a regression model, which column or key holds the numeric value it predicts?",
            found["columns"], allow_none=True)
    return questions


def _picked(answers: dict, name: str) -> str | None:
    choice = answers.get(name, {}).get("choice")
    return None if choice in (None, NONE) else choice


def _train_cmd(entry: str, text: str) -> str:
    cmd = f"python {shlex.quote(entry)}"
    return cmd + " --epochs 10" if "--epochs" in text else cmd


def classify_jev(repo: str | Path, hints: dict, files: dict[str, str],
                 ask: Callable[..., dict] = jev.ask) -> tuple[Profile, list[dict]]:
    repo = Path(repo).resolve()
    found = extract(files)
    questions = build_questions(hints, files, found)
    res = ask(context_text(hints, files), questions)
    answers = res["answers"]

    arch = answers["archetype"]
    archetype, confidence = arch["choice"], float(arch.get("confidence", 0.0))
    probs = arch.get("probabilities") or {}
    candidates = sorted(probs, key=lambda k: -probs[k])[:2] if confidence < CONFIDENCE_THRESHOLD else []
    train = _picked(answers, "train_file") or (hints.get("entry_points") or [None])[0]
    if train is None:
        raise SetupError("couldn't find a training script in the repo")
    model = _picked(answers, "model_entry")
    classifies = archetype in ("image_classification", "text_classification")
    classes = found["classes"] if classifies else None
    target = _picked(answers, "target") if archetype == "regression" else None

    model_name = model.split(":")[-1] if model else next(iter(hints.get("model_hints", {})), None)
    parts = [TITLES[archetype]]
    if classes:
        parts.append(f"{len(classes)} classes")
    if target:
        parts.append(f"predicting {target!r}")
    if model_name:
        parts.append(f"model {model_name}")
    notes = [f"classified by {res.get('model', jev.JEV_MODEL)} (confidence {confidence:.2f})"]
    if classifies and not classes:
        notes.append("class names not found in the code; the adapter's classes() decides them")

    profile = Profile(
        schema=1, archetype=archetype, confidence=confidence, candidates=candidates,
        framework=answers["framework"]["choice"], python=hints.get("python"),
        entry_points={"train": train, "model": model, "data": _picked(answers, "data_entry")},
        train_cmd=_train_cmd(train, files.get(train, "")), classes=classes, target=target, input_shape=None,
        checkpoint=(hints.get("checkpoints") or [None])[0], existing_logs=hints.get("existing_logs", []),
        extra_viz=[], summary=", ".join(parts) + ".", notes="; ".join(notes),
    )
    return profile, [res.get("usage", {})]
