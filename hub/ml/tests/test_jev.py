"""Jev as the classifier (PR #2's idea, ML_PLAN 6.4): candidates from the code, typed answers, no API key."""

import io
import json
import urllib.error
from pathlib import Path

import pytest

from ml import prescan as prescan_mod
from ml.setup import jev
from ml.setup.classify_jev import ARCHETYPES, classify_jev, extract
from ml.setup.llm import LLMError
from ml.tests.test_setup import PROFILE, FakeLLM, adapter_reply, repo, run_flow, statuses  # noqa: F401

CIFAR = Path(__file__).resolve().parents[3] / "ml-examples" / "cifar10"


def jev_reply(archetype="regression", confidence=0.93, probs=None, **picks):
    probs = probs or {a: (confidence if a == archetype else (1 - confidence) / 3) for a in ARCHETYPES}
    answers = {"archetype": {"type": "choice", "choice": archetype, "confidence": confidence, "probabilities": probs},
               "framework": {"type": "choice", "choice": picks.pop("framework", "other"), "confidence": 0.8}}
    for name, value in picks.items():
        answers[name] = {"type": "choice", "choice": value, "confidence": 0.9}
    return {"model": "jev-1.14.0", "answers": answers, "usage": {"input_tokens": 1234, "output_tokens": 9}}


class FakeJev:
    def __init__(self, reply):
        self.reply, self.calls = reply, []

    def __call__(self, state, questions, timeout=60):
        self.calls.append({"state": state, "questions": questions})
        return self.reply


def test_candidates_come_from_the_real_cifar10_code():
    files = prescan_mod.read_files(CIFAR, ["train.py", "model.py", "data.py"])
    found = extract(files)
    assert found["models"] == ["model.py:SmallCNN"]
    assert "data.py:get_loaders" in found["loaders"]
    assert found["classes"][3] == "cat" and len(found["classes"]) == 10  # from CLASSES in data.py, not Jev


def test_classify_with_jev_builds_a_full_profile(repo):
    hints = prescan_mod.prescan(repo)
    files = prescan_mod.read_files(repo, hints["files_sent"])
    ask = FakeJev(jev_reply(train_file="train.py", data_entry="data.py:load", target=extract(files)["columns"][0]
                            if extract(files)["columns"] else "none"))
    profile, usage = classify_jev(repo, hints, files, ask)
    assert profile.archetype == "regression" and profile.confidence == 0.93 and profile.candidates == []
    assert profile.entry_points.train == "train.py" and profile.entry_points.data == "data.py:load"
    assert profile.train_cmd == "python train.py" and profile.summary.startswith("Regression model")
    assert usage == [{"input_tokens": 1234, "output_tokens": 9}]
    questions = ask.calls[0]["questions"]
    assert set(questions["archetype"]["criteria"]) == set(ARCHETYPES)
    assert all(len(q["criteria"]) <= jev.MAX_CRITERIA for q in questions.values())
    assert "actually does" in questions["archetype"]["instructions"]  # evidence in code, not usefulness
    assert "<file path=\"train.py\">" in ask.calls[0]["state"]


def test_low_confidence_offers_the_top_two(repo):
    hints = prescan_mod.prescan(repo)
    probs = {"regression": 0.45, "generic": 0.35, "image_classification": 0.15, "text_classification": 0.05}
    profile, _ = classify_jev(repo, hints, {}, FakeJev(jev_reply(confidence=0.45, probs=probs)))
    assert profile.candidates == ["regression", "generic"]


def test_setup_flow_with_jev_classifier_and_claude_adapter(repo):
    llm = FakeLLM([], [adapter_reply()])  # no classify replies: Jev does that
    ask = FakeJev(jev_reply(train_file="train.py"))
    _, ws, events = run_flow(repo, llm, backend="jev", jev_ask=ask)
    assert statuses(events) == ["scanning", "classifying", "adapting", "needs_approval"]
    assert [c["kind"] for c in llm.calls] == ["adapt"] and len(ask.calls) == 1
    assert json.loads((repo / ".forge" / "profile.json").read_text())["notes"].startswith("classified by jev")
    ws.__exit__(None, None, None)


def test_without_an_anthropic_key_jev_still_classifies_then_setup_says_why_it_stopped(repo):
    def no_key(*args, **kwargs):
        raise LLMError("no valid Anthropic credentials: set ANTHROPIC_API_KEY in the hub's environment")

    _, ws, events = run_flow(repo, no_key, backend="jev", jev_ask=FakeJev(jev_reply(train_file="train.py")))
    failed = [e["payload"] for e in events if e["type"] == "ml.project_state"][-1]
    assert failed["status"] == "failed" and "ANTHROPIC_API_KEY" in failed["error"]
    assert json.loads((repo / ".forge" / "profile.json").read_text())["archetype"] == "regression"
    ws.__exit__(None, None, None)


def test_jev_errors_become_readable(monkeypatch):
    monkeypatch.delenv("TYPESAFE_API_KEY", raising=False)
    with pytest.raises(LLMError, match="TYPESAFE_API_KEY"):
        jev.ask("state", {})
    monkeypatch.setenv("TYPESAFE_API_KEY", "bad")

    def reject(req, timeout):
        raise urllib.error.HTTPError(req.full_url, 401, "Unauthorized", {}, io.BytesIO(b"{}"))

    monkeypatch.setattr(jev.urllib.request, "urlopen", reject)
    with pytest.raises(LLMError, match="rejected the API key"):
        jev.ask("state", {"q": {"type": "noul", "instructions": "x"}})


def test_jev_request_shape(monkeypatch):
    monkeypatch.setenv("TYPESAFE_API_KEY", "k")
    sent = {}

    class Resp(io.BytesIO):
        def __enter__(self):
            return self

        def __exit__(self, *exc):
            return False

    def capture(req, timeout):
        sent.update(url=req.full_url, headers=dict(req.header_items()), body=json.loads(req.data))
        return Resp(json.dumps({"model": "jev-1.14.0", "answers": {}}).encode())

    monkeypatch.setattr(jev.urllib.request, "urlopen", capture)
    jev.ask("x" * 400_000, {"q": {"type": "noul", "instructions": "y"}})
    assert sent["url"] == "https://api.typesafe.ai/v1/systemone"
    assert sent["headers"]["Authorization"] == "Bearer k" and sent["body"]["model"] == "jev-1.14.0"
    assert sent["headers"]["Typesafe-danger-zone-long-context"] == "true"  # >60k tokens
