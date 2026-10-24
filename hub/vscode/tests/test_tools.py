import pytest

from vscode import tools
from vscode.tests.fake_extension import fake_extension, project_root  # noqa: F401 - pytest fixtures


def test_errors_path_is_quoted(fake_extension):
    tools.get_errors("my dir/train.py")
    assert fake_extension == [("GET", "/errors?path=my%20dir/train.py", None)]


def test_highlight_defaults_end_to_start(fake_extension, project_root):
    tools.highlight("train.py", 40, note="lr too high")
    assert fake_extension[0][2] == {"path": str(project_root / "train.py"), "start": 40, "end": 40,
                                    "note": "lr too high", "ms": 4000}


def test_relative_paths_resolve_against_the_project(fake_extension, project_root):
    """VS Code may be open on a subfolder (Unity opens unity/), so repo-relative paths go out absolute."""
    tools.open_file("hub/agent/ws.py", 3)
    tools.open_file("/elsewhere/x.py")
    assert [b["path"] for _, _, b in fake_extension] == [str(project_root / "hub/agent/ws.py"), "/elsewhere/x.py"]


def test_unknown_project_leaves_the_path_alone(fake_extension, monkeypatch, project_root):
    monkeypatch.setenv("DEFAULT_PROJECT", "nope")
    tools.open_file("train.py")
    assert fake_extension[0][2]["path"] == "train.py"


def test_refused_command_raises_with_extension_message(fake_extension):
    with pytest.raises(RuntimeError, match="command not allowed"):
        tools.run_command("x")
