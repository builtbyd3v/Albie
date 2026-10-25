"""GitHub import: the CIFAR-10 preset (static, trains the checked-in copy) and any other repo (clone + the
cold-open setup, then its own train_cmd). GitHub and git are stubbed; the setup's forge.check runs for real."""

import base64
import shutil
import sys
from pathlib import Path

import pytest
from fastapi import FastAPI
from fastapi.testclient import TestClient

from events import EventBus
from ml import github_import
from ml.router import router
from ml.service import MLService
from ml.tests.test_api import make_app as make_app_with_ws
from ml.tests.test_setup import PROFILE, FakeLLM, adapter_reply, repo  # noqa: F401 - repo is a fixture


def make_app(repo: Path) -> FastAPI:
    app = FastAPI()
    app.state.bus = EventBus()
    app.state.ml = MLService(repo)
    app.include_router(router)
    return app


def test_import_endpoint_inspects_folder_and_train_starts_only_approved_worker(monkeypatch, tmp_path):
    sources = {
        "train.py": "from model import SmallCNN\nfrom data import get_loaders\n",
        "model.py": "import torch.nn as nn\nclass SmallCNN(nn.Module):\n    pass\n",
        "data.py": "import torchvision.datasets as datasets\ndef get_loaders():\n    return datasets.CIFAR10\n",
        "requirements.txt": "torch\ntorchvision\n",
    }

    def response(path, *, ref=None):
        if path == "/repos/owner/demo":
            return {"default_branch": "main", "private": False}
        if path == "/repos/owner/demo/contents/ml-examples/cifar10":
            return [{"name": name, "type": "file"} for name in sources]
        filename = path.rsplit("/", 1)[-1]
        content = base64.b64encode(sources[filename].encode()).decode()
        return {"type": "file", "size": len(sources[filename]), "encoding": "base64", "content": content,
                "sha": "fixture-sha"}

    monkeypatch.setattr(github_import, "_get_json", response)
    monkeypatch.setattr(MLService, "python", lambda self: sys.executable)
    popen_calls = []

    class Child:
        def poll(self):
            return None

    def popen(argv, **kwargs):
        popen_calls.append((argv, kwargs))
        return Child()

    monkeypatch.setattr("ml.service.subprocess.Popen", popen)
    with TestClient(make_app(tmp_path / "repo")) as client:
        imported = client.post("/ml/github/import", json={
            "url": "https://github.com/owner/demo/tree/main/ml-examples/cifar10",
        })
        assert imported.status_code == 200
        result = imported.json()
        assert result["supported"] and result["model"] == "SmallCNN" and result["dataset"] == "CIFAR-10"
        assert set(result["files"]) == set(sources)
        assert result["mode"] == "preset" and result["runs"].startswith("ml-examples/cifar10")  # says what really trains

        started = client.post("/ml/github/train", json={"import_id": result["import_id"], "epochs": 5})
        assert started.status_code == 200
        assert started.json()["run_id"]

    argv, kwargs = popen_calls[0]
    assert Path(argv[1]).name == "training_worker.py"
    assert argv[argv.index("--dataset") + 1] == "cifar10"
    assert argv[argv.index("--model") + 1] == "smallcnn"
    assert argv[argv.index("--epochs") + 1] == "5"
    assert kwargs["cwd"].name == "gatorhack-dubz"
    assert "FORGE_RUN_ID" in kwargs["env"] and "FORGE_HUB" in kwargs["env"]


def _not_the_preset(path, *, ref=None):
    """GitHub API stub: a public repo whose root doesn't have the preset's four files."""
    if path == "/repos/owner/linreg":
        return {"default_branch": "main", "private": False, "size": 12}
    if path.endswith("/contents"):
        return [{"name": "train.py", "type": "file"}, {"name": "data.py", "type": "file"}]
    raise AssertionError(f"unexpected GitHub call {path}")


