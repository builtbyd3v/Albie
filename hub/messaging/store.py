"""Recent Slack messages and the "seen up to" mark behind the sit-down digest."""
from __future__ import annotations

import json
import logging
from collections import deque
from pathlib import Path

from events import SlackMessage

log = logging.getLogger("forge.messaging")


def _ts(value: str) -> float:
    try:
        return float(value)
    except (TypeError, ValueError):
        return 0.0


class MessageStore:
    """Ring buffer of the last messages (newest first on read) plus last_seen_ts persisted to state.json."""

    def __init__(self, data_dir: Path, maxlen: int = 50) -> None:
        self.data_dir = Path(data_dir)
        self.state_path = self.data_dir / "state.json"
        self._messages: deque[SlackMessage] = deque(maxlen=maxlen)
        self.last_seen_ts: str = "0"
        self._load()

    def _load(self) -> None:
        try:
            state = json.loads(self.state_path.read_text(encoding="utf-8"))
            self.last_seen_ts = str(state.get("last_seen_ts", "0"))
        except FileNotFoundError:
            pass
        except (OSError, ValueError) as e:
            log.warning("messages state unreadable (%s); starting at 0", e)

    def _save(self) -> None:
        self.data_dir.mkdir(parents=True, exist_ok=True)
        self.state_path.write_text(json.dumps({"last_seen_ts": self.last_seen_ts}), encoding="utf-8")

    def add(self, message: SlackMessage) -> None:
        self._messages.append(message)

    def latest(self, n: int = 5) -> list[SlackMessage]:
        """Newest first."""
        ordered = sorted(self._messages, key=lambda m: _ts(m.ts), reverse=True)
        return ordered[: max(0, n)]

    def unread(self) -> list[SlackMessage]:
        """Everything newer than the last digest, newest first."""
        seen = _ts(self.last_seen_ts)
        return [m for m in self.latest(len(self._messages)) if _ts(m.ts) > seen]

    def mark_seen(self) -> None:
        """Move the mark to the newest message we have (no-op when empty)."""
        newest = max((_ts(m.ts) for m in self._messages), default=None)
        if newest is None or newest <= _ts(self.last_seen_ts):
            return
        self.last_seen_ts = next(m.ts for m in self._messages if _ts(m.ts) == newest)
        self._save()

    def __len__(self) -> int:
        return len(self._messages)
