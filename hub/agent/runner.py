"""Claude Agent SDK runner: one pet turn (qa or code) -> stream of agent.* events.

API used (claude-agent-sdk, verified against the installed 0.2.173 source and
https://code.claude.com/docs/en/agent-sdk/python):
  ClaudeSDKClient(options) ; await client.connect() ; await client.query(text)
  async for msg in client.receive_response()  # ends after ResultMessage
  await client.interrupt() ; await client.disconnect()
  ClaudeAgentOptions(tools, allowed_tools, disallowed_tools, permission_mode="dontAsk",
                     cwd, system_prompt, model, effort, setting_sources,
                     include_partial_messages, max_turns, stderr)
  Messages: StreamEvent(.event raw API stream event), AssistantMessage(.content
  [TextBlock|ToolUseBlock|...]), ResultMessage(.result, .is_error, .subtype,
  .terminal_reason)
"""

from __future__ import annotations

import asyncio
import contextlib
import logging
import os
import re
import subprocess
import time
import uuid
from pathlib import Path
from typing import Any

from claude_agent_sdk import (
    AssistantMessage,
    ClaudeAgentOptions,
    ClaudeSDKClient,
    ResultMessage,
    StreamEvent,
    TextBlock,
    ToolUseBlock,
)

from . import events as ev
from . import tools as forge_tools
from .bus import Emit
from .config import ProjectConfig
from .events import AskMode
from .turn import ConcreteMode, TurnControl, classify_mode

log = logging.getLogger(__name__)

QA_TOOLS = ["Read", "Grep", "Glob"]
CODE_TOOLS = ["Read", "Grep", "Glob", "Edit", "Write", "Bash"]
# Irreversible / out-of-scope actions the pet must never take without a human.
CODE_DENY = [
    "Bash(git push:*)",
    "Bash(git checkout:*)",
    "Bash(git switch:*)",
    "Bash(git reset:*)",
    "Bash(git branch -D:*)",
    "Bash(rm -rf:*)",
    "Bash(sudo:*)",
]
PARTIAL_MIN_INTERVAL_S = 0.15

PERSONA = (
    "You are Bit, a small, cute, competent gator pet living in a developer's XR workspace. "
    "Your reply is spoken aloud and shown in a speech bubble."
)
QA_PROMPT = (
    PERSONA
    + " Answer questions about the code repository in your working directory. Look things up "
    "with your read-only tools before answering. Reply in 1-2 short spoken sentences (about 25 words), plain text, "
    "no markdown, no code blocks. Mention relevant file paths relative to the repo root "
    "(e.g. hub/agent/ws.py). If you are not sure, say so briefly."
)
CODE_PROMPT = (
    PERSONA
    + " You are making a code change in the repository in your working directory. You are already "
    "on a fresh git branch created for this task: stay on it, do not switch branches, do not push, "
    "and do not delete anything you were not asked to. Keep changes minimal and focused. When done, "
    "finish with a 1-2 sentence plain-text summary of what you changed, naming files relative to "
    "the repo root."
)


def _model() -> str:
    return os.getenv("AGENT_MODEL", "claude-sonnet-5-5")


def _options(project: ProjectConfig, mode: ConcreteMode, pet_id: str = "bit") -> ClaudeAgentOptions:
    tools = QA_TOOLS if mode == "qa" else CODE_TOOLS
    effort = os.getenv("AGENT_QA_EFFORT" if mode == "qa" else "AGENT_CODE_EFFORT")
    return ClaudeAgentOptions(
        tools=tools,  # built-in tools that exist at all
        allowed_tools=tools + forge_tools.mcp_names(),  # ...and are auto-approved, plus Bit's CAD/VS Code tools
        mcp_servers={forge_tools.SERVER: forge_tools.mcp_server(pet_id)},
        disallowed_tools=CODE_DENY if mode == "code" else [],
        permission_mode="dontAsk",  # never prompt: anything not pre-approved is denied
        cwd=str(project.path),
        system_prompt=QA_PROMPT if mode == "qa" else CODE_PROMPT,
        model=_model(),
        effort=effort or ("low" if mode == "qa" else "medium"),  # type: ignore[arg-type]
        setting_sources=["project"],  # load the repo's CLAUDE.md, not the user's global settings
        include_partial_messages=(mode == "qa"),  # token deltas -> smooth captions
        max_turns=12 if mode == "qa" else 40,
        stderr=lambda line: log.debug("claude-cli: %s", line),
    )


