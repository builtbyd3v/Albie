"""ml-examples/cifar10: noise, overrides, stable ids, and its hand-written adapter passing forge.check."""

import json
import sys
from pathlib import Path

import pytest

from forge import atlas
from forge.check import run_checks

CIFAR = Path(__file__).resolve().parents[2] / "ml-examples" / "cifar10"
sys.path.insert(0, str(CIFAR))
import data  # noqa: E402

needs_data = pytest.mark.skipif(not (CIFAR / "data" / "cifar-10-batches-py").exists(),
                                reason="run `python data.py` in ml-examples/cifar10 to download CIFAR-10")


def test_noise_is_deterministic_3_percent_and_always_changes_the_label():
    targets = [i % 10 for i in range(50_000)]
    train = data.flip_labels(targets, "train")
    assert train == data.flip_labels(targets, "train")
    assert len(train) == 1500
    assert all(noisy != targets[i] for i, noisy in train.items())
    val = data.flip_labels(targets[:10_000], "val")
    assert len(val) == 30 and max(val) < data.VAL_SIZE


@needs_data
def test_val_set_has_stable_ids_noise_and_overrides(tmp_path):
    clean = data.get_dataset("val", overrides=None)
    assert len(clean) == 1000 and clean.ids[7] == "val_0007"
    noise = data.flip_labels([int(t) for t in clean.dataset.targets], "val")
    assert sum(clean.labels[i] != clean.dataset.targets[i] for i in range(1000)) == len(noise) == 30

    first = next(iter(noise))
    overrides = tmp_path / "labels_override.json"
    overrides.write_text(json.dumps({"relabel": {f"val_{first:04d}": int(clean.dataset.targets[first])},
                                     "remove": ["val_0000"]}))
    fixed = data.get_dataset("val", overrides=overrides)
    assert len(fixed) == 999 and "val_0000" not in fixed.ids
    pos = fixed.ids.index(f"val_{first:04d}")
    assert fixed.labels[pos] == clean.dataset.targets[first]  # relabel restored the clean label


@needs_data
def test_atlas_uses_dataset_ids_and_noisy_labels():
    ds = data.get_dataset("val", overrides=None)
    _, meta, items = atlas.build(ds)
    assert [i["id"] for i in items] == ds.ids
    assert [i["label_idx"] for i in items] == ds.labels


@needs_data
def test_cifar10_adapter_passes_forge_check():
    result = run_checks(CIFAR)
    failed = [c for c in result["checks"] if not c["ok"]]
    assert result["ok"], failed
    assert {c["name"] for c in result["checks"]} >= {"profile", "graph", "samples", "predict", "classes_match_outputs"}


def test_check_reports_a_broken_adapter_instead_of_raising(tmp_path):
    (tmp_path / ".forge").mkdir()
    (tmp_path / ".forge" / "profile.json").write_text(json.dumps({"archetype": "image_classification"}))
    (tmp_path / ".forge" / "adapter.py").write_text(
        "from forge.adapter import Adapter\n"
        "class Broken(Adapter):\n"
        "    archetype = 'image_classification'\n"
        "    def build_model(self):\n"
        "        raise RuntimeError('model.py not found')\n")
    result = run_checks(tmp_path)
    assert not result["ok"]
    build = next(c for c in result["checks"] if c["name"] == "build_model")
    assert "model.py not found" in build["detail"]
    assert json.loads((tmp_path / ".forge" / "check.json").read_text())["ok"] is False
