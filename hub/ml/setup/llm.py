"""The one wrapper for every LLM call in ML setup (ML_PLAN 6.7): switching models is a change to this file.

Claude via the Anthropic SDK: structured outputs (the reply always parses against the schema), streaming
(adapter replies are long), automatic prompt caching (the repo files are re-sent every round), and the
server-side fallback for policy declines. The API key lives only in the hub's environment
(ANTHROPIC_API_KEY, or an `ant auth login` profile) and never in git.
"""

from __future__ import annotations

import json
import os
from dataclasses import dataclass, field
from typing import Any

MODEL = os.environ.get("FORGE_LLM_MODEL", "claude-opus-5-5")
MAX_TOKENS = 32_000
BETAS = ["server-side-fallback-2026-07-01"]
NO_CREDENTIALS = "no valid Anthropic credentials: set ANTHROPIC_API_KEY in the hub's environment"


class LLMError(RuntimeError):
    """The model declined, ran out of tokens, or the API failed. The message is safe to show the user."""


@dataclass
class LLMResult:
    data: dict
    content: list[Any]  # the assistant turn as returned; append it unchanged for the next round
    usage: dict = field(default_factory=dict)


_client = None


def _get_client():
    global _client
    if _client is None:
        import anthropic

        _client = anthropic.Anthropic()
    return _client


def complete_json(system: str, messages: list[dict], schema: dict, *, timeout: float = 60.0,
                  effort: str = "medium") -> LLMResult:
    """One call; returns the reply parsed as JSON matching `schema` (a JSON Schema object).

    `messages` is the conversation so far. For another round, append
    {"role": "assistant", "content": result.content} and the next user message.
    """
    import anthropic

    client = _get_client().with_options(timeout=timeout, max_retries=1)
    try:
        with client.beta.messages.stream(
            model=MODEL,
            max_tokens=MAX_TOKENS,
            system=system,
            messages=messages,
            output_config={"effort": effort, "format": {"type": "json_schema", "schema": schema}},
            cache_control={"type": "ephemeral"},  # caches the growing prefix: repo files are paid once
            betas=BETAS,
            fallbacks="default",
        ) as stream:
            message = stream.get_final_message()
    except anthropic.AuthenticationError as exc:
        raise LLMError(NO_CREDENTIALS) from exc
    except TypeError as exc:  # the SDK raises this before sending when no credentials are configured at all
        if "authentication" in str(exc).lower():
            raise LLMError(NO_CREDENTIALS) from exc
        raise
    except anthropic.RateLimitError as exc:
        raise LLMError("Anthropic rate limit hit; try again in a minute") from exc
    except anthropic.APITimeoutError as exc:
        raise LLMError(f"the LLM took longer than {timeout:.0f}s") from exc
    except anthropic.APIStatusError as exc:
        raise LLMError(f"Anthropic API error {exc.status_code}: {exc.message}") from exc
    except anthropic.APIConnectionError as exc:
        raise LLMError("can't reach the Anthropic API (network)") from exc

    if message.stop_reason == "refusal":
        category = getattr(message.stop_details, "category", None) if message.stop_details else None
        raise LLMError(f"the model declined this request ({category or 'no category'})")
    if message.stop_reason == "max_tokens":
        raise LLMError(f"the reply hit the {MAX_TOKENS}-token limit before finishing")
    text = next((b.text for b in message.content if b.type == "text"), None)
    if text is None:
        raise LLMError("the model returned no text")
    try:
        data = json.loads(text)
    except json.JSONDecodeError as exc:
        raise LLMError(f"the reply wasn't valid JSON: {exc}") from exc
    u = message.usage
    usage = {"input": u.input_tokens, "output": u.output_tokens,
             "cache_read": u.cache_read_input_tokens or 0, "cache_write": u.cache_creation_input_tokens or 0}
    return LLMResult(data=data, content=list(message.content), usage=usage)
