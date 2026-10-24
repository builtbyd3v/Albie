"""iMessage reader against a tiny chat.db built in the test (the real check is a live text from a second phone)."""
from __future__ import annotations

import asyncio
import sqlite3
import time
from pathlib import Path
from typing import Any

from fastapi import FastAPI
from fastapi.testclient import TestClient

from events import SlackMessage
from messaging import MessageStore, SlackService, SlackSettings
from messaging import tools as mtools
from messaging.imessage import IMessageService, IMessageSettings
from messaging.imessage.db import APPLE_EPOCH
from messaging.imessage.sender import SenderStatus
from messaging.router import router

DM, GROUP = "iMessage;-;+15550001", "iMessage;+;chat42"


class BusRecorder:
    def __init__(self) -> None:
        self.events: list[tuple[str, Any]] = []

    async def publish(self, type_: str, payload: Any) -> None:
        self.events.append((type_, payload))

    def types(self) -> list[str]:
        return [t for t, _ in self.events]


def make_db(path: Path) -> sqlite3.Connection:
    db = sqlite3.connect(path)
    db.executescript("""
        CREATE TABLE message (ROWID INTEGER PRIMARY KEY, guid TEXT, text TEXT, attributedBody BLOB, handle_id INTEGER,
                              is_from_me INTEGER, date INTEGER, date_edited INTEGER, thread_originator_guid TEXT,
                              associated_message_type INTEGER);
        CREATE TABLE chat (ROWID INTEGER PRIMARY KEY, guid TEXT, chat_identifier TEXT, display_name TEXT, style INTEGER);
        CREATE TABLE chat_message_join (chat_id INTEGER, message_id INTEGER);
        CREATE TABLE handle (ROWID INTEGER PRIMARY KEY, id TEXT);
        INSERT INTO handle VALUES (1, '+15550001');
        INSERT INTO chat VALUES (1, 'iMessage;-;+15550001', '+15550001', '', 45);
        INSERT INTO chat VALUES (2, 'iMessage;+;chat42', 'chat42', 'Family', 43);
    """)
    db.commit()
    return db


def add_text(db: sqlite3.Connection, chat_rowid: int, text: str, ago_s: float = 60.0, me: bool = False) -> None:
    date = int((time.time() - ago_s - APPLE_EPOCH) * 1_000_000_000)
    cur = db.execute("INSERT INTO message (guid, text, handle_id, is_from_me, date) VALUES (?, ?, 1, ?, ?)",
                     (f"g{time.monotonic_ns()}", text, int(me), date))
    db.execute("INSERT INTO chat_message_join VALUES (?, ?)", (chat_rowid, cur.lastrowid))
    db.commit()


def make_service(tmp_path: Path, store: MessageStore | None = None) -> tuple[IMessageService, BusRecorder]:
    bus = BusRecorder()
    settings = IMessageSettings(enabled=True, db_path=tmp_path / "chat.db", poll_ms=20)
    svc = IMessageService(settings, store or MessageStore(tmp_path / "messages"), bus)

    async def sender_start() -> SenderStatus:
        svc.sender.status = SenderStatus(True, "0.6.0")
        return svc.sender.status

    async def no_chats() -> list[dict[str, object]]:
        return []

    svc.sender.start = sender_start  # type: ignore[method-assign]
    svc.sender.chats = no_chats  # type: ignore[method-assign]
    return svc, bus


async def wait_for(check, timeout: float = 2.0) -> None:
    deadline = time.monotonic() + timeout
    while not check():
        assert time.monotonic() < deadline, "timed out"
        await asyncio.sleep(0.02)


