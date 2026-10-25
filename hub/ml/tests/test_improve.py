"""Albie improves the model: diagnose the last run, propose a change in .forge/improve/, apply on yes, retrain."""

import asyncio
import json
import shutil
import sys
import textwrap
import time
from pathlib import Path
from types import SimpleNamespace

import pytest

from events import EventBus
from ml import improve, logtail
from ml.service import MLService
from ml.setup.adapt import GOLDEN
from ml.setup.llm import LLMError, LLMResult
from ml.tools import MLTools, spoken


def reply(data: dict) -> LLMResult:
    return LLMResult(data=data, content=[{"type": "text", "text": json.dumps(data)}], usage={})


def write_run(repo: Path, run_id: str, val: list[float], train: list[float]) -> None:
    run = repo / ".forge" / "runs" / run_id
    run.mkdir(parents=True)
    (run / "run.json").write_text(json.dumps({"run_id": run_id, "total_epochs": len(val)}))
    events = [{"t": 10.0 * i, "type": "ml.metrics",
               "payload": {"run_id": run_id, "epoch": i + 1, "train_acc": t, "val_acc": v, "train_loss": 1.0, "val_loss": 1.2}}
              for i, (v, t) in enumerate(zip(val, train))]
    events.append({"t": 100.0, "type": "ml.run_state",
                   "payload": {"run_id": run_id, "status": "done", "epoch": len(val), "total_epochs": len(val)}})
    (run / "events.jsonl").write_text("".join(json.dumps(e) + "\n" for e in events))
    (run / "results.json").write_text("{}")


@pytest.fixture()
def cifar(tmp_path) -> Path:
    """A copy of the golden example's code and profile (no data), with one finished, overfitting run."""
    repo = tmp_path / "cifar10"
    repo.mkdir()
    for name in ("train.py", "model.py", "data.py"):
        shutil.copyfile(GOLDEN / name, repo / name)
    (repo / ".forge").mkdir()
    shutil.copyfile(GOLDEN / ".forge" / "profile.json", repo / ".forge" / "profile.json")
    write_run(repo, "base0001", val=[0.45, 0.55, 0.6, 0.605], train=[0.5, 0.7, 0.85, 0.9])
    return repo


def test_diagnose_reads_the_gap_and_the_epoch_time(cifar):
    d = improve.diagnose(MLService(cifar))
    assert d["run_id"] == "base0001" and d["best_val_acc"] == 0.605 and d["train_val_gap"] == pytest.approx(0.295)
    assert d["seconds_per_epoch"] == 10.0
    assert improve.headline(d) == "60.5% validation accuracy after 4 epochs"


def test_scripted_change_for_the_golden_example_applies_and_undoes(cifar, monkeypatch):
    monkeypatch.setenv("FORGE_ML_IMPROVE", "scripted")
    svc, before = MLService(cifar), (cifar / "train.py").read_text()
    plan = improve.propose(svc)
    assert plan["source"] == "scripted" and "overfitting: 90% on training but 60% on validation" in plan["diagnosis"]
    assert (cifar / "train.py").read_text() == before  # a proposal never touches the repo

    improve.apply(svc)
    assert "OneCycleLR" in (cifar / "train.py").read_text()
    assert svc.profile()["train_cmd"] == "python train.py --epochs 10 --full"
    assert improve.plan(svc)["applied"]

    assert improve.undo(svc) == ["train.py"]
    assert (cifar / "train.py").read_text() == before and svc.profile()["train_cmd"] == "python train.py --epochs 10"
    assert improve.plan(svc) == {}


def test_claude_failing_falls_back_to_the_scripted_change_only_for_the_golden_example(cifar, tmp_path):
    def down(*_, **__):
        raise LLMError("no valid Anthropic credentials")

    assert improve.propose(MLService(cifar), llm=down)["source"] == "scripted"
    (cifar / "train.py").write_text((cifar / "train.py").read_text() + "\n# my own edit\n")
    improve.undo(MLService(cifar))
    shutil.rmtree(cifar / ".forge" / "improve")
    with pytest.raises(improve.ImproveError, match="credentials"):
        improve.propose(MLService(cifar), llm=down)


def test_a_reply_that_drops_a_forge_line_goes_back_once(cifar):
    original = (cifar / "train.py").read_text()
    broken = original.replace("forge.evaluate(model, val_loader)  # forge", "print('done')")
    fixed = original.replace("default=1e-3", "default=2e-3")
    replies = iter([broken, fixed])
    seen = []

    def llm(system, messages, schema, **_):
        seen.append(messages[-1]["content"])
        return reply({"diagnosis": "Learning rate too low.", "changes": [{"title": "Higher LR", "why": "faster"}],
                      "patches": [{"path": "train.py", "content": next(replies)}], "train_args": [], "epochs": 10,
                      "expected": "a bit better"})

    plan = improve.propose(MLService(cifar), llm=llm)
    assert plan["source"] == "claude" and "dropped Forge lines" in seen[1]
    assert "default=2e-3" in (cifar / ".forge" / "improve" / "train.py").read_text()


