"""GitHub importer API and approved-worker wiring; external GitHub is stubbed, repository code is never run."""

import base64
import sys
from pathlib import Path

from fastapi import FastAPI
from fastapi.testclient import TestClient

from events import EventBus
from ml import github_import
from ml.router import router
from ml.service import MLService


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


def test_import_rejects_unsupported_format_without_starting_training(monkeypatch, tmp_path):
    def response(path, *, ref=None):
        if path == "/repos/owner/demo":
            return {"default_branch": "main", "private": False}
        if path.endswith("/contents"):  # repository root listing
            return [{"name": name, "type": "file"} for name in github_import.SUPPORTED_FILES]
        sources = {
            "train.py": "print('hello')\n", "model.py": "class Other: pass\n",
            "data.py": "def get_loaders(): pass\n", "requirements.txt": "numpy\n",
        }
        filename = path.rsplit("/", 1)[-1]
        content = base64.b64encode(sources[filename].encode()).decode()
        return {"type": "file", "size": len(sources[filename]), "encoding": "base64", "content": content,
                "sha": "fixture-sha"}

    monkeypatch.setattr(github_import, "_get_json", response)
    with TestClient(make_app(tmp_path / "repo")) as client:
        result = client.post("/ml/github/import", json={"url": "https://github.com/owner/demo"})
        assert result.status_code == 200 and not result.json()["supported"]
        denied = client.post("/ml/github/train", json={"import_id": result.json()["import_id"], "epochs": 5})
        assert denied.status_code == 409


def test_github_presets_default_and_env(monkeypatch):
    from ml.router import DEFAULT_GITHUB_PRESETS, github_presets

    monkeypatch.delenv("FORGE_ML_GITHUB_PRESETS", raising=False)
    assert github_presets() == DEFAULT_GITHUB_PRESETS
    monkeypatch.setenv("FORGE_ML_GITHUB_PRESETS", "Ours|https://github.com/a/b; bad|http://x ;Two|https://github.com/c/d/tree/main/e")
    assert github_presets() == [{"label": "Ours", "url": "https://github.com/a/b"},
                                {"label": "Two", "url": "https://github.com/c/d/tree/main/e"}]