def test_any_other_repo_is_cloned_set_up_and_trains_its_own_code(monkeypatch, repo):
    """Issue #70: not the preset -> clone -> the cold-open setup -> Accept -> Train runs the repo's train_cmd."""
    monkeypatch.setattr(github_import, "_get_json", _not_the_preset)
    cloned = {"import_id": "abc123def456", "repository": "owner/linreg", "url": "https://github.com/owner/linreg",
              "ref": "main", "path": str(repo)}
    monkeypatch.setattr("ml.service.clone_repository", lambda url: dict(cloned))
    app = make_app_with_ws(tmp_repo := repo.parent / "unused")
    app.state.ml_llm = FakeLLM([PROFILE], [adapter_reply()])
    app.state.ml_backend = "claude"
    with TestClient(app) as client, client.websocket_connect("/ws") as ws:
        imported = client.post("/ml/github/import", json={"url": "https://github.com/owner/linreg"})
        assert imported.status_code == 200, imported.text
        result = imported.json()
        assert result["supported"] and result["mode"] == "clone" and result["path"] == str(repo)
        assert "its own code" in result["runs"] and result["format"] == "cold-open"
        seen = []
        while not seen or seen[-1]["status"] not in ("needs_approval", "ready", "failed"):
            event = ws.receive_json()
            if event["type"] == "ml.project_state":
                seen.append(event["payload"])
        assert [s["status"] for s in seen] == ["scanning", "classifying", "adapting", "needs_approval"]
        assert all(s["repository"] == "owner/linreg" for s in seen)  # the panel says which repo it is

        early = client.post("/ml/github/train", json={"import_id": result["import_id"], "epochs": 3})
        assert early.status_code == 409 and "isn't ready" in early.json()["detail"]
        plain = client.post("/ml/train", json={"epochs": 3})  # the plain Train route can't skip Accept either
        assert plain.status_code == 409 and "accept or skip" in plain.json()["detail"]

        assert client.post("/ml/project/patch", json={"accept": True}).json() == {"applied": ["train.py"]}
        state = client.get("/ml/project").json()
        assert state["status"] == "ready" and state["source"] == "https://github.com/owner/linreg"

        popen_calls = []

        class Child:
            def poll(self):
                return None

        monkeypatch.setattr("ml.service.subprocess.Popen", lambda argv, **kw: popen_calls.append((argv, kw)) or Child())
        started = client.post("/ml/github/train", json={"import_id": result["import_id"], "epochs": 3})
        assert started.status_code == 200, started.text
    argv, kwargs = popen_calls[0]
    assert argv[1:] == ["train.py"] and Path(kwargs["cwd"]) == repo  # the cloned repo's own command, in its folder
    assert not tmp_repo.exists()


def test_clone_failure_is_a_clear_422(monkeypatch, tmp_path):
    monkeypatch.setattr(github_import, "_get_json", _not_the_preset)

    def refuse(url):
        raise github_import.GitHubImportError("The repository has a link pointing outside itself (data); it wasn't imported.")

    monkeypatch.setattr("ml.service.clone_repository", refuse)
    with TestClient(make_app(tmp_path / "repo")) as client:
        r = client.post("/ml/github/import", json={"url": "https://github.com/owner/linreg"})
        assert r.status_code == 422 and "link pointing outside" in r.json()["detail"]
        assert client.get("/ml/project").json()["status"] == "idle"  # nothing was opened


def _fake_git(source: Path):
    """Stands in for git: `clone` copies `source` (as if fetched), `sparse-checkout set <dir>` keeps only <dir>."""
    calls = []

    def git(args, cwd=None):
        calls.append(args)
        if args[0] == "clone":
            shutil.copytree(source, args[-1])
            (Path(args[-1]) / ".git").mkdir()
        elif args[0] == "sparse-checkout":
            keep = args[-1]
            for child in Path(cwd).iterdir():
                if child.name not in (".git", keep.split("/")[0]):
                    shutil.rmtree(child) if child.is_dir() else child.unlink()

    return git, calls


