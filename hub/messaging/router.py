"""REST for messaging (PROJECT_CONTEXT section 7): the Slack panel's channel list and history
(/messages/channels, /messages/history, /messages/channels/{id}/seen, /messages/users/me), /messages/latest,
/messages/drafts (create, PATCH, send, cancel), /messages/digest, and /messages/focus (the REST form of the ws
`messages.focus` message: "Reply with Bit" on a card).

There is no direct send endpoint. A draft is created (nothing posted), Unity shows it in the composer with Send /
Cancel, the user may edit it (PATCH), and only /drafts/{id}/send posts it, under the user's own name.

Every endpoint but /digest answers 503 while Slack is disabled; a Slack rate limit that outlasts one Retry-After wait
answers 503 with {"error": "ratelimited", "retry_after": s}.
"""
from __future__ import annotations

import logging
from typing import Literal

from fastapi import APIRouter, Depends, HTTPException, Query, Request
from pydantic import BaseModel

from events import ChannelSummary, Draft, NotifyDigest, SlackMessage

from .slack_client import DraftError, RateLimited, SlackService

router = APIRouter(prefix="/messages", tags=["messages"])
log = logging.getLogger("forge.messaging.router")


def service(request: Request) -> SlackService:
    return request.app.state.slack


def enabled(request: Request) -> SlackService:
    svc = service(request)
    if not svc.enabled:
        raise HTTPException(503, "Slack is not configured: set SLACK_BOT_TOKEN and SLACK_APP_TOKEN in hub/.env")
    return svc


def _slack_error(e: Exception, what: str) -> HTTPException:
    if isinstance(e, RateLimited):
        return HTTPException(503, {"error": "ratelimited", "retry_after": e.retry_after, "detail": str(e)})
    if isinstance(e, DraftError):
        return HTTPException(404, str(e))
    if isinstance(e, LookupError):
        return HTTPException(404, str(e))
    if isinstance(e, ValueError):
        return HTTPException(422, str(e))
    return HTTPException(502, f"{what}: {e}")


class DraftRequest(BaseModel):
    channel: str | None = None     # id, #name or a person's name; omitted -> the replied-to message's channel, else SLACK_DEFAULT_CHANNEL
    text: str
    thread_ts: str | None = None   # reply in this thread
    reply_to_ts: str | None = None  # the message being answered (attaches reply_to, implies its channel + thread)


class DraftPatch(BaseModel):
    """Only the fields sent change (`thread_ts: null` clears the thread)."""
    text: str | None = None
    channel: str | None = None
    thread_ts: str | None = None


class SentReply(BaseModel):
    ts: str


class OkReply(BaseModel):
    ok: bool = True


class SeenReply(BaseModel):
    ok: bool = True
    unread: int = 0


class ChannelsReply(BaseModel):
    channels: list[ChannelSummary]


class HistoryReply(BaseModel):
    channel: str
    channel_name: str                 # as the channel list names it: "#forge-team", the DM partner
    kind: Literal["channel", "private", "dm", "group-dm"]
    messages: list[SlackMessage]      # oldest -> newest, top-level only (reply_count on threaded ones)
    has_more: bool


class MeReply(BaseModel):
    user_id: str
    name: str
    display_name: str


class DigestRequest(BaseModel):
    pet_id: str = "bit"


class FocusRequest(BaseModel):
    pet_id: str = "bit"
    channel: str
    ts: str


# ------------------------------------------------------------------ the panel
@router.get("/channels", response_model=ChannelsReply)
async def channels(request: Request, svc: SlackService = Depends(service)) -> ChannelsReply:
    """Every conversation the user is in, with unread / mention counts. Same rows as notify.channels."""
    result = []
    if svc.enabled and not svc._channels_loaded:
        try:
            await svc.refresh_channels()
        except Exception as e:  # noqa: BLE001
            raise _slack_error(e, "Slack error") from None
    result.extend(svc.channel_summaries())
    result.sort(key=lambda ch: float(ch.last_ts or 0), reverse=True)
    imessage = getattr(request.app.state, "imessage", None)
    if not result and not svc.enabled and (imessage is None or not imessage.available):
        raise HTTPException(503, "Messaging is not configured on this hub")
    return ChannelsReply(channels=result)


