"""Messaging module: store, digest, event filtering, router (no network; the Slack client is a small stub)."""
from __future__ import annotations

import asyncio
from pathlib import Path
from typing import Any

import pytest
from fastapi import FastAPI
from fastapi.testclient import TestClient

from events import SlackMessage
from messaging import MessageStore, SlackService, SlackSettings
from messaging import tools as mtools
from messaging.router import router


def msg(ts: str, text: str = "hi", user: str = "U1", channel: str = "C1", mention: bool = False) -> SlackMessage:
    return SlackMessage(channel=channel, channel_name="forge-team", user=user, user_name="hudson", text=text, ts=ts,
                        mentions_me=mention)


class BusRecorder:
    def __init__(self) -> None:
        self.events: list[tuple[str, Any]] = []

    async def publish(self, type_: str, payload: Any) -> None:
        self.events.append((type_, payload))

    def types(self) -> list[str]:
        return [t for t, _ in self.events]


class StubClient:
    """Just enough of AsyncWebClient for the handler and send paths."""

    def __init__(self) -> None:
        self.posted: list[dict[str, Any]] = []

    async def auth_test(self) -> dict[str, Any]:
        return {"user_id": "UBOT", "user": "bit", "team": "forge"}

    async def users_info(self, user: str) -> dict[str, Any]:
        return {"user": {"id": user, "real_name": "Hudson M", "profile": {"display_name": "hudson"}}}

    async def conversations_info(self, channel: str) -> dict[str, Any]:
        if channel.startswith("D"):
            return {"channel": {"id": channel, "is_im": True}}
        return {"channel": {"id": channel, "name": "forge-team"}}

    async def conversations_list(self, **_: Any) -> dict[str, Any]:
        return {"channels": [{"id": "C1", "name": "forge-team"}], "response_metadata": {"next_cursor": ""}}

    async def chat_postMessage(self, **kwargs: Any) -> dict[str, Any]:
        self.posted.append(kwargs)
        return {"ts": "1700000099.000100"}


def make_service(tmp_path: Path, *, enabled: bool = True) -> tuple[SlackService, BusRecorder, list[tuple[str, str]]]:
    spoken: list[tuple[str, str]] = []

    async def speak(pet_id: str, text: str) -> bool:
        spoken.append((pet_id, text))
        return True

    settings = SlackSettings(bot_token="xoxb-x" if enabled else "", app_token="xapp-x" if enabled else "",
                             user_id="UME", default_channel="#forge-team")
    bus = BusRecorder()
    svc = SlackService(settings, MessageStore(tmp_path / "messages"), bus, speak)  # type: ignore[arg-type]
    svc._client = StubClient()
    svc.bot_user_id = "UBOT"
    return svc, bus, spoken


# ------------------------------------------------------------------ store
def test_store_unread_mark_seen_and_persist(tmp_path: Path) -> None:
    store = MessageStore(tmp_path / "m")
    for ts in ("1700000001.000100", "1700000003.000100", "1700000002.000100"):
        store.add(msg(ts))
    assert [m.ts for m in store.latest(2)] == ["1700000003.000100", "1700000002.000100"]
    assert len(store.unread()) == 3
    store.mark_seen()
    assert store.unread() == []
    store.add(msg("1700000004.000100"))
    assert [m.ts for m in store.unread()] == ["1700000004.000100"]
    again = MessageStore(tmp_path / "m")  # mark survives a restart
    assert again.last_seen_ts == "1700000003.000100"


def test_store_ring_buffer_caps(tmp_path: Path) -> None:
    store = MessageStore(tmp_path / "m", maxlen=3)
    for i in range(5):
        store.add(msg(f"170000000{i}.000100"))
    assert len(store) == 3
    assert store.latest(10)[0].ts == "1700000004.000100"


# ------------------------------------------------------------------ digest
def test_digest_empty_is_quiet(tmp_path: Path) -> None:
    svc, bus, spoken = make_service(tmp_path)
    d = asyncio.run(svc.digest())
    assert d.count == 0 and d.line == "" and d.messages == []
    assert bus.types() == ["notify.digest"]
    assert spoken == []


def test_digest_speaks_and_marks_seen(tmp_path: Path) -> None:
    svc, bus, spoken = make_service(tmp_path)
    for i, mention in enumerate((False, True, False), start=1):
        svc.store.add(msg(f"170000000{i}.000100", mention=mention))
    d = asyncio.run(svc.digest("bit"))
    assert (d.count, d.mentions) == (3, 1)
    assert d.line == "You have 3 new messages, 1 mentions you."
    assert [m.ts for m in d.messages][0] == "1700000003.000100"
    assert bus.types() == ["notify.digest", "pet.say"]
    assert bus.events[1][1] == {"pet_id": "bit", "text": d.line}
    assert spoken == [("bit", d.line)]
    assert asyncio.run(svc.digest()).count == 0  # seen now


def test_digest_caps_messages_at_four(tmp_path: Path) -> None:
    svc, _, _ = make_service(tmp_path)
    for i in range(6):
        svc.store.add(msg(f"170000000{i}.000100"))
    d = asyncio.run(svc.digest())
    assert d.count == 6 and len(d.messages) == 4


