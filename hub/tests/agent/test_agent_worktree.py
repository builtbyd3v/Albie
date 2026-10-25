"""Crew worktrees against a real temporary git repo: start from the main checkout's state, land back in it."""

from __future__ import annotations

import subprocess
from pathlib import Path

import pytest

from agent import runner


def git(repo: Path, *args: str) -> str:
    return subprocess.run(["git", "-C", str(repo), "-c", "user.email=t@t", "-c", "user.name=t", *args],
                          check=True, capture_output=True, text=True).stdout


@pytest.fixture()
def repo(tmp_path: Path, monkeypatch: pytest.MonkeyPatch) -> Path:
    repo = tmp_path / "repo"
    repo.mkdir()
    git(repo, "init", "-q")
    (repo / "a.txt").write_text("".join(f"{i}\n" for i in range(1, 31)))
    git(repo, "add", "-A")
    git(repo, "commit", "-q", "-m", "init")
    monkeypatch.delenv("AGENT_WORKTREE_DIR", raising=False)
    return repo


def edit(path: Path, old: str, new: str) -> None:
    path.write_text(path.read_text().replace(old, new, 1))


def test_worktree_starts_from_uncommitted_edits(repo: Path) -> None:
    edit(repo / "a.txt", "2\n", "two\n")  # Bit's edit, not committed
    path, branch, base = runner.create_task_worktree(repo, "tweak", "abcdef12", "builder")
    assert (path / "a.txt").read_text().startswith("1\ntwo\n")
    assert base != git(repo, "rev-parse", "HEAD").strip()  # the stash-create snapshot, not HEAD
    assert git(repo, "status", "--porcelain").strip() == "M a.txt"  # main checkout untouched
    assert git(repo, "stash", "list") == ""


def test_clean_worktree_is_based_on_head(repo: Path) -> None:
    _, _, base = runner.create_task_worktree(repo, "tweak", "abcdef12", "builder")
    assert base == git(repo, "rev-parse", "HEAD").strip()


def test_finished_work_lands_uncommitted_and_the_worktree_goes(repo: Path) -> None:
    edit(repo / "a.txt", "2\n", "two\n")
    head, current = git(repo, "rev-parse", "HEAD"), git(repo, "branch", "--show-current")
    path, branch, base = runner.create_task_worktree(repo, "tweak", "abcdef12", "builder")
    edit(path / "a.txt", "20\n", "twenty\n")
    git(path, "commit", "-q", "-am", "crew")
    (path / "new.txt").write_text("left uncommitted\n")  # e.g. Scribe didn't commit
    (path / "img.bin").write_bytes(bytes(range(256)))

    note = runner.land_task_worktree(repo, path, branch, base)
    assert note == "My changes are in your checkout: 3 files."
    text = (repo / "a.txt").read_text()
    assert "two\n" in text and "twenty\n" in text  # Bit's edit kept, the crew's added
    assert (repo / "new.txt").read_text() == "left uncommitted\n"
    assert (repo / "img.bin").read_bytes() == bytes(range(256))
    assert git(repo, "rev-parse", "HEAD") == head and git(repo, "branch", "--show-current") == current
    assert git(repo, "diff", "--cached", "--name-only") == ""  # plain modified files, nothing staged
    assert not path.exists() and branch not in git(repo, "branch", "--list", "pet/*")


def test_conflict_keeps_the_branch_and_names_it(repo: Path) -> None:
    path, branch, base = runner.create_task_worktree(repo, "tweak", "abcdef12", "fixer")
    edit(path / "a.txt", "20\n", "twenty\n")
    git(path, "commit", "-q", "-am", "crew")
    edit(repo / "a.txt", "20\n", "XX\n")  # meanwhile the user changed the same line

    note = runner.land_task_worktree(repo, path, branch, base)
    assert note is not None and branch in note and "conflicted" in note and "repo-crew/fixer-abcdef" in note
    assert "XX\n" in (repo / "a.txt").read_text() and "twenty" not in (repo / "a.txt").read_text()  # untouched
    assert path.exists() and branch in git(repo, "branch", "--list", "pet/*")


def test_no_changes_is_a_no_op(repo: Path) -> None:
    edit(repo / "a.txt", "2\n", "two\n")
    path, branch, base = runner.create_task_worktree(repo, "look around", "abcdef12", "shipper")
    assert runner.land_task_worktree(repo, path, branch, base) is None
    assert git(repo, "status", "--porcelain").strip() == "M a.txt"
    assert not path.exists()  # nothing to keep


def test_a_crew_code_turn_lands_and_says_so(repo: Path, monkeypatch: pytest.MonkeyPatch) -> None:
    """runner._run_code end to end with the SDK stream replaced: the member edits its worktree, the summary leads
    with where the work went, and the edit is in the main checkout."""
    import asyncio

    from claude_agent_sdk import ResultMessage

    from agent.config import ProjectConfig
    from agent.crew import TYPES

    async def stream(project, mode, text, control, pet_id="bit", agent=None, cwd=None):
        edit(cwd / "a.txt", "5\n", "five\n")  # left uncommitted
        yield ResultMessage(subtype="success", duration_ms=1, duration_api_ms=1, is_error=False, num_turns=1,
                            session_id="s", result="Spelled out five.")

    monkeypatch.setattr(runner, "_stream", stream)
    sent: list = []

    async def emit(event) -> None:
        sent.append(event)

    project = ProjectConfig(id="demo", path=repo)
    out = asyncio.run(runner.run_turn(project, "fix five", "code", emit, pet_id="builder", agent=TYPES["builder"]))
    assert out == "My changes are in your checkout: 1 file. Spelled out five."
    assert sent[-1].type == "agent.task_complete" and sent[-1].payload["summary"] == out
    assert "five\n" in (repo / "a.txt").read_text()
    assert git(repo, "worktree", "list").count("\n") == 1 and not any((repo.parent / "repo-crew").iterdir())
