"""Bit's messaging tools (voice model + Claude runner), registered through agent/tools.py like the CAD tools.

Sending is two tools on purpose: messages_send only WRITES a draft (Unity pops a Send / Cancel card);
messages_confirm is the spoken "send it" / "never mind". Nothing reaches Slack without one of those two confirmations.
"""
from __future__ import annotations

from typing import Any

from .slack_client import DraftError, SlackService

TOOLS: list[dict[str, Any]] = [
    {"name": "messages_read_latest",
     "description": "The newest Slack messages in the user's inbox (channel, who, text, ts, is_dm, mentions_me). Use it "
                    "to answer 'what did X say' or before replying to someone. Newest first.",
     "parameters": {"type": "object", "properties": {
         "n": {"type": "integer", "minimum": 1, "maximum": 20, "description": "how many (default 5)"}}}},
    {"name": "messages_send",
     "description": "Write a Slack reply on the user's behalf. This only WRITES A DRAFT: nothing is sent. A card with "
                    "the exact text and Send / Cancel buttons appears next to you. After calling, tell the user in one "
                    "short line what you wrote and ask them to tap Send or say 'send it'. NEVER say the message was sent. "
                    "The message posts under the USER's own name, so write in their voice (first person, their tone). To "
                    "answer someone, pass that message's ts as reply_to_ts; the reply then goes to their channel, in thread. "
                    "Calling again replaces the previous draft (use that when the user wants changes).",
     "parameters": {"type": "object", "properties": {
         "text": {"type": "string", "description": "the message, as the user would write it"},
         "reply_to_ts": {"type": "string", "description": "ts of the message being answered (optional)"},
         "channel": {"type": "string", "description": "channel id or #name (optional; default channel otherwise)"},
         "thread_ts": {"type": "string", "description": "thread to post in (optional; implied by reply_to_ts)"}},
         "required": ["text"]}},
    {"name": "messages_confirm",
     "description": "The user's decision on the open draft. send=true when they say 'send it' / 'yes' / 'go ahead' "
                    "(posts it under their name), send=false when they say 'cancel' / 'never mind' / 'don't send'. "
                    "Only call this after the user clearly decided; a tap on the card does the same thing.",
     "parameters": {"type": "object", "properties": {
         "send": {"type": "boolean", "description": "true = post it, false = discard it"},
         "draft_id": {"type": "string", "description": "optional; defaults to the open draft"}},
         "required": ["send"]}},
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
            draft = await svc.create_draft(text, args.get("channel"), args.get("thread_ts"), args.get("reply_to_ts"))
            return {"draft_id": draft.draft_id, "status": "awaiting_confirmation", "channel_name": draft.channel_name,
                    "text": draft.text, "as": "you" if svc.as_user else "Bit",
                    "next": "Tell the user what you wrote and ask them to tap Send or say 'send it'. Not sent yet."}
        if name == "messages_confirm":
            draft_id = args.get("draft_id") or None
            if bool(args.get("send")):
                draft, ts = await svc.send_draft(draft_id)
                return {"status": "sent", "ts": ts, "channel_name": draft.channel_name, "text": draft.text}
            draft = await svc.cancel_draft(draft_id)
            return {"status": "cancelled", "draft_id": draft.draft_id}
    except (LookupError, ValueError) as e:  # DraftError is a LookupError
        return {"error": str(e)}
    except Exception as e:  # noqa: BLE001
        return {"error": f"Slack error: {e}"}
    return {"error": f"Unknown messaging tool {name!r}"}
