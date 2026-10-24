"""Code mode shows Albie's edits in VS Code: the file opens on the Edit tool use and its lines glow on the result."""
import asyncio
import inspect
from pathlib import Path

from claude_agent_sdk import AssistantMessage, ResultMessage, ToolResultBlock, ToolUseBlock, UserMessage

from agent import runner
from agent.config import ProjectConfig
from vscode.tests.fake_extension import fake_extension  # noqa: F401 - pytest fixture


def _make(cls, **given):
    """SDK dataclasses grow fields between versions: fill the ones a test doesn't care about with None."""
    params = inspect.signature(cls).parameters
    return cls(**{k: given.get(k) for k in params if k in given or params[k].default is inspect.Parameter.empty})


def test_edit_opens_then_glows_in_vscode(tmp_path: Path, monkeypatch, fake_extension):
    part = tmp_path / "rocket.py"
    part.write_text("length = 300\nfins = 3\n")

    async def stream(*_args, **_kw):
        yield _make(AssistantMessage, model="m", content=[ToolUseBlock(
            id="t1", name="Edit", input={"file_path": str(part), "old_string": "fins = 3", "new_string": "fins = 4"})])
        part.write_text("length = 300\nfins = 4\n")  # what the Edit tool does
        yield _make(UserMessage, content=[ToolResultBlock(tool_use_id="t1", content="ok", is_error=False)])
        yield _make(ResultMessage, subtype="success", duration_ms=1, duration_api_ms=1, is_error=False, num_turns=1,
                    session_id="s", result="Added a fourth fin.")

    monkeypatch.setattr(runner, "_stream", stream)
    project = ProjectConfig(id="sandbox", path=tmp_path, default_mode="code", edit_in_place=True)
    events = []

    async def emit(e):
        events.append(e)

    async def turn():
        out = await runner._run_code(project, "add a fourth fin", emit, "bit", None)
        await asyncio.sleep(0.3)  # the open / glow run in the background
        return out

    assert asyncio.run(turn()) == "Added a fourth fin."
    assert fake_extension[0] == ("POST", "/open", {"path": str(part), "line": 2})
    assert fake_extension[1][1] == "/highlight" and fake_extension[1][2]["start"] == 2
    assert not (tmp_path / ".git").exists()  # edit_in_place: no pet/* branch was made