def test_apply_refuses_a_file_changed_after_the_proposal(cifar, monkeypatch):
    monkeypatch.setenv("FORGE_ML_IMPROVE", "scripted")
    svc = MLService(cifar)
    improve.propose(svc)
    (cifar / "train.py").write_text("print('edited by hand')\n")
    with pytest.raises(improve.ImproveError, match="changed after"):
        improve.apply(svc)


TRAIN = textwrap.dedent('''
    import argparse, time
    from pathlib import Path
    parser = argparse.ArgumentParser()
    parser.add_argument("--epochs", type=int)
    parser.add_argument("--wider", action="store_true")
    args = parser.parse_args()
    base = BASE
    out = Path("lightning_logs/version_0/metrics.csv")
    out.parent.mkdir(parents=True, exist_ok=True)
    lines = ["epoch,train_acc,val_acc"]
    for e in range(args.epochs):
        time.sleep(0.15)
        lines.append(f"{e},{base + 0.1 * e},{base + 0.05 * e}")
        out.write_text("\\n".join(lines) + "\\n")
''')


def test_albie_trains_improves_applies_and_says_the_new_score(tmp_path, monkeypatch):
    monkeypatch.setattr(logtail, "POLL_S", 0.05)
    repo = tmp_path / "repo"
    (repo / ".forge").mkdir(parents=True)
    (repo / ".forge" / "profile.json").write_text(json.dumps(
        {"archetype": "generic", "python": sys.executable, "train_cmd": "python train.py --epochs 10"}))
    (repo / "train.py").write_text(TRAIN.replace("BASE", "0.5"))
    better = TRAIN.replace("BASE", "0.5 + (0.3 if args.wider else 0)")

    def llm(system, messages, schema, **_):
        assert "run_diagnosis" in messages[0]["content"][0]["text"]
        return reply({"diagnosis": "It plateaus early.", "changes": [{"title": "A wider model", "why": "more capacity"},
                                                                      {"title": "Longer warmup", "why": "steadier"}],
                      "patches": [{"path": "train.py", "content": better}], "train_args": ["--wider"], "epochs": 3,
                      "expected": "about 85%"})

    said: list[str] = []

    async def speak(pet_id, text):
        said.append(text)
        return True

    async def until(cond, timeout=30.0):
        end = time.monotonic() + timeout
        while not cond():
            assert time.monotonic() < end, said
            await asyncio.sleep(0.05)

    async def flow():
        state = SimpleNamespace(bus=EventBus(), ml=MLService(repo), ml_llm=llm)
        tools = MLTools(state, tmp_path / "data", speak)
        tools._open_diff = lambda plan: None  # no VS Code in tests

        assert "error" in await tools.call("ml_improve", {})  # nothing to look at before the first run
        assert (await tools.call("ml_train", {"epochs": 3}))["epochs"] == 3
        await until(lambda: said)
        assert said[-1] == "Done! 60.0% validation accuracy after 3 epochs."

        assert (await tools.call("ml_improve", {}))["status"] == "started"
        await until(lambda: len(said) == 2)
        assert said[1].startswith("It plateaus early. I'd change it to use a wider model and longer warmup. I expect about 85%.")
        assert "0.3 if" not in (repo / "train.py").read_text()  # proposed, not applied

        applied = await tools.call("ml_apply", {})
        assert applied["applied"] == ["train.py"] and applied["epochs"] == 3
        assert json.loads((repo / ".forge" / "profile.json").read_text())["train_cmd"].endswith("--wider")
        await until(lambda: len(said) == 3)
        assert said[2] == "Done! Validation accuracy went up from 60.0% to 90.0%."

        assert (await tools.call("ml_status", {}))["improvement"]["applied"]
        assert (await tools.call("ml_undo", {}))["restored"] == ["train.py"]
        assert "0.3 if" not in (repo / "train.py").read_text()
        assert (await tools.call("ml_status", {}))["best_val_acc"] == 0.6  # the improved run is set aside
        assert len(list((repo / ".forge" / "runs-undone").iterdir())) == 1

    asyncio.run(flow())


def test_open_finds_a_project_by_name_and_remembers_it(tmp_path, cifar, monkeypatch):
    monkeypatch.setenv("FORGE_ML_PROJECTS", str(cifar.parent))
    tools = MLTools(SimpleNamespace(bus=EventBus(), ml=MLService(tmp_path)), tmp_path / "data", None)
    assert tools.resolve("CIFAR 10") == cifar
    tools.remember(cifar)
    assert tools.resolve("") == cifar  # "the ML stuff I've been working on"
    with pytest.raises(Exception, match="no ML project called"):
        tools.resolve("imagenet-transformer")


def test_the_spoken_summary_stays_short_even_when_the_reply_rambles():
    plan = {"diagnosis": "It's overfitting: 76% vs 58%. Cat is weak. Dog too. And deer.",
            "changes": [{"title": "AdamW with weight decay", "why": ""}, {"title": "Random crops and flips.", "why": ""}],
            "expected": "I expect around 75%, up from 58%. Each epoch takes 10 s."}
    assert spoken(plan) == ("It's overfitting: 76% vs 58%. Cat is weak. I'd change it to use AdamW with weight decay and "
                            "random crops and flips. I expect around 75%, up from 58%. The change is open in VS Code. "
                            "Want me to apply it and retrain?")
