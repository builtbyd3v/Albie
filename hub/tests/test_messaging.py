"""Messaging module: store, digest, event filtering, drafts, tools, router (no network; the Slack client is a stub)."""
from __future__ import annotations

import asyncio
import logging
from pathlib import Path
from typing import Any

import pytest
from fastapi import FastAPI
from fastapi.testclient import TestClient

from events import SlackMessage
from messaging import DraftError, MessageStore, SlackService, SlackSettings
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

    def last(self, type_: str) -> Any:
        return next(p for t, p in reversed(self.events) if t == type_)


class SlackApiError(Exception):
    """Shape of slack_sdk.errors.SlackApiError as the backfill reads it: .response["error"], .response.headers."""

    def __init__(self, error: str, retry_after: str | None = None) -> None:
        super().__init__(error)

        class Resp(dict):
            headers = {"Retry-After": retry_after} if retry_after else {}

        self.response = Resp(error=error)


class StubClient:
    """Just enough of AsyncWebClient for the handler, posting and backfill paths."""

    def __init__(self, identity: str = "UBOT") -> None:
        self.identity = identity
        self.posted: list[dict[str, Any]] = []
        self.conversations: list[dict[str, Any]] = []            # users.conversations pages (list of channel dicts)
        self.history: dict[str, list[dict[str, Any]]] = {}       # channel -> messages (newest first, like Slack)
        self.history_errors: dict[str, list[Exception]] = {}     # channel -> exceptions to raise first
        self.calls: list[tuple[str, dict[str, Any]]] = []

    async def users_conversations(self, **kwargs: Any) -> dict[str, Any]:
        self.calls.append(("users.conversations", kwargs))
        page = int(kwargs.get("cursor") or 0)
        chunk = self.conversations[page * 2:(page + 1) * 2]  # two per page so cursors get exercised
        more = (page + 1) * 2 < len(self.conversations)
        return {"channels": chunk, "response_metadata": {"next_cursor": str(page + 1) if more else ""}}

    async def conversations_history(self, channel: str, **kwargs: Any) -> dict[str, Any]:
        self.calls.append(("conversations.history", {"channel": channel, **kwargs}))
        queued = self.history_errors.get(channel)
        if queued:
            raise queued.pop(0)
        oldest = float(kwargs.get("oldest") or 0)
        return {"messages": [m for m in self.history.get(channel, []) if float(m["ts"]) > oldest]}

    async def auth_test(self) -> dict[str, Any]:
        return {"user_id": self.identity, "user": "bit" if self.identity == "UBOT" else "adrian", "team": "forge"}

    async def users_info(self, user: str) -> dict[str, Any]:
        return {"user": {"id": user, "real_name": "Hudson M", "profile": {"display_name": "hudson"}}}

    async def conversations_info(self, channel: str) -> dict[str, Any]:
        if channel.startswith("D"):
            return {"channel": {"id": channel, "is_im": True}}
        if channel.startswith("G"):
            return {"channel": {"id": channel, "is_mpim": True}}
        return {"channel": {"id": channel, "name": "forge-team"}}

    async def conversations_list(self, **_: Any) -> dict[str, Any]:
        return {"channels": [{"id": "C1", "name": "forge-team"}], "response_metadata": {"next_cursor": ""}}

    async def chat_postMessage(self, **kwargs: Any) -> dict[str, Any]:
        self.posted.append(kwargs)
        return {"ts": "1700000099.000100"}


def make_service(tmp_path: Path, *, enabled: bool = True, as_user: bool = False, voice: bool = True,
                 ) -> tuple[SlackService, BusRecorder, list[tuple[str, str]]]:
    spoken: list[tuple[str, str]] = []
    noted: list[tuple[str, str]] = []

    async def speak(pet_id: str, text: str) -> bool:
        if voice:
            spoken.append((pet_id, text))
        return voice  # False = no live voice session (speech.speak found nobody)

    async def note(pet_id: str, text: str) -> bool:
        noted.append((pet_id, text))
        return voice

    settings = SlackSettings(bot_token="xoxb-x" if enabled else "", app_token="xapp-x" if enabled else "",
                             user_token="xoxp-x" if as_user else "", user_id="UME", default_channel="#forge-team")
    bus = BusRecorder()
    svc = SlackService(settings, MessageStore(tmp_path / "messages"), bus, speak, note)  # type: ignore[arg-type]
    svc.noted = noted  # type: ignore[attr-defined]  (test-only handle)
    svc._client = StubClient("UBOT")
    if as_user:
        svc._user_client = StubClient("UME")
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
    assert (m.user_name, m.channel_name, m.is_dm, m.mentions_me, m.thread_ts) == \
        ("hudson", "forge-team", False, True, "1700000009.000100")
    assert bus.types() == ["notify.message"]
    assert bus.events[0][1] is m
    assert svc.store.latest(1) == [m]


