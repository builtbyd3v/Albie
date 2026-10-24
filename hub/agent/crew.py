"""Bit's crew: extra gators the user hatches in Unity, each a Claude runner with its own role.

Unity owns who exists (it hatches/sends home the gators and sends ``crew.roster`` on every change and on
reconnect). The hub owns the work: ``assign`` starts a background runner turn for one member, with that
type's persona and tool set, so several members work in parallel. Crew code tasks run in their own git
worktree (runner.create_task_worktree), never in the main checkout, so their branches don't fight. When one
finishes, its changes are applied to the main checkout as uncommitted edits (runner.land_task_worktree).

Bit (the lead) drives the crew with four tools, registered lead-only in agent/tools.py so Grok and Bit's
own Claude runner get them but crew runners don't: spawn_agent, dismiss_agent, assign_task, crew_status.
When a member finishes, listeners are told (ws.py lets the voice session say it out loud).

Wire format (docs/PET_AGENT_CONTRACT.md, Addendum D):
    client -> hub  crew.roster  {members: [{pet_id, agent_type}]}
    hub -> client  crew.spawn   {agent_type}       (pet_id == agent_type: one gator per type)
    hub -> client  crew.dismiss {pet_id}
"""

from __future__ import annotations

import asyncio
import logging
from collections.abc import Awaitable, Callable
from dataclasses import dataclass, field
from typing import Any, Literal

from . import events as ev
from . import tools
from .bus import Emitter, bus
from .config import ProjectNotFound, resolve_project
from .turn import TurnControl, classify_mode

log = logging.getLogger(__name__)

ToolKit = Literal["read", "review", "code", "docs", "cad"]
SPAWN_WAIT_S = 3.0  # how long spawn_agent waits for Unity's roster to show the new gator
SPOKEN_MAX = 220  # announcements are trimmed to this many characters


@dataclass(frozen=True)
class AgentType:
    id: str
    name: str
    role: str  # one line, also shown on the hatchery egg in Unity
    persona: str  # how this agent works (appended to its system prompt)
    kit: ToolKit
    default_mode: Literal["qa", "code", "auto"] = "auto"

    @property
    def read_only(self) -> bool:
        return self.kit in ("read", "review")


# Keep ids, names and roles in sync with unity/.../Crew/GatorAgentTypes.cs.
TYPES: dict[str, AgentType] = {t.id: t for t in (
    AgentType("scout", "Scout", "maps the codebase and finds where things live",
              "You explore before anyone builds: trace how code fits together, find the files and functions that "
              "matter, and explain the path through them. You never change files.", "read", "qa"),
    AgentType("builder", "Builder", "builds new features end to end",
              "You implement features: read the surrounding code first, follow its conventions, keep the change "
              "focused, and add or update tests when the repo has them.", "code", "code"),
    AgentType("fixer", "Fixer", "hunts down bugs and fixes the root cause",
              "You debug: reproduce the problem, find the root cause rather than the symptom, and make the "
              "smallest fix that solves it. Say what the cause was.", "code", "code"),
    AgentType("tester", "Tester", "writes and runs tests",
              "You write and run tests. Only add or change test files and test fixtures, never production code; "
              "if a test exposes a bug, report it instead of fixing it. Report what passed and what failed.",
              "code", "code"),
    AgentType("reviewer", "Reviewer", "reviews branches and diffs for bugs",
              "You review code: read diffs and branches (git diff, git log, git show), look for real bugs, missing "
              "edge cases and risky changes, and rank what you find by severity. You never change files.",
              "review", "qa"),
    AgentType("scribe", "Scribe", "writes docs, READMEs and comments",
              "You write documentation: READMEs, docstrings, comments and guides that match how the code really "
              "works. Only change documentation and comments, never behaviour.", "docs", "code"),
    AgentType("shipper", "Shipper", "handles git, changelogs and pull request write-ups",
              "You get work ready to ship: inspect branches (including the crew's pet/* branches), write commit "
              "messages, changelogs and pull request descriptions, and tidy release notes. You never push.",
              "code", "auto"),
    AgentType("designer", "Designer", "tweaks the rocket CAD design",
              "You work on the CAD model: read and change the rocket design with your CAD tools, and explain the "
              "effect of each parameter change in plain words.", "cad", "qa"),
    AgentType("sentinel", "Sentinel", "audits the code for security problems",
              "You audit security: look for injection, unsafe file or shell use, leaked secrets, missing auth and "
              "unsafe defaults, and rank what you find by severity. You never change files.", "review", "qa"),
)}


@dataclass
class Member:
    pet_id: str
    type: AgentType
    task: asyncio.Task[str] | None = None
    control: TurnControl | None = None
    mode: str | None = None
    title: str = ""

    @property
    def busy(self) -> bool:
        return self.task is not None and not self.task.done()


