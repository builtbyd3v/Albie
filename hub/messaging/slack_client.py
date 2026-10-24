"""Bit's Slack connection: Bolt AsyncApp over Socket Mode, running inside the hub's event loop.

Two identities (PROJECT_CONTEXT 8.7):
- the app's bot token keeps the socket and is the fallback identity;
- the USER's token (SLACK_USER_TOKEN) is how Bit reads the user's inbox (names of DMs it can't see as a bot)
  and posts replies under the user's own name. Without it Bit falls back to the bot: it only sees channels it
  was invited to and posts as "Bit", with one warning.

Inbound `message` / `app_mention` events (bot and user subscriptions, deduped) -> SlackMessage -> MessageStore +
notify.message on the EventBus. Sending is two steps: `create_draft` emits notify.draft and nothing leaves;
`send_draft` (the card's Send button or the messages_confirm tool) posts it and emits notify.sent.
`digest` is the sit-down summary: notify.digest, pet.say and the spoken greeting through `voice_speak`.
`focus` is "Reply with Bit" on a message card (ws `messages.focus` / POST /messages/focus): Bit reads the message
out loud and asks whether to answer; the next `messages_send` without a target replies in that thread.

Once connected, `backfill` (a background task) reads what arrived while the hub was down: every conversation the
user is in, `conversations.history` since `last_seen_ts` (or the last two hours), through the same filter as live
events but without notify.message, so the sit-down digest counts them and Unity sees no flood.

Without tokens the service stays disabled (one warning) and the hub boots as usual. Connection trouble is
logged and retried; it never takes the hub down.
"""
from __future__ import annotations

import asyncio
import contextlib
import logging
import time
import uuid
from collections import deque
from collections.abc import Awaitable, Callable
from typing import Any

from agent.events import MessagesFocus
from events import Draft, EventBus, NotifyDigest, NotifyDraftCancelled, NotifySent, SlackMessage

from .settings import SlackSettings
from .store import MessageStore

log = logging.getLogger("forge.messaging")

VoiceSpeak = Callable[[str, str], Awaitable[bool]]
VoiceNote = Callable[[str, str], Awaitable[bool]]  # (pet_id, one line of context the voice model reads, no reply)
FOCUS_TEXT_CHARS = 200
ACCEPTED_SUBTYPES: tuple[str | None, ...] = (None, "thread_broadcast", "file_share")
RECONNECT_S = (2, 5, 10, 30)
DRAFT_EXPIRY_S = 120.0
BACKFILL_TYPES = "public_channel,private_channel,im,mpim"
BACKFILL_PAGES = 3           # users.conversations pages of 200
BACKFILL_PER_CHANNEL = 20    # newest messages per conversation
BACKFILL_WINDOW_S = 2 * 3600  # with no last_seen_ts: this far back


class DraftError(LookupError):
    """No open draft with that id (never existed, already sent, cancelled or expired)."""


