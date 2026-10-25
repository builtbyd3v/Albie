"""Recent Slack messages per conversation and the per-conversation "seen up to" marks behind the sit-down digest
and the channel list's unread counts.

One ring of the last `maxlen` messages per channel id (bounded by `max_channels` rings; the one whose newest message
is oldest goes first), merged by ts so a history fetch and a live event never duplicate a post. `seen` is one ts per
channel plus a floor for every channel (what `mark_seen(None)`, the digest, sets); both live in state.json. The old
single `last_seen_ts` file is read as the floor.
"""
from __future__ import annotations

import json
import logging
from pathlib import Path

from events import SlackMessage

log = logging.getLogger("forge.messaging")

STATE_VERSION = 2


def _ts(value: str | None) -> float:
    try:
        return float(value or 0)
    except (TypeError, ValueError):
        return 0.0


def _is_reply(m: SlackMessage) -> bool:
    return bool(m.thread_ts) and m.thread_ts != m.ts


class MessageStore:
    def __init__(self, data_dir: Path, maxlen: int = 500, max_channels: int = 400) -> None:
        self.data_dir = Path(data_dir)
        self.state_path = self.data_dir / "state.json"
        self.maxlen = maxlen
        self.max_channels = max_channels
        self._rings: dict[str, dict[str, SlackMessage]] = {}   # channel -> ts -> message
        self.seen: dict[str, str] = {}                        # channel -> last seen ts
        self.seen_floor: str = "0"                            # every channel is seen up to here (the digest's mark)
        self._load()

    # ------------------------------------------------------------------ persistence
    def _load(self) -> None:
        try:
            state = json.loads(self.state_path.read_text(encoding="utf-8"))
        except FileNotFoundError:
            return
        except (OSError, ValueError) as e:
            log.warning("messages state unreadable (%s); starting at 0", e)
            return
        if "seen" in state or "seen_floor" in state:
            self.seen_floor = str(state.get("seen_floor", "0"))
            self.seen = {str(k): str(v) for k, v in (state.get("seen") or {}).items()}
        else:  # version 1: one mark for everything
            self.seen_floor = str(state.get("last_seen_ts", "0"))
            if _ts(self.seen_floor) > 0:
                log.info("messages state migrated: last_seen_ts %s is now the floor for every channel", self.seen_floor)

    def _save(self) -> None:
        self.data_dir.mkdir(parents=True, exist_ok=True)
        body = {"version": STATE_VERSION, "seen_floor": self.seen_floor, "seen": self.seen}
        self.state_path.write_text(json.dumps(body), encoding="utf-8")

    @property
    def last_seen_ts(self) -> str:
        """The floor: everything, in every channel, is seen up to here (what the digest sets)."""
        return self.seen_floor

    def seen_ts(self, channel: str) -> str:
        """Where this channel is seen up to: its own mark or the floor, whichever is later."""
        own = self.seen.get(channel, "0")
        return own if _ts(own) >= _ts(self.seen_floor) else self.seen_floor

    # ------------------------------------------------------------------ messages
    def add(self, message: SlackMessage) -> bool:
        """Merge one message by (channel, ts). True when it is new, False when it replaced the stored copy."""
        ring = self._rings.get(message.channel)
        if ring is None:
            if len(self._rings) >= self.max_channels:
                self._evict_channel()
            ring = self._rings[message.channel] = {}
        new = message.ts not in ring
        ring[message.ts] = message
        if len(ring) > self.maxlen:
            del ring[min(ring, key=_ts)]
        return new

    def _evict_channel(self) -> None:
        def newest(cid: str) -> float:
            return max((_ts(ts) for ts in self._rings[cid]), default=0.0)

        victim = min(self._rings, key=newest)
        del self._rings[victim]

    def get(self, channel: str, ts: str) -> SlackMessage | None:
        return self._rings.get(channel, {}).get(ts)

    def find(self, ts: str) -> SlackMessage | None:
        """A message by ts in any channel (ts are unique enough across a workspace for the reply flow)."""
        for ring in self._rings.values():
            if ts in ring:
                return ring[ts]
        return None

    def channels(self) -> list[str]:
        return list(self._rings)

    def _all(self, channel: str | None) -> list[SlackMessage]:
        if channel is not None:
            return list(self._rings.get(channel, {}).values())
        return [m for ring in self._rings.values() for m in ring.values()]

    def latest(self, n: int = 5, channel: str | None = None) -> list[SlackMessage]:
        """Newest first, across every channel or one."""
        ordered = sorted(self._all(channel), key=lambda m: _ts(m.ts), reverse=True)
        return ordered[: max(0, n)]

    def last(self, channel: str) -> SlackMessage | None:
        ring = self._rings.get(channel)
        if not ring:
            return None
        return ring[max(ring, key=_ts)]

    def count(self, channel: str) -> int:
        return len(self._rings.get(channel, {}))

    def history(self, channel: str, limit: int = 50, before: str | None = None) -> list[SlackMessage]:
        """Top-level messages (thread replies left out) oldest -> newest, the `limit` newest ones older than `before`."""
        cutoff = _ts(before) if before else None
        rows = [m for m in self._all(channel) if not _is_reply(m) and (cutoff is None or _ts(m.ts) < cutoff)]
        rows.sort(key=lambda m: _ts(m.ts))
        return rows[-max(0, limit):] if limit else []

    def oldest_ts(self, channel: str) -> str | None:
        ring = self._rings.get(channel)
        return min(ring, key=_ts) if ring else None

    # ------------------------------------------------------------------ seen / unread
    def unread(self, channel: str | None = None) -> list[SlackMessage]:
        """Inbox messages (not the user's own) newer than their channel's seen mark, newest first."""
        out = [m for m in self._all(channel) if not m.is_me and _ts(m.ts) > _ts(self.seen_ts(m.channel))]
        out.sort(key=lambda m: _ts(m.ts), reverse=True)
        return out

    def mentions(self, channel: str | None = None) -> list[SlackMessage]:
        return [m for m in self.unread(channel) if m.mentions_me]

    def mark_seen(self, channel: str | None = None) -> bool:
        """Move the mark to the newest message we have: in one channel, or (None) everywhere, which also raises the
        floor so conversations not loaded yet count as seen. True when anything moved."""
        if channel is not None:
            newest = self.last(channel)
            if newest is None or _ts(newest.ts) <= _ts(self.seen_ts(channel)):
                return False
            self.seen[channel] = newest.ts
            self._save()
            return True
        moved = False
        newest_all = "0"
        for cid in self._rings:
            newest = self.last(cid)
            if newest is None:
                continue
            if _ts(newest.ts) > _ts(newest_all):
                newest_all = newest.ts
            if _ts(newest.ts) > _ts(self.seen_ts(cid)):
                self.seen[cid] = newest.ts
                moved = True
        if _ts(newest_all) > _ts(self.seen_floor):
            self.seen_floor = newest_all
            moved = True
        if moved:
            self._save()
        return moved

    def __len__(self) -> int:
        return sum(len(r) for r in self._rings.values())
