"""Agent REST endpoints (PROJECT_CONTEXT section 7): typed fallback for talking to Bit."""
from __future__ import annotations

import anthropic
from fastapi import APIRouter, Depends, HTTPException, Request
from pydantic import BaseModel, Field

from agent.brain import Brain

router = APIRouter(prefix="/agent", tags=["agent"])


def brain(request: Request) -> Brain:
    return request.app.state.brain


class TextIn(BaseModel):
    pet_id: str
    text: str = Field(min_length=1, max_length=2000)


@router.post("/text")
async def post_text(body: TextIn, b: Brain = Depends(brain)) -> dict[str, str]:
    try:
        return {"reply": await b.reply(body.pet_id, body.text)}
    except anthropic.AuthenticationError:
        raise HTTPException(503, "Claude API key missing or invalid (set ANTHROPIC_API_KEY)") from None
    except anthropic.RateLimitError:
        raise HTTPException(429, "Claude rate limit hit, try again in a moment") from None
    except anthropic.APIStatusError as e:
        raise HTTPException(502, f"Claude API error {e.status_code}: {e.message}") from None
    except anthropic.APIConnectionError:
        raise HTTPException(502, "Can't reach the Claude API (network)") from None