# ------------------------------------------------------------------ git branch
def _slug(text: str, n: int = 24) -> str:
    s = re.sub(r"[^a-z0-9]+", "-", text.lower()).strip("-")
    return s[:n].rstrip("-") or "task"


def _git(repo: Path, *args: str) -> subprocess.CompletedProcess[str]:
    return subprocess.run(
        ["git", "-C", str(repo), *args], capture_output=True, text=True, timeout=15
    )


def create_task_branch(repo: Path, text: str, task_id: str) -> str | None:
    """Create and check out pet/<slug>-<shortid>. None if repo isn't a git work tree."""
    if _git(repo, "rev-parse", "--is-inside-work-tree").stdout.strip() != "true":
        return None
    branch = f"pet/{_slug(text)}-{task_id[:6]}"
    res = _git(repo, "checkout", "-b", branch)
    if res.returncode != 0:
        raise RuntimeError(f"git checkout -b failed: {res.stderr.strip()}")
    log.info("created branch %s in %s", branch, repo)
    return branch


# ------------------------------------------------------------- progress text
def describe_tool(block: ToolUseBlock, repo: Path) -> str:
    """Short human text for a tool call, e.g. 'Editing hub/agent/ws.py'."""
    inp: dict[str, Any] = block.input or {}

    def rel(p: Any) -> str:
        try:
            return str(Path(str(p)).resolve().relative_to(repo))
        except (ValueError, OSError):
            return Path(str(p)).name

    name = block.name
    if name in ("Edit", "MultiEdit"):
        return f"Editing {rel(inp.get('file_path', ''))}"
    if name == "Write":
        return f"Writing {rel(inp.get('file_path', ''))}"
    if name == "Read":
        return f"Reading {rel(inp.get('file_path', ''))}"
    if name == "Grep":
        return f"Searching for '{str(inp.get('pattern', ''))[:40]}'"
    if name == "Glob":
        return f"Looking for {str(inp.get('pattern', ''))[:40]}"
    if name == "Bash":
        desc = inp.get("description") or str(inp.get("command", ""))[:50]
        return f"Running: {desc}"
    return f"Using {name.removeprefix(f'mcp__{forge_tools.SERVER}__')}"


# ---------------------------------------------------------------- the turn
async def run_turn(
    project: ProjectConfig,
    text: str,
    mode: AskMode,
    emit: Emit,
    *,
    pet_id: str,
    control: TurnControl | None = None,
) -> str:
    """Run one pet turn. Returns the final answer/summary text ('' if cancelled)."""
    concrete = classify_mode(text, mode)
    log.info("turn pet=%s project=%s mode=%s model=%s", pet_id, project.id, concrete, _model())
    await emit(ev.thinking(pet_id))
    try:
        if concrete == "qa":
            return await _run_qa(project, text, emit, pet_id, control)
        return await _run_code(project, text, emit, pet_id, control)
    except asyncio.CancelledError:
        raise
    except Exception:
        log.exception("agent turn failed (pet=%s project=%s)", pet_id, project.id)
        msg = "Sorry, something went wrong while I was thinking."
        await emit(ev.error(pet_id, msg))
        return msg


async def _stream(
    project: ProjectConfig, mode: ConcreteMode, text: str, control: TurnControl | None, pet_id: str = "bit"
):  # -> AsyncIterator[Message]
    """Connect a ClaudeSDKClient, send the prompt, yield messages until ResultMessage."""
    client = ClaudeSDKClient(options=_options(project, mode, pet_id))
    await client.connect()
    if control is not None:
        control.set_interrupter(client.interrupt)
    try:
        await client.query(text)
        async for msg in client.receive_response():
            yield msg
    finally:
        if control is not None:
            control.set_interrupter(None)
        await client.disconnect()