# ------------------------------------------------------------------ inbound events
def event(**over: Any) -> dict[str, Any]:
    base = {"type": "message", "channel": "C1", "user": "U1", "text": "hey <@UME> build ready?", "ts": "1700000010.000100"}
    return {**base, **over}


def test_on_event_records_and_publishes(tmp_path: Path) -> None:
    svc, bus, _ = make_service(tmp_path)
    m = asyncio.run(svc.on_event(event(thread_ts="1700000009.000100")))
    assert m is not None
    assert (m.user_name, m.channel_name, m.mentions_me, m.thread_ts) == ("hudson", "forge-team", True, "1700000009.000100")
    assert bus.types() == ["notify.message"]
    assert bus.events[0][1] is m
    assert svc.store.latest(1) == [m]
    assert m.model_dump()["mentions_me"] is True


def test_on_event_filters(tmp_path: Path) -> None:
    svc, bus, _ = make_service(tmp_path)

    async def run() -> list[Any]:
        return [
            await svc.on_event(event(bot_id="B1")),                       # another bot
            await svc.on_event(event(user="UBOT")),                       # Bit's own post
            await svc.on_event(event(subtype="message_changed")),         # edit
            await svc.on_event(event(text="")),                           # nothing to show
            await svc.on_event(event()),                                  # the real one
            await svc.on_event(event(type="app_mention")),                # same post, second delivery
            await svc.on_event(event(channel="D1", text="dm", ts="1700000011.000100")),
        ]

    out = asyncio.run(run())
    assert out[:4] == [None, None, None, None]
    assert out[4] is not None and out[5] is None
    assert out[6] is not None and out[6].channel_name == "dm" and out[6].mentions_me is False
    assert bus.types() == ["notify.message", "notify.message"]


# ------------------------------------------------------------------ send + tools
def test_send_resolves_name_threads_and_emits(tmp_path: Path) -> None:
    svc, bus, _ = make_service(tmp_path)
    ts = asyncio.run(svc.send("#forge-team", "on it", "1700000010.000100"))
    assert ts == "1700000099.000100"
    assert svc._client.posted == [{"channel": "C1", "text": "on it", "thread_ts": "1700000010.000100"}]
    assert bus.types() == ["notify.sent"]
    assert bus.events[0][1].model_dump() == {"channel": "C1", "channel_name": "forge-team", "text": "on it", "ts": ts}


def test_tools(tmp_path: Path) -> None:
    svc, _, _ = make_service(tmp_path)
    svc.store.add(msg("1700000001.000100", text="first"))
    svc.store.add(msg("1700000002.000100", text="second"))

    async def run() -> list[dict[str, Any]]:
        return [
            await mtools.call_tool("messages_read_latest", {"n": 1}, svc),
            await mtools.call_tool("messages_send", {"text": "yes, 3pm works", "thread_ts": "1700000002.000100"}, svc),
            await mtools.call_tool("messages_send", {"text": "  "}, svc),
            await mtools.call_tool("messages_send", {"text": "x", "channel": "#nope"}, svc),
            await mtools.call_tool("messages_nope", {}, svc),
        ]

    read, sent, empty, missing, unknown = asyncio.run(run())
    assert [m["text"] for m in read["messages"]] == ["second"]
    assert sent == {"ok": True, "ts": "1700000099.000100"}
    assert svc._client.posted[-1]["channel"] == "C1"  # default channel
    assert "error" in empty and "error" in missing and "error" in unknown
    assert any(t["name"] == "messages_send" and "MUST" in t["description"] for t in mtools.TOOLS)


def test_tools_disabled(tmp_path: Path) -> None:
    svc, _, _ = make_service(tmp_path, enabled=False)
    assert "error" in asyncio.run(mtools.call_tool("messages_read_latest", {}, svc))


# ------------------------------------------------------------------ router
@pytest.fixture()
def api(tmp_path: Path):
    def build(enabled: bool) -> TestClient:
        app = FastAPI()
        app.include_router(router)
        svc, _, _ = make_service(tmp_path, enabled=enabled)
        app.state.slack = svc
        return TestClient(app)

    return build


def test_router_disabled_503s_but_digest_works(api) -> None:
    c = api(False)
    assert c.get("/messages/latest").status_code == 503
    assert c.post("/messages/send", json={"text": "hi"}).status_code == 503
    r = c.post("/messages/digest")
    assert r.status_code == 200 and r.json() == {"count": 0, "mentions": 0, "line": "", "messages": []}


def test_router_enabled(api) -> None:
    c = api(True)
    svc: SlackService = c.app.state.slack
    svc.store.add(msg("1700000001.000100", text="first", mention=True))
    r = c.get("/messages/latest?n=5")
    assert r.status_code == 200 and r.json()[0]["text"] == "first"
    assert set(r.json()[0]) == {"channel", "channel_name", "user", "user_name", "text", "ts", "thread_ts", "mentions_me"}
    r = c.post("/messages/send", json={"channel": "C1", "text": "hey", "thread_ts": "1700000001.000100"})
    assert r.status_code == 200 and r.json() == {"ts": "1700000099.000100"}
    assert c.post("/messages/send", json={"text": "   "}).status_code == 422
    assert c.post("/messages/send", json={"channel": "#missing", "text": "x"}).status_code == 404
    r = c.post("/messages/digest", json={"pet_id": "bit"})
    assert r.status_code == 200 and r.json()["line"] == "You have 1 new message, and it mentions you."
