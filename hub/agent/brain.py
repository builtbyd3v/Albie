"""Bit's brain, text version: Claude runs a tool loop over the module tools (cad/tools.py and later ml, messaging).

The Grok voice layer will reuse the same tool list and `call_tool`; only the model in the loop changes.
Env: ANTHROPIC_API_KEY, FORGE_AGENT_MODEL (default claude-opus-5-5).
"""
from __future__ import annotations

import asyncio
import json
import logging
import os
from collections.abc import Awaitable, Callable
from typing import Any

import anthropic
from anthropic.types.beta import BetaToolUseBlock

from events import EventBus, PetSay, PetState

log = logging.getLogger("forge.agent")

MODEL = os.environ.get("FORGE_AGENT_MODEL", "claude-opus-5-5")
MAX_STEPS = 8
SYSTEM = (
    "You are Bit, a small, cute, competent AI pet that lives on an engineer's desk in mixed reality. "
    "Reply in 1 to 3 short spoken sentences, no markdown: your words are shown in a speech bubble and "
    "will be read aloud. Use your tools to look things up and to change things; never guess a value a tool "
    "can tell you. If a tool returns an error, say what went wrong in plain words."
)
PANELS = {"cad": "cad-viewer"}  # tool prefix -> panel Bit jumps to while working

CallTool = Callable[[str, dict[str, Any]], Awaitable[dict[str, Any]]]


class Brain:
    def __init__(self, bus: EventBus, tools: list[dict[str, Any]], call_tool: CallTool,
                 client: anthropic.AsyncAnthropic | None = None) -> None:
        self.bus, self.call_tool = bus, call_tool
        self.tools = [{"name": t["name"], "description": t["description"], "input_schema": t["parameters"]}
                      for t in tools]
        self._client = client
        # ponytail: one shared conversation, never trimmed; fine for a demo-length session.
        self.messages: list[dict[str, Any]] = []
        self._lock = asyncio.Lock()  # turns must not interleave in the shared history

    @property
    def client(self) -> anthropic.AsyncAnthropic:
        if self._client is None:  # lazy, so the hub starts without ANTHROPIC_API_KEY
            self._client = anthropic.AsyncAnthropic()
        return self._client

    async def reply(self, pet_id: str, text: str) -> str:
        async with self._lock:
            await self.bus.publish("pet.state", PetState(pet_id=pet_id, mode="thinking"))
            start = len(self.messages)
            self.messages.append({"role": "user", "content": text})
            try:
                answer = await self._loop(pet_id, start)
            except Exception:
                del self.messages[start:]  # a half-finished turn would break the next request
                await self.bus.publish("pet.state", PetState(pet_id=pet_id, mode="idle"))
                raise
            await self.bus.publish("pet.say", PetSay(pet_id=pet_id, text=answer))
            await self.bus.publish("pet.state", PetState(pet_id=pet_id, mode="speaking"))
            return answer

    async def _loop(self, pet_id: str, start: int) -> str:
        for _ in range(MAX_STEPS):
            response = await self.client.beta.messages.create(
                model=MODEL, max_tokens=16000, system=SYSTEM, tools=self.tools, messages=self.messages,
                output_config={"effort": "low"},  # chat-speed turns; delegate does the deep thinking
                betas=["server-side-fallback-2026-07-01"], fallbacks="default",
            )
            if response.stop_reason == "refusal":
                del self.messages[start:]  # drop the whole declined turn so the next one starts clean
                return "Hmm, I can't help with that one."
            self.messages.append({"role": "assistant", "content": response.content})
            if response.stop_reason != "tool_use":
                return " ".join(b.text for b in response.content if b.type == "text").strip()
            results = []
            for block in (b for b in response.content if b.type == "tool_use"):
                panel = PANELS.get(block.name.split("_", 1)[0])
                await self.bus.publish("pet.state", PetState(pet_id=pet_id, mode="working", target_panel=panel))
                results.append(await self._run(block))
            self.messages.append({"role": "user", "content": results})
        return "That took too many steps, let me stop there."

    async def _run(self, block: BetaToolUseBlock) -> dict[str, Any]:
        try:
            out = await self.call_tool(block.name, block.input)
            return {"type": "tool_result", "tool_use_id": block.id, "content": json.dumps(out, default=str)}
        except Exception as e:  # noqa: BLE001 - a crashing tool must come back to the model, not kill the turn
            log.exception("tool %s failed", block.name)
            return {"type": "tool_result", "tool_use_id": block.id, "content": f"Error: {e}", "is_error": True}
