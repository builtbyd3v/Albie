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
import threading
import time
import uuid
from pathlib import Path
from typing import TYPE_CHECKING, Any

from claude_agent_sdk import (
    AssistantMessage,
    ClaudeAgentOptions,
    ClaudeSDKClient,
    ResultMessage,
    StreamEvent,
    TextBlock,
    ToolResultBlock,
    ToolUseBlock,
    UserMessage,
)
from vscode import live as vscode_live

from . import events as ev
from . import tools as forge_tools
from .bus import Emit
from .config import ProjectConfig
from .events import AskMode
from .turn import ConcreteMode, TurnControl, classify_mode

if TYPE_CHECKING:
    from .crew import AgentType

log = logging.getLogger(__name__)

QA_TOOLS = ["Read", "Grep", "Glob"]
CODE_TOOLS = ["Read", "Grep", "Glob", "Edit", "Write", "Bash"]
DOCS_TOOLS = ["Read", "Grep", "Glob", "Edit", "Write"]
# Reviewer / Sentinel: read-only git through Bash (dontAsk denies every other command).
REVIEW_BASH = ["Bash(git diff:*)", "Bash(git log:*)", "Bash(git show:*)", "Bash(git status:*)", "Bash(git branch:*)"]
# Scribe: Bash only to commit its docs on its own branch.
DOCS_BASH = ["Bash(git add:*)", "Bash(git commit:*)", "Bash(git status:*)", "Bash(git diff:*)"]
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
# Crew members share the repo through worktrees: they must not touch other branches or worktrees either.
CREW_DENY = CODE_DENY + ["Bash(git worktree:*)", "Bash(git merge:*)", "Bash(git rebase:*)", "Bash(git stash:*)"]
# Hub tools (agent/tools.py) a crew runner may call, by name prefix: the editor for everyone, plus its kit's own
# module. Never cad_* outside the Designer, never messages_* (Slack goes through Bit), never the crew tools.
CREW_FORGE_TOOLS: tuple[str, ...] = ("vscode_",)
KIT_FORGE_TOOLS: dict[str, tuple[str, ...]] = {"cad": ("cad_",)}
PARTIAL_MIN_INTERVAL_S = 0.15

PERSONA = (
    "You are Albie, a small, cute, competent gator pet living in a developer's XR workspace. "
    "Your reply is spoken aloud and shown in a speech bubble."
)
QA_PROMPT = (
    PERSONA
    + " Answer questions about the code repository in your working directory. Look things up "
    "with your read-only tools before answering. Reply in 1-2 short spoken sentences (about 25 words), plain text, "
    "no markdown, no code blocks. Mention relevant file paths relative to the repo root "
    "(e.g. hub/agent/ws.py). If you are not sure, say so briefly."
)
CREW_CODE_PROMPT = (
    " You are making a code change in your own git worktree, already on a fresh branch created for this task: "
    "stay on it, do not switch branches, do not push, and do not touch other worktrees. Keep changes minimal "
    "and focused. When done, commit your work on this branch with a clear message, then finish with a 1-2 "
    "sentence plain-text summary of what you changed, naming files relative to the repo root."
)
CREW_QA_PROMPT = (
    " Look things up with your read-only tools before answering. Reply in 1-3 short sentences (up to 5 "
    "for a review or audit), plain text, no markdown, no code blocks. Mention file paths relative to the "
    "repo root. If you are not sure, say so briefly."
)
CODE_PROMPT = (
    PERSONA
    + " You are making a code change in the repository in your working directory. You are already "
    "on a fresh git branch created for this task: stay on it, do not switch branches, do not push, "
    "and do not delete anything you were not asked to. Keep changes minimal and focused. When done, "
    "finish with a 1-2 sentence plain-text summary of what you changed, naming files relative to "
    "the repo root."
)


EDIT_IN_PLACE_PROMPT = (
    PERSONA
    + " You are making a change in the folder the user has open in VS Code right now, in place: no branch, no "
    "commit. Create or edit the files directly; each one opens live in VS Code as you change it. Keep changes "
    "minimal and focused, and do not delete anything you were not asked to. When done, finish with a 1-2 sentence "
    "plain-text summary of what you changed, naming files relative to the folder."
)


