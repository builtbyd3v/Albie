import pytest

from vscode import tools
from vscode.tests.fake_extension import fake_extension  # noqa: F401 - pytest fixture


def test_errors_path_is_quoted(fake_extension):
    tools.get_errors("my dir/train.py")
    assert fake_extension == [("GET", "/errors?path=my%20dir/train.py", None)]


def test_highlight_defaults_end_to_start(fake_extension):
    tools.highlight("train.py", 40, note="lr too high")
    assert fake_extension[0][2] == {"path": "train.py", "start": 40, "end": 40, "note": "lr too high", "ms": 4000}


def test_refused_command_raises_with_extension_message(fake_extension):
    with pytest.raises(RuntimeError, match="command not allowed"):
        tools.run_command("x")