def test_on_event_filters(tmp_path: Path) -> None:
    svc, bus, _ = make_service(tmp_path)

    async def run() -> list[Any]:
        return [
            await svc.on_event(event(bot_id="B1")),                       # another bot
            await svc.on_event(event(user="UBOT")),                       # Bit's own post
            await svc.on_event(event(user="UME", text="my own words")),   # the user's own message: not inbox
            await svc.on_event(event(subtype="message_changed")),         # edit
            await svc.on_event(event(text="")),                           # nothing to show
            await svc.on_event(event()),                                  # the real one
            await svc.on_event(event(type="app_mention")),                # same post, second delivery (bot + user subs)
            await svc.on_event(event(channel="D1", text="dm", ts="1700000011.000100")),
            await svc.on_event(event(channel="G1", text="group", ts="1700000012.000100")),
        ]

    out = asyncio.run(run())
    assert out[:5] == [None] * 5
    assert out[5] is not None and out[6] is None
    dm, group = out[7], out[8]
    assert (dm.channel_name, dm.is_dm, dm.mentions_me) == ("dm", True, True)        # a DM counts as a mention
    assert (group.channel_name, group.is_dm, group.mentions_me) == ("group-dm", True, True)
    assert bus.types() == ["notify.message"] * 3


def test_user_id_derived_from_user_token(tmp_path: Path) -> None:
    svc, _, _ = make_service(tmp_path, as_user=True)
    svc.user_id = ""  # env not set

    class Handler:
        async def connect_async(self) -> None:
            pass

    svc._handler = Handler()
    asyncio.run(svc._connect())
    assert (svc.user_id, svc.user_name, svc.connected, svc.as_user) == ("UME", "adrian", True, True)
    assert svc.health()["as_user"] is True and svc.health()["user_id"] == "UME"


# ------------------------------------------------------------------ startup backfill
def _hist(ts: str, text: str, user: str = "U1", **over: Any) -> dict[str, Any]:
    return {"type": "message", "user": user, "text": text, "ts": ts, **over}


def test_backfill_feeds_the_digest_without_notifying(tmp_path: Path) -> None:
    svc, bus, _ = make_service(tmp_path, as_user=True)
    reader: StubClient = svc._user_client
    reader.conversations = [{"id": "C1", "name": "forge-team"}, {"id": "D1", "is_im": True},
                            {"id": "C2", "name": "random"}]  # 3 -> two pages
    reader.history = {
        "C1": [_hist("1700007000.000100", "newest"), _hist("1700006000.000100", "mine", user="UME"),
               _hist("1700005000.000100", "from a bot", bot_id="B1"), _hist("1700004000.000100", "older")],
        "D1": [_hist("1700006500.000100", "dm for you")],
        "C2": [],
    }
    n = asyncio.run(svc.backfill(now=1700008000.0))
    assert n == 3 and svc.backfilled == 3
    assert bus.events == []  # nothing told to Unity
    assert [m.text for m in svc.store.latest(10)] == ["newest", "dm for you", "older"]
    dm = svc.store.latest(10)[1]
    assert (dm.channel_name, dm.is_dm, dm.mentions_me) == ("dm", True, True)  # names resolved like live events
    assert svc.store.latest(10)[0].channel_name == "forge-team"
    conv_calls = [kw for name, kw in reader.calls if name == "users.conversations"]
    assert len(conv_calls) == 2 and conv_calls[0]["types"] == "public_channel,private_channel,im,mpim"
    assert conv_calls[0]["exclude_archived"] is True and conv_calls[0]["limit"] == 200 and conv_calls[1]["cursor"] == "1"
    hist = [kw for name, kw in reader.calls if name == "conversations.history"]
    assert [h["channel"] for h in hist] == ["C1", "D1", "C2"] and all(h["limit"] == 20 for h in hist)
    assert hist[0]["oldest"] == f"{1700008000.0 - 7200:.6f}"  # no last_seen_ts: the last two hours
    # the digest counts them; a live duplicate of a backfilled message is dropped
    assert asyncio.run(svc.on_event(event(channel="C1", text="newest", ts="1700007000.000100"))) is None
    assert asyncio.run(svc.digest()).count == 3