def _model() -> str:
    return os.getenv("AGENT_MODEL", "claude-sonnet-5-5")


def crew_prompt(agent: AgentType, mode: ConcreteMode) -> str:
    return (
        f"You are {agent.name}, a small gator on Albie's crew in a developer's XR workspace; your job: {agent.role}. "
        f"{agent.persona} Your reply is shown in a speech bubble above you and may be read aloud by Albie."
        + (CREW_QA_PROMPT if mode == "qa" else CREW_CODE_PROMPT)
    )


def _crew_tools(agent: AgentType, mode: ConcreteMode) -> tuple[list[str], list[str]]:
    """(built-in tools, auto-approved tools) for a crew member's kit. A kit with a Bash allow-list gets the Bash
    tool but only its listed commands approved (a bare "Bash" would approve every command)."""
    if agent.kit == "review":
        return QA_TOOLS + ["Bash"], QA_TOOLS + REVIEW_BASH
    if mode == "qa" or agent.kit in ("read", "cad"):
        return QA_TOOLS, QA_TOOLS
    if agent.kit == "docs":
        return DOCS_TOOLS + ["Bash"], DOCS_TOOLS + DOCS_BASH
    return CODE_TOOLS, CODE_TOOLS


def crew_forge_allow(agent: AgentType) -> tuple[str, ...]:
    """Name prefixes of the hub tools this crew member's runner gets (see CREW_FORGE_TOOLS)."""
    return CREW_FORGE_TOOLS + KIT_FORGE_TOOLS.get(agent.kit, ())


