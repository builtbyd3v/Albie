"""Configuration for the optional macOS Messages database reader. Off unless IMESSAGE_ENABLED=1 (opt-in).

IMESSAGE_ALLOW is the privacy gate: only 1:1 chats with these handles are ever read or sent to. Empty = nothing.
"""
from __future__ import annotations

import os
import re
import sys
from dataclasses import dataclass, field
from pathlib import Path


def normalize_handle(raw: str) -> str:
    """One canonical form per handle: emails lowercased, phones as +digits (a bare 10-digit number is US, +1)."""
    value = raw.strip()
    if "@" in value:
        return value.lower()
    value = re.sub(r"[\s\-().]", "", value)
    if value.isdigit() and len(value) == 10:
        return "+1" + value
    if value.isdigit() and len(value) == 11 and value.startswith("1"):
        return "+" + value
    return value


def handle_variants(handle: str) -> set[str]:
    """The spellings chat.db may store for one normalized handle (after the same punctuation strip in SQL)."""
    if "@" in handle or not handle.startswith("+"):
        return {handle}
    out = {handle, handle[1:]}
    if handle.startswith("+1") and len(handle) == 12:
        out.add(handle[2:])
    return out


def parse_allow(raw: str) -> frozenset[str]:
    return frozenset(h for h in (normalize_handle(part) for part in raw.split(",")) if h)


@dataclass(frozen=True)
class IMessageSettings:
    enabled: bool
    db_path: Path
    poll_ms: int = 1000
    recent_days: int = 3
    digest: bool = False
    cli: str = "imsg"
    allow: frozenset[str] = field(default_factory=frozenset)

    @classmethod
    def from_env(cls) -> "IMessageSettings":
        default_path = Path.home() / "Library" / "Messages" / "chat.db"
        # Opt-in: it reads personal texts into the headset, so it never turns on by itself.
        enabled = sys.platform == "darwin" and os.getenv("IMESSAGE_ENABLED", "").strip().lower() in {"1", "true", "yes", "on"}
        try:
            poll_ms = max(250, min(30_000, int(os.getenv("IMESSAGE_POLL_MS", "1000"))))
        except ValueError:
            poll_ms = 1000
        try:
            recent_days = max(1, min(3650, int(os.getenv("IMESSAGE_RECENT_DAYS", "3"))))
        except ValueError:
            recent_days = 3
        digest = os.getenv("IMESSAGE_DIGEST", "false").strip().lower() in {"1", "true", "yes", "on"}
        return cls(enabled=enabled, db_path=Path(os.getenv("IMESSAGE_DB", str(default_path))).expanduser(),
                   poll_ms=poll_ms, recent_days=recent_days, digest=digest,
                   cli=os.getenv("IMESSAGE_CLI", "imsg").strip() or "imsg",
                   allow=parse_allow(os.getenv("IMESSAGE_ALLOW", "")))
