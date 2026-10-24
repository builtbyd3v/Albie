"""Bit's Slack connection: Bolt AsyncApp over Socket Mode, running inside the hub's event loop.

Two identities (PROJECT_CONTEXT 8.7):
- the app's bot token keeps the socket and is the fallback identity;
- the USER's token (SLACK_USER_TOKEN) is how Bit reads the user's conversations (DMs it can't see as a bot)
  and posts replies under the user's own name. Without it Bit falls back to the bot: it only sees channels it
  was invited to and posts as "Bit", with one warning.

Inbound `message` / `app_mention` events (bot and user subscriptions, deduped) -> SlackMessage -> MessageStore +
notify.message on the EventBus. The user's own posts are kept too (`is_me`, so the open conversation stays live);
the digest and the unread counts skip them. Sending is two steps: `create_draft` emits notify.draft and nothing
leaves; `update_draft` (the composer's edit) re-emits it with `edited`; `send_draft` (the Send button or the
messages_confirm tool) posts it and emits notify.sent.
`digest` is the sit-down summary across every conversation: notify.digest, pet.say and the spoken greeting.
`focus` is "Reply with Bit" on a message (ws `messages.focus` / POST /messages/focus).

The Slack-like panel (PROJECT_CONTEXT 7): `refresh_channels` lists every conversation the user is in
(`conversations.list`, at connect and every 60 s), `channel_summaries` shapes the channel list with per-channel unread
/ mention counts from the store, and `notify.channels` (sticky, debounced 300 ms) goes out whenever those change.
`history` serves a conversation's top-level messages from the store when it has a full page, else from
`conversations.history`, merged into the store so the next open is instant and live events append.

Once connected, `backfill` (a background task) reads what arrived while the hub was down: every conversation the
user is in since its seen mark (or the last two hours), through the same filter as live events but without
notify.message, so the sit-down digest counts them and Unity sees no flood.

Rate limits: one Retry-After wait per call (REST) or per run (backfill, refresh); the next one fails soft:
`RateLimited` -> a 503 with a JSON error from the router, one log line. Without tokens the service stays disabled
(one warning) and the hub boots as usual. Connection trouble is logged and retried; it never takes the hub down.
"""
from __future__ import annotations

import asyncio
import contextlib
import logging
import re
import time
import uuid
from collections import deque
from collections.abc import Awaitable, Callable
from dataclasses import dataclass, field
from typing import Any

from agent.events import MessagesFocus
from events import (ChannelSummary, Draft, EventBus, NotifyChannels, NotifyDigest, NotifyDraftCancelled, NotifySent,
                    SlackMessage)

from .settings import SlackSettings
from .store import MessageStore

log = logging.getLogger("forge.messaging")

VoiceSpeak = Callable[[str, str], Awaitable[bool]]
VoiceNote = Callable[[str, str], Awaitable[bool]]  # (pet_id, one line of context the voice model reads, no reply)
NOTIFY_CHANNELS = "notify.channels"
FOCUS_TEXT_CHARS = 200
ACCEPTED_SUBTYPES: tuple[str | None, ...] = (None, "thread_broadcast", "file_share")
RECONNECT_S = (2, 5, 10, 30)
DRAFT_EXPIRY_S = 120.0
CONVERSATION_TYPES = "public_channel,private_channel,im,mpim"
LIST_PAGES = 5               # conversations.list / users.list pages of 200
BACKFILL_PER_CHANNEL = 20    # newest messages per conversation
BACKFILL_WINDOW_S = 2 * 3600  # with no seen mark: this far back
CHANNELS_REFRESH_S = 60.0
CHANNELS_DEBOUNCE_S = 0.3
HISTORY_MAX = 100


class DraftError(LookupError):
    """No open draft with that id (never existed, already sent, cancelled or expired)."""


class RateLimited(RuntimeError):
    """Slack rate limited us twice in a row for one request; the router answers 503 with retry_after."""

    def __init__(self, what: str, retry_after: float) -> None:
        super().__init__(f"Slack rate limited {what}; retry in {retry_after:.0f}s")
        self.retry_after = retry_after


@dataclass
class UserRecord:
    id: str
    name: str = ""            # Slack username
    display_name: str = ""
    real_name: str = ""

    @property
    def label(self) -> str:
        return self.display_name or self.real_name or self.name or self.id

    @property
    def initials(self) -> str:
        return _initials(self.label)

    def matches(self, text: str) -> bool:
        """`text` names this person: a username, display or real name, or one of their name parts ("Hudson" for
        `marr.hudson` / "Hudson Marr"), case-insensitive. Several words must all be parts of one name."""
        wanted = _name_parts(text)
        if not wanted:
            return False
        for n in (self.name, self.display_name, self.real_name):
            if not n:
                continue
            if _clean(n).lower() == _clean(text).lower():
                return True
            if wanted <= _name_parts(n):
                return True
        return False