def test_backfill_starts_at_last_seen_and_uses_the_bot_without_a_user_token(tmp_path: Path) -> None:
    svc, _, _ = make_service(tmp_path)
    svc.store.add(msg("1700000005.000100"))
    svc.store.mark_seen()
    bot: StubClient = svc._client
    bot.conversations = [{"id": "C1", "name": "forge-team"}]
    bot.history = {"C1": [_hist("1700000006.000100", "after"), _hist("1700000004.000100", "before")]}
    assert asyncio.run(svc.backfill()) == 1
    assert bot.calls[-1][1]["oldest"] == "1700000005.000100"
    assert [m.text for m in svc.store.unread()] == ["after"]


def test_backfill_rate_limit_waits_once_then_stops(tmp_path: Path, monkeypatch: pytest.MonkeyPatch) -> None:
    svc, _, _ = make_service(tmp_path, as_user=True)
    reader: StubClient = svc._user_client
    reader.conversations = [{"id": "C1", "name": "a"}, {"id": "C2", "name": "b"}, {"id": "C3", "name": "c"}]
    reader.history = {c: [_hist("1700000009.000100", c)] for c in ("C1", "C2", "C3")}
    reader.history_errors = {"C1": [SlackApiError("ratelimited", retry_after="7")],
                             "C2": [SlackApiError("ratelimited", retry_after="3")]}
    slept: list[float] = []

    async def fake_sleep(s: float) -> None:
        slept.append(s)

    monkeypatch.setattr("messaging.slack_client.asyncio.sleep", fake_sleep)
    assert asyncio.run(svc.backfill(now=1700000010.0)) == 1  # C1 after the wait; C2 hit the limit again -> stop; C3 never asked
    assert slept == [7.0]
    assert [kw["channel"] for name, kw in reader.calls if name == "conversations.history"] == ["C1", "C1", "C2"]


def test_backfill_missing_scope_skips_the_channel(tmp_path: Path, caplog) -> None:
    caplog.set_level(logging.INFO, logger="forge.messaging")
    svc, _, _ = make_service(tmp_path, as_user=True)
    reader: StubClient = svc._user_client
    reader.conversations = [{"id": "G1", "is_mpim": True}, {"id": "C2", "name": "b"}]
    reader.history = {"G1": [_hist("1700000009.000100", "secret")], "C2": [_hist("1700000009.000200", "fine")]}
    reader.history_errors = {"G1": [SlackApiError("missing_scope")]}
    assert asyncio.run(svc.backfill(now=1700000010.0)) == 1
    assert "missing_scope" in caplog.text and "1 skipped" in caplog.text
    assert [m.text for m in svc.store.latest(5)] == ["fine"]


def test_backfill_runs_after_connect_and_survives_a_list_failure(tmp_path: Path) -> None:
    svc, _, _ = make_service(tmp_path, as_user=True)
    svc.user_id = ""

    class Handler:
        async def connect_async(self) -> None:
            pass

    async def boom(**kwargs: Any) -> dict[str, Any]:
        raise RuntimeError("network down")

    svc._user_client.users_conversations = boom  # type: ignore[method-assign]
    svc._handler = Handler()

    async def run() -> int | None:
        await svc._connect()
        assert svc._backfill is not None
        await svc._backfill
        return svc.backfilled

    assert asyncio.run(run()) == 0  # listed nothing, stored nothing, raised nothing


# ------------------------------------------------------------------ drafts
def test_draft_create_send_emits_and_posts_as_user(tmp_path: Path) -> None:
    svc, bus, _ = make_service(tmp_path, as_user=True)
    svc.store.add(msg("1700000010.000100", text="build ready by 3?"))

    async def run() -> tuple[Any, Any, str]:
        draft = await svc.create_draft("yes, by 3", reply_to_ts="1700000010.000100")
        assert svc.open_draft is draft
        sent, ts = await svc.send_draft(draft.draft_id)
        return draft, sent, ts

    draft, sent, ts = asyncio.run(run())
    assert (draft.channel, draft.channel_name, draft.thread_ts) == ("C1", "forge-team", "1700000010.000100")
    assert draft.reply_to is not None and draft.reply_to.text == "build ready by 3?"
    assert sent is draft and ts == "1700000099.000100" and svc.open_draft is None
    assert svc._user_client.posted == [{"channel": "C1", "text": "yes, by 3", "thread_ts": "1700000010.000100"}]
    assert svc._client.posted == []  # not as Bit
    assert bus.types() == ["notify.draft", "notify.sent"]
    assert bus.last("notify.sent").model_dump() == {"draft_id": draft.draft_id, "channel": "C1",
                                                    "channel_name": "forge-team", "text": "yes, by 3", "ts": ts}


