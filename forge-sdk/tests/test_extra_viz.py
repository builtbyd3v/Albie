"""A7: extra charts from `<repo>/.forge/extra_viz.py` (figures -> PNG cards, dicts -> VizCards), plus the
`forge.evaluate` hook that runs them before ml.results_ready."""

import json
from pathlib import Path

import torch
import torch.nn as nn

import forge
from forge import viz

EXTRAS = '''\
class _Figure:
    def savefig(self, path, **kwargs):
        open(path, "wb").write(b"\\x89PNG")


def viz_residual_hist(ctx):
    return _Figure()


def viz_class_balance(ctx):
    return {"kind": "bar", "title": "Class balance", "x": ctx.classes, "series": [{"name": "n", "y": [1]}]}


def viz_boom(ctx):
    raise RuntimeError("bad chart")
'''


def make_repo(tmp_path, extras=EXTRAS, viz_ids=("residual_hist", "class_balance")):
    repo = tmp_path / "repo"
    (repo / ".forge").mkdir(parents=True)
    (repo / ".forge" / "profile.json").write_text(json.dumps({"extra_viz": [{"viz_id": v, "why": "w"} for v in viz_ids]}))
    if extras:
        (repo / ".forge" / "extra_viz.py").write_text(extras)
    run = tmp_path / "runs" / "run1"
    run.mkdir(parents=True)
    return repo, repo / ".forge", run


def test_find_forge_dir_walks_up(tmp_path):
    repo, forge_dir, _ = make_repo(tmp_path)
    nested = repo / "src" / "deep"
    nested.mkdir(parents=True)
    assert viz.find_forge_dir(nested) == forge_dir
    assert viz.find_forge_dir(forge_dir.parent) == forge_dir
    other = tmp_path / "elsewhere"
    other.mkdir()
    assert viz.find_forge_dir(other) is None


def test_only_the_classifier_picks_run_and_figures_become_pngs(tmp_path):
    extras = '''\
class Figure:
    def savefig(self, path, **kwargs):
        open(path, "wb").write(b"\\x89PNG")


def viz_residual_hist(ctx):
    return Figure()


def viz_class_balance(ctx):
    raise AssertionError("not picked, must not run")
'''
    repo, forge_dir, run = make_repo(tmp_path, extras=extras, viz_ids=("residual_hist",))
    memory = []
    original = viz._close
    viz._close = lambda figure: memory.append(figure)  # matplotlib may not be installed for the SDK's tests
    try:
        cards = viz.run_extra_viz(forge_dir, run, viz.VizContext(classes=["a", "b"]))
    finally:
        viz._close = original
    assert cards == []  # a figure is an image card, served from the PNG
    assert (run / "viz" / "residual_hist.png").read_bytes() == b"\x89PNG"
    assert not (run / "viz.json").exists()
    assert len(memory) == 1  # closed after saving


def test_dict_charts_are_written_as_vizcards(tmp_path):
    repo, forge_dir, run = make_repo(tmp_path)
    cards = viz.run_extra_viz(forge_dir, run, viz.VizContext(classes=["a", "b"]))
    assert [c["viz_id"] for c in cards] == ["class_balance"]  # boom failed, figure made a PNG
    card = cards[0]
    assert card["kind"] == "bar" and card["panel"] == "ml-viz" and card["title"] == "Class balance"
    assert "png_url" not in card  # the hub fills it, never the repo
    assert json.loads((run / "viz.json").read_text()) == cards


def test_a_broken_extras_file_is_a_warning_not_a_failure(tmp_path):
    repo, forge_dir, run = make_repo(tmp_path, extras="def viz_residual_hist(ctx):\n    return 1 / 0\n")
    warnings = []
    assert viz.run_extra_viz(forge_dir, run, viz.VizContext(), warn=warnings.append) == []
    assert any("failed" in warning and "ZeroDivisionError" in warning for warning in warnings)
    broken = tmp_path / "broken"
    broken.mkdir()
    (broken / ".forge").mkdir()
    (broken / ".forge" / "extra_viz.py").write_text("import does_not_exist\n")
    warnings.clear()
    run2 = tmp_path / "runs" / "run2"
    run2.mkdir()
    assert viz.run_extra_viz(broken / ".forge", run2, viz.VizContext(), warn=warnings.append) == []
    assert "failed to import" in warnings[0]


def test_no_extras_file_means_no_work(tmp_path):
    repo, forge_dir, run = make_repo(tmp_path, extras=None, viz_ids=())
    assert viz.run_extra_viz(forge_dir, run, viz.VizContext()) == []


def test_a_profile_that_picked_no_extras_runs_nothing(tmp_path):
    """A stale extra_viz.py from an earlier setup must not run when the current profile has no extras."""
    repo, forge_dir, run = make_repo(tmp_path, extras=EXTRAS, viz_ids=())
    assert viz.wanted_ids(forge_dir) == []
    assert viz.run_extra_viz(forge_dir, run, viz.VizContext()) == []
    assert not (run / "viz").exists()


def test_no_profile_falls_back_to_every_chart(tmp_path):
    """Hand-written extras without a profile: still useful (CIFAR-10's golden folder has a profile, but a
    user debugging by hand may not)."""
    repo, forge_dir, run = make_repo(tmp_path, viz_ids=())
    (forge_dir / "profile.json").unlink()
    cards = viz.run_extra_viz(forge_dir, run, viz.VizContext(classes=["a", "b"]))
    assert [c["viz_id"] for c in cards] == ["class_balance"]


class EchoModel(nn.Module):
    def __init__(self):
        super().__init__()
        self.dummy = nn.Parameter(torch.zeros(1))

    def forward(self, x):
        return nn.functional.one_hot(x[:, 0].long() % 3, 3).float() * 10 + self.dummy


class Indexed(torch.utils.data.Dataset):
    def __init__(self, n):
        self.n = n
        self.ids = [f"val_{i:04d}" for i in range(n)]

    def __len__(self):
        return self.n

    def __getitem__(self, i):
        return torch.full((2,), float(i)), i % 3


def test_evaluate_runs_extras_before_results_ready(tmp_path, monkeypatch):
    """The hook: a real run writes viz.json next to results.json, and the code path never raises."""
    repo, forge_dir, _ = make_repo(tmp_path)
    (forge_dir / "extra_viz.py").write_text(
        "def viz_class_balance(ctx):\n"
        "    assert ctx.probs is not None and len(ctx.ids) == 9 and ctx.classes == ['a', 'b', 'c']\n"
        "    return {'kind': 'bar', 'title': 'Balance', 'x': ctx.classes}\n")
    monkeypatch.chdir(repo)
    events = []
    original = forge.client.emit
    try:
        forge.client.emit = lambda kind, payload, **kw: (events.append(kind), original(kind, payload, **kw))
        forge.evaluate(EchoModel(), Indexed(9), classes=["a", "b", "c"])
    finally:
        forge.client.emit = original
    run = max((p for p in (tmp_path / "runs").iterdir() if p.is_dir()), key=lambda p: p.stat().st_mtime)
    cards = json.loads((run / "viz.json").read_text())
    assert [c["viz_id"] for c in cards] == ["class_balance"]
    assert events.index("ml.results_ready") > 0  # extras ran inside evaluate's finish(), before this event
    assert (run / "results.json").exists()
