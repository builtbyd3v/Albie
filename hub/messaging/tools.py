"""Bit's messaging tools (voice model + Claude runner), registered through agent/tools.py like the CAD tools."""
from __future__ import annotations

from typing import Any

from .slack_client import SlackService

TOOLS: list[dict[str, Any]] = [
    {"name": "messages_read_latest",
     "description": "The newest Slack messages Bit has seen (channel, who, text, ts). Use it to answer 'what did X "
                    "say' or before replying to someone. Newest first.",
     "parameters": {"type": "object", "properties": {
         "n": {"type": "integer", "minimum": 1, "maximum": 20, "description": "how many (default 5)"}}}},
    {"name": "messages_send",
     "description": "Post a Slack message as Bit. IRREVERSIBLE: before calling, you MUST read the exact text back to "
                    "the user and get a clear yes; never send without that confirmation, and never change the text "
                    "after they approved it. To answer someone, pass their message's ts as thread_ts so the reply lands "
                    "in their thread. channel is an id or #name; omit it for the default channel.",
     "parameters": {"type": "object", "properties": {
         "text": {"type": "string", "description": "the message, exactly as the user approved it"},
         "channel": {"type": "string", "description": "channel id or #name (optional)"},
         "thread_ts": {"type": "string", "description": "ts of the message being answered (optional)"}},
         "required": ["text"]}},
]


async def call_tool(name: str, args: dict[str, Any], svc: SlackService) -> dict[str, Any]:
    """Run one tool. Errors come back as {"error": ...} so the model can explain them."""
    if not svc.enabled:
        return {"error": "Slack is not configured on this hub"}
    try:
        if name == "messages_read_latest":
            n = int(args.get("n") or 5)
            return {"messages": [m.model_dump() for m in svc.store.latest(max(1, min(n, 20)))]}
        if name == "messages_send":
            text = str(args.get("text") or "").strip()
            if not text:
                return {"error": "text is empty"}
            ts = await svc.send(args.get("channel"), text, args.get("thread_ts"))
            return {"ok": True, "ts": ts}
    except LookupError as e:
        return {"error": str(e)}
    except Exception as e:  # noqa: BLE001
        return {"error": f"Slack error: {e}"}
    return {"error": f"Unknown messaging tool {name!r}"}