def test_draft_falls_back_to_bot_without_user_token(tmp_path: Path) -> None:
    svc, bus, _ = make_service(tmp_path)

    async def run() -> None:
        d = await svc.create_draft("on it", "#forge-team")
        await svc.send_draft(d.draft_id)

    asyncio.run(run())
    assert svc._client.posted == [{"channel": "C1", "text": "on it", "thread_ts": None}]
    assert bus.types() == ["notify.draft", "notify.sent"]


def test_draft_supersede_cancel_and_unknown(tmp_path: Path) -> None:
    svc, bus, _ = make_service(tmp_path)

    async def run() -> None:
        first = await svc.create_draft("v1")
        second = await svc.create_draft("v2")          # replaces first
        assert svc.open_draft is second
        with pytest.raises(DraftError):
            await svc.send_draft(first.draft_id)       # stale id
        with pytest.raises(DraftError):
            await svc.cancel_draft("nope")
        cancelled = await svc.cancel_draft(None)       # None = whichever is open
        assert cancelled is second and svc.open_draft is None
        with pytest.raises(DraftError):
            await svc.send_draft(None)                 # nothing open
        with pytest.raises(ValueError):
            await svc.create_draft("   ")

    asyncio.run(run())
    assert bus.types() == ["notify.draft", "notify.draft_cancelled", "notify.draft", "notify.draft_cancelled"]
    assert [p.reason for t, p in bus.events if t == "notify.draft_cancelled"] == ["superseded", "cancelled"]
    assert svc._client.posted == []


def test_draft_expires(tmp_path: Path) -> None:
    svc, bus, _ = make_service(tmp_path)
    svc.draft_expiry_s = 0.05

    async def run() -> None:
        d = await svc.create_draft("too slow")
        await asyncio.sleep(0.2)
        assert svc.open_draft is None
        with pytest.raises(DraftError):
            await svc.send_draft(d.draft_id)

    asyncio.run(run())
    assert bus.types() == ["notify.draft", "notify.draft_cancelled"]
    assert bus.last("notify.draft_cancelled").reason == "expired"


# ------------------------------------------------------------------ focus ("Reply with Bit")
def test_focus_speaks_notes_and_seeds_the_next_send(tmp_path: Path) -> None:
    svc, bus, spoken = make_service(tmp_path, as_user=True)
    svc.store.add(msg("1700000001.000100", text="unrelated"))
    svc.store.add(msg("1700000002.000100", text="can you review my PR today"))

    async def run() -> list[Any]:
        found = await svc.focus("C1", "1700000002.000100")
        drafted = await mtools.call_tool("messages_send", {"text": "yes, after lunch"}, svc)
        draft = svc.open_draft
        sent = await mtools.call_tool("messages_confirm", {"send": True}, svc)
        return [found, drafted, draft, sent]

    found, drafted, draft, sent = asyncio.run(run())
    assert found is not None and found.ts == "1700000002.000100"
    assert spoken == [("bit", "hudson in #forge-team says: can you review my PR today. Want me to answer?")]
    assert bus.types() == ["notify.draft", "notify.sent"]  # voice spoke, so no pet.say caption
    note = svc.noted[0][1]  # type: ignore[attr-defined]
    assert "hudson" in note and "#forge-team" in note and "can you review my PR today" in note and "messages_send" in note
    assert drafted["status"] == "awaiting_confirmation"
    assert (draft.channel, draft.thread_ts, draft.reply_to.ts) == ("C1", "1700000002.000100", "1700000002.000100")
    assert svc._user_client.posted[-1]["thread_ts"] == "1700000002.000100"
    assert sent["status"] == "sent" and svc.focused is None  # answered: the focus is spent


