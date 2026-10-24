"""REST for messaging (PROJECT_CONTEXT section 7): /messages/latest, /messages/send, /messages/digest."""
from __future__ import annotations

from fastapi import APIRouter, Depends, HTTPException, Query, Request
from pydantic import BaseModel

from events import NotifyDigest, SlackMessage

from .slack_client import SlackService

router = APIRouter(prefix="/messages", tags=["messages"])


def service(request: Request) -> SlackService:
    return request.app.state.slack


def enabled(request: Request) -> SlackService:
    svc = service(request)
    if not svc.enabled:
        raise HTTPException(503, "Slack is not configured: set SLACK_BOT_TOKEN and SLACK_APP_TOKEN in hub/.env")
    return svc


class SendRequest(BaseModel):
    channel: str | None = None   # id or #name; omitted -> SLACK_DEFAULT_CHANNEL
    text: str
    thread_ts: str | None = None


class SendReply(BaseModel):
    ts: str


class DigestRequest(BaseModel):
    pet_id: str = "bit"


@router.get("/latest", response_model=list[SlackMessage])
async def latest(n: int = Query(5, ge=1, le=50), svc: SlackService = Depends(enabled)) -> list[SlackMessage]:
    return svc.store.latest(n)


@router.post("/send", response_model=SendReply)
async def send(req: SendRequest, svc: SlackService = Depends(enabled)) -> SendReply:
    if not req.text.strip():
        raise HTTPException(422, "text is empty")
    try:
        return SendReply(ts=await svc.send(req.channel, req.text, req.thread_ts))
    except LookupError as e:
        raise HTTPException(404, str(e)) from None
    except Exception as e:  # noqa: BLE001 - Slack API errors come back as a readable 502
        raise HTTPException(502, f"Slack refused the message: {e}") from None


@router.post("/digest", response_model=NotifyDigest)
async def digest(req: DigestRequest | None = None, svc: SlackService = Depends(service)) -> NotifyDigest:
    """Unity calls this when DeskPresence goes Seated. Works with Slack disabled too (empty digest, Bit stays quiet)."""
    return await svc.digest((req or DigestRequest()).pet_id)