def _ts(value: str) -> float:
    try:
        return float(value)
    except (TypeError, ValueError):
        return 0.0


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
        self.backfilled: int | None = None          # messages recovered at startup (None = not run yet)
        self._waited = False                        # backfill: one rate-limit wait per run...
        self._gave_up = False                       # ...the second rate limit stops it
        self._user_names: dict[str, str] = {}
        self._channels: dict[str, tuple[str, bool]] = {}   # id -> (name, is_dm)
        self._channel_ids: dict[str, str] = {}             # "name" -> id
        self._recent: deque[tuple[str, str]] = deque(maxlen=200)  # (channel, ts) already recorded
        self._warned_as_bot = False
        # drafts: exactly one open at a time
        self.open_draft: Draft | None = None
        self.draft_expiry_s = DRAFT_EXPIRY_S
        self._expiry: asyncio.Task[None] | None = None

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
                return
            except asyncio.CancelledError:
                raise
            except Exception as e:  # noqa: BLE001 - bad token, no network: keep the hub up and retry
                delay = RECONNECT_S[min(attempt, len(RECONNECT_S) - 1)]
                attempt += 1
                log.error("Slack connect failed (%s); retrying in %ss", e, delay)
                await asyncio.sleep(delay)

    async def stop(self) -> None:
        for task in (self._task, self._backfill, self._expiry):
            if task is not None:
                task.cancel()
                with contextlib.suppress(asyncio.CancelledError):
                    await task
        self._task = self._backfill = self._expiry = None
        if self._handler is not None and self.connected:
            with contextlib.suppress(Exception):
                await self._handler.close_async()
        self.connected = False

    def health(self) -> dict[str, Any]:
        return {"enabled": self.enabled, "connected": self.connected, "as_user": self.as_user,
                "bot_user_id": self.bot_user_id, "user_id": self.user_id or None,
                "open_draft": self.open_draft.draft_id if self.open_draft else None}

    # ------------------------------------------------------------------ inbound
    @property
    def _reader(self) -> Any:
        """Client for lookups: the user's view when we have it (it can see their DMs), else the bot's."""
        return self._user_client if self._user_client is not None else self._client

    def accepts(self, event: dict[str, Any]) -> bool:
        if event.get("subtype") not in ACCEPTED_SUBTYPES:
            return False
        if event.get("bot_id") or not event.get("user") or not event.get("text"):
            return False
        if self.bot_user_id and event["user"] == self.bot_user_id:
            return False
        if self.user_id and event["user"] == self.user_id:
            return False  # the user's own messages are not their inbox
        key = (event.get("channel", ""), event.get("ts", ""))
        if key in self._recent:
            return False  # bot + user subscriptions, or app_mention + message, deliver the same post twice
        return True

    async def on_event(self, event: dict[str, Any], publish: bool = True) -> SlackMessage | None:
        """One Slack event dict -> stored SlackMessage + notify.message, or None when ignored.
        publish=False (the startup backfill) stores it for the digest without telling Unity."""
        if not self.accepts(event):
            return None
        self._recent.append((event.get("channel", ""), event.get("ts", "")))
        channel = event["channel"]
        name, is_dm = await self.channel_info(channel)
        text = event["text"]
        msg = SlackMessage(
            channel=channel,
            channel_name=name,
            is_dm=is_dm,
            user=event["user"],
            user_name=await self.user_display_name(event["user"]),
            text=text,
            ts=event["ts"],
            thread_ts=event.get("thread_ts"),
            mentions_me=is_dm or (bool(self.user_id) and f"<@{self.user_id}>" in text),
        )
        self.store.add(msg)
        if publish:
            await self.bus.publish("notify.message", msg)
            log.info("slack #%s %s: %.80s", msg.channel_name, msg.user_name, msg.text)
        return msg

    # ------------------------------------------------------------------ startup backfill
    async def backfill(self, now: float | None = None) -> int:
        """Pull what arrived while the hub was off into the store (no notify.message). Returns how many."""
        oldest = self.store.last_seen_ts if _ts(self.store.last_seen_ts) > 0 else f"{(now or time.time()) - BACKFILL_WINDOW_S:.6f}"
        self._waited = self._gave_up = False
        channels: list[dict[str, Any]] = []
        cursor: str | None = None
        for _ in range(BACKFILL_PAGES):
            page = await self._api("users.conversations", self._reader.users_conversations, types=BACKFILL_TYPES,
                                   exclude_archived=True, limit=200, cursor=cursor)
            if page is None:
                break
            channels.extend(page.get("channels", []))
            cursor = (page.get("response_metadata") or {}).get("next_cursor") or None
            if not cursor:
                break
        events: list[dict[str, Any]] = []
        skipped = 0
        for ch in channels:
            cid = ch.get("id")
            if not cid:
                continue
            if ch.get("is_im"):
                self._channels.setdefault(cid, ("dm", True))
            elif ch.get("is_mpim"):
                self._channels.setdefault(cid, ("group-dm", True))
            elif ch.get("name"):
                self._channels.setdefault(cid, (ch["name"], False))
                self._channel_ids.setdefault(ch["name"].lower(), cid)
            history = await self._api(f"conversations.history {cid}", self._reader.conversations_history, channel=cid,
                                      oldest=oldest, limit=BACKFILL_PER_CHANNEL)
            if history is None:
                skipped += 1
                if self._gave_up:
                    break
                continue
            events.extend({**m, "channel": cid} for m in history.get("messages", []) if isinstance(m, dict))
        count = 0
        for event in sorted(events, key=lambda e: _ts(e.get("ts", ""))):  # oldest first: the ring keeps the newest
            if await self.on_event(event, publish=False) is not None:
                count += 1
        self.backfilled = count
        log.info("Slack backfill: %d conversation(s), %d new message(s) since %s%s", len(channels), count, oldest,
                 f", {skipped} skipped" if skipped else "")
        return count

    async def _api(self, what: str, call: Callable[..., Awaitable[Any]], **kwargs: Any) -> Any | None:
        """One Slack Web API call for the backfill: wait out the first rate limit (once per run), stop on the
        second, log and skip anything else (missing scope, not in channel, network)."""
        while True:
            try:
                return await call(**kwargs)
            except asyncio.CancelledError:
                raise
            except Exception as e:  # noqa: BLE001 - slack_sdk.errors.SlackApiError or a network error
                retry_after = _retry_after(e)
                if retry_after is None:
                    log.warning("Slack %s failed (%s); skipping", what, _error_of(e))
                    return None
                if self._waited:
                    self._gave_up = True
                    log.warning("Slack %s rate limited again; backfill stops here", what)
                    return None
                self._waited = True
                log.warning("Slack %s rate limited; waiting %.0fs once", what, retry_after)
                await asyncio.sleep(retry_after)

    async def user_display_name(self, user_id: str) -> str:
        if user_id not in self._user_names:
            name = user_id
            try:
                info = (await self._reader.users_info(user=user_id))["user"]
                profile = info.get("profile") or {}
                name = profile.get("display_name") or info.get("real_name") or info.get("name") or user_id
            except Exception as e:  # noqa: BLE001
                log.warning("users.info %s failed: %s", user_id, e)
            self._user_names[user_id] = name
        return self._user_names[user_id]

    async def channel_info(self, channel_id: str) -> tuple[str, bool]:
        """(channel_name, is_dm). DMs are named "dm", group DMs "group-dm"."""
        if channel_id not in self._channels:
            name, is_dm = channel_id, False
            try:
                info = (await self._reader.conversations_info(channel=channel_id))["channel"]
                if info.get("is_im"):
                    name, is_dm = "dm", True
                elif info.get("is_mpim"):
                    name, is_dm = "group-dm", True
                else:
                    name = info.get("name") or channel_id
            except Exception as e:  # noqa: BLE001
                log.warning("conversations.info %s failed: %s", channel_id, e)
            self._channels[channel_id] = (name, is_dm)
        return self._channels[channel_id]

    async def channel_name(self, channel_id: str) -> str:
        return (await self.channel_info(channel_id))[0]

    # ------------------------------------------------------------------ channels
    async def resolve_channel(self, channel: str | None) -> str:
        """Channel id, or '#name' / 'name' looked up once via conversations.list; empty -> SLACK_DEFAULT_CHANNEL."""
        value = (channel or self.settings.default_channel or "").strip()
        if not value:
            raise LookupError("No channel given and SLACK_DEFAULT_CHANNEL is not set")
        if value[0] in "CGD" and value[1:].isalnum() and value.isupper():
            return value
        name = value.lstrip("#").lower()
        if name not in self._channel_ids:
            cursor = None
            while True:
                page = await self._reader.conversations_list(types="public_channel,private_channel", limit=200,
                                                             cursor=cursor, exclude_archived=True)
                for ch in page.get("channels", []):
                    self._channel_ids[ch["name"].lower()] = ch["id"]
                    self._channels.setdefault(ch["id"], (ch["name"], False))
                cursor = (page.get("response_metadata") or {}).get("next_cursor") or None
                if not cursor:
                    break
        if name not in self._channel_ids:
            who = "you are" if self.as_user else "Bit is"
            raise LookupError(f"No Slack channel named #{name} (is it one {who} in?)")
        return self._channel_ids[name]

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
        draft = Draft(draft_id=uuid.uuid4().hex[:8], channel=channel_id, channel_name=await self.channel_name(channel_id),
                      text=text, thread_ts=thread_ts, reply_to=reply_to)
        self.open_draft = draft
        self._expiry = asyncio.create_task(self._expire(draft.draft_id), name=f"draft-expiry-{draft.draft_id}")
        await self.bus.publish("notify.draft", draft)
        log.info("draft %s -> #%s: %.80s", draft.draft_id, draft.channel_name, draft.text)
        return draft

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
        if not ts:
            return None
        return next((m for m in self.store.latest(len(self.store)) if m.ts == ts), None)

    def find(self, channel: str, ts: str) -> SlackMessage | None:
        return next((m for m in self.store.latest(len(self.store)) if m.ts == ts and m.channel == channel), None)

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
        """Sit-down summary: everything unread -> notify.digest, and Bit says the line (also when nothing is new:
        "You have no new messages today.", so the arrival greeting always has a sentence)."""
        d = NotifyDigest.build(self.store.unread())
        self.store.mark_seen()
        await self.bus.publish("notify.digest", d)
        await self.bus.publish("pet.say", {"pet_id": pet_id, "text": d.line})
        try:
            spoke = await self.voice_speak(pet_id, d.line)
        except Exception:  # noqa: BLE001 - the greeting is best effort; the caption already went out
            log.exception("spoken digest failed")
            spoke = False
        log.info("digest for %s: %s (spoken=%s)", pet_id, d.line, spoke)
        return d