def test_clone_repository_shallow_folder_reuse_and_limits(monkeypatch, tmp_path):
    source = tmp_path / "remote"
    (source / "mnist").mkdir(parents=True)
    (source / "mnist" / "main.py").write_text("print('train')\n")
    (source / "other").mkdir()
    (source / "other" / "big.bin").write_bytes(b"x" * 10)
    monkeypatch.setenv("FORGE_ML_IMPORT_DIR", str(tmp_path / "imports"))
    monkeypatch.setattr(github_import, "_get_json", lambda path, ref=None: {"default_branch": "main", "private": False, "size": 10**9})
    git, calls = _fake_git(source)
    monkeypatch.setattr(github_import, "_git", git)

    # A whole repo over the size limit is refused before cloning; one folder of it is fine (sparse).
    with pytest.raises(github_import.GitHubImportError, match="import limit"):
        github_import.clone_repository("https://github.com/owner/examples")
    assert calls == []
    got = github_import.clone_repository("https://github.com/owner/examples/tree/main/mnist")
    assert Path(got["path"]).name == "mnist" and (Path(got["path"]) / "main.py").exists()
    assert Path(got["path"]).is_relative_to(tmp_path / "imports")
    clone_args = calls[0]
    assert clone_args[:6] == ["clone", "--depth", "1", "--single-branch", "--no-tags", "--branch"]
    assert "--sparse" in clone_args and clone_args[-2] == "https://github.com/owner/examples.git"
    assert calls[1] == ["sparse-checkout", "set", "--", "mnist"]
    assert not (Path(got["path"]).parent / "other").exists()

    # Imported again: the same folder (and its .forge/ setup) is reused, no second clone.
    assert github_import.clone_repository("https://github.com/owner/examples/tree/main/mnist") == got
    assert len(calls) == 2

    monkeypatch.setattr(github_import, "_get_json", lambda path, ref=None: {"private": True})
    with pytest.raises(github_import.GitHubImportError, match="public"):
        github_import.clone_repository("https://github.com/owner/secret")


def test_checkout_guardrails(tmp_path, monkeypatch):
    root = tmp_path / "checkout"
    root.mkdir()
    with pytest.raises(github_import.GitHubImportError, match="no Python files"):
        github_import._check_checkout(root)
    (root / "train.py").write_text("print(1)\n")
    github_import._check_checkout(root)
    (root / "secrets").symlink_to(Path.home())
    with pytest.raises(github_import.GitHubImportError, match="pointing outside"):
        github_import._check_checkout(root)
    (root / "secrets").unlink()
    (root / "weights.bin").write_bytes(b"x" * 64)
    monkeypatch.setattr(github_import, "MAX_CHECKOUT_BYTES", 32)
    with pytest.raises(github_import.GitHubImportError, match="over"):
        github_import._check_checkout(root)


def test_github_presets_default_and_env(monkeypatch):
    from ml.router import DEFAULT_GITHUB_PRESETS, github_presets

    monkeypatch.delenv("FORGE_ML_GITHUB_PRESETS", raising=False)
    assert github_presets() == DEFAULT_GITHUB_PRESETS
    monkeypatch.setenv("FORGE_ML_GITHUB_PRESETS", "Ours|https://github.com/a/b; bad|http://x ;Two|https://github.com/c/d/tree/main/e")
    assert github_presets() == [{"label": "Ours", "url": "https://github.com/a/b"},
                                {"label": "Two", "url": "https://github.com/c/d/tree/main/e"}]


def test_dataset_follows_the_training_run_ids(tmp_path):
    """An LLM adapter can number samples val_00000 while forge.watch in the training script says val_0000: the
    run's own dataset view wins, so its results and live predictions line up with the dots on screen."""
    import json

    repo = tmp_path / "mnist"
    (repo / ".forge" / "project").mkdir(parents=True)
    (repo / ".forge" / "project" / "dataset.json").write_text(json.dumps(
        [{"id": f"val_{i:05d}", "name": f"val_{i:05d}.png", "kind": "image", "label_idx": i % 10} for i in range(3)]))
    run = repo / ".forge" / "runs" / "abc12345"
    run.mkdir(parents=True)
    (run / "dataset.json").write_text(json.dumps(
        [{"id": f"val_{i:04d}", "name": f"val_{i:04d}", "kind": "image", "label_idx": i % 10} for i in range(3)]))
    with TestClient(make_app(repo)) as client:
        assert [item["id"] for item in client.get("/ml/dataset").json()] == ["val_0000", "val_0001", "val_0002"]