def _cancelled(control: TurnControl | None) -> bool:
    return control is not None and control.cancelled.is_set()


async def _run_qa(
    project: ProjectConfig, text: str, emit: Emit, pet_id: str, control: TurnControl | None
) -> str:
    current = ""  # text of the current assistant message (reset on each new message)
    last_sent, last_t = "", 0.0
    final: str | None = None
    async with contextlib.aclosing(_stream(project, "qa", text, control, pet_id)) as stream:
        async for msg in stream:
            if isinstance(msg, StreamEvent):
                e = msg.event
                if msg.parent_tool_use_id:
                    continue
                if e.get("type") == "message_start":
                    current = ""
                elif e.get("type") == "content_block_delta" and e["delta"].get("type") == "text_delta":
                    current += e["delta"]["text"]
                    now = time.monotonic()
                    if current.strip() and now - last_t >= PARTIAL_MIN_INTERVAL_S:
                        last_sent, last_t = current, now
                        await emit(ev.partial_answer(pet_id, current.strip()))
            elif isinstance(msg, AssistantMessage) and not msg.parent_tool_use_id:
                # Fallback when partial streaming is unavailable: whole text blocks.
                block_text = "".join(b.text for b in msg.content if isinstance(b, TextBlock))
                if block_text.strip() and block_text != last_sent:
                    current = block_text
                    last_sent = block_text
                    await emit(ev.partial_answer(pet_id, block_text.strip()))
            elif isinstance(msg, ResultMessage):
                if _cancelled(control):
                    return ""
                if msg.is_error:
                    log.error("qa result error: subtype=%s errors=%s", msg.subtype, msg.errors)
                    raise RuntimeError(f"agent result error: {msg.subtype}")
                final = (msg.result or current).strip()
    if _cancelled(control):
        return ""
    answer = final or current.strip() or "Hmm, I couldn't find an answer."
    await emit(ev.answer_done(pet_id, answer))
    return answer


async def _run_code(
    project: ProjectConfig, text: str, emit: Emit, pet_id: str, control: TurnControl | None
) -> str:
    task_id = uuid.uuid4().hex[:8]
    branch = await asyncio.to_thread(create_task_branch, project.path, text, task_id)
    title = text if len(text) <= 60 else text[:57] + "..."
    await emit(ev.task_started(pet_id, task_id, title))
    if branch is None:
        await emit(ev.task_progress(pet_id, task_id, "Not a git repo, editing in place"))
    else:
        await emit(ev.task_progress(pet_id, task_id, f"Working on branch {branch}"))

    last_text = ""
    summary: str | None = None
    async with contextlib.aclosing(_stream(project, "code", text, control, pet_id)) as stream:
        async for msg in stream:
            if isinstance(msg, AssistantMessage) and not msg.parent_tool_use_id:
                for block in msg.content:
                    if isinstance(block, ToolUseBlock):
                        await emit(ev.task_progress(pet_id, task_id, describe_tool(block, project.path)))
                    elif isinstance(block, TextBlock) and block.text.strip():
                        last_text = block.text.strip()
            elif isinstance(msg, ResultMessage):
                if _cancelled(control):
                    return ""
                if msg.is_error:
                    log.error("code result error: subtype=%s errors=%s", msg.subtype, msg.errors)
                    raise RuntimeError(f"agent result error: {msg.subtype}")
                summary = (msg.result or last_text).strip()
    if _cancelled(control):
        return ""
    summary = summary or last_text or "Done."
    await emit(ev.task_complete(pet_id, task_id, summary, branch=branch))
    return summary


def get_runner():  # -> run_turn-compatible callable
    """FAKE_AGENT=1 -> scripted runner, else the real SDK runner."""
    if os.getenv("FAKE_AGENT", "0") == "1":
        from . import fake

        return fake.run_turn
    return run_turn