Listener = Callable[[Member, str], Awaitable[None]]


def find_type(name: str | None) -> AgentType | None:
    """Type by id or display name, case-insensitive ('Tester', 'tester', 'the tester')."""
    key = (name or "").strip().lower().removeprefix("the ").strip()
    return TYPES.get(key) or next((t for t in TYPES.values() if t.name.lower() == key), None)


def spoken(text: str) -> str:
    """First couple of sentences, at most SPOKEN_MAX characters."""
    text = " ".join(text.split())
    if len(text) <= SPOKEN_MAX:
        return text
    cut = text[:SPOKEN_MAX]
    end = max(cut.rfind(". "), cut.rfind("! "), cut.rfind("? "))
    return cut[: end + 1] if end > 15 else cut.rsplit(" ", 1)[0] + "..."


class Crew:
    def __init__(self) -> None:
        self.members: dict[str, Member] = {}
        self.project_id: str | None = None  # from the Unity client's hello
        self._roster_seen = False  # a Unity client has sent crew.roster at least once
        self._changed = asyncio.Event()
        self._listeners: list[Listener] = []

    # ------------------------------------------------------------ roster (Unity is the source of truth)
    def set_roster(self, entries: list[dict[str, Any]]) -> None:
        """Replace the membership. A gator that left keeps a running code task (its worktree), loses a Q&A."""
        seen: dict[str, Member] = {}
        for e in entries:
            t = find_type(e.get("agent_type"))
            pid = str(e.get("pet_id") or (t.id if t else ""))
            if t is None or not pid:
                log.warning("crew.roster: unknown agent_type %r", e.get("agent_type"))
                continue
            seen[pid] = self.members.get(pid) or Member(pet_id=pid, type=t)
        for pid, m in self.members.items():
            if pid not in seen and m.busy and m.mode != "code":
                m.task.cancel()  # type: ignore[union-attr]
        self.members = seen
        self._roster_seen = True
        self._changed.set()
        self._changed = asyncio.Event()
        log.info("crew roster: %s", ", ".join(self.members) or "(empty)")

    def get(self, name: str | None) -> Member | None:
        key = (name or "").strip().lower()
        if key in self.members:
            return self.members[key]
        t = find_type(key)
        return self.members.get(t.id) if t else None

    def roster(self) -> list[dict[str, Any]]:
        return [{"agent": m.type.name, "type": m.type.id, "role": m.type.role, "busy": m.busy,
                 **({"working_on": m.title} if m.busy else {})} for m in self.members.values()]

    def add_listener(self, fn: Listener) -> None:
        self._listeners.append(fn)

    def remove_listener(self, fn: Listener) -> None:
        if fn in self._listeners:
            self._listeners.remove(fn)

    # ------------------------------------------------------------ actions (Bit's tools call these)
    async def spawn(self, type_name: str) -> dict[str, Any]:
        t = find_type(type_name)
        if t is None:
            return {"error": f"no agent type {type_name!r}", "types": list(TYPES)}
        if t.id in self.members:
            return {"status": "already_here", "agent": t.name}
        if bus.count == 0:
            return {"error": "no headset is connected, so there is nowhere to hatch a gator"}
        await bus.broadcast(ev.crew_spawn(t.id))
        deadline = asyncio.get_running_loop().time() + SPAWN_WAIT_S
        while t.id not in self.members:
            left = deadline - asyncio.get_running_loop().time()
            if left <= 0:
                # Older clients don't report a roster: assume it hatched so assign_task still works.
                if not self._roster_seen:
                    self.members[t.id] = Member(pet_id=t.id, type=t)
                    break
                return {"error": f"{t.name} didn't hatch (no answer from the headset)"}
            try:
                await asyncio.wait_for(self._changed.wait(), left)
            except asyncio.TimeoutError:
                pass
        return {"status": "hatched", "agent": t.name, "role": t.role}

    async def dismiss(self, name: str) -> dict[str, Any]:
        m = self.get(name)
        if m is None:
            return {"error": f"{name!r} isn't on the crew", "crew": [r["agent"] for r in self.roster()]}
        await self.cancel(m.pet_id)
        self.members.pop(m.pet_id, None)
        await bus.broadcast(ev.crew_dismiss(m.pet_id))
        return {"status": "sent_home", "agent": m.type.name}

    async def assign(self, name: str, task: str, mode: str | None = None) -> dict[str, Any]:
        if not task.strip():
            return {"error": "empty task"}
        m = self.get(name)
        if m is None:
            spawned = await self.spawn(name)  # "have the tester run the tests" with no tester yet: hatch one
            if "error" in spawned:
                return spawned
            m = self.get(name)
            if m is None:
                return {"error": f"{name!r} isn't on the crew"}
        if m.busy:
            return {"error": f"{m.type.name} is still busy with: {m.title}"}
        try:
            project = resolve_project(self.project_id)
        except ProjectNotFound as exc:
            return {"error": str(exc)}
        concrete = "qa" if m.type.read_only else classify_mode(task, mode if mode in ("qa", "code") else m.type.default_mode)
        m.control, m.mode = TurnControl(), concrete
        m.title = task if len(task) <= 60 else task[:57] + "..."
        m.task = asyncio.create_task(self._run(m, project, task, concrete))
        return {"status": "started", "agent": m.type.name, "mode": concrete}

    async def cancel(self, name: str) -> bool:
        m = self.get(name)
        return m is not None and await self.cancel_member(m)

    async def cancel_member(self, m: Member) -> bool:
        if not m.busy:
            return False
        if m.control is not None:
            try:
                await m.control.cancel()
            except Exception:
                log.exception("crew interrupt failed (%s)", m.pet_id)
        task = m.task
        try:
            await asyncio.wait_for(asyncio.shield(task), 3.0)  # type: ignore[arg-type]
        except (asyncio.TimeoutError, asyncio.CancelledError):
            task.cancel()  # type: ignore[union-attr]
        await bus.broadcast(ev.pet_state(m.pet_id, "idle"))
        return True

    async def _run(self, m: Member, project, task: str, mode: str) -> str:
        from .runner import get_runner, worktree_root  # runner imports tools; keep this module importable on its own

        emit = Emitter(m.pet_id, redact=[str(worktree_root(project.path)), str(project.path)])
        try:
            result = await get_runner()(project, task, mode, emit, pet_id=m.pet_id, control=m.control, agent=m.type)
        except asyncio.CancelledError:
            return ""
        except Exception:
            log.exception("crew turn crashed (%s)", m.pet_id)
            await emit(ev.error(m.pet_id, "Sorry, something went wrong."))
            return ""
        if result and not (m.control and m.control.cancelled.is_set()):
            for fn in list(self._listeners):
                try:
                    await fn(m, result)
                except Exception:
                    log.exception("crew listener failed")
        return result


