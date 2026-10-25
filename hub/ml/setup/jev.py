"""TypeSafe Jev: a decision model for the classify step (ML_PLAN 6.4), the idea from PR #2.

Jev answers typed questions about a `state` (here the pre-scan + repo files): `choice` over named options
with `confidence` and per-option `probabilities`, `noul` for yes/no. No JSON to repair, ~0.4 s, about
$0.001 per repo. It can't write text, so the adapter (6.5) still needs an LLM (llm.py).
Docs: docs.typesafe.ai/api. Env: TYPESAFE_API_KEY (hub environment only, never in git).
"""

from __future__ import annotations

import json
import os
import urllib.error
import urllib.request
from typing import Any

from .llm import LLMError

JEV_URL = "https://api.typesafe.ai/v1/systemone"
JEV_MODEL = os.environ.get("FORGE_JEV_MODEL", "jev-1.14.0")
LONG_CONTEXT_HEADER = "TypeSafe-Danger-Zone-Long-Context"  # experimental: past the ~64k-token default
DEFAULT_LIMIT_TOKENS = 60_000
MAX_CRITERIA = 255
TIMEOUT_S = 60


def available() -> bool:
    return bool(os.environ.get("TYPESAFE_API_KEY"))


def ask(state: str, questions: dict[str, Any], timeout: float = TIMEOUT_S) -> dict[str, Any]:
    """One systemone call. Returns the full response: {model, answers: {name: {...}}, usage}."""
    key = os.environ.get("TYPESAFE_API_KEY")
    if not key:
        raise LLMError("no TYPESAFE_API_KEY in the hub's environment")
    body = {"model": JEV_MODEL, "state": state, "questions": questions}
    data = json.dumps(body).encode()
    headers = {"Content-Type": "application/json", "Authorization": f"Bearer {key}"}
    if len(data) // 4 > DEFAULT_LIMIT_TOKENS:  # rough: 4 chars per token
        headers[LONG_CONTEXT_HEADER] = "true"
    req = urllib.request.Request(JEV_URL, data=data, headers=headers, method="POST")
    try:
        with urllib.request.urlopen(req, timeout=timeout) as resp:
            return json.load(resp)
    except urllib.error.HTTPError as exc:
        detail = exc.read().decode(errors="replace")[:500]
        if exc.code in (401, 403):
            raise LLMError("TypeSafe rejected the API key (TYPESAFE_API_KEY)") from exc
        raise LLMError(f"TypeSafe API error {exc.code}: {detail}") from exc
    except (urllib.error.URLError, TimeoutError) as exc:
        raise LLMError(f"can't reach TypeSafe ({getattr(exc, 'reason', exc)})") from exc