@dataclass
class ChannelRecord:
    id: str
    name: str = ""            # Slack's name for channels / mpims; "" for DMs
    kind: str = "channel"     # channel | private | dm | group-dm
    topic: str | None = None
    user: str | None = None   # DM: the other person
    is_member: bool = True
    members: list[str] = field(default_factory=list)  # group DM: usernames parsed from the mpdm- name
    label: str = ""           # what the panel shows: "#name", the person, or "a, b"

    @property
    def is_dm(self) -> bool:
        return self.kind in ("dm", "group-dm")

    @property
    def short_name(self) -> str:
        """SlackMessage.channel_name: the channel's name, "dm" or "group-dm"."""
        return self.kind if self.is_dm else (self.name or self.id)

    @classmethod
    def from_api(cls, ch: dict[str, Any]) -> "ChannelRecord":
        name = ch.get("name") or ""
        if ch.get("is_im"):
            kind = "dm"
        elif ch.get("is_mpim"):
            kind = "group-dm"
        elif ch.get("is_private") or ch.get("is_group"):
            kind = "private"
        else:
            kind = "channel"
        topic = (ch.get("topic") or {}).get("value") or None
        members = _mpdm_members(name) if kind == "group-dm" else []
        return cls(id=ch["id"], name=name, kind=kind, topic=topic, user=ch.get("user"),
                   is_member=bool(ch.get("is_member", True)) and not ch.get("is_user_deleted"), members=members)


def _ts(value: str | None) -> float:
    try:
        return float(value or 0)
    except (TypeError, ValueError):
        return 0.0


_ZERO_WIDTH = re.compile(r"[\u200b-\u200d\u2060\ufeff]")
_NAME_SEP = re.compile(r"[\s._\-@,]+")


def _clean(text: str) -> str:
    """Slack profiles carry zero-width spaces ("marr.\u200bhudson"); drop them."""
    return _ZERO_WIDTH.sub("", text or "").strip()


def _name_parts(text: str) -> set[str]:
    return {p for p in _NAME_SEP.split(_clean(text).lower()) if p}


def _initials(label: str) -> str:
    parts = [p for p in _NAME_SEP.split(_clean(label)) if p]
    if not parts:
        return "?"
    if len(parts) >= 2:
        return (parts[0][0] + parts[1][0]).upper()
    return parts[0][:2].upper()


def _mpdm_members(name: str) -> list[str]:
    """"mpdm-adrian--hudson--jane-1" -> ["adrian", "hudson", "jane"]."""
    if not name.startswith("mpdm-"):
        return []
    body = re.sub(r"-\d+$", "", name[len("mpdm-"):])
    return [p for p in body.split("--") if p]


def _looks_like_id(value: str) -> bool:
    """A conversation id (C..., G..., D...): upper-case alphanumerics with at least one digit, so "Dan" is a person."""
    return len(value) >= 2 and value[0] in "CGD" and value[1:].isalnum() and value.isupper() \
        and any(c.isdigit() for c in value)


def _error_of(exc: Exception) -> str:
    """Slack's error code ("missing_scope", "not_in_channel") when the exception carries a response, else str()."""
    resp = getattr(exc, "response", None)
    try:
        code = resp["error"] if resp is not None else None
    except (KeyError, TypeError):
        code = None
    return str(code or exc)


def _retry_after(exc: Exception) -> float | None:
    """Seconds to wait when `exc` is a Slack `ratelimited` error, else None."""
    if _error_of(exc) != "ratelimited":
        return None
    headers = getattr(getattr(exc, "response", None), "headers", None) or {}
    try:
        return max(1.0, float(headers.get("Retry-After", 1)))
    except (TypeError, ValueError):
        return 1.0


def _trim(text: str, limit: int = FOCUS_TEXT_CHARS) -> str:
    text = " ".join(text.split())
    return text if len(text) <= limit else text[: limit - 3].rstrip() + "..."


def _where(msg: SlackMessage) -> str:
    return "a DM" if msg.is_dm else f"#{msg.channel_name}"


def _is_reply(msg: SlackMessage) -> bool:
    return bool(msg.thread_ts) and msg.thread_ts != msg.ts


def focus_line(msg: SlackMessage) -> str:
    """What Bit says when a message card is tapped."""
    text = _trim(msg.text)
    if text and text[-1] not in ".!?":
        text += "."
    return f"{msg.user_name} in {_where(msg)} says: {text} Want me to answer?"


