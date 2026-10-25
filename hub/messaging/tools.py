"""Bit's messaging tools (voice model + Claude runner), registered through agent/tools.py like the CAD tools.

Reading: messages_read_latest (the inbox, newest first), messages_list_channels (every conversation with its unread
count), messages_read_channel (the last n in one conversation). Showing: messages_open puts a conversation on the
Slack panel (opens the MSG view). Sending is two tools on purpose: messages_send only
WRITES a draft (Unity shows it in the composer with Send / Cancel); messages_confirm is the spoken "send it" /
"never mind". Nothing reaches Slack or iMessage without one of those two confirmations. messages_edit_draft rewords the
open draft in place (same draft, same files). messages_send can attach the open CAD design's files (`attach`, resolved
by main.py through cad/tools.py `attachments`) and show the draft in the messenger view (`open_messenger`, through
`show`). iMessage drafts (opt-in, messaging/imessage/) carry text only.
"""
from __future__ import annotations

from collections.abc import Awaitable, Callable
from typing import TYPE_CHECKING, Any

from agent.events import DISPLAY_NAME

from events import Attachment

from .slack_client import AmbiguousName, SlackService

Attach = Callable[[list[str]], Awaitable[list[Attachment]]]  # ["all"] / ["step", "drawing"] -> files on the hub's disk
Show = Callable[[str], Awaitable[dict[str, Any]]]
FILE_KINDS = ["all", "step", "stl", "drawing", "parts"]

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
    {"name": "messages_open",
     "description": "Open a conversation in the MSG view (it opens if it isn't already): a Slack channel ('#general'), a "
                    "person's name for the user's Slack DM with them ('Adrian'), or an iMessage chat (an im: id from "
                    "messages_list_channels, or via='imessage' with the contact's name). Use it for 'open my chat with "
                    "Adrian', 'pull up #general', 'show me my texts with Mom'. It only shows it; nothing is sent. The "
                    "user has to be at their desk. Then say in a few words what you opened.",
     "parameters": {"type": "object", "properties": {
         "channel": {"type": "string", "description": "#name, channel id, a person's name (their DM), or im:<chat-guid>"},
         "via": {"type": "string", "enum": ["slack", "imessage"],
                 "description": "optional; which messenger when a name could be either (default: Slack, then iMessage)"}},
         "required": ["channel"]}},
    {"name": "messages_send",
     "description": "Write a Slack or iMessage reply on the user's behalf. This only WRITES A DRAFT: nothing is sent. The "
                    "text appears with Send / Cancel buttons. After calling, tell the user in one short line what you "
                    "wrote (and which files are on it) and ask them to tap Send or say 'send it'. NEVER say the message "
                    "was sent. It posts under the USER's own name, so write in their voice (first person, their tone). To "
                    "answer someone, pass that message's ts as reply_to_ts; the reply then goes to their channel, in "
                    "thread. To message a person directly ('message Adrian ...') pass their name as channel: it becomes "
                    "their DM. For an iMessage reply, pass its ts as reply_to_ts; for a new iMessage draft, set "
                    "via='imessage' and pass an existing contact/chat name or im:<chat-guid> as channel. attach puts the "
                    "open CAD design's files on a Slack draft (for a supplier, a teammate or a print shop; 'with all the "
                    "files' = all). To change the wording afterwards use messages_edit_draft; calling this again starts a "
                    "new draft and drops the old one and its files.",
     "parameters": {"type": "object", "properties": {
         "text": {"type": "string", "description": "the message, as the user would write it"},
         "reply_to_ts": {"type": "string", "description": "ts of the message being answered (optional)"},
         "channel": {"type": "string", "description": "channel id, #name, or a person's name for a DM (optional; "
                                                      "default channel otherwise); or an iMessage chat id/name"},
         "via": {"type": "string", "enum": ["slack", "imessage"], "description": "Optional provider for a new conversation"},
         "thread_ts": {"type": "string", "description": "thread to post in (optional; implied by reply_to_ts)"},
         "attach": {"type": "array", "items": {"type": "string", "enum": FILE_KINDS},
                    "description": "files of the CAD design open on the desk, current version: step = the CAD model, "
                                   "stl = mesh for 3D printing, drawing = engineering drawing PDF, parts = parts list "
                                   "CSV, all = all four (Slack only)"},
         "open_messenger": {"type": "boolean",
                            "description": "show the draft in the Slack messenger view (default: true with files, "
                                           "else it pops up as a card next to you)"}},
         "required": ["text"]}},
    {"name": "messages_edit_draft",
     "description": "Reword the open Slack draft ('rephrase it', 'make it shorter', 'say we need them by Friday'): pass "
                    "the whole new text. Keeps the recipient, the thread and the files; the card updates. Still NOT sent: "
                    "say what changed and ask them to tap Send or say 'send it'.",
     "parameters": {"type": "object", "properties": {
         "text": {"type": "string", "description": "the full new message, in the user's voice"}},
         "required": ["text"]}},
    {"name": "messages_confirm",
     "description": "The user's decision on the open draft. send=true when they say 'send it' / 'yes' / 'go ahead' "
                    "(posts it under their name, with its files), send=false when they say 'cancel' / 'never mind' / "
                    "'don't send'. Only call this after the user clearly decided; a tap on the card does the same thing.",
     "parameters": {"type": "object", "properties": {
         "send": {"type": "boolean", "description": "true = post it, false = discard it"},
         "draft_id": {"type": "string", "description": "optional; defaults to the open draft"}},
         "required": ["send"]}},
]


def _row(m: Any) -> dict[str, Any]:
    return m.model_dump(exclude_none=True)


