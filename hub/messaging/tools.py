"""Bit's messaging tools (voice model + Claude runner), registered through agent/tools.py like the CAD tools.

Reading: messages_read_latest (the inbox, newest first), messages_list_channels (every conversation with its unread
count), messages_read_channel (the last n in one conversation). Sending is two tools on purpose: messages_send only
WRITES a draft (Unity shows it in the composer with Send / Cancel); messages_confirm is the spoken "send it" /
"never mind". Nothing reaches Slack or iMessage without one of those two confirmations.
"""
from __future__ import annotations

from typing import TYPE_CHECKING, Any

from agent.events import DISPLAY_NAME

from .slack_client import SlackService

if TYPE_CHECKING:
    from .imessage.service import IMessageService

TOOLS: list[dict[str, Any]] = [
    {"name": "messages_read_latest",
     "description": "The newest Slack and iMessage messages in the user's inbox across every conversation (source, channel, who, text, ts, "
                    "is_dm, mentions_me). Use it to answer 'what did X say' or before replying to someone. Newest first.",
     "parameters": {"type": "object", "properties": {
         "n": {"type": "integer", "minimum": 1, "maximum": 20, "description": "how many (default 5)"}}}},
    {"name": "messages_list_channels",
     "description": "The user's Slack and iMessage conversations: channels, private channels, DMs and group "
                    "DMs, each with its unread and mention counts and the last message. Use it for 'anything new?', "
                    "'who messaged me?' or to find the right channel or person before messages_read_channel / messages_send.",
     "parameters": {"type": "object", "properties": {}}},
    {"name": "messages_read_channel",
     "description": "The last n messages of ONE Slack or iMessage conversation, oldest to newest, including the user's own "
                    "(is_me). For iMessage, use a channel id from messages_list_channels (starts with im:).",
     "parameters": {"type": "object", "properties": {
         "channel": {"type": "string", "description": "#name, id, or a person's name (their DM)"},
         "n": {"type": "integer", "minimum": 1, "maximum": 50, "description": "how many (default 10)"}},
         "required": ["channel"]}},
    {"name": "messages_send",
     "description": "Write a Slack or iMessage reply on the user's behalf. This only WRITES A DRAFT: nothing is sent. The text "
                    "appears in the composer with Send / Cancel buttons next to you. After calling, tell the user in one "
                    "short line what you wrote and ask them to tap Send or say 'send it'. NEVER say the message was sent. "
                    "The message posts under the USER's own name, so write in their voice (first person, their tone). To "
                    "answer someone, pass that message's ts as reply_to_ts; the reply then goes to their channel, in thread. "
                    "For an iMessage reply, pass its ts as reply_to_ts; for a new iMessage draft, set via='imessage' and "
                    "pass an existing contact/chat name or im:<chat-guid> as channel. "
                    "Calling again replaces the previous draft (use that when the user wants changes).",
     "parameters": {"type": "object", "properties": {
         "text": {"type": "string", "description": "the message, as the user would write it"},
         "reply_to_ts": {"type": "string", "description": "ts of the message being answered (optional)"},
         "channel": {"type": "string", "description": "Slack channel/person name or an iMessage chat id/name"},
         "via": {"type": "string", "enum": ["slack", "imessage"], "description": "Optional provider for a new conversation"},
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


async def call_tool(name: str, args: dict[str, Any], svc: SlackService,
                    imessage: IMessageService | None = None) -> dict[str, Any]:
    """Run one tool. Errors come back as {"error": ...} so the model can explain them."""
    if not svc.enabled and (imessage is None or not imessage.available):
        return {"error": "Messaging is not configured on this hub"}
    try:
        if name == "messages_read_latest":
            n = int(args.get("n") or 5)
            return {"messages": [m.model_dump() for m in svc.store.latest(max(1, min(n, 20)))]}
        if name == "messages_list_channels":
            if svc.enabled and not svc._channels_loaded:
                await svc.refresh_channels()
            channels = [{"id": c.id, "name": c.name, "kind": c.kind, "unread": c.unread, "mentions": c.mentions,
                         "last_text": c.last_text, "source": c.source, "last_ts": c.last_ts}
                        for c in svc.channel_summaries()] if svc.enabled else []
            if not svc.enabled and imessage is not None and imessage.available:
                channels.extend({"id": c.id, "name": c.name, "kind": c.kind, "unread": c.unread,
                                 "mentions": c.mentions, "last_text": c.last_text, "source": c.source,
                                 "last_ts": c.last_ts} for c in imessage.channel_summaries())
            channels.sort(key=lambda c: float(c.get("last_ts") or 0), reverse=True)
            return {"channels": channels}
        if name == "messages_read_channel":
            channel = str(args.get("channel") or "").strip()
            if not channel:
                return {"error": "channel is empty"}
            n = max(1, min(int(args.get("n") or 10), 50))
            if channel.startswith("im:"):
                if imessage is None or not imessage.available:
                    return {"error": "iMessage is unavailable on this Mac"}
                channel_id, channel_name, kind, messages, _ = await imessage.history(channel, n)
                return {"channel": channel_id, "channel_name": channel_name, "kind": kind,
                        "messages": [_row(m) for m in messages]}
            rec, messages, _ = await svc.history(channel, n)
            return {"channel": rec.id, "channel_name": rec.label, "kind": rec.kind, "messages": [_row(m) for m in messages]}
        if name == "messages_send":
            text = str(args.get("text") or "").strip()
            if not text:
                return {"error": "text is empty"}
            reply_to_ts = args.get("reply_to_ts")
            requested_channel = args.get("channel")
            reply_to = svc.store.find(str(reply_to_ts)) if reply_to_ts else None
            focused_imessage = bool(imessage and imessage.focused and not
                                    (reply_to_ts or args.get("thread_ts") or requested_channel))
            use_imessage = (args.get("via") == "imessage" or str(requested_channel or "").startswith("im:")
                            or bool(reply_to and reply_to.source == "imessage") or focused_imessage)
            if use_imessage:
                if imessage is None or not imessage.available:
                    return {"error": "iMessage is unavailable on this Mac"}
                if svc.open_draft is not None:
                    await svc.cancel_draft(svc.open_draft.draft_id)
                draft = await imessage.create_draft(text, requested_channel,
                                                     str(reply_to_ts) if reply_to_ts else None)
                return {"draft_id": draft.draft_id, "status": "awaiting_confirmation", "source": "imessage",
                        "channel_name": draft.channel_name, "text": draft.text,
                        "next": "Tell the user what you wrote and ask them to tap Send or say 'send it'. Not sent yet."}
            if not (reply_to_ts or args.get("thread_ts") or requested_channel) and svc.focused is not None:
                reply_to_ts = svc.focused.ts
            if not svc.enabled:
                return {"error": "Slack is not configured on this hub"}
            if imessage is not None and imessage.open_draft is not None:
                await imessage.cancel_draft(imessage.open_draft.draft_id)
            draft = await svc.create_draft(text, requested_channel, args.get("thread_ts"), reply_to_ts)
            return {"draft_id": draft.draft_id, "status": "awaiting_confirmation", "channel_name": draft.channel_name,
                    "text": draft.text, "as": "you" if svc.as_user else DISPLAY_NAME,
                    "next": "Tell the user what you wrote and ask them to tap Send or say 'send it'. Not sent yet."}
        if name == "messages_confirm":
            draft_id = args.get("draft_id") or None
            use_imessage = str(draft_id or "").startswith("im-")
            if use_imessage and (imessage is None or not imessage.available):
                return {"error": "iMessage is unavailable on this Mac"}
            if not use_imessage and not draft_id and imessage and imessage.open_draft and not svc.open_draft:
                use_imessage = True
            if bool(args.get("send")):
                draft, ts = await (imessage.send_draft(draft_id) if use_imessage else svc.send_draft(draft_id))
                return {"status": "sent", "source": draft.source, "ts": ts,
                        "channel_name": draft.channel_name, "text": draft.text}
            draft = await (imessage.cancel_draft(draft_id) if use_imessage else svc.cancel_draft(draft_id))
            return {"status": "cancelled", "draft_id": draft.draft_id}
    except (LookupError, ValueError) as e:  # DraftError is a LookupError
        return {"error": str(e)}
    except Exception as e:  # noqa: BLE001
        return {"error": f"Messaging error: {e}"}
    return {"error": f"Unknown messaging tool {name!r}"}
