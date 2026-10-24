"""Read-only iMessage ingestion for macOS hosts signed into Messages."""
from __future__ import annotations

import asyncio
import contextlib
import logging
import sqlite3
from contextlib import closing
from collections.abc import Awaitable, Callable
from typing import Any
import time
import uuid

from events import ChannelSummary, Draft, EventBus, NotifyDraftCancelled, NotifySent, SlackMessage
from messaging.store import MessageStore
from messaging.slack_client import DraftError

from .db import MessagesDatabaseError, chat_rowid, connect, outgoing_match, rows, text_body, timestamp
from .sender import IMessageSendError, IMessageSender
from .settings import IMessageSettings

log = logging.getLogger("forge.messaging.imessage")
VoiceNote = Callable[[str, str], Awaitable[bool]]


class IMessageService:
    def __init__(self, settings: IMessageSettings, store: MessageStore, bus: EventBus,
                 voice_note: VoiceNote | None = None) -> None:
        self.settings, self.store, self.bus, self.voice_note = settings, store, bus, voice_note
        self.enabled = settings.enabled
        self.available = False
        self.status = "disabled" if not self.enabled else "starting"
        self.error: str | None = None
        self.last_rowid = 0
        self._task: asyncio.Task[None] | None = None
        self.sender = IMessageSender(settings.cli)
        self.chat_names: dict[str, str] = {}
        self.open_draft: Draft | None = None
        self._expiry: asyncio.Task[None] | None = None
        self.draft_expiry_s = 120.0
        self.focused: SlackMessage | None = None
        self.channels_changed: Callable[[], None] | None = None
        if getattr(bus, "make_sticky", None):
            bus.make_sticky("notify.channels")

    async def start(self) -> None:
        if not self.enabled:
            self.status = "disabled (IMESSAGE_ENABLED=false)"
            return
        try:
            await self.sender.start()
            if not self.sender.status.available:
                log.warning("iMessage sending unavailable: %s", self.sender.status.error)
            for chat in await self.sender.chats():
                guid = str(chat.get("guid") or "")
                name = str(chat.get("contact_name") or chat.get("name") or "").strip()
                if guid and name:
                    self.chat_names[guid] = name
            with closing(connect(self.settings.db_path)) as db:
                cutoff = time.time() - self.settings.recent_days * 86_400
                initial = list(reversed(rows(db, limit=1000, since_unix=cutoff)))
                for row in initial:
                    await self._ingest(row, notify=False)
                    self.last_rowid = max(self.last_rowid, int(row["rowid"]))
                row = db.execute("SELECT COALESCE(MAX(ROWID), 0) FROM message").fetchone()
                self.last_rowid = max(self.last_rowid, int(row[0] or 0))
            # First time the hub sees a chat, its backlog counts as read: otherwise 30 days of texts land as unread.
            # Chats with a seen mark keep it, so texts that arrived while the hub was off still show as new.
            for channel in self.store.channels():
                if channel.startswith("im:") and channel not in self.store.seen:
                    self.store.mark_seen(channel)
            self.available, self.status, self.error = True, "available", None
            if self.channels_changed is not None:
                self.channels_changed()
            self._task = asyncio.create_task(self._poll(), name="imessage-poll")
            log.info("iMessage reader connected to local Messages database")
        except (MessagesDatabaseError, sqlite3.Error, OSError) as exc:
            self.available = False
            self.error = str(exc)
            if "denied" in str(exc).lower() or "unable to open" in str(exc).lower():
                self.status = "Full Disk Access required: open System Settings > Privacy & Security > " \
                              "Full Disk Access, enable the app running the hub, then restart it"
                self.error = f"{exc}. {self.status}."
            else:
                self.status = "unavailable"
            log.warning("iMessage reader unavailable: %s", exc)

    async def stop(self) -> None:
        if self._expiry:
            self._expiry.cancel()
            with contextlib.suppress(asyncio.CancelledError):
                await self._expiry
            self._expiry = None
        if self._task:
            self._task.cancel()
            with contextlib.suppress(asyncio.CancelledError):
                await self._task
            self._task = None

    def health(self) -> dict[str, Any]:
        return {"enabled": self.enabled, "available": self.available, "status": self.status,
                "error": self.error, "digest": self.settings.digest,
                "setup_instructions": "Enable Full Disk Access for the app running the hub in System Settings > "
                                      "Privacy & Security > Full Disk Access, then restart the hub.",
                "full_disk_access_settings": "x-apple.systempreferences:com.apple.preference.security?Privacy_AllFiles",
                "send_available": self.sender.status.available,
                "send_error": self.sender.status.error, "sender_version": self.sender.status.version}

    def channel_summaries(self) -> list[ChannelSummary]:
        summaries = []
        for channel in self.store.channels():
            if not channel.startswith("im:"):
                continue
            last = self.store.last(channel)
            if last is None:
                continue
            unread = self.store.unread(channel)
            summaries.append(ChannelSummary(id=channel, name=last.channel_name, kind="dm" if last.is_dm else "group-dm",
                                            unread=len(unread), mentions=sum(m.mentions_me for m in unread),
                                            last_ts=last.ts, last_text=last.text, source="imessage"))
        summaries.sort(key=lambda ch: _ts(ch.last_ts), reverse=True)
        return summaries

    async def history(self, channel: str, limit: int = 50, before: str | None = None
                      ) -> tuple[str, str, str, list[SlackMessage], bool]:
        """The newest `limit` messages of one chat. No paging into older texts: `before` returns nothing, has_more is
        always False (so the panel shows no "Load earlier")."""
        guid = await self.resolve_channel(channel)
        with closing(connect(self.settings.db_path)) as db:
            chat = db.execute("SELECT display_name, chat_identifier, style FROM chat WHERE guid = ? LIMIT 1", (guid,)).fetchone()
            fetched = [] if before else rows(db, limit=limit, chat_guid=guid)
        if chat is None:
            raise LookupError(f"No iMessage conversation {channel!r}")
        messages = [self._message(row) for row in reversed(fetched)]
        for message in messages:
            self.store.add(message)
        return f"im:{guid}", self._chat_label(guid, chat), "dm" if int(chat[2] or 0) == 45 else "group-dm", messages, False

    def _chat_label(self, guid: str, chat: Any) -> str:
        return self.chat_names.get(guid) or str(chat[0] or chat[1] or f"im:{guid}")

    def chat_name(self, guid: str) -> str:
        """A chat's display name without reading its messages (drafts need only the name)."""
        with closing(connect(self.settings.db_path)) as db:
            chat = db.execute("SELECT display_name, chat_identifier FROM chat WHERE guid = ? LIMIT 1", (guid,)).fetchone()
        if chat is None:
            raise LookupError(f"No iMessage conversation im:{guid}")
        return self._chat_label(guid, chat)

    async def resolve_channel(self, value: str | None) -> str:
        if not value:
            if self.focused is not None:
                return self.focused.channel.removeprefix("im:")
            raise LookupError("Choose an iMessage conversation first")
        wanted = value.strip()
        if wanted.startswith("im:"):
            return wanted[3:]
        if not self.available:
            raise LookupError("iMessage is unavailable on this Mac")
        with closing(connect(self.settings.db_path)) as db:
            chats = db.execute("SELECT guid, chat_identifier, display_name FROM chat ORDER BY ROWID DESC LIMIT 1000").fetchall()
        exact = [r for r in chats if wanted.lower() in {str(r[1] or "").lower(), str(r[2] or "").lower(),
                                                        self.chat_names.get(str(r[0]), "").lower()}]
        if not exact:
            exact = [r for r in chats if wanted.lower() in " ".join((str(r[2] or ""),
                                                                       self.chat_names.get(str(r[0]), ""))).lower()]
        if len(exact) > 1:
            raise LookupError(f"More than one iMessage chat matches {wanted!r}; use its im:<chat-guid> id")
        if not exact:
            raise LookupError(f"No iMessage conversation matches {wanted!r}; use a chat id from messages_list_channels")
        return str(exact[0][0])

    async def create_draft(self, text: str, channel: str | None = None, reply_to_ts: str | None = None) -> Draft:
        if not self.enabled or not self.available:
            raise RuntimeError("iMessage is unavailable on this Mac")
        if not self.sender.status.available:
            raise RuntimeError(f"iMessage sending is unavailable: {self.sender.status.error}")
        text = text.strip()
        if not text:
            raise ValueError("text is empty")
        reply_to = self.store.find(reply_to_ts) if reply_to_ts else self.focused
        if reply_to is not None and channel is None:
            channel = reply_to.channel
        guid = await self.resolve_channel(channel)
        name = self.chat_name(guid)
        await self._clear_draft("superseded")
        draft = Draft(draft_id=f"im-{uuid.uuid4().hex[:8]}", channel=f"im:{guid}", channel_name=name,
                      text=text, thread_ts=None, reply_to=reply_to, source="imessage")
        self._open(draft)
        await self.bus.publish("notify.draft", draft)
        return draft

    async def update_draft(self, draft_id: str, changes: dict[str, Any]) -> Draft:
        draft = self.open_draft
        if draft is None or draft.draft_id != draft_id:
            raise DraftError(f"No open iMessage draft {draft_id} (sent, cancelled or expired?)")
        update: dict[str, Any] = {"edited": True}
        if "text" in changes:
            text = str(changes["text"] or "").strip()
            if not text:
                raise ValueError("text is empty")
            update["text"] = text
        if changes.get("channel"):
            guid = await self.resolve_channel(str(changes["channel"]))
            name = self.chat_name(guid)
            update.update(channel=f"im:{guid}", channel_name=name, thread_ts=None)
        if self.open_draft is not draft:
            raise DraftError(f"Draft {draft_id} closed while updating")
        self._take(draft_id)
        new = draft.model_copy(update=update)
        self._open(new)
        await self.bus.publish("notify.draft", new)
        return new

    async def send_draft(self, draft_id: str | None = None) -> tuple[Draft, str]:
        draft = self.open_draft
        if draft is None or (draft_id and draft.draft_id != draft_id):
            raise DraftError(f"No open iMessage draft{f' {draft_id}' if draft_id else ''} (sent, cancelled or expired?)")
        guid = draft.channel.removeprefix("im:")
        with closing(connect(self.settings.db_path)) as db:
            chat = chat_rowid(db, guid)
            before_rowid = int(db.execute("SELECT COALESCE(MAX(ROWID), 0) FROM message").fetchone()[0] or 0)
        if chat is None:
            raise DraftError(f"iMessage conversation {draft.channel} no longer exists")
        self._take(draft.draft_id)
        try:
            await self.sender.send(chat[0], draft.text)
        except Exception:
            await self.bus.publish("notify.draft_cancelled",
                                   NotifyDraftCancelled(draft_id=draft.draft_id, reason="send_failed"))
            raise
        confirmed = None
        deadline = time.monotonic() + 8.0
        while time.monotonic() < deadline:
            with closing(connect(self.settings.db_path)) as db:
                confirmed = outgoing_match(db, guid, draft.text, before_rowid)
            if confirmed:
                break
            await asyncio.sleep(0.25)
        if not confirmed:
            await self.bus.publish("notify.draft_cancelled",
                                   NotifyDraftCancelled(draft_id=draft.draft_id, reason="send_unconfirmed"))
            raise IMessageSendError("No matching outgoing Messages row appeared; send outcome is uncertain. Check Messages before retrying.")
        sent = self._message(confirmed)
        self.store.add(sent)
        ts = sent.ts
        if self.focused is not None and draft.reply_to is not None and draft.reply_to.ts == self.focused.ts:
            self.focused = None
        await self.bus.publish("notify.sent", NotifySent(draft_id=draft.draft_id, channel=draft.channel,
                                                         channel_name=draft.channel_name, text=draft.text,
                                                         ts=ts, source="imessage"))
        return draft, ts

    async def cancel_draft(self, draft_id: str | None = None) -> Draft:
        draft = self._take(draft_id)
        await self.bus.publish("notify.draft_cancelled", NotifyDraftCancelled(draft_id=draft.draft_id, reason="cancelled"))
        return draft

    def _open(self, draft: Draft) -> None:
        self.open_draft = draft
        self._expiry = asyncio.create_task(self._expire(draft.draft_id), name=f"imessage-draft-expiry-{draft.draft_id}")

    def _take(self, draft_id: str | None) -> Draft:
        draft = self.open_draft
        if draft is None or (draft_id and draft.draft_id != draft_id):
            raise DraftError(f"No open iMessage draft{f' {draft_id}' if draft_id else ''} (sent, cancelled or expired?)")
        self.open_draft = None
        if self._expiry:
            self._expiry.cancel()
            self._expiry = None
        return draft

    async def _clear_draft(self, reason: str) -> None:
        if self.open_draft is not None:
            draft = self._take(None)
            await self.bus.publish("notify.draft_cancelled", NotifyDraftCancelled(draft_id=draft.draft_id, reason=reason))

    async def _expire(self, draft_id: str) -> None:
        await asyncio.sleep(self.draft_expiry_s)
        if self.open_draft is not None and self.open_draft.draft_id == draft_id:
            self._expiry = None
            draft = self._take(draft_id)
            await self.bus.publish("notify.draft_cancelled", NotifyDraftCancelled(draft_id=draft.draft_id, reason="expired"))

    async def focus(self, channel: str, ts: str, pet_id: str = "bit") -> SlackMessage | None:
        message = self.store.get(channel, ts)
        if message is None:
            return None
        self.focused = message
        if self.voice_note:
            await self.voice_note(pet_id, f"iMessage from {message.user_name}: {message.text}. If the user wants to reply, "
                                          "use messages_send to draft a response, then wait for explicit confirmation.")
        return message

    async def _poll(self) -> None:
        while True:
            try:
                with closing(connect(self.settings.db_path)) as db:
                    batch = list(reversed(rows(db, after_rowid=self.last_rowid, limit=200)))
                for row in batch:
                    await self._ingest(row)
                    self.last_rowid = max(self.last_rowid, int(row["rowid"]))
            except asyncio.CancelledError:
                raise
            except (MessagesDatabaseError, sqlite3.Error, OSError) as exc:
                self.error = str(exc)
                log.warning("iMessage poll failed: %s", exc)
            await asyncio.sleep(self.settings.poll_ms / 1000)

    async def _ingest(self, row: dict[str, Any], *, notify: bool = True) -> None:
        if 2000 <= int(row.get("associated_message_type") or 0) <= 3999:
            return
        chat_guid = str(row.get("chat_guid") or "")
        if not chat_guid:
            return
        channel = f"im:{chat_guid}"
        message = self._message(row, channel=channel)
        if self.store.add(message) and notify:
            await self.bus.publish("notify.message", message)
            if self.channels_changed is not None:
                self.channels_changed()

    def _message(self, row: dict[str, Any], *, channel: str | None = None, chat_name: str | None = None) -> SlackMessage:
        chat_guid = str(row.get("chat_guid") or (channel or "").removeprefix("im:"))
        channel = channel or f"im:{chat_guid}"
        is_me = bool(row.get("is_from_me"))
        handle = str(row.get("handle") or ("me" if is_me else "Unknown"))
        display = str(row.get("display_name") or "").strip()
        identifier = str(row.get("chat_identifier") or "").strip()
        label = chat_name or self.chat_names.get(chat_guid) or display or identifier or handle
        body = text_body(row.get("text"), row.get("attributedBody")) or ("[attachment]" if row.get("attributedBody") else "[message]")
        return SlackMessage(channel=channel, channel_name=label, is_dm=int(row.get("style") or 0) == 45,
                            user=handle, user_name="You" if is_me else handle, text=body,
                            ts=timestamp(row.get("date")), mentions_me=False,
                            edited=bool(row.get("date_edited")), is_me=is_me,
                            user_initials="ME" if is_me else _initials(handle), source="imessage")


def _ts(value: str | None) -> float:
    try:
        return float(value or 0)
    except (TypeError, ValueError):
        return 0.0


def _initials(label: str) -> str:
    parts = [p for p in label.replace("@", " ").replace(".", " ").split() if p]
    if len(parts) > 1:
        return (parts[0][0] + parts[1][0]).upper()
    return (parts[0][:2].upper() if parts else "?")