def _files(draft: Any) -> list[str]:
    return [a.name for a in draft.attachments]


async def call_tool(name: str, args: dict[str, Any], svc: SlackService, imessage: IMessageService | None = None,
                    attach: Attach | None = None, show: Show | None = None) -> dict[str, Any]:
    """Run one tool. Errors come back as {"error": ...} so the model can explain them. `attach` makes the CAD files,
    `show(view)` opens a workspace view (agent/workspace.py); without them those options answer an error / do nothing."""
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
        if name == "messages_open":
            channel = str(args.get("channel") or "").strip()
            if not channel:
                return {"error": "channel is empty"}
            has_imessage = imessage is not None and imessage.available
            use_imessage = channel.startswith("im:") or args.get("via") == "imessage" or not svc.enabled
            if not use_imessage:
                try:
                    return await svc.open_conversation(channel)
                except AmbiguousName:
                    raise
                except LookupError:
                    if not has_imessage:
                        raise
                    use_imessage = True  # not a Slack channel or person: maybe an iMessage contact
            if imessage is None or not imessage.available:
                return {"error": "iMessage is unavailable on this hub"}
            guid = await imessage.resolve_channel(channel)
            cid = f"im:{guid}"
            row = next((c for c in imessage.channel_summaries() if c.id == cid), None)
            return await svc.show_conversation(cid, imessage.chat_name(guid), row.kind if row else "dm")
        if name == "messages_send":
            text = str(args.get("text") or "").strip()
            if not text:
                return {"error": "text is empty"}
            kinds = [str(k).strip().lower() for k in args.get("attach") or [] if str(k).strip()]
            files: list[Attachment] = []
            if kinds:
                if attach is None:
                    return {"error": "Attaching files isn't available on this hub"}
                try:
                    files = await attach(kinds)
                except Exception as e:  # noqa: BLE001 - a CAD failure, not a Slack one
                    return {"error": f"Couldn't make the design's files: {e}"}
            view: dict[str, Any] = {}
            opening = args.get("open_messenger")
            if (bool(files) if opening is None else bool(opening)) and show is not None:
                shown = await show("messages")  # first, so the messenger's composer takes the draft
                view = {"view_error": shown["error"]} if "error" in shown else {}
            reply_to_ts = args.get("reply_to_ts")
            requested_channel = args.get("channel")
            reply_to = svc.store.find(str(reply_to_ts)) if reply_to_ts else None
            focused_imessage = bool(imessage and imessage.focused and not
                                    (reply_to_ts or args.get("thread_ts") or requested_channel))
            use_imessage = (args.get("via") == "imessage" or str(requested_channel or "").startswith("im:")
                            or bool(reply_to and reply_to.source == "imessage") or focused_imessage)
            if use_imessage:
                if files:
                    return {"error": "Files can only go on a Slack message, not an iMessage"}
                if imessage is None or not imessage.available:
                    return {"error": "iMessage is unavailable on this Mac"}
                if svc.open_draft is not None:
                    await svc.cancel_draft(svc.open_draft.draft_id)
                draft = await imessage.create_draft(text, requested_channel,
                                                     str(reply_to_ts) if reply_to_ts else None)
                return {"draft_id": draft.draft_id, "status": "awaiting_confirmation", "source": "imessage",
                        "channel_name": draft.channel_name, "text": draft.text,
                        "next": "Tell the user what you wrote and ask them to tap Send or say 'send it'. Not sent yet.",
                        **view}
            if not (reply_to_ts or args.get("thread_ts") or requested_channel) and svc.focused is not None:
                reply_to_ts = svc.focused.ts  # reply from a message card: no target named -> the message in focus
            if not svc.enabled:
                return {"error": "Slack is not configured on this hub"}
            if imessage is not None and imessage.open_draft is not None:
                await imessage.cancel_draft(imessage.open_draft.draft_id)
            draft = await svc.create_draft(text, requested_channel, args.get("thread_ts"), reply_to_ts,
                                           attachments=files)
            return {"draft_id": draft.draft_id, "status": "awaiting_confirmation", "channel_name": draft.channel_name,
                    "text": draft.text, "files": _files(draft), "as": "you" if svc.as_user else DISPLAY_NAME,
                    "next": "Tell the user what you wrote and ask them to tap Send or say 'send it'. Not sent yet.",
                    **view}
        if name == "messages_edit_draft":
            text = str(args.get("text") or "").strip()
            if not text:
                return {"error": "text is empty"}
            target: Any = svc
            if svc.open_draft is None and imessage is not None and imessage.open_draft is not None:
                target = imessage
            if target.open_draft is None:
                return {"error": "There's no open draft (it was sent, cancelled or expired); write a new one with "
                                 "messages_send"}
            draft = await target.update_draft(target.open_draft.draft_id, {"text": text})
            return {"draft_id": draft.draft_id, "status": "awaiting_confirmation", "channel_name": draft.channel_name,
                    "text": draft.text, "files": _files(draft),
                    "next": "Say what changed and ask them to tap Send or say 'send it'. Not sent yet."}
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
                        "channel_name": draft.channel_name, "text": draft.text, "files": _files(draft)}
            draft = await (imessage.cancel_draft(draft_id) if use_imessage else svc.cancel_draft(draft_id))
            return {"status": "cancelled", "draft_id": draft.draft_id}
    except (LookupError, ValueError) as e:  # DraftError is a LookupError
        return {"error": str(e)}
    except Exception as e:  # noqa: BLE001
        return {"error": f"Messaging error: {e}"}
    return {"error": f"Unknown messaging tool {name!r}"}