def _options(project: ProjectConfig, mode: ConcreteMode, pet_id: str = "bit",
             agent: AgentType | None = None, cwd: Path | None = None) -> ClaudeAgentOptions:
    """Bit (agent None): QA/CODE tools plus every hub tool incl. the lead-only crew tools. A crew member: its own
    kit and prompt, and only the hub tools in crew_forge_allow (vscode_* and, for the Designer, cad_*)."""
    lead = agent is None
    allow: tuple[str, ...] | None = None
    if lead:
        tools = QA_TOOLS if mode == "qa" else CODE_TOOLS
        approved = tools
        from .crew import lead_rules  # registers the crew tools; imported late (crew imports this module lazily)

        code_prompt = EDIT_IN_PLACE_PROMPT if project.edit_in_place else CODE_PROMPT  # the sandbox has no branch
        prompt = (QA_PROMPT if mode == "qa" else code_prompt) + lead_rules()
    else:
        tools, approved = _crew_tools(agent, mode)
        prompt = crew_prompt(agent, mode)
        allow = crew_forge_allow(agent)
    deny = (CODE_DENY if lead else CREW_DENY) if mode == "code" else []
    effort = os.getenv("AGENT_QA_EFFORT" if mode == "qa" else "AGENT_CODE_EFFORT")
    return ClaudeAgentOptions(
        tools=tools,  # built-in tools that exist at all
        allowed_tools=approved + forge_tools.mcp_names(lead, allow),  # ...auto-approved, plus the hub tools
        mcp_servers={forge_tools.SERVER: forge_tools.mcp_server(pet_id, lead, allow)},
        disallowed_tools=deny,
        permission_mode="dontAsk",  # never prompt: anything not pre-approved is denied
        cwd=str(cwd or project.path),
        system_prompt=prompt,
        model=_model(),
        effort=effort or "low",  # code too (Dev, 2026-10-24): faster small edits beat a more careful plan in the demo  # type: ignore[arg-type]
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


def _git_bytes(repo: Path, *args: str, stdin: bytes | None = None) -> subprocess.CompletedProcess[bytes]:
    """Patches stay bytes (binary files, any text encoding)."""
    return subprocess.run(["git", "-C", str(repo), *args], capture_output=True, input=stdin, timeout=60)


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


def worktree_root(repo: Path) -> Path:
    """AGENT_WORKTREE_DIR, else a sibling of the repo: <repo>-crew/."""
    raw = os.getenv("AGENT_WORKTREE_DIR")
    return Path(raw).expanduser() if raw else repo.parent / f"{repo.name}-crew"


def create_task_worktree(repo: Path, text: str, task_id: str, pet_id: str) -> tuple[Path, str, str] | None:
    """New worktree on pet/<pet>/<slug>-<shortid>, so crew members never switch the main checkout's branch under
    each other. It starts from the main checkout as it is now: `git stash create` snapshots uncommitted tracked
    edits (Bit's) as a commit without touching the checkout; HEAD if there are none. Returns (path, branch, base
    commit), None if repo isn't a git work tree."""
    if _git(repo, "rev-parse", "--is-inside-work-tree").stdout.strip() != "true":
        return None
    base = _git(repo, "stash", "create").stdout.strip() or _git(repo, "rev-parse", "HEAD").stdout.strip() or "HEAD"
    branch = f"pet/{_slug(pet_id, 16)}/{_slug(text)}-{task_id[:6]}"
    path = worktree_root(repo) / f"{_slug(pet_id, 16)}-{task_id[:6]}"
    path.parent.mkdir(parents=True, exist_ok=True)
    res = _git(repo, "worktree", "add", "-b", branch, str(path), base)
    if res.returncode != 0:
        raise RuntimeError(f"git worktree add failed: {res.stderr.strip()}")
    log.info("created worktree %s on %s from %s", path, branch, base[:10])
    return path, branch, base


_land_lock = threading.Lock()  # one crew task lands in the main checkout at a time


def land_task_worktree(repo: Path, path: Path, branch: str, base: str) -> str | None:
    """Bring a finished crew task into the main checkout as plain uncommitted edits: everything in the worktree
    since `base` (commits and anything left uncommitted) goes through `git apply`, which changes only files on disk
    (never the index or branch) and applies all or nothing. Then the worktree and its branch are removed. If it
    doesn't apply (the user or Bit changed the same lines), both stay so it can be merged by hand.
    Returns a short spoken note, or None when the task changed nothing."""
    with _land_lock:
        _git(path, "add", "-A")  # include uncommitted and new files
        files = _git(path, "diff", "--cached", "--name-only", base).stdout.split()
        if files:
            patch = _git_bytes(path, "diff", "--cached", "--binary", base).stdout
            res = _git_bytes(repo, "apply", "--whitespace=nowarn", "-", stdin=patch)
            if res.returncode != 0:
                log.warning("crew changes on %s didn't apply: %s", branch, res.stderr.decode(errors="replace").strip())
                return (f"My changes conflicted with yours, so they're still on branch {branch}, "
                        f"in {path.parent.name}/{path.name}.")
        _git(repo, "worktree", "remove", "--force", str(path))
        _git(repo, "branch", "-D", branch)
        log.info("landed %d file(s) from %s", len(files), branch)
    if not files:
        return None
    return f"My changes are in your checkout: {len(files)} file{'s' if len(files) != 1 else ''}."


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
    agent: AgentType | None = None,
) -> str:
    """Run one pet turn (agent: a crew member's type, None = Bit). Returns the final text ('' if cancelled)."""
    concrete = classify_mode(text, mode)
    log.info("turn pet=%s project=%s mode=%s model=%s", pet_id, project.id, concrete, _model())
    await emit(ev.thinking(pet_id))
    try:
        if concrete == "qa":
            return await _run_qa(project, text, emit, pet_id, control, agent)
        return await _run_code(project, text, emit, pet_id, control, agent)
    except asyncio.CancelledError:
        raise
    except Exception:
        log.exception("agent turn failed (pet=%s project=%s)", pet_id, project.id)
        msg = "Sorry, something went wrong while I was thinking."
        await emit(ev.error(pet_id, msg))
        return msg