crew = Crew()


# ------------------------------------------------------------------ Bit's crew tools (lead only)
_AGENT = {"type": "string", "description": "crew member, by type: " + ", ".join(TYPES)}
TOOLS: list[dict[str, Any]] = [
    {"name": "spawn_agent",
     "description": "Hatch a helper gator onto the desk. One per type: "
                    + "; ".join(f"{t.id} ({t.role})" for t in TYPES.values()) + ".",
     "parameters": {"type": "object", "properties": {"type": {"type": "string", "enum": list(TYPES)}},
                    "required": ["type"]}},
    {"name": "assign_task",
     "description": "Give a crew member a self-contained task to work on in the background (hatches it first if "
                    "needed). It works in parallel and reports back when done.",
     "parameters": {"type": "object", "properties": {
         "agent": _AGENT,
         "task": {"type": "string", "description": "self-contained instruction incl. context from the conversation"},
         "mode": {"type": "string", "enum": ["qa", "code", "auto"],
                  "description": "qa = answer only, code = change code, auto = the agent's default"}},
         "required": ["agent", "task"]}},
    {"name": "dismiss_agent",
     "description": "Send a crew member home (removes its gator, stops its current question).",
     "parameters": {"type": "object", "properties": {"agent": _AGENT}, "required": ["agent"]}},
    {"name": "crew_status",
     "description": "Who is on the crew and what each one is working on.",
     "parameters": {"type": "object", "properties": {}}},
]


async def call_tool(name: str, args: dict[str, Any]) -> dict[str, Any]:
    if name == "spawn_agent":
        return await crew.spawn(str(args.get("type", "")))
    if name == "assign_task":
        return await crew.assign(str(args.get("agent", "")), str(args.get("task", "")), args.get("mode"))
    if name == "dismiss_agent":
        return await crew.dismiss(str(args.get("agent", "")))
    if name == "crew_status":
        return {"crew": crew.roster(), "available_types": [t for t in TYPES if t not in crew.members]}
    return {"error": f"unknown tool {name!r}"}


def lead_rules() -> str:
    """Appended to Bit's instructions (voice persona and Claude prompt)."""
    return (" You lead a crew of helper gators the user can hatch: "
            + "; ".join(f"{t.name} ({t.role})" for t in TYPES.values())
            + ". Use spawn_agent to hatch one, assign_task to give one work (it hatches if needed and works in "
              "parallel on its own branch), dismiss_agent to send one home and crew_status to check on them. "
              "After assigning, just say who's on it; you'll hear back when they finish.")


tools.register(TOOLS, call_tool, lead_only=True)
