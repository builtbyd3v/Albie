"""Bit's Slack connection: Bolt AsyncApp over Socket Mode, running inside the hub's event loop.

Inbound `message` / `app_mention` events become SlackMessage -> MessageStore + notify.message on the EventBus.
`send` posts with chat.postMessage (in-thread when thread_ts is given) and emits notify.sent.
`digest` is the sit-down summary: notify.digest, pet.say and the spoken greeting through `voice_speak`.

Without tokens the service stays disabled (one warning) and the hub boots as usual. Connection trouble is
logged and retried; it never takes the hub down.
"""
from __future__ import annotations

import asyncio
import contextlib
import logging
from collections import deque
from collections.abc import Awaitable, Callable
from typing import Any

from events import EventBus, NotifyDigest, NotifySent, SlackMessage

from .settings import SlackSettings
from .store import MessageStore

log = logging.getLogger("forge.messaging")

VoiceSpeak = Callable[[str, str], Awaitable[bool]]
ACCEPTED_SUBTYPES: tuple[str | None, ...] = (None, "thread_broadcast", "file_share")
RECONNECT_S = (2, 5, 10, 30)


class SlackService:
    def __init__(self, settings: SlackSettings, store: MessageStore, bus: EventBus, voice_speak: VoiceSpeak) -> None:
        self.settings = settings
        self.store = store
        self.bus = bus
        self.voice_speak = voice_speak
        self.enabled = settings.configured
        self.connected = False
        self.bot_user_id: str | None = None
        self._client: Any = None            # slack_sdk.web.async_client.AsyncWebClient (or a test stub)
        self._handler: Any = None           # AsyncSocketModeHandler
        self._task: asyncio.Task[None] | None = None
        self._user_names: dict[str, str] = {}
        self._channel_names: dict[str, str] = {}
        self._channel_ids: dict[str, str] = {}   # "#name" -> id
        self._recent: deque[tuple[str, str]] = deque(maxlen=200)  # (channel, ts) already recorded

    # ------------------------------------------------------------------ lifecycle
    async def start(self) -> None:
        if not self.enabled:
            log.warning("Slack disabled: set SLACK_BOT_TOKEN and SLACK_APP_TOKEN in hub/.env (PROJECT_CONTEXT 8.7)")
            return
        from slack_bolt.adapter.socket_mode.aiohttp import AsyncSocketModeHandler
        from slack_bolt.async_app import AsyncApp

        app = AsyncApp(token=self.settings.bot_token, token_verification_enabled=False)
        self._client = app.client
        subtypes = list(ACCEPTED_SUBTYPES)

        @app.event({"type": "message", "subtype": subtypes})
        async def _message(event: dict[str, Any]) -> None:
            await self.on_event(event)

        @app.event("app_mention")
        async def _mention(event: dict[str, Any]) -> None:
            await self.on_event(event)

        self._handler = AsyncSocketModeHandler(app, self.settings.app_token)
        self._task = asyncio.create_task(self._connect(), name="slack-connect")

    async def _connect(self) -> None:
        attempt = 0
        while True:
            try:
                if self.bot_user_id is None:
                    auth = await self._client.auth_test()
                    self.bot_user_id = auth["user_id"]
                    log.info("Slack bot %s (%s) in team %s", auth.get("user"), self.bot_user_id, auth.get("team"))
                await self._handler.connect_async()
                self.connected = True
                log.info("Slack Socket Mode connected")
                return
            except asyncio.CancelledError:
                raise
            except Exception as e:  # noqa: BLE001 - bad token, no network: keep the hub up and retry
                delay = RECONNECT_S[min(attempt, len(RECONNECT_S) - 1)]
                attempt += 1
                log.error("Slack connect failed (%s); retrying in %ss", e, delay)
                await asyncio.sleep(delay)

    async def stop(self) -> None:
        if self._task is not None:
            self._task.cancel()
            with contextlib.suppress(asyncio.CancelledError):
                await self._task
            self._task = None
        if self._handler is not None and self.connected:
            with contextlib.suppress(Exception):
                await self._handler.close_async()
        self.connected = False

    def health(self) -> dict[str, Any]:
        return {"enabled": self.enabled, "connected": self.connected, "bot_user_id": self.bot_user_id}

    # ------------------------------------------------------------------ inbound
    def accepts(self, event: dict[str, Any]) -> bool:
        if event.get("subtype") not in ACCEPTED_SUBTYPES:
            return False
        if event.get("bot_id") or not event.get("user") or not event.get("text"):
            return False
        if self.bot_user_id and event["user"] == self.bot_user_id:
            return False
        key = (event.get("channel", ""), event.get("ts", ""))
        if key in self._recent:
            return False  # app_mention and message arrive for the same post
        return True

    async def on_event(self, event: dict[str, Any]) -> SlackMessage | None:
        """One Slack event dict -> stored SlackMessage + notify.message, or None when ignored."""
        if not self.accepts(event):
            return None
        self._recent.append((event.get("channel", ""), event.get("ts", "")))
        channel = event["channel"]
        msg = SlackMessage(
            channel=channel,
            channel_name=await self.channel_name(channel),
            user=event["user"],
            user_name=await self.user_name(event["user"]),
            text=event["text"],
            ts=event["ts"],
            thread_ts=event.get("thread_ts"),
            mentions_me=bool(self.settings.user_id) and f"<@{self.settings.user_id}>" in event["text"],
        )
        self.store.add(msg)
        await self.bus.publish("notify.message", msg)
        log.info("slack #%s %s: %.80s", msg.channel_name, msg.user_name, msg.text)
        return msg

    async def user_name(self, user_id: str) -> str:
        if user_id not in self._user_names:
            name = user_id
            try:
                info = (await self._client.users_info(user=user_id))["user"]
                profile = info.get("profile") or {}
                name = profile.get("display_name") or info.get("real_name") or info.get("name") or user_id
            except Exception as e:  # noqa: BLE001
                log.warning("users.info %s failed: %s", user_id, e)
            self._user_names[user_id] = name
        return self._user_names[user_id]

    async def channel_name(self, channel_id: str) -> str:
        if channel_id not in self._channel_names:
            name = channel_id
            try:
                info = (await self._client.conversations_info(channel=channel_id))["channel"]
                name = "dm" if info.get("is_im") else info.get("name") or channel_id
            except Exception as e:  # noqa: BLE001
                log.warning("conversations.info %s failed: %s", channel_id, e)
            self._channel_names[channel_id] = name
        return self._channel_names[channel_id]

    # ------------------------------------------------------------------ outbound
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
                page = await self._client.conversations_list(types="public_channel,private_channel", limit=200,
                                                             cursor=cursor, exclude_archived=True)
                for ch in page.get("channels", []):
                    self._channel_ids[ch["name"].lower()] = ch["id"]
                    self._channel_names.setdefault(ch["id"], ch["name"])
                cursor = (page.get("response_metadata") or {}).get("next_cursor") or None
                if not cursor:
                    break
        if name not in self._channel_ids:
            raise LookupError(f"No Slack channel named #{name} (is Bit invited to it?)")
        return self._channel_ids[name]

    async def send(self, channel: str | None, text: str, thread_ts: str | None = None) -> str:
        if not self.enabled:
            raise RuntimeError("Slack is not configured on this hub")
        channel_id = await self.resolve_channel(channel)
        res = await self._client.chat_postMessage(channel=channel_id, text=text, thread_ts=thread_ts)
        ts = res["ts"]
        await self.bus.publish("notify.sent", NotifySent(channel=channel_id, channel_name=await self.channel_name(channel_id),
                                                         text=text, ts=ts))
        return ts

    # ------------------------------------------------------------------ digest
    async def digest(self, pet_id: str = "bit") -> NotifyDigest:
        """Sit-down summary: everything unread -> notify.digest; if anything is new, Bit says the line."""
        d = NotifyDigest.build(self.store.unread())
        self.store.mark_seen()
        await self.bus.publish("notify.digest", d)
        if d.count:
            await self.bus.publish("pet.say", {"pet_id": pet_id, "text": d.line})
            try:
                spoke = await self.voice_speak(pet_id, d.line)
            except Exception:  # noqa: BLE001 - the greeting is best effort; the caption already went out
                log.exception("spoken digest failed")
                spoke = False
            log.info("digest for %s: %s (spoken=%s)", pet_id, d.line, spoke)
        return d
