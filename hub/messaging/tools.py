"""Bit's messaging tools (voice model + Claude runner), registered through agent/tools.py like the CAD tools.

Reading: messages_read_latest (the inbox, newest first), messages_list_channels (every conversation with its unread
count), messages_read_channel (the last n in one conversation). Showing: messages_open puts a conversation on the
Slack panel (opens the MSG view). Sending is two tools on purpose: messages_send only
WRITES a draft (Unity shows it in the composer with Send / Cancel); messages_confirm is the spoken "send it" /
"never mind". Nothing reaches Slack without one of those two confirmations.
"""
from __future__ import annotations

from typing import Any

from agent.events import DISPLAY_NAME

from .slack_client import SlackService

TOOLS: list[dict[str, Any]] = [
    {"name": "messages_read_latest",
     "description": "The newest Slack messages in the user's inbox across every conversation (channel, who, text, ts, "
                    "is_dm, mentions_me). Use it to answer 'what did X say' or before replying to someone. Newest first.",
     "parameters": {"type": "object", "properties": {
         "n": {"type": "integer", "minimum": 1, "maximum": 20, "description": "how many (default 5)"}}}},
    {"name": "messages_list_channels",
     "description": "The user's Slack conversations: channels, private channels, DMs (named after the person) and group "
                    "DMs, each with its unread and mention counts and the last message. Use it for 'anything new?', "
                    "'who messaged me?' or to find the right channel or person before messages_read_channel / messages_send.",
     "parameters": {"type": "object", "properties": {}}},
    {"name": "messages_read_channel",
     "description": "The last n messages of ONE Slack conversation, oldest to newest, including the user's own "
                    "(is_me). channel is a #name, a channel id, or a person's name for their DM ('Hudson').",
     "parameters": {"type": "object", "properties": {
         "channel": {"type": "string", "description": "#name, id, or a person's name (their DM)"},
         "n": {"type": "integer", "minimum": 1, "maximum": 50, "description": "how many (default 10)"}},
         "required": ["channel"]}},
    {"name": "messages_open",
     "description": "Open a Slack conversation on the user's Slack panel (the MSG view opens if it isn't already): a "
                    "channel ('#general'), or a person's name for the user's DM with them ('Adrian'). Use it for 'open my "
                    "chat with Adrian', 'pull up #general', 'show me the thread with Jade'. It only shows it; nothing is "
                    "sent. The user has to be at their desk. Then say in a few words what you opened.",
     "parameters": {"type": "object", "properties": {
         "channel": {"type": "string", "description": "#name, channel id, or a person's name (their DM)"}},
         "required": ["channel"]}},
    {"name": "messages_send",
     "description": "Write a Slack reply on the user's behalf. This only WRITES A DRAFT: nothing is sent. The text "
                    "appears in the composer with Send / Cancel buttons next to you. After calling, tell the user in one "
                    "short line what you wrote and ask them to tap Send or say 'send it'. NEVER say the message was sent. "
                    "The message posts under the USER's own name, so write in their voice (first person, their tone). To "
                    "answer someone, pass that message's ts as reply_to_ts; the reply then goes to their channel, in thread. "
                    "To message a person directly ('tell Hudson ...') pass their name as channel: it becomes their DM. "
                    "Calling again replaces the previous draft (use that when the user wants changes).",
     "parameters": {"type": "object", "properties": {
         "text": {"type": "string", "description": "the message, as the user would write it"},
         "reply_to_ts": {"type": "string", "description": "ts of the message being answered (optional)"},
         "channel": {"type": "string", "description": "channel id, #name, or a person's name for a DM (optional; "
                                                      "default channel otherwise)"},
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


def _row(m: Any) -> dict[str, Any]:
    return m.model_dump(exclude_none=True)


async def call_tool(name: str, args: dict[str, Any], svc: SlackService) -> dict[str, Any]:
    """Run one tool. Errors come back as {"error": ...} so the model can explain them."""
    if not svc.enabled:
        return {"error": "Slack is not configured on this hub"}
    try:
        if name == "messages_read_latest":
            n = int(args.get("n") or 5)
            return {"messages": [m.model_dump() for m in svc.store.latest(max(1, min(n, 20)))]}
        if name == "messages_list_channels":
            if not svc._channels_loaded:
                await svc.refresh_channels()
            return {"channels": [{"id": c.id, "name": c.name, "kind": c.kind, "unread": c.unread, "mentions": c.mentions,
                                  "last_text": c.last_text} for c in svc.channel_summaries()]}
        if name == "messages_read_channel":
            channel = str(args.get("channel") or "").strip()
            if not channel:
                return {"error": "channel is empty"}
            n = max(1, min(int(args.get("n") or 10), 50))
            rec, messages, _ = await svc.history(channel, n)
            return {"channel": rec.id, "channel_name": rec.label, "kind": rec.kind, "messages": [_row(m) for m in messages]}
        if name == "messages_open":
            channel = str(args.get("channel") or "").strip()
            if not channel:
                return {"error": "channel is empty"}
            return await svc.open_conversation(channel)
        if name == "messages_send":
            text = str(args.get("text") or "").strip()
            if not text:
                return {"error": "text is empty"}
            reply_to_ts = args.get("reply_to_ts")
            if not (reply_to_ts or args.get("thread_ts") or args.get("channel")) and svc.focused is not None:
                reply_to_ts = svc.focused.ts  # "Reply with Bit": no target named -> the message in focus
            draft = await svc.create_draft(text, args.get("channel"), args.get("thread_ts"), reply_to_ts)
            return {"draft_id": draft.draft_id, "status": "awaiting_confirmation", "channel_name": draft.channel_name,
                    "text": draft.text, "as": "you" if svc.as_user else DISPLAY_NAME,
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