def test_focus_explicit_target_wins_and_focus_persists_until_answered(tmp_path: Path) -> None:
    svc, _, _ = make_service(tmp_path)
    svc.store.add(msg("1700000002.000100", text="ping", channel="C1"))

    async def run() -> None:
        await svc.focus("C1", "1700000002.000100")
        await mtools.call_tool("messages_send", {"text": "elsewhere", "channel": "#forge-team"}, svc)
        assert svc.open_draft is not None and svc.open_draft.thread_ts is None and svc.open_draft.reply_to is None
        await mtools.call_tool("messages_confirm", {"send": True}, svc)
        assert svc.focused is not None  # that send didn't answer the focused message
        await mtools.call_tool("messages_send", {"text": "now you"}, svc)
        assert svc.open_draft is not None and svc.open_draft.thread_ts == "1700000002.000100"

    asyncio.run(run())


def test_focus_without_voice_says_it_as_a_caption(tmp_path: Path) -> None:
    svc, bus, spoken = make_service(tmp_path, voice=False)
    long = "word " * 80  # 400 chars
    dm = msg("1700000003.000100", text=long, channel="D1").model_copy(update={"channel_name": "dm", "is_dm": True})
    svc.store.add(dm)
    found = asyncio.run(svc.focus("D1", "1700000003.000100", "bit"))
    assert found is not None and spoken == []
    assert bus.types() == ["pet.say"]
    line = bus.events[0][1]["text"]
    assert line.startswith("hudson in a DM says: word word") and line.endswith("... Want me to answer?")
    assert len(line) < 200 + 60


def test_focus_unknown_message_is_quiet(tmp_path: Path) -> None:
    svc, bus, spoken = make_service(tmp_path)
    svc.store.add(msg("1700000002.000100", channel="C1"))
    assert asyncio.run(svc.focus("C1", "1.0")) is None
    assert asyncio.run(svc.focus("C9", "1700000002.000100")) is None  # right ts, wrong channel
    assert spoken == [] and bus.events == [] and svc.focused is None


def test_focus_from_ws_uses_the_sessions_pet(tmp_path: Path) -> None:
    svc, _, spoken = make_service(tmp_path)
    svc.store.add(msg("1700000002.000100", channel="C1"))

    class Session:
        pet_id = "bit"

    asyncio.run(svc.focus_from_ws(Session(), {"channel": "C1", "ts": "1700000002.000100"}))
    assert spoken[0][0] == "bit"
    with pytest.raises(Exception):  # bad payload -> ValidationError, ws.py answers agent.error
        asyncio.run(svc.focus_from_ws(Session(), {"channel": "C1"}))


# ------------------------------------------------------------------ tools
def test_tools_send_is_draft_then_confirm(tmp_path: Path) -> None:
    svc, bus, _ = make_service(tmp_path, as_user=True)
    svc.store.add(msg("1700000001.000100", text="first"))
    svc.store.add(msg("1700000002.000100", text="second"))

    async def run() -> list[dict[str, Any]]:
        return [
            await mtools.call_tool("messages_read_latest", {"n": 1}, svc),
            await mtools.call_tool("messages_send", {"text": "yes, 3pm works", "reply_to_ts": "1700000002.000100"}, svc),
            {"posted_before_confirm": list(svc._user_client.posted)},
            await mtools.call_tool("messages_confirm", {"send": True}, svc),
            await mtools.call_tool("messages_send", {"text": "  "}, svc),
            await mtools.call_tool("messages_send", {"text": "x", "channel": "#nope"}, svc),
            await mtools.call_tool("messages_send", {"text": "never mind this"}, svc),
            await mtools.call_tool("messages_confirm", {"send": False}, svc),
            await mtools.call_tool("messages_confirm", {"send": True}, svc),   # nothing open any more
            await mtools.call_tool("messages_nope", {}, svc),
        ]

    read, drafted, before, sent, empty, missing, drafted2, cancelled, nothing, unknown = asyncio.run(run())
    assert [m["text"] for m in read["messages"]] == ["second"]
    assert read["messages"][0]["is_dm"] is False
    assert drafted["status"] == "awaiting_confirmation" and drafted["as"] == "you" and "draft_id" in drafted
    assert before == {"posted_before_confirm": []}  # nothing left before the confirm
    assert sent["status"] == "sent" and sent["ts"] == "1700000099.000100"
    assert svc._user_client.posted[-1] == {"channel": "C1", "text": "yes, 3pm works", "thread_ts": "1700000002.000100"}
    assert "error" in empty and "error" in missing and "error" in unknown
    assert drafted2["status"] == "awaiting_confirmation"
    assert cancelled == {"status": "cancelled", "draft_id": drafted2["draft_id"]}
    assert "error" in nothing
    assert bus.types() == ["notify.draft", "notify.sent", "notify.draft", "notify.draft_cancelled"]
    send_tool = next(t for t in mtools.TOOLS if t["name"] == "messages_send")
    assert "DRAFT" in send_tool["description"] and "NEVER say the message was sent" in send_tool["description"]
    assert {t["name"] for t in mtools.TOOLS} == {"messages_read_latest", "messages_send", "messages_confirm"}