class SlackService:
    def __init__(self, settings: SlackSettings, store: MessageStore, bus: EventBus, voice_speak: VoiceSpeak,
                 voice_note: VoiceNote | None = None) -> None:
        self.settings = settings
        self.store = store
        self.bus = bus
        self.voice_speak = voice_speak
        self.voice_note = voice_note
        self.focused: SlackMessage | None = None   # "Reply with Bit": messages_send without a target answers this
        self.enabled = settings.configured
        self.connected = False
        self.bot_user_id: str | None = None
        self.user_id: str = settings.user_id        # the human; derived from the user token when we have one
        self.user_name: str | None = None
        self._client: Any = None                    # bot AsyncWebClient (or a test stub)
        self._user_client: Any = None               # user AsyncWebClient when SLACK_USER_TOKEN is set
        self._handler: Any = None                   # AsyncSocketModeHandler
        self._task: asyncio.Task[None] | None = None
        self._backfill: asyncio.Task[None] | None = None
        self._refresh: asyncio.Task[None] | None = None
        self._push: asyncio.Task[None] | None = None
        self.backfilled: int | None = None          # inbox messages recovered at startup (None = not run yet)
        self._waited = False                        # backfill / refresh: one rate-limit wait per run...
        self._gave_up = False                       # ...the second rate limit stops it
        self._users: dict[str, UserRecord] = {}
        self._users_loaded = False
        self._channels: dict[str, ChannelRecord] = {}
        self._channels_loaded = False
        self._channels_signature: tuple[Any, ...] = ()
        self._history_loaded: set[str] = set()      # conversations whose newest page came from Slack once
        self._has_more: dict[str, bool] = {}
        self._recent: deque[tuple[str, str]] = deque(maxlen=200)  # (channel, ts) already recorded live
        self._warned_as_bot = False
        self.channels_debounce_s = CHANNELS_DEBOUNCE_S
        self.channels_refresh_s = CHANNELS_REFRESH_S
        # drafts: exactly one open at a time
        self.open_draft: Draft | None = None
        self.draft_expiry_s = DRAFT_EXPIRY_S
        self._expiry: asyncio.Task[None] | None = None
        make_sticky = getattr(bus, "make_sticky", None)
        if make_sticky is not None:
            make_sticky(NOTIFY_CHANNELS)  # late joiners get the channel list on connect

    @property
    def as_user(self) -> bool:
        return self._user_client is not None

    # ------------------------------------------------------------------ lifecycle
    async def start(self) -> None:
        if not self.enabled:
            log.warning("Slack disabled: set SLACK_BOT_TOKEN and SLACK_APP_TOKEN in hub/.env (PROJECT_CONTEXT 8.7)")
            return
        import ssl

        import certifi
        from slack_bolt.adapter.socket_mode.aiohttp import AsyncSocketModeHandler
        from slack_bolt.async_app import AsyncApp
        from slack_sdk.web.async_client import AsyncWebClient

        # Explicit CA bundle: python.org builds on macOS ship without one, and both the Web API and the
        # Socket Mode WebSocket (which reuses web_client.ssl) fail with CERTIFICATE_VERIFY_FAILED otherwise.
        ssl_ctx = ssl.create_default_context(cafile=certifi.where())
        web_client = AsyncWebClient(token=self.settings.bot_token, ssl=ssl_ctx)
        app = AsyncApp(client=web_client)
        self._client = web_client
        if self.settings.user_token:
            self._user_client = AsyncWebClient(token=self.settings.user_token, ssl=ssl_ctx)
        else:
            log.warning("No SLACK_USER_TOKEN: Bit reads only channels it was invited to and posts as Bit, not as you")

        @app.event("message")  # bot and user subscriptions both land here; accepts() filters subtypes and senders
        async def _message(event: dict[str, Any]) -> None:
            await self.on_event(event)

        @app.event("app_mention")
        async def _mention(event: dict[str, Any]) -> None:
            await self.on_event(event)

        self._handler = AsyncSocketModeHandler(app, self.settings.app_token, web_client=web_client)
        self._task = asyncio.create_task(self._connect(), name="slack-connect")

    async def _connect(self) -> None:
        attempt = 0
        while True:
            try:
                if self.bot_user_id is None:
                    auth = await self._client.auth_test()
                    self.bot_user_id = auth["user_id"]
                    log.info("Slack bot %s (%s) in team %s", auth.get("user"), self.bot_user_id, auth.get("team"))
                if self._user_client is not None and self.user_name is None:
                    me = await self._user_client.auth_test()
                    self.user_id, self.user_name = me["user_id"], me.get("user") or me["user_id"]
                    log.info("Slack user token: acting as %s (%s)", self.user_name, self.user_id)
                await self._handler.connect_async()
                self.connected = True
                log.info("Slack Socket Mode connected (posting as %s)", "you" if self.as_user else "Bit")
                self._backfill = asyncio.create_task(self.backfill(), name="slack-backfill")
                self._refresh = asyncio.create_task(self._refresh_loop(), name="slack-channels-refresh")
                return
            except asyncio.CancelledError:
                raise
            except Exception as e:  # noqa: BLE001 - bad token, no network: keep the hub up and retry
                delay = RECONNECT_S[min(attempt, len(RECONNECT_S) - 1)]
                attempt += 1
                log.error("Slack connect failed (%s); retrying in %ss", e, delay)
                await asyncio.sleep(delay)

    async def stop(self) -> None:
        for task in (self._task, self._backfill, self._refresh, self._push, self._expiry):
            if task is not None:
                task.cancel()
                with contextlib.suppress(asyncio.CancelledError):
                    await task
        self._task = self._backfill = self._refresh = self._push = self._expiry = None
        if self._handler is not None and self.connected:
            with contextlib.suppress(Exception):
                await self._handler.close_async()
        self.connected = False

    def health(self) -> dict[str, Any]:
        return {"enabled": self.enabled, "connected": self.connected, "as_user": self.as_user,
                "bot_user_id": self.bot_user_id, "user_id": self.user_id or None,
                "channels": sum(1 for r in self._channels.values() if r.is_member),
                "open_draft": self.open_draft.draft_id if self.open_draft else None}

    # ------------------------------------------------------------------ Slack Web API plumbing
    @property
    def _reader(self) -> Any:
        """Client for lookups: the user's view when we have it (it can see their DMs), else the bot's."""
        return self._user_client if self._user_client is not None else self._client

    async def _call(self, what: str, call: Callable[..., Awaitable[Any]], **kwargs: Any) -> Any:
        """One Web API call for a request: wait out one rate limit, raise RateLimited on the second;
        anything else propagates (slack_sdk.errors.SlackApiError or a network error)."""
        waited = False
        while True:
            try:
                return await call(**kwargs)
            except asyncio.CancelledError:
                raise
            except Exception as e:  # noqa: BLE001
                retry_after = _retry_after(e)
                if retry_after is None:
                    raise
                if waited:
                    log.warning("Slack %s rate limited again; giving up (retry in %.0fs)", what, retry_after)
                    raise RateLimited(what, retry_after) from None
                waited = True
                log.warning("Slack %s rate limited; waiting %.0fs once", what, retry_after)
                await asyncio.sleep(retry_after)

    async def _api(self, what: str, call: Callable[..., Awaitable[Any]], **kwargs: Any) -> Any | None:
        """A call inside a background run (backfill, refresh): wait out the first rate limit (once per run), stop on
        the second, log and skip anything else (missing scope, not in channel, network). None = skipped."""
        while True:
            try:
                return await call(**kwargs)
            except asyncio.CancelledError:
                raise
            except Exception as e:  # noqa: BLE001
                retry_after = _retry_after(e)
                if retry_after is None:
                    log.warning("Slack %s failed (%s); skipping", what, _error_of(e))
                    return None
                if self._waited:
                    self._gave_up = True
                    log.warning("Slack %s rate limited again; this run stops here", what)
                    return None
                self._waited = True
                log.warning("Slack %s rate limited; waiting %.0fs once", what, retry_after)
                await asyncio.sleep(retry_after)

    async def _pages(self, what: str, call: Callable[..., Awaitable[Any]], key: str, **kwargs: Any
                     ) -> list[dict[str, Any]] | None:
        """Paginate a list call (soft errors). None when the first page already failed."""
        out: list[dict[str, Any]] = []
        cursor: str | None = None
        for i in range(LIST_PAGES):
            page = await self._api(what, call, limit=200, cursor=cursor, **kwargs)
            if page is None:
                return None if i == 0 else out
            out.extend(x for x in page.get(key, []) if isinstance(x, dict))
            cursor = (page.get("response_metadata") or {}).get("next_cursor") or None
            if not cursor:
                break
        return out

    # ------------------------------------------------------------------ users
    async def user(self, user_id: str) -> UserRecord:
        rec = self._users.get(user_id)
        if rec is None:
            rec = UserRecord(id=user_id)
            try:
                rec = self._user_from_api((await self._reader.users_info(user=user_id))["user"])
            except Exception as e:  # noqa: BLE001
                log.warning("users.info %s failed: %s", user_id, e)
            self._users[user_id] = rec
        return rec

    @staticmethod
    def _user_from_api(info: dict[str, Any]) -> UserRecord:
        profile = info.get("profile") or {}
        return UserRecord(id=info["id"], name=_clean(info.get("name") or ""), display_name=_clean(profile.get("display_name") or ""),
                          real_name=_clean(info.get("real_name") or profile.get("real_name") or ""))

    async def user_display_name(self, user_id: str) -> str:
        return (await self.user(user_id)).label

    async def _load_users(self) -> None:
        """users.list once (soft): names for DM partners and group DMs, and "tell Hudson ..." lookups."""
        if self._users_loaded:
            return
        rows = await self._pages("users.list", self._reader.users_list, "members")
        if rows is None:
            return
        for info in rows:
            if info.get("id") and not info.get("deleted"):
                self._users[info["id"]] = self._user_from_api(info)
        self._users_loaded = True
        log.info("Slack users: %d cached", len(rows))

    async def find_user(self, text: str) -> UserRecord | None:
        """The person called `text` ("Hudson", "@hudson", "Hudson M"), from the cache, after one users.list if needed."""
        wanted = text.strip().lstrip("@")
        if not wanted:
            return None
        hit = next((u for u in self._users.values() if u.matches(wanted)), None)
        if hit is None and not self._users_loaded:
            await self._load_users()
            hit = next((u for u in self._users.values() if u.matches(wanted)), None)
        return hit

    # ------------------------------------------------------------------ channels
    async def _register(self, rec: ChannelRecord) -> ChannelRecord:
        """Keep one record per conversation; resolve its panel label (DM partners through users.info)."""
        if rec.kind == "dm":
            rec.label = (await self.user(rec.user)).label if rec.user else "dm"
        elif rec.kind == "group-dm":
            others = [m for m in rec.members if m.lower() != (self.user_name or "").lower()]
            by_name = {u.name.lower(): u.label for u in self._users.values() if u.name}
            rec.label = ", ".join(by_name.get(m.lower(), m) for m in others) or rec.name or "group-dm"
        else:
            rec.label = f"#{rec.name}" if rec.name else rec.id
        self._channels[rec.id] = rec
        return rec

    async def record(self, channel_id: str) -> ChannelRecord:
        """The conversation's record, via conversations.info the first time. Unknown ids get a bare record."""
        rec = self._channels.get(channel_id)
        if rec is None:
            try:
                info = (await self._reader.conversations_info(channel=channel_id))["channel"]
                rec = ChannelRecord.from_api(info)
            except Exception as e:  # noqa: BLE001
                log.warning("conversations.info %s failed: %s", channel_id, e)
                rec = ChannelRecord(id=channel_id, name=channel_id)
            rec = await self._register(rec)
        return rec

    async def channel_info(self, channel_id: str) -> tuple[str, bool]:
        """(channel_name, is_dm) for SlackMessage. DMs are named "dm", group DMs "group-dm"."""
        rec = await self.record(channel_id)
        return rec.short_name, rec.is_dm

    async def channel_name(self, channel_id: str) -> str:
        return (await self.channel_info(channel_id))[0]

    async def channel_label(self, channel_id: str) -> str:
        """The panel's name for it: "#forge-team", the DM partner, the group DM's people."""
        return (await self.record(channel_id)).label

    def _by_name(self, name: str) -> ChannelRecord | None:
        return next((r for r in self._channels.values() if not r.is_dm and r.name.lower() == name), None)

    async def refresh_channels(self) -> bool:
        """conversations.list (every type, archived out, only conversations the user is in) -> records.
        True when the list changed (membership, names, topics). Soft on errors: the old list stays."""
        self._waited = self._gave_up = False
        rows = await self._pages("conversations.list", self._reader.conversations_list, "channels",
                                 types=CONVERSATION_TYPES, exclude_archived=True)
        if rows is None:
            return False
        listed: set[str] = set()
        for ch in rows:
            if not ch.get("id"):
                continue
            rec = ChannelRecord.from_api(ch)
            if not rec.is_member:
                self._channels.pop(rec.id, None)  # left it since the last refresh
                continue
            listed.add(rec.id)
            await self._register(rec)
        if self._channels_loaded:  # not the first list: channels the user left go; DMs learned from events stay
            for cid in [c for c, r in self._channels.items() if r.is_member and not r.is_dm and c not in listed]:
                del self._channels[cid]
        self._channels_loaded = True
        signature = tuple(sorted((r.id, r.label, r.kind, r.topic or "") for r in self._channels.values() if r.is_member))
        changed = signature != self._channels_signature
        self._channels_signature = signature
        return changed

    async def _refresh_loop(self) -> None:
        while True:
            await asyncio.sleep(self.channels_refresh_s)
            try:
                if await self.refresh_channels():
                    self.channels_changed()
            except asyncio.CancelledError:
                raise
            except Exception:  # noqa: BLE001
                log.exception("channel refresh failed")

    def _default_channel_id(self) -> str | None:
        value = self.settings.default_channel.strip()
        if not value:
            return None
        if _looks_like_id(value):
            return value
        rec = self._by_name(value.lstrip("#").lower())
        return rec.id if rec else None

    def channel_summaries(self) -> list[ChannelSummary]:
        """The channel list: every conversation the user is in, with counts from the store.
        DMs / group DMs with unread first, then by last_ts desc."""
        default = self._default_channel_id()
        rows: list[ChannelSummary] = []
        for rec in self._channels.values():
            if not rec.is_member:
                continue
            last = self.store.last(rec.id)
            unread = len(self.store.unread(rec.id))
            rows.append(ChannelSummary(id=rec.id, name=rec.label or rec.short_name, kind=rec.kind, unread=unread,  # type: ignore[arg-type]
                                       mentions=len(self.store.mentions(rec.id)) if unread else 0,
                                       last_ts=last.ts if last else None, last_text=last.text if last else None,
                                       topic=rec.topic, is_default=rec.id == default))
        rows.sort(key=lambda r: (0 if r.kind in ("dm", "group-dm") and r.unread else 1, -_ts(r.last_ts)))
        return rows

    def channels_changed(self) -> None:
        """Something in the channel list moved (a message, a seen mark, the list itself): push notify.channels soon.
        Debounced: one push per `channels_debounce_s`, always with the state at that moment."""
        if not self.enabled or (self._push is not None and not self._push.done()):
            return
        try:
            self._push = asyncio.get_running_loop().create_task(self._push_channels(), name="slack-notify-channels")
        except RuntimeError:  # no loop (a sync test): nothing to push to
            self._push = None

    async def _push_channels(self) -> None:
        await asyncio.sleep(self.channels_debounce_s)
        await self.bus.publish(NOTIFY_CHANNELS, NotifyChannels(channels=self.channel_summaries()))

    async def mark_seen(self, channel: str) -> str:
        """POST /messages/channels/{id}/seen: the user has this conversation open. Returns the resolved id."""
        cid = await self.resolve_channel(channel)
        if self.store.mark_seen(cid):
            self.channels_changed()
        return cid

    async def me(self) -> dict[str, str]:
        """Who the hub posts as: the user (with a user token) or Bit."""
        if self.as_user and self.user_id:
            u = await self.user(self.user_id)
            return {"user_id": self.user_id, "name": self.user_name or u.name or self.user_id, "display_name": u.label}
        return {"user_id": self.bot_user_id or "", "name": "bit", "display_name": "Bit"}

    # ------------------------------------------------------------------ inbound
    def accepts(self, event: dict[str, Any], live: bool = True) -> bool:
        if event.get("subtype") not in ACCEPTED_SUBTYPES:
            return False
        if event.get("bot_id") or not event.get("user") or not event.get("text") or not event.get("ts"):
            return False
        if self.bot_user_id and event["user"] == self.bot_user_id:
            return False
        if live and (event.get("channel", ""), event.get("ts", "")) in self._recent:
            return False  # bot + user subscriptions, or app_mention + message, deliver the same post twice
        return True

    async def to_message(self, event: dict[str, Any]) -> SlackMessage:
        """One Slack message dict (a live event or a conversations.history row with `channel` added) -> SlackMessage."""
        channel = event["channel"]
        name, is_dm = await self.channel_info(channel)
        uid = event["user"]
        who = await self.user(uid)
        text = event["text"]
        is_me = bool(self.user_id) and uid == self.user_id
        return SlackMessage(
            channel=channel, channel_name=name, is_dm=is_dm, user=uid, user_name=who.label, text=text,
            ts=event["ts"], thread_ts=event.get("thread_ts"),
            mentions_me=(not is_me) and (is_dm or (bool(self.user_id) and f"<@{self.user_id}>" in text)),
            reply_count=event.get("reply_count"), edited=bool(event.get("edited")), is_me=is_me,
            user_initials=who.initials,
        )

    async def on_event(self, event: dict[str, Any], publish: bool = True) -> SlackMessage | None:
        """One Slack event dict -> stored SlackMessage + notify.message, or None when ignored or already stored.
        publish=False (the startup backfill) stores it for the digest without telling Unity."""
        if not self.accepts(event):
            return None
        self._recent.append((event.get("channel", ""), event.get("ts", "")))
        msg = await self.to_message(event)
        if not self.store.add(msg):
            return None  # a history fetch got there first
        if _is_reply(msg):
            parent = self.store.get(msg.channel, msg.thread_ts or "")
            if parent is not None:
                self.store.add(parent.model_copy(update={"reply_count": (parent.reply_count or 0) + 1}))
        if publish:
            await self.bus.publish("notify.message", msg)
            self.channels_changed()
            log.info("slack #%s %s%s: %.80s", msg.channel_name, msg.user_name, " (you)" if msg.is_me else "", msg.text)
        return msg

    # ------------------------------------------------------------------ history
    async def history(self, channel: str, limit: int = 50, before: str | None = None
                      ) -> tuple[ChannelRecord, list[SlackMessage], bool]:
        """Top-level messages of one conversation, oldest -> newest, and whether older ones exist.
        From the store when it holds a full page (after one fetch), else conversations.history merged into it.
        Raises LookupError (no such conversation), RateLimited, or the Slack error."""
        limit = max(1, min(limit, HISTORY_MAX))
        cid = await self.resolve_channel(channel)
        rec = await self.record(cid)
        cached = self.store.history(cid, limit + 1, before)
        if cid in self._history_loaded and len(cached) >= limit:
            page = cached[-limit:]
            has_more = len(cached) > limit or self._has_more.get(cid, True)
            return rec, page, has_more
        try:
            res = await self._call(f"conversations.history {cid}", self._reader.conversations_history, channel=cid,
                                   limit=limit, latest=before, inclusive=False)
        except RateLimited:
            raise
        except Exception as e:  # noqa: BLE001
            code = _error_of(e)
            if code in ("channel_not_found", "not_in_channel"):
                raise LookupError(f"No Slack conversation {cid} ({code})") from None
            raise
        by_ts: dict[str, SlackMessage] = {}
        for m in res.get("messages", []):
            if not isinstance(m, dict):
                continue
            event = {**m, "channel": cid}
            if not self.accepts(event, live=False):
                continue
            msg = await self.to_message(event)
            self.store.add(msg)
            if not _is_reply(msg) or event.get("subtype") == "thread_broadcast":
                by_ts[msg.ts] = msg
        for m in self.store.history(cid, limit, before):  # live posts that arrived meanwhile
            by_ts.setdefault(m.ts, m)
        rows = sorted(by_ts.values(), key=lambda m: _ts(m.ts))[-limit:]
        has_more = bool(res.get("has_more"))
        if before is None:
            self._history_loaded.add(cid)
            self._has_more[cid] = has_more
        self.channels_changed()
        return rec, rows, has_more

    # ------------------------------------------------------------------ startup backfill
    async def backfill(self, now: float | None = None) -> int:
        """Pull what arrived while the hub was off into the store (no notify.message). Returns how many inbox
        messages (the user's own are stored but not counted). Also the first channel list + notify.channels."""
        window = f"{(now or time.time()) - BACKFILL_WINDOW_S:.6f}"
        await self._load_users()
        await self.refresh_channels()
        self._waited = self._gave_up = False
        records = [r for r in self._channels.values() if r.is_member]
        events: list[dict[str, Any]] = []
        skipped = 0
        for rec in records:
            seen = self.store.seen_ts(rec.id)
            oldest = seen if _ts(seen) > 0 else window
            history = await self._api(f"conversations.history {rec.id}", self._reader.conversations_history,
                                      channel=rec.id, oldest=oldest, limit=BACKFILL_PER_CHANNEL)
            if history is None:
                skipped += 1
                if self._gave_up:
                    break
                continue
            events.extend({**m, "channel": rec.id} for m in history.get("messages", []) if isinstance(m, dict))
        count = 0
        for event in sorted(events, key=lambda e: _ts(e.get("ts", ""))):  # oldest first: the ring keeps the newest
            msg = await self.on_event(event, publish=False)
            if msg is not None and not msg.is_me:
                count += 1
        self.backfilled = count
        self.channels_changed()
        log.info("Slack backfill: %d conversation(s), %d new message(s)%s", len(records), count,
                 f", {skipped} skipped" if skipped else "")
        return count

    # ------------------------------------------------------------------ resolving a target
    async def resolve_channel(self, channel: str | None) -> str:
        """Conversation id for an id, '#name' / 'name' (a channel), or a person's name (their DM, opened if needed);
        empty -> SLACK_DEFAULT_CHANNEL."""
        value = (channel or self.settings.default_channel or "").strip()
        if not value:
            raise LookupError("No channel given and SLACK_DEFAULT_CHANNEL is not set")
        if _looks_like_id(value):
            return value
        name = value.lstrip("#").lower()
        rec = self._by_name(name)
        if rec is None and (value.startswith("#") or not self._channels_loaded):
            await self.refresh_channels()  # a channel we haven't listed yet
            rec = self._by_name(name)
        if rec is not None:
            return rec.id
        if not value.startswith("#"):
            person = await self.find_user(value)
            if person is not None:
                return await self.dm_with(person.id)
        who = "you are" if self.as_user else "Bit is"
        raise LookupError(f"No Slack channel named #{name} (is it one {who} in?) and nobody called {value.lstrip('#')!r}")

    async def dm_with(self, user_id: str) -> str:
        """The DM conversation with that person: a known one, else conversations.open (im:write)."""
        rec = next((r for r in self._channels.values() if r.kind == "dm" and r.user == user_id), None)
        if rec is None:
            res = await self._call("conversations.open", self._reader.conversations_open, users=user_id)
            ch = res["channel"]
            rec = await self._register(ChannelRecord(id=ch["id"], kind="dm", user=user_id))
            log.info("opened DM %s with %s", rec.id, rec.label)
        return rec.id

    # ------------------------------------------------------------------ drafts (the only way out)
    async def create_draft(self, text: str, channel: str | None = None, thread_ts: str | None = None,
                           reply_to_ts: str | None = None) -> Draft:
        """Write a reply and show it (notify.draft). Nothing is posted until send_draft. Replaces any open draft."""
        if not self.enabled:
            raise RuntimeError("Slack is not configured on this hub")
        text = text.strip()
        if not text:
            raise ValueError("text is empty")
        reply_to = self._find(reply_to_ts or thread_ts)
        if reply_to is not None and channel is None:
            channel = reply_to.channel
        if thread_ts is None and reply_to is not None:
            thread_ts = reply_to.thread_ts or reply_to.ts
        channel_id = await self.resolve_channel(channel)
        await self._clear_draft("superseded")
        draft = Draft(draft_id=uuid.uuid4().hex[:8], channel=channel_id, channel_name=await self._draft_name(channel_id),
                      text=text, thread_ts=thread_ts, reply_to=reply_to)
        self._open(draft)
        await self.bus.publish("notify.draft", draft)
        log.info("draft %s -> %s: %.80s", draft.draft_id, draft.channel_name, draft.text)
        return draft

    async def update_draft(self, draft_id: str, changes: dict[str, Any]) -> Draft:
        """PATCH /messages/drafts/{id}: the user edited the composer. Same draft_id, `edited`, notify.draft again;
        the expiry timer restarts. `changes` holds only the fields that were sent (text, channel, thread_ts)."""
        draft = self.open_draft
        if draft is None or draft.draft_id != draft_id:
            raise DraftError(f"No open draft {draft_id} (sent, cancelled or expired?)")
        update: dict[str, Any] = {"edited": True}
        if "text" in changes:
            text = str(changes["text"] or "").strip()
            if not text:
                raise ValueError("text is empty")
            update["text"] = text
        if changes.get("channel"):
            cid = await self.resolve_channel(str(changes["channel"]))
            update["channel"], update["channel_name"] = cid, await self._draft_name(cid)
            if cid != draft.channel:
                update["thread_ts"], update["reply_to"] = None, None  # a thread lives in its channel
        if "thread_ts" in changes:
            update["thread_ts"] = changes["thread_ts"] or None
        if self.open_draft is not draft:
            raise DraftError(f"Draft {draft_id} closed while updating")  # expired during the channel lookup
        new = draft.model_copy(update=update)
        self._take(draft_id)
        self._open(new)
        await self.bus.publish("notify.draft", new)
        log.info("draft %s edited -> %s: %.80s", new.draft_id, new.channel_name, new.text)
        return new

    async def _draft_name(self, channel_id: str) -> str:
        """Draft.channel_name: the channel without its '#', or the person (a DM)."""
        return (await self.channel_label(channel_id)).lstrip("#")

    def _open(self, draft: Draft) -> None:
        self.open_draft = draft
        self._expiry = asyncio.create_task(self._expire(draft.draft_id), name=f"draft-expiry-{draft.draft_id}")

    async def send_draft(self, draft_id: str | None = None) -> tuple[Draft, str]:
        """Post the open draft (the Send button / "yes, send it"). Returns (draft, ts) and emits notify.sent."""
        draft = self._take(draft_id)
        if self._user_client is None and not self._warned_as_bot:
            self._warned_as_bot = True
            log.warning("posting as Bit (no SLACK_USER_TOKEN)")
        res = await (self._user_client or self._client).chat_postMessage(channel=draft.channel, text=draft.text,
                                                                           thread_ts=draft.thread_ts)
        ts = res["ts"]
        if self.focused is not None and draft.reply_to is not None and draft.reply_to.ts == self.focused.ts:
            self.focused = None  # answered
        await self.bus.publish("notify.sent", NotifySent(draft_id=draft.draft_id, channel=draft.channel,
                                                         channel_name=draft.channel_name, text=draft.text, ts=ts))
        log.info("sent draft %s as %s, ts=%s", draft.draft_id, "you" if self.as_user else "Bit", ts)
        return draft, ts

    async def cancel_draft(self, draft_id: str | None = None) -> Draft:
        draft = self._take(draft_id)
        await self.bus.publish("notify.draft_cancelled", NotifyDraftCancelled(draft_id=draft.draft_id, reason="cancelled"))
        return draft

    def _take(self, draft_id: str | None) -> Draft:
        """Pop the open draft if `draft_id` matches it (None = whichever is open); stops the expiry timer."""
        draft = self.open_draft
        if draft is None or (draft_id and draft_id != draft.draft_id):
            raise DraftError(f"No open draft{f' {draft_id}' if draft_id else ''} (sent, cancelled or expired?)")
        self.open_draft = None
        if self._expiry is not None:
            self._expiry.cancel()
            self._expiry = None
        return draft

    async def _clear_draft(self, reason: str) -> None:
        if self.open_draft is None:
            return
        draft = self._take(None)
        await self.bus.publish("notify.draft_cancelled", NotifyDraftCancelled(draft_id=draft.draft_id, reason=reason))

    async def _expire(self, draft_id: str) -> None:
        await asyncio.sleep(self.draft_expiry_s)
        if self.open_draft is not None and self.open_draft.draft_id == draft_id:
            self._expiry = None  # we are that task; don't cancel ourselves in _take
            draft = self._take(draft_id)
            await self.bus.publish("notify.draft_cancelled", NotifyDraftCancelled(draft_id=draft.draft_id, reason="expired"))
            log.info("draft %s expired unsent", draft_id)

    def _find(self, ts: str | None) -> SlackMessage | None:
        return self.store.find(ts) if ts else None

    def find(self, channel: str, ts: str) -> SlackMessage | None:
        return self.store.get(channel, ts)

    # ------------------------------------------------------------------ focus ("Reply with Bit")
    async def focus(self, channel: str, ts: str, pet_id: str = "bit") -> SlackMessage | None:
        """Bit reads the message and offers to answer; messages_send without a target then replies in its thread.
        Spoken on the live voice session, else pet.say. None (and nothing said) if the message isn't in the store."""
        msg = self.find(channel, ts)
        if msg is None:
            log.warning("messages.focus: no message %s in %s", ts, channel)
            return None
        self.focused = msg
        line = focus_line(msg)
        if self.voice_note is not None:
            try:
                await self.voice_note(pet_id, f"Slack message in focus, from {msg.user_name} in {_where(msg)}: "
                                              f"\"{_trim(msg.text)}\". To answer it call messages_send; the reply goes "
                                              "to that thread unless you name another target.")
            except Exception:  # noqa: BLE001
                log.exception("focus note failed")
        try:
            spoke = await self.voice_speak(pet_id, line)
        except Exception:  # noqa: BLE001
            log.exception("spoken focus failed")
            spoke = False
        if not spoke:
            await self.bus.publish("pet.say", {"pet_id": pet_id, "text": line})
        log.info("focus for %s: #%s %s (spoken=%s)", pet_id, msg.channel_name, msg.ts, spoke)
        return msg

    async def focus_from_ws(self, session: Any, payload: dict[str, Any]) -> None:
        """ws `messages.focus {pet_id, channel, ts}` (agent.ws.client_handlers); the session supplies the pet id."""
        f = MessagesFocus.model_validate(payload)
        await self.focus(f.channel, f.ts, f.pet_id or getattr(session, "pet_id", "bit"))

    # ------------------------------------------------------------------ digest
    async def digest(self, pet_id: str = "bit") -> NotifyDigest:
        """Sit-down summary: everything unread in every conversation -> notify.digest, and Bit says the line (also when
        nothing is new: "You have no new messages today.", so the arrival greeting always has a sentence). Marks every
        conversation seen."""
        d = NotifyDigest.build(self.store.unread())
        if self.store.mark_seen():
            self.channels_changed()
        await self.bus.publish("notify.digest", d)
        await self.bus.publish("pet.say", {"pet_id": pet_id, "text": d.line})
        try:
            spoke = await self.voice_speak(pet_id, d.line)
        except Exception:  # noqa: BLE001 - the greeting is best effort; the caption already went out
            log.exception("spoken digest failed")
            spoke = False
        log.info("digest for %s: %s (spoken=%s)", pet_id, d.line, spoke)
        return d
