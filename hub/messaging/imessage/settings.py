"""Configuration for the optional macOS Messages database reader. Off unless IMESSAGE_ENABLED=1 (opt-in)."""
from __future__ import annotations

import os
import sys
from dataclasses import dataclass
from pathlib import Path


@dataclass(frozen=True)
class IMessageSettings:
    enabled: bool
    db_path: Path
    poll_ms: int = 1000
    recent_days: int = 3
    digest: bool = False
    cli: str = "imsg"

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
                   cli=os.getenv("IMESSAGE_CLI", "imsg").strip() or "imsg")
