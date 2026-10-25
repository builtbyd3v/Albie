"""ml_github: "Albie, pull up <repo>" finds a GitHub repo, waits for a yes, clones it and opens it on the ML desk."""
import asyncio
import subprocess
from types import SimpleNamespace

import pytest

from events import EventBus
from ml import github
from ml.service import MLService, NotFound
from ml.tools import MLTools


def test_parse_reads_owner_repo_and_urls_but_not_plain_words():
    assert github.parse("kuangliu/pytorch-cifar") == "kuangliu/pytorch-cifar"
    assert github.parse("https://github.com/kuangliu/pytorch-cifar.git") == "kuangliu/pytorch-cifar"
    assert github.parse("github.com/kuangliu/pytorch-cifar/") == "kuangliu/pytorch-cifar"
    assert github.parse("pytorch cifar classifier") is None


def fake_gh(calls):
    def gh(path, params=None):
        calls.append((path, params))
        if path == "search/repositories":
            return {"items": [{"full_name": "kuangliu/pytorch-cifar", "description": "95% on CIFAR10",
                               "stargazers_count": 6000}] if "cifar" in params["q"] else []}
        return {"full_name": path.removeprefix("repos/"), "description": None, "stargazers_count": 3}
    return gh


def test_find_searches_words_and_looks_up_an_exact_name():
    calls = []
    assert github.find("pytorch cifar", fake_gh(calls)) == {
        "full_name": "kuangliu/pytorch-cifar", "description": "95% on CIFAR10", "stars": 6000}
    assert github.find("me/mine", fake_gh(calls))["full_name"] == "me/mine"
    assert calls[1][0] == "repos/me/mine"
    with pytest.raises(NotFound, match="no GitHub repo"):
        github.find("unicorn", fake_gh(calls))


def make_origin(tmp_path):
    origin = tmp_path / "remote" / "me" / "tiny.git"
    origin.mkdir(parents=True)
    (origin / "train.py").write_text("print('hi')\n")
    git = ["git", "-C", str(origin), "-c", "user.name=t", "-c", "user.email=t@t"]
    subprocess.run(["git", "init", "-q", str(origin)], check=True)
    subprocess.run(git + ["add", "."], check=True)
    subprocess.run(git + ["commit", "-qm", "init"], check=True)
    return f"file://{tmp_path / 'remote'}/"


def test_clone_downloads_once_and_links_the_shared_venv(tmp_path, monkeypatch):
    base = make_origin(tmp_path)
    venv = tmp_path / "shared-venv"
    venv.mkdir()
    monkeypatch.setenv("FORGE_ML_VENV", str(venv))
    dest = github.clone("me/tiny", tmp_path / "clones", base)
    assert (dest / "train.py").exists() and dest.name == "tiny"
    assert (dest / ".venv").resolve() == venv  # the repo trains with the torch env, not the hub's Python
    (dest / "train.py").write_text("edited\n")
    assert github.clone("me/tiny", tmp_path / "clones", base) == dest  # already here: no second download
    assert (dest / "train.py").read_text() == "edited\n"


def test_ml_github_asks_first_then_clones_and_opens(tmp_path, monkeypatch):
    base = make_origin(tmp_path)
    monkeypatch.setattr(github, "gh", fake_gh([]))
    monkeypatch.setattr(github, "BASE", base)
    tools = MLTools(SimpleNamespace(bus=EventBus(), ml=MLService(tmp_path)), tmp_path / "data", None)
    opened = []

    async def fake_open(name):
        opened.append(name)
        return {"project": "tiny", "status": "classifying"}
    monkeypatch.setattr(tools, "open", fake_open)

    async def flow():
        offer = await tools.call("ml_github", {"query": "me/tiny"})
        assert offer["full_name"] == "me/tiny" and "confirm" in offer["note"] and not opened  # nothing cloned yet
        done = await tools.call("ml_github", {"query": "me/tiny", "confirm": True})
        assert done == {"repo": "me/tiny", "project": "tiny", "status": "classifying"}
        assert opened == [str(tmp_path / "data" / "ml" / "github" / "tiny")]
        assert "error" in await tools.call("ml_github", {"query": ""})
    asyncio.run(flow())