def test_first_run_backlog_is_read_but_new_texts_are_unread(tmp_path: Path) -> None:
    db = make_db(tmp_path / "chat.db")
    for i in range(30):
        add_text(db, 1, f"old {i}", ago_s=3600 + i)
    add_text(db, 2, "group old", ago_s=7200)

    async def run() -> None:
        svc, bus = make_service(tmp_path)
        await svc.start()
        try:
            assert svc.available
            assert svc.store.unread() == []                      # 30 days of texts don't land as unread
            assert len(svc.store.history(f"im:{DM}", 100)) == 30
            add_text(db, 1, "are you coming?", ago_s=0)
            await wait_for(lambda: "notify.message" in bus.types())
            assert [m.text for m in svc.store.unread()] == ["are you coming?"]
        finally:
            await svc.stop()

    asyncio.run(run())


def test_restart_keeps_texts_that_arrived_while_the_hub_was_off(tmp_path: Path) -> None:
    db = make_db(tmp_path / "chat.db")
    add_text(db, 1, "before", ago_s=3600)

    async def run() -> None:
        svc, _ = make_service(tmp_path)
        await svc.start()
        await svc.stop()
        add_text(db, 1, "while off", ago_s=10)
        again, _ = make_service(tmp_path, MessageStore(tmp_path / "messages"))  # same seen marks on disk
        await again.start()
        try:
            assert [m.text for m in again.store.unread()] == ["while off"]
        finally:
            await again.stop()

    asyncio.run(run())


def test_history_is_the_newest_texts_only(tmp_path: Path) -> None:
    db = make_db(tmp_path / "chat.db")
    for i in range(12):
        add_text(db, 1, f"t{i}", ago_s=100 - i)

    async def run() -> None:
        svc, _ = make_service(tmp_path)
        await svc.start()
        try:
            channel, name, kind, messages, has_more = await svc.history(f"im:{DM}", 5)
            assert (channel, name, kind, has_more) == (f"im:{DM}", "+15550001", "dm", False)
            assert [m.text for m in messages] == ["t7", "t8", "t9", "t10", "t11"]
            _, _, _, older, has_more = await svc.history(f"im:{DM}", 5, before=messages[0].ts)
            assert older == [] and has_more is False         # no paging into older texts
            assert svc.chat_name(GROUP) == "Family"
        finally:
            await svc.stop()

    asyncio.run(run())


def _slack(tmp_path: Path, store: MessageStore, bus: BusRecorder) -> SlackService:
    async def speak(pet_id: str, text: str) -> bool:
        return True

    return SlackService(SlackSettings(), store, bus, speak)  # type: ignore[arg-type]


def test_focus_is_one_message_across_both_providers(tmp_path: Path) -> None:
    db = make_db(tmp_path / "chat.db")
    add_text(db, 1, "dinner at 7?", ago_s=30)
    bus = BusRecorder()
    store = MessageStore(tmp_path / "messages")
    slack = _slack(tmp_path, store, bus)
    store.add(SlackMessage(channel="C1", channel_name="forge-team", is_dm=False, user="U1", user_name="Hudson",
                           text="build ready?", ts="1700000000.000100"))
    svc, _ = make_service(tmp_path, store)
    svc.available = True
    svc.sender.status = SenderStatus(True, "0.6.0")
    text = asyncio.run(svc.history(f"im:{DM}", 5))[3][-1]          # loads the text into the shared store
    app = FastAPI()
    app.include_router(router)
    app.state.slack, app.state.imessage = slack, svc
    client = TestClient(app)

    assert client.post("/messages/focus", json={"channel": text.channel, "ts": text.ts}).status_code == 200
    assert svc.focused is not None and slack.focused is None
    assert client.post("/messages/focus", json={"channel": "C1", "ts": "1700000000.000100"}).status_code == 200
    assert slack.focused is not None and svc.focused is None   # the Slack tap wins: no stale iMessage focus
    assert client.post("/messages/focus", json={"channel": text.channel, "ts": text.ts}).status_code == 200
    assert svc.focused is not None and slack.focused is None

    async def reply() -> dict[str, Any]:
        # A bare "reply to that" goes to the message tapped last, here the iMessage.
        out = await mtools.call_tool("messages_send", {"text": "yes!"}, slack, svc)
        await svc.cancel_draft()
        return out

    assert asyncio.run(reply()).get("source") == "imessage"
