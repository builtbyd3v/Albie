"""REST for messaging (PROJECT_CONTEXT section 7): /messages/latest, /messages/drafts[/{id}/send|cancel], /messages/digest,
and /messages/focus (the REST form of the ws `messages.focus` message: "Reply with Bit" on a card).

There is no direct send endpoint. A draft is created (nothing posted), Unity shows it as a Send / Cancel card,
and only /drafts/{id}/send posts it, under the user's own name.
"""
from __future__ import annotations

from fastapi import APIRouter, Depends, HTTPException, Query, Request
from pydantic import BaseModel

from events import Draft, NotifyDigest, SlackMessage

from .slack_client import DraftError, SlackService

router = APIRouter(prefix="/messages", tags=["messages"])


def service(request: Request) -> SlackService:
    return request.app.state.slack


def enabled(request: Request) -> SlackService:
    svc = service(request)
    if not svc.enabled:
        raise HTTPException(503, "Slack is not configured: set SLACK_BOT_TOKEN and SLACK_APP_TOKEN in hub/.env")
    return svc


class DraftRequest(BaseModel):
    channel: str | None = None     # id or #name; omitted -> the replied-to message's channel, else SLACK_DEFAULT_CHANNEL
    text: str
    thread_ts: str | None = None   # reply in this thread
    reply_to_ts: str | None = None  # the message being answered (attaches reply_to, implies its channel + thread)


class SentReply(BaseModel):
    ts: str


class OkReply(BaseModel):
    ok: bool = True


class DigestRequest(BaseModel):
    pet_id: str = "bit"


class FocusRequest(BaseModel):
    pet_id: str = "bit"
    channel: str
    ts: str


@router.get("/latest", response_model=list[SlackMessage])
async def latest(n: int = Query(5, ge=1, le=50), svc: SlackService = Depends(enabled)) -> list[SlackMessage]:
    return svc.store.latest(n)


@router.post("/drafts", response_model=Draft)
async def create_draft(req: DraftRequest, svc: SlackService = Depends(enabled)) -> Draft:
    """Write the reply and emit notify.draft. Posts nothing."""
    if not req.text.strip():
        raise HTTPException(422, "text is empty")
    try:
        return await svc.create_draft(req.text, req.channel, req.thread_ts, req.reply_to_ts)
    except LookupError as e:
        raise HTTPException(404, str(e)) from None
    except Exception as e:  # noqa: BLE001 - Slack API errors come back as a readable 502
        raise HTTPException(502, f"Slack error: {e}") from None


@router.post("/drafts/{draft_id}/send", response_model=SentReply)
async def send_draft(draft_id: str, svc: SlackService = Depends(enabled)) -> SentReply:
    """The card's Send button: post the draft under the user's name, emit notify.sent."""
    try:
        _, ts = await svc.send_draft(draft_id)
        return SentReply(ts=ts)
    except DraftError as e:
        raise HTTPException(404, str(e)) from None
    except Exception as e:  # noqa: BLE001
        raise HTTPException(502, f"Slack refused the message: {e}") from None


@router.post("/drafts/{draft_id}/cancel", response_model=OkReply)
async def cancel_draft(draft_id: str, svc: SlackService = Depends(enabled)) -> OkReply:
    try:
        await svc.cancel_draft(draft_id)
    except DraftError as e:
        raise HTTPException(404, str(e)) from None
    return OkReply()


@router.post("/focus", response_model=SlackMessage)
async def focus(req: FocusRequest, svc: SlackService = Depends(enabled)) -> SlackMessage:
    """Tap on a message card: Bit reads it out and offers to answer; the next messages_send replies in that thread."""
    msg = await svc.focus(req.channel, req.ts, req.pet_id)
    if msg is None:
        raise HTTPException(404, f"No message {req.ts} in {req.channel} (only the last 50 are kept)")
    return msg


@router.post("/digest", response_model=NotifyDigest)
async def digest(req: DigestRequest | None = None, svc: SlackService = Depends(service)) -> NotifyDigest:
    """Unity calls this on the first arrival at the desk. Works with Slack disabled too (count 0, Bit says there is nothing new)."""
    return await svc.digest((req or DigestRequest()).pet_id)
