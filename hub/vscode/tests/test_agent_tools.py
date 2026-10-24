import asyncio

from vscode import tools
from vscode.tests.fake_extension import fake_extension, project_root  # noqa: F401 - pytest fixtures

SAMPLE_ARGS = {
    "vscode_get_context": {},
    "vscode_get_errors": {"path": "train.py"},
    "vscode_open_file": {"path": "train.py", "line": 12},
    "vscode_highlight": {"path": "train.py", "start": 40},
    "vscode_run_command": {"command": "workbench.action.togglePanel"},
    "vscode_train": {},
}


def call(name, args=None):
    return asyncio.run(tools.call_tool(name, args or {}))


def test_every_tool_has_a_schema_and_a_handler(fake_extension):
    assert sorted(t["name"] for t in tools.TOOLS) == sorted(SAMPLE_ARGS)
    for t in tools.TOOLS:
        assert t["parameters"]["type"] == "object" and t["description"]
        if t["name"] != "vscode_run_command":  # the fake extension refuses every /run
            assert "error" not in call(t["name"], SAMPLE_ARGS[t["name"]]), t["name"]


def test_highlight_args_reach_the_extension(fake_extension, project_root):
    assert call("vscode_highlight", {"path": "train.py", "start": 40, "end": 52, "note": "lr too high"}) == {"ok": True}
    assert fake_extension[-1] == ("POST", "/highlight", {"path": str(project_root / "train.py"), "start": 40,
                                                          "end": 52, "note": "lr too high", "ms": 4000})


def test_errors_come_back_to_the_model_instead_of_raising(fake_extension):
    assert call("vscode_run_command", {"command": "x"}) == {"error": "command not allowed: x"}
    assert "error" in call("vscode_nope")


def test_extension_down_is_a_plain_error(monkeypatch):
    monkeypatch.setattr(tools, "BASE", "http://127.0.0.1:9")  # nothing listens on port 9
    assert call("vscode_get_context")["error"].startswith("VS Code isn't reachable")