async def _stream(
    project: ProjectConfig, mode: ConcreteMode, text: str, control: TurnControl | None, pet_id: str = "bit",
    agent: AgentType | None = None, cwd: Path | None = None,
):  # -> AsyncIterator[Message]
    """Connect a ClaudeSDKClient, send the prompt, yield messages until ResultMessage."""
    client = ClaudeSDKClient(options=_options(project, mode, pet_id, agent, cwd))
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
    project: ProjectConfig, text: str, emit: Emit, pet_id: str, control: TurnControl | None,
    agent: AgentType | None = None,
) -> str:
    current = ""  # text of the current assistant message (reset on each new message)
    last_sent, last_t = "", 0.0
    final: str | None = None
    async with contextlib.aclosing(_stream(project, "qa", text, control, pet_id, agent)) as stream:
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
    project: ProjectConfig, text: str, emit: Emit, pet_id: str, control: TurnControl | None,
    agent: AgentType | None = None,
) -> str:
    task_id = uuid.uuid4().hex[:8]
    cwd: Path | None = None
    base: str | None = None
    if project.edit_in_place:  # the sandbox: edit what VS Code shows, no branch to switch to
        branch = None
    elif agent is None:  # Bit: a branch in the main checkout (unchanged)
        branch = await asyncio.to_thread(create_task_branch, project.path, text, task_id)
    else:  # crew: its own worktree, so parallel members never share a checkout
        made = await asyncio.to_thread(create_task_worktree, project.path, text, task_id, pet_id)
        cwd, branch, base = made if made else (None, None, None)
    title = text if len(text) <= 60 else text[:57] + "..."
    await emit(ev.task_started(pet_id, task_id, title))
    if branch is None:
        await emit(ev.task_progress(pet_id, task_id, "Editing in place" if project.edit_in_place
                                    else "Not a git repo, editing in place"))
    else:
        await emit(ev.task_progress(pet_id, task_id, f"Working on branch {branch}"))

    root = cwd or project.path
    last_text = ""
    summary: str | None = None
    edits: dict[str, ToolUseBlock] = {}  # edit tool uses waiting for their result, by id
    live: set[asyncio.Task[None]] = set()

    def show(coro: Any) -> None:  # VS Code open / glow in the background: never slows the turn
        task = asyncio.create_task(coro)
        live.add(task)
        task.add_done_callback(live.discard)

    async with contextlib.aclosing(_stream(project, "code", text, control, pet_id, agent, cwd)) as stream:
        async for msg in stream:
            if isinstance(msg, AssistantMessage) and not msg.parent_tool_use_id:
                for block in msg.content:
                    if isinstance(block, ToolUseBlock):
                        await emit(ev.task_progress(pet_id, task_id, describe_tool(block, root)))
                        if block.name in vscode_live.EDIT_TOOLS:  # the file opens on the stream as Albie edits it
                            edits[block.id] = block
                            show(vscode_live.opening(block.name, block.input))
                    elif isinstance(block, TextBlock) and block.text.strip():
                        last_text = block.text.strip()
            elif isinstance(msg, UserMessage) and edits and isinstance(msg.content, list):
                for result in msg.content:  # an edit landed: glow its lines
                    if isinstance(result, ToolResultBlock) and (done := edits.pop(result.tool_use_id, None)):
                        if not result.is_error:
                            show(vscode_live.edited(done.name, done.input))
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
    if cwd is not None and branch and base:  # crew: bring the work back to the checkout the user has open
        try:
            note = await asyncio.to_thread(land_task_worktree, project.path, cwd, branch, base)
        except Exception:
            log.exception("landing %s failed", branch)
            note = f"I couldn't bring my changes over, so they're still on branch {branch}."
        if note:
            summary = f"{note} {summary}"  # first, so the spoken announcement keeps it
    await emit(ev.task_complete(pet_id, task_id, summary, branch=branch))
    return summary


def get_runner():  # -> run_turn-compatible callable
    """FAKE_AGENT=1 -> scripted runner, else the real SDK runner."""
    if os.getenv("FAKE_AGENT", "0") == "1":
        from . import fake

        return fake.run_turn
    return run_turn
