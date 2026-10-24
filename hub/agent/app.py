"""Agent-module helpers for the hub app (main.py): hub/.env loading and the /health fields.

The agent has no app of its own any more: there is one process, `uvicorn main:app`, and one socket, `/ws`.
"""

from __future__ import annotations

import os
from pathlib import Path

from dotenv import load_dotenv

from .bus import bus

HUB_DIR = Path(__file__).resolve().parents[1]


def load_env() -> None:
    """Load hub/.env (does not override variables already set in the shell)."""
    load_dotenv(HUB_DIR / ".env")


def health_fields() -> dict[str, object]:
    """Agent fields merged into /health."""
    return {
        "fake_agent": os.getenv("FAKE_AGENT", "0") == "1",
        "stt": os.getenv("STT_PROVIDER", "none"),
        "agent_sockets": bus.count,
    }