@router.get("/history", response_model=HistoryReply)
async def history(request: Request, channel: str = Query(..., description="conversation id (or #name / a person)"),
                  limit: int = Query(50, ge=1, le=100), before: str | None = Query(None, description="ts; older than this"),
                  svc: SlackService = Depends(service)) -> HistoryReply:
    try:
        if channel.startswith("im:"):
            imessage = getattr(request.app.state, "imessage", None)
            if imessage is None or not imessage.available:
                raise HTTPException(503, "iMessage is unavailable on this Mac")
            channel_id, channel_name, kind, messages, has_more = await imessage.history(channel, limit, before)
            return HistoryReply(channel=channel_id, channel_name=channel_name, kind=kind,
                                messages=messages, has_more=has_more)
        if not svc.enabled:
            raise HTTPException(503, "Slack is not configured on this hub")
        rec, messages, has_more = await svc.history(channel, limit, before)
    except HTTPException:
        raise
    except Exception as e:  # noqa: BLE001
        raise _slack_error(e, "Slack error") from None
    return HistoryReply(channel=rec.id, channel_name=rec.label or rec.short_name, kind=rec.kind,  # type: ignore[arg-type]
                        messages=messages, has_more=has_more)


@router.post("/channels/{channel_id}/seen", response_model=SeenReply)
async def seen(channel_id: str, request: Request, svc: SlackService = Depends(service)) -> SeenReply:
    """The user has this conversation open: its unread count drops to 0 (notify.channels follows)."""
    try:
        if channel_id.startswith("im:"):
            imessage = getattr(request.app.state, "imessage", None)
            if imessage is None or not imessage.available:
                raise HTTPException(503, "iMessage is unavailable on this Mac")
            svc.store.mark_seen(channel_id)
            svc.channels_changed()
        else:
            if not svc.enabled:
                raise HTTPException(503, "Slack is not configured on this hub")
            await svc.mark_seen(channel_id)
    except HTTPException:
        raise
    except Exception as e:  # noqa: BLE001
        raise _slack_error(e, "Slack error") from None
    return SeenReply()


@router.get("/users/me", response_model=MeReply)
async def me(svc: SlackService = Depends(enabled)) -> MeReply:
    """Who the hub posts as (the user with SLACK_USER_TOKEN, else Bit)."""
    return MeReply(**await svc.me())


@router.get("/latest", response_model=list[SlackMessage])
async def latest(request: Request, n: int = Query(5, ge=1, le=50),
                 svc: SlackService = Depends(service)) -> list[SlackMessage]:
    imessage = getattr(request.app.state, "imessage", None)
    if not svc.enabled and (imessage is None or not imessage.available):
        raise HTTPException(503, "Messaging is not configured on this hub")
    return svc.store.latest(n)


# ------------------------------------------------------------------ drafts
@router.post("/drafts", response_model=Draft)
async def create_draft(req: DraftRequest, request: Request, svc: SlackService = Depends(service)) -> Draft:
    """Write the reply and emit notify.draft. Posts nothing."""
    if not req.text.strip():
        raise HTTPException(422, "text is empty")
    try:
        if req.channel and req.channel.startswith("im:"):
            imessage = getattr(request.app.state, "imessage", None)
            if imessage is None or not imessage.available:
                raise HTTPException(503, "iMessage is unavailable on this Mac")
            if svc.open_draft is not None:
                await svc.cancel_draft(svc.open_draft.draft_id)
            return await imessage.create_draft(req.text, req.channel, req.reply_to_ts)
        if not svc.enabled:
            raise HTTPException(503, "Slack is not configured on this hub")
        imessage = getattr(request.app.state, "imessage", None)
        if imessage is not None and imessage.open_draft is not None:
            await imessage.cancel_draft(imessage.open_draft.draft_id)
        return await svc.create_draft(req.text, req.channel, req.thread_ts, req.reply_to_ts)
    except HTTPException:
        raise
    except Exception as e:  # noqa: BLE001 - Slack API errors come back as a readable 502
        raise _slack_error(e, "Slack error") from None