def test_tools_disabled(tmp_path: Path) -> None:
    svc, _, _ = make_service(tmp_path, enabled=False)
    assert "error" in asyncio.run(mtools.call_tool("messages_read_latest", {}, svc))


# ------------------------------------------------------------------ router
@pytest.fixture()
def api(tmp_path: Path):
    def build(enabled: bool) -> TestClient:
        app = FastAPI()
        app.include_router(router)
        svc, _, _ = make_service(tmp_path, enabled=enabled, as_user=enabled)
        app.state.slack = svc
        return TestClient(app)

    return build


def test_router_disabled_503s_but_digest_works(api) -> None:
    c = api(False)
    assert c.get("/messages/latest").status_code == 503
    assert c.post("/messages/drafts", json={"text": "hi"}).status_code == 503
    assert c.post("/messages/drafts/abc/send").status_code == 503
    assert c.post("/messages/send", json={"text": "hi"}).status_code in (404, 405)  # no direct send endpoint
    r = c.post("/messages/digest")
    assert r.status_code == 200 and r.json() == {"count": 0, "mentions": 0, "line": "", "messages": []}


def test_router_enabled_draft_flow(api) -> None:
    c = api(True)
    svc: SlackService = c.app.state.slack
    svc.store.add(msg("1700000001.000100", text="first", mention=True))
    r = c.get("/messages/latest?n=5")
    assert r.status_code == 200 and r.json()[0]["text"] == "first"
    assert set(r.json()[0]) == {"channel", "channel_name", "is_dm", "user", "user_name", "text", "ts", "thread_ts", "mentions_me"}

    r = c.post("/messages/drafts", json={"text": "hey", "reply_to_ts": "1700000001.000100"})
    assert r.status_code == 200
    draft = r.json()
    assert set(draft) == {"draft_id", "channel", "channel_name", "text", "thread_ts", "reply_to"}
    assert draft["channel"] == "C1" and draft["thread_ts"] == "1700000001.000100" and draft["reply_to"]["text"] == "first"
    assert svc._user_client.posted == []

    assert c.post("/messages/drafts", json={"text": "   "}).status_code == 422
    assert c.post("/messages/drafts", json={"channel": "#missing", "text": "x"}).status_code == 404
    # that 404 did not disturb the open draft; the 422 neither
    assert svc.open_draft is not None and svc.open_draft.draft_id == draft["draft_id"]

    assert c.post(f"/messages/drafts/{draft['draft_id']}/cancel").status_code == 200
    assert c.post(f"/messages/drafts/{draft['draft_id']}/send").status_code == 404  # gone

    d2 = c.post("/messages/drafts", json={"channel": "C1", "text": "take two"}).json()
    r = c.post(f"/messages/drafts/{d2['draft_id']}/send")
    assert r.status_code == 200 and r.json() == {"ts": "1700000099.000100"}
    assert svc._user_client.posted == [{"channel": "C1", "text": "take two", "thread_ts": None}]
    assert c.post(f"/messages/drafts/{d2['draft_id']}/cancel").status_code == 404

    r = c.post("/messages/digest", json={"pet_id": "bit"})
    assert r.status_code == 200 and r.json()["line"] == "You have 1 new message, and it mentions you."

    r = c.post("/messages/focus", json={"pet_id": "bit", "channel": "C1", "ts": "1700000001.000100"})
    assert r.status_code == 200 and r.json()["text"] == "first" and svc.focused is not None
    assert c.post("/messages/focus", json={"channel": "C1", "ts": "nope"}).status_code == 404
    assert api(False).post("/messages/focus", json={"channel": "C1", "ts": "1"}).status_code == 503