@router.patch("/drafts/{draft_id}", response_model=Draft)
async def update_draft(draft_id: str, req: DraftPatch, request: Request,
                       svc: SlackService = Depends(service)) -> Draft:
    """The user edited the composer: same draft_id, `edited: true`, notify.draft again. 404 if the draft is gone."""
    changes = req.model_dump(include=req.model_fields_set)
    try:
        if draft_id.startswith("im-"):
            imessage = getattr(request.app.state, "imessage", None)
            if imessage is None or not imessage.available:
                raise HTTPException(503, "iMessage is unavailable on this Mac")
            return await imessage.update_draft(draft_id, changes)
        if not svc.enabled:
            raise HTTPException(503, "Slack is not configured on this hub")
        return await svc.update_draft(draft_id, changes)
    except HTTPException:
        raise
    except Exception as e:  # noqa: BLE001
        raise _slack_error(e, "Slack error") from None


@router.post("/drafts/{draft_id}/send", response_model=SentReply)
async def send_draft(draft_id: str, request: Request, svc: SlackService = Depends(service)) -> SentReply:
    """The card's Send button: post the draft under the user's name, emit notify.sent."""
    try:
        if draft_id.startswith("im-"):
            imessage = getattr(request.app.state, "imessage", None)
            if imessage is None or not imessage.available:
                raise HTTPException(503, "iMessage is unavailable on this Mac")
            _, ts = await imessage.send_draft(draft_id)
        else:
            if not svc.enabled:
                raise HTTPException(503, "Slack is not configured on this hub")
            _, ts = await svc.send_draft(draft_id)
        return SentReply(ts=ts)
    except DraftError as e:
        raise HTTPException(404, str(e)) from None
    except HTTPException:
        raise
    except Exception as e:  # noqa: BLE001
        raise _slack_error(e, "Slack refused the message") from None


@router.post("/drafts/{draft_id}/cancel", response_model=OkReply)
async def cancel_draft(draft_id: str, request: Request, svc: SlackService = Depends(service)) -> OkReply:
    try:
        if draft_id.startswith("im-"):
            imessage = getattr(request.app.state, "imessage", None)
            if imessage is None or not imessage.available:
                raise HTTPException(503, "iMessage is unavailable on this Mac")
            await imessage.cancel_draft(draft_id)
        else:
            if not svc.enabled:
                raise HTTPException(503, "Slack is not configured on this hub")
            await svc.cancel_draft(draft_id)
    except DraftError as e:
        raise HTTPException(404, str(e)) from None
    return OkReply()


# ------------------------------------------------------------------ focus + digest
@router.post("/focus", response_model=SlackMessage)
async def focus(req: FocusRequest, request: Request, svc: SlackService = Depends(service)) -> SlackMessage:
    """Tap on a message card: Bit reads it out and offers to answer; the next messages_send replies in that thread."""
    if req.channel.startswith("im:"):
        imessage = getattr(request.app.state, "imessage", None)
        if imessage is None or not imessage.available:
            raise HTTPException(503, "iMessage is unavailable on this Mac")
        msg = await imessage.focus(req.channel, req.ts, req.pet_id)
        if msg is not None:
            svc.focused = None  # one message in focus across both providers
    else:
        if not svc.enabled:
            raise HTTPException(503, "Slack is not configured on this hub")
        msg = await svc.focus(req.channel, req.ts, req.pet_id)
        imessage = getattr(request.app.state, "imessage", None)
        if msg is not None and imessage is not None:
            imessage.focused = None
    if msg is None:
        raise HTTPException(404, f"No message {req.ts} in {req.channel} (only the last 500 per conversation are kept)")
    return msg


@router.post("/digest", response_model=NotifyDigest)
async def digest(request: Request, req: DigestRequest | None = None,
                 svc: SlackService = Depends(service)) -> NotifyDigest:
    """Unity calls this on the first arrival at the desk. Works with Slack disabled too (count 0, Bit says there is nothing new)."""
    pet_id = (req or DigestRequest()).pet_id
    imessage = getattr(request.app.state, "imessage", None)
    if imessage is None or not imessage.available:
        return await svc.digest(pet_id)
    unread = svc.store.unread()
    messages = [m for m in unread if m.source != "imessage" or imessage.settings.digest]
    digest = NotifyDigest.build(messages)
    for channel in {m.channel for m in messages}:
        svc.store.mark_seen(channel)
    svc.channels_changed()
    await svc.bus.publish("notify.digest", digest)
    await svc.bus.publish("pet.say", {"pet_id": pet_id, "text": digest.line})
    try:
        await svc.voice_speak(pet_id, digest.line)
    except Exception:  # noqa: BLE001 - speech is best-effort; the digest card already went out
        log.exception("combined messaging digest speech failed")
    return digest
