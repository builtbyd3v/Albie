"""Messaging module: store, digest, event filtering, channel list, history, drafts, tools, router
(no network; the Slack web clients are stubs)."""
from __future__ import annotations

import asyncio
import json
import logging
from pathlib import Path
from typing import Any

import pytest
from fastapi import FastAPI
from fastapi.testclient import TestClient

from events import EventBus, SlackMessage
from messaging import DraftError, MessageStore, RateLimited, SlackService, SlackSettings
from messaging import tools as mtools
from messaging.router import router


def msg(ts: str, text: str = "hi", user: str = "U1", channel: str = "C1", mention: bool = False,
        is_me: bool = False, **over: Any) -> SlackMessage:
    base = dict(channel=channel, channel_name="forge-team", user=user, user_name="hudson", text=text, ts=ts,
                mentions_me=mention, is_me=is_me)
    return SlackMessage(**{**base, **over})


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
    """Shape of slack_sdk.errors.SlackApiError as the service reads it: .response["error"], .response.headers."""

    def __init__(self, error: str, retry_after: str | None = None) -> None:
        super().__init__(error)

        class Resp(dict):
            headers = {"Retry-After": retry_after} if retry_after else {}

        self.response = Resp(error=error)


USERS: dict[str, dict[str, Any]] = {
    "U1": {"id": "U1", "name": "hudson", "real_name": "Hudson M", "profile": {"display_name": "hudson"}},
    "U2": {"id": "U2", "name": "jane.d", "real_name": "Jane Doe", "profile": {"display_name": ""}},
    "UME": {"id": "UME", "name": "adrian", "real_name": "Adrian P", "profile": {"display_name": "adrian"}},
}


class StubClient:
    """Just enough of AsyncWebClient for the handler, posting, listing and history paths."""

    def __init__(self, identity: str = "UBOT") -> None:
        self.identity = identity
        self.posted: list[dict[str, Any]] = []
        # conversations.list rows (two per page so cursors get exercised); conversations.info reads the same rows
        self.conversations: list[dict[str, Any]] = [{"id": "C1", "name": "forge-team", "is_member": True}]
        self.history: dict[str, list[dict[str, Any]]] = {}       # channel -> messages (newest first, like Slack)
        self.history_errors: dict[str, list[Exception]] = {}     # channel -> exceptions to raise first
        self.users: dict[str, dict[str, Any]] = dict(USERS)
        self.users_list_error: Exception | None = None
        self.calls: list[tuple[str, dict[str, Any]]] = []

    def called(self, name: str) -> list[dict[str, Any]]:
        return [kw for n, kw in self.calls if n == name]

    async def conversations_list(self, **kwargs: Any) -> dict[str, Any]:
        self.calls.append(("conversations.list", kwargs))
        page = int(kwargs.get("cursor") or 0)
        chunk = self.conversations[page * 2:(page + 1) * 2]
        more = (page + 1) * 2 < len(self.conversations)
        return {"channels": chunk, "response_metadata": {"next_cursor": str(page + 1) if more else ""}}

    async def conversations_history(self, channel: str, **kwargs: Any) -> dict[str, Any]:
        self.calls.append(("conversations.history", {"channel": channel, **kwargs}))
        queued = self.history_errors.get(channel)
        if queued:
            raise queued.pop(0)
        if channel not in self.history and not any(c["id"] == channel for c in self.conversations):
            raise SlackApiError("channel_not_found")
        oldest = float(kwargs.get("oldest") or 0)
        latest = float(kwargs["latest"]) if kwargs.get("latest") else None
        rows = [m for m in self.history.get(channel, [])
                if float(m["ts"]) > oldest and (latest is None or float(m["ts"]) < latest)
                and (not m.get("thread_ts") or m["thread_ts"] == m["ts"] or m.get("subtype") == "thread_broadcast")]
        limit = int(kwargs.get("limit") or 100)
        return {"messages": rows[:limit], "has_more": len(rows) > limit}

    async def auth_test(self) -> dict[str, Any]:
        return {"user_id": self.identity, "user": "bit" if self.identity == "UBOT" else "adrian", "team": "forge"}

    async def users_info(self, user: str) -> dict[str, Any]:
        self.calls.append(("users.info", {"user": user}))
        return {"user": self.users.get(user, {"id": user, "name": user.lower(), "real_name": f"User {user}"})}

    async def users_list(self, **kwargs: Any) -> dict[str, Any]:
        self.calls.append(("users.list", kwargs))
        if self.users_list_error is not None:
            raise self.users_list_error
        return {"members": list(self.users.values()), "response_metadata": {"next_cursor": ""}}

    async def conversations_info(self, channel: str) -> dict[str, Any]:
        self.calls.append(("conversations.info", {"channel": channel}))
        row = next((c for c in self.conversations if c["id"] == channel), None)
        if row is not None:
            return {"channel": row}
        if channel.startswith("D"):
            return {"channel": {"id": channel, "is_im": True, "user": "U1"}}
        if channel.startswith("G"):
            return {"channel": {"id": channel, "is_mpim": True, "name": "mpdm-adrian--hudson--jane.d-1"}}
        return {"channel": {"id": channel, "name": "forge-team"}}

    async def conversations_open(self, users: str) -> dict[str, Any]:
        self.calls.append(("conversations.open", {"users": users}))
        cid = f"D{users}"
        row = {"id": cid, "is_im": True, "user": users}
        if not any(c["id"] == cid for c in self.conversations):
            self.conversations.append(row)
        return {"channel": row}

    async def chat_postMessage(self, **kwargs: Any) -> dict[str, Any]:
        self.posted.append(kwargs)
        return {"ts": "1700000099.000100"}


def make_service(tmp_path: Path, *, enabled: bool = True, as_user: bool = False, voice: bool = True,
                 bus: Any | None = None) -> tuple[SlackService, Any, list[tuple[str, str]]]:
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
    bus = bus if bus is not None else BusRecorder()
    svc = SlackService(settings, MessageStore(tmp_path / "messages"), bus, speak, note)  # type: ignore[arg-type]
    svc.noted = noted  # type: ignore[attr-defined]  (test-only handle)
    svc._client = StubClient("UBOT")
    if as_user:
        svc._user_client = StubClient("UME")
        svc.user_name = "adrian"
    svc.bot_user_id = "UBOT"
    svc.channels_debounce_s = 0.01
    return svc, bus, spoken


def reader(svc: SlackService) -> StubClient:
    return svc._user_client or svc._client


async def settle(svc: SlackService) -> None:
    """Let the debounced notify.channels push fire."""
    await asyncio.sleep(svc.channels_debounce_s * 4)


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
    assert json.loads((tmp_path / "m" / "state.json").read_text())["seen"] == {"C1": "1700000003.000100"}


def test_store_migrates_the_old_single_mark(tmp_path: Path) -> None:
    (tmp_path / "m").mkdir()
    (tmp_path / "m" / "state.json").write_text(json.dumps({"last_seen_ts": "1700000005.000100"}))
    store = MessageStore(tmp_path / "m")
    assert store.last_seen_ts == "1700000005.000100" and store.seen_ts("D9") == "1700000005.000100"
    store.add(msg("1700000004.000100", channel="D9"))   # before the old mark: seen in every channel
    store.add(msg("1700000006.000100", channel="D9"))
    assert [m.ts for m in store.unread("D9")] == ["1700000006.000100"]


def test_store_ring_buffer_caps(tmp_path: Path) -> None:
    store = MessageStore(tmp_path / "m", maxlen=3)
    for i in range(5):
        store.add(msg(f"170000000{i}.000100"))
    assert len(store) == 3
    assert store.latest(10)[0].ts == "1700000004.000100"


def test_store_is_per_conversation(tmp_path: Path) -> None:
    store = MessageStore(tmp_path / "m", maxlen=3, max_channels=2)
    for i in range(4):
        store.add(msg(f"170000000{i}.000100", channel="C1"))
    store.add(msg("1700000005.000100", channel="D1", mention=True))
    assert store.count("C1") == 3 and store.count("D1") == 1 and len(store) == 4   # one ring per channel
    assert store.add(msg("1700000005.000100", channel="D1", mention=True, text="edited copy")) is False  # merged by ts
    assert store.last("D1").text == "edited copy" and store.count("D1") == 1
    assert len(store.unread()) == 4 and len(store.unread("D1")) == 1 and len(store.mentions()) == 1
    assert store.mark_seen("D1") is True and store.unread("D1") == [] and len(store.unread("C1")) == 3
    assert store.mark_seen("D1") is False  # nothing newer
    assert store.seen_ts("D1") == "1700000005.000100" and store.seen_ts("C1") == "0"
    store.add(msg("1700000009.000100", channel="G1"))  # third channel: the ring whose newest is oldest (C1) goes
    assert set(store.channels()) == {"D1", "G1"}
    store.mark_seen()
    assert store.unread() == [] and store.last_seen_ts == "1700000009.000100" and store.seen_ts("C1") == "1700000009.000100"


def test_store_history_is_top_level_oldest_first_and_pages(tmp_path: Path) -> None:
    store = MessageStore(tmp_path / "m")
    for i in range(1, 6):
        store.add(msg(f"170000000{i}.000100", text=f"m{i}"))
    store.add(msg("1700000003.500000", text="a reply", thread_ts="1700000003.000100"))
    store.add(msg("1700000006.000000", text="own", is_me=True))
    assert [m.text for m in store.history("C1", 3)] == ["m4", "m5", "own"]   # replies out, own posts in
    assert [m.text for m in store.history("C1", 10, before="1700000003.000100")] == ["m1", "m2"]
    assert store.history("C9", 5) == []
    assert len(store.unread()) == 6  # the reply counts as inbox; the user's own post never does


# ------------------------------------------------------------------ digest
def test_digest_empty_still_greets(tmp_path: Path) -> None:
    """Nothing new: the line is the zero-case sentence and Bit says it (the arrival card needs a headline)."""
    svc, bus, spoken = make_service(tmp_path)
    d = asyncio.run(svc.digest())
    assert d.count == 0 and d.line == "You have no new messages today." and d.messages == []
    assert bus.types() == ["notify.digest", "pet.say"]
    assert bus.events[1][1] == {"pet_id": "bit", "text": d.line}
    assert spoken == [("bit", d.line)]


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


def test_digest_counts_every_conversation_with_per_channel_seen(tmp_path: Path) -> None:
    svc, bus, _ = make_service(tmp_path, as_user=True)
    svc.store.add(msg("1700000001.000100", channel="C1"))
    svc.store.add(msg("1700000002.000100", channel="D1", channel_name="dm", is_dm=True, mention=True))
    svc.store.add(msg("1700000003.000100", channel="G1", channel_name="group-dm", is_dm=True, mention=True))
    svc.store.add(msg("1700000004.000100", channel="C1", is_me=True, user="UME", text="mine"))

    async def run() -> list[Any]:
        await svc.mark_seen("D1")                       # the DM was open in the panel
        first = await svc.digest()
        svc.store.add(msg("1700000005.000100", channel="C2", channel_name="random"))
        second = await svc.digest()
        await settle(svc)
        return [first, second]

    first, second = asyncio.run(run())
    assert (first.count, first.mentions) == (2, 1)      # C1 + G1; D1 was seen, "mine" never counts
    assert [m.channel for m in first.messages] == ["G1", "C1"]
    assert (second.count, second.mentions) == (1, 0)    # only what arrived after the first sit-down
    assert svc.store.unread() == []
    state = json.loads((tmp_path / "messages" / "state.json").read_text())
    assert state["seen_floor"] == "1700000005.000100" and state["seen"]["D1"] == "1700000002.000100"
    assert bus.types().count("notify.channels") >= 1   # the counts changed


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
    assert (m.is_me, m.edited, m.reply_count, m.user_initials) == (False, False, None, "HU")
    assert bus.types() == ["notify.message"]
    assert bus.events[0][1] is m
    assert svc.store.latest(1) == [m]


def test_on_event_filters(tmp_path: Path) -> None:
    svc, bus, _ = make_service(tmp_path)

    async def run() -> list[Any]:
        out = [
            await svc.on_event(event(bot_id="B1")),                       # another bot
            await svc.on_event(event(user="UBOT")),                       # Bit's own post
            await svc.on_event(event(subtype="message_changed")),         # edit
            await svc.on_event(event(text="")),                           # nothing to show
            await svc.on_event(event()),                                  # the real one
            await svc.on_event(event(type="app_mention")),                # same post, second delivery (bot + user subs)
            await svc.on_event(event(channel="D1", text="dm", ts="1700000011.000100")),
            await svc.on_event(event(channel="G1", text="group", ts="1700000012.000100")),
        ]
        await settle(svc)
        return out

    out = asyncio.run(run())
    assert out[:4] == [None] * 4
    assert out[4] is not None and out[5] is None
    dm, group = out[6], out[7]
    assert (dm.channel_name, dm.is_dm, dm.mentions_me) == ("dm", True, True)        # a DM counts as a mention
    assert (group.channel_name, group.is_dm, group.mentions_me) == ("group-dm", True, True)
    assert bus.types() == ["notify.message"] * 3 + ["notify.channels"]  # one debounced list push for the three


def test_on_event_keeps_the_users_own_posts_as_is_me(tmp_path: Path) -> None:
    svc, bus, _ = make_service(tmp_path, as_user=True)

    async def run() -> list[Any]:
        mine = await svc.on_event(event(user="UME", text="my own words <@UME>", channel="D1"))
        theirs = await svc.on_event(event(ts="1700000011.000100", channel="D1", text="reply"))
        await settle(svc)
        return [mine, theirs]

    mine, theirs = asyncio.run(run())
    assert mine is not None and mine.is_me is True and mine.mentions_me is False  # own post, even in a DM
    assert mine.user_name == "adrian" and mine.user_initials == "AD"
    assert bus.types()[:2] == ["notify.message", "notify.message"]              # the open conversation stays live
    assert bus.events[0][1].is_me is True
    assert [m.text for m in svc.store.unread()] == ["reply"]                     # the digest skips it
    assert asyncio.run(svc.digest()).count == 1
    rows = svc.channel_summaries()
    d1 = next(r for r in rows if r.id == "D1")
    assert (d1.unread, d1.last_text) == (0, "reply")


def test_live_reply_bumps_the_parents_reply_count(tmp_path: Path) -> None:
    svc, bus, _ = make_service(tmp_path)

    async def run() -> None:
        await svc.on_event(event())
        await svc.on_event(event(ts="1700000010.500000", thread_ts="1700000010.000100", text="in thread"))
        await svc.on_event(event(ts="1700000010.600000", thread_ts="1700000010.000100", text="again"))

    asyncio.run(run())
    parent = svc.store.get("C1", "1700000010.000100")
    assert parent is not None and parent.reply_count == 2
    assert [m.text for m in svc.store.history("C1", 10)] == ["hey <@UME> build ready?"]  # replies stay out of history
    assert bus.types().count("notify.message") == 3


def test_user_id_derived_from_user_token(tmp_path: Path) -> None:
    svc, _, _ = make_service(tmp_path, as_user=True)
    svc.user_id = ""  # env not set
    svc.user_name = None

    class Handler:
        async def connect_async(self) -> None:
            pass

    svc._handler = Handler()

    async def run() -> None:
        await svc._connect()
        assert svc._backfill is not None and svc._refresh is not None
        await svc._backfill
        svc._refresh.cancel()

    asyncio.run(run())
    assert (svc.user_id, svc.user_name, svc.connected, svc.as_user) == ("UME", "adrian", True, True)
    assert svc.health()["as_user"] is True and svc.health()["user_id"] == "UME" and svc.health()["channels"] == 1


# ------------------------------------------------------------------ startup backfill
def _hist(ts: str, text: str, user: str = "U1", **over: Any) -> dict[str, Any]:
    return {"type": "message", "user": user, "text": text, "ts": ts, **over}


def test_backfill_feeds_the_digest_without_notifying(tmp_path: Path) -> None:
    svc, bus, _ = make_service(tmp_path, as_user=True)
    r = reader(svc)
    r.conversations = [{"id": "C1", "name": "forge-team", "is_member": True}, {"id": "D1", "is_im": True, "user": "U1"},
                       {"id": "C2", "name": "random", "is_member": True}, {"id": "C3", "name": "not-mine", "is_member": False}]
    r.history = {
        "C1": [_hist("1700007000.000100", "newest"), _hist("1700006000.000100", "mine", user="UME"),
               _hist("1700005000.000100", "from a bot", bot_id="B1"), _hist("1700004000.000100", "older")],
        "D1": [_hist("1700006500.000100", "dm for you")],
        "C2": [],
    }

    async def run() -> int:
        n = await svc.backfill(now=1700008000.0)
        await settle(svc)
        return n

    n = asyncio.run(run())
    assert n == 3 and svc.backfilled == 3
    assert bus.types() == ["notify.channels"]  # no notify.message flood; the first channel list goes out once
    assert [m.text for m in svc.store.latest(10)] == ["newest", "dm for you", "mine", "older"]  # own post kept (is_me)
    assert svc.store.latest(10)[2].is_me is True
    dm = svc.store.latest(10)[1]
    assert (dm.channel_name, dm.is_dm, dm.mentions_me) == ("dm", True, True)  # names resolved like live events
    assert svc.store.latest(10)[0].channel_name == "forge-team"
    conv_calls = r.called("conversations.list")
    assert len(conv_calls) == 2 and conv_calls[0]["types"] == "public_channel,private_channel,im,mpim"
    assert conv_calls[0]["exclude_archived"] is True and conv_calls[0]["limit"] == 200 and conv_calls[1]["cursor"] == "1"
    hist = r.called("conversations.history")
    assert [h["channel"] for h in hist] == ["C1", "D1", "C2"] and all(h["limit"] == 20 for h in hist)  # C3: not a member
    assert hist[0]["oldest"] == f"{1700008000.0 - 7200:.6f}"  # no seen mark: the last two hours
    assert len(r.called("users.list")) == 1
    # the digest counts them; a live duplicate of a backfilled message is dropped
    assert asyncio.run(svc.on_event(event(channel="C1", text="newest", ts="1700007000.000100"))) is None
    assert asyncio.run(svc.digest()).count == 3
    assert [c.id for c in bus.last("notify.channels").channels] == ["D1", "C1", "C2"]  # DM with unread first


def test_backfill_starts_at_each_channels_seen_mark_and_uses_the_bot_without_a_user_token(tmp_path: Path) -> None:
    svc, _, _ = make_service(tmp_path)
    svc.store.add(msg("1700000005.000100"))
    svc.store.mark_seen()
    bot: StubClient = svc._client
    bot.conversations = [{"id": "C1", "name": "forge-team", "is_member": True}]
    bot.history = {"C1": [_hist("1700000006.000100", "after"), _hist("1700000004.000100", "before")]}
    assert asyncio.run(svc.backfill()) == 1
    assert bot.called("conversations.history")[-1]["oldest"] == "1700000005.000100"
    assert [m.text for m in svc.store.unread()] == ["after"]


def test_backfill_rate_limit_waits_once_then_stops(tmp_path: Path, monkeypatch: pytest.MonkeyPatch) -> None:
    svc, _, _ = make_service(tmp_path, as_user=True)
    r = reader(svc)
    r.conversations = [{"id": "C1", "name": "a"}, {"id": "C2", "name": "b"}, {"id": "C3", "name": "c"}]
    r.history = {c: [_hist("1700000009.000100", c)] for c in ("C1", "C2", "C3")}
    r.history_errors = {"C1": [SlackApiError("ratelimited", retry_after="7")],
                        "C2": [SlackApiError("ratelimited", retry_after="3")]}
    slept: list[float] = []

    async def fake_sleep(s: float) -> None:
        slept.append(s)

    monkeypatch.setattr("messaging.slack_client.asyncio.sleep", fake_sleep)
    assert asyncio.run(svc.backfill(now=1700000010.0)) == 1  # C1 after the wait; C2 hit the limit again -> stop; C3 never asked
    assert [s for s in slept if s >= 1] == [7.0]  # (the rest is the notify.channels debounce)
    assert [kw["channel"] for kw in r.called("conversations.history")] == ["C1", "C1", "C2"]


def test_backfill_missing_scope_skips_the_channel(tmp_path: Path, caplog) -> None:
    caplog.set_level(logging.INFO, logger="forge.messaging")
    svc, _, _ = make_service(tmp_path, as_user=True)
    r = reader(svc)
    r.conversations = [{"id": "G1", "is_mpim": True, "name": "mpdm-adrian--hudson-1"}, {"id": "C2", "name": "b"}]
    r.history = {"G1": [_hist("1700000009.000100", "secret")], "C2": [_hist("1700000009.000200", "fine")]}
    r.history_errors = {"G1": [SlackApiError("missing_scope")]}
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

    r = reader(svc)
    r.conversations_list = boom  # type: ignore[method-assign]
    r.users_list_error = RuntimeError("network down")
    svc._handler = Handler()

    async def run() -> int | None:
        await svc._connect()
        assert svc._backfill is not None
        await svc._backfill
        await svc.stop()
        return svc.backfilled

    assert asyncio.run(run()) == 0  # listed nothing, stored nothing, raised nothing


# ------------------------------------------------------------------ channel list
def _workspace(svc: SlackService) -> StubClient:
    r = reader(svc)
    r.conversations = [
        {"id": "C1", "name": "forge-team", "is_member": True, "topic": {"value": "the build"}},
        {"id": "C2", "name": "random", "is_member": True},
        {"id": "C3", "name": "secret", "is_member": True, "is_private": True},
        {"id": "C4", "name": "elsewhere", "is_member": False},
        {"id": "D1", "is_im": True, "user": "U1"},
        {"id": "D2", "is_im": True, "user": "U2"},
        {"id": "G1", "is_mpim": True, "name": "mpdm-adrian--hudson--jane.d-1"},
    ]
    return r


def test_channel_list_shaping_and_sorting(tmp_path: Path) -> None:
    svc, bus, _ = make_service(tmp_path, as_user=True)
    _workspace(svc)
    svc.store.add(msg("1700000001.000100", channel="C2", channel_name="random", text="old news"))
    svc.store.add(msg("1700000005.000100", channel="C1", text="latest in forge", mention=True))
    svc.store.add(msg("1700000003.000100", channel="D2", channel_name="dm", is_dm=True, mention=True, text="hey"))
    svc.store.add(msg("1700000004.000100", channel="G1", channel_name="group-dm", is_dm=True, mention=True, text="grp"))
    svc.store.add(msg("1700000006.000100", channel="D1", channel_name="dm", is_dm=True, text="seen already"))

    async def run() -> list[Any]:
        changed = await svc.refresh_channels()
        svc.store.mark_seen("D1")
        again = await svc.refresh_channels()
        return [changed, again, svc.channel_summaries()]

    changed, again, rows = asyncio.run(run())
    assert changed is True and again is False
    by_id = {r.id: r for r in rows}
    assert [r.id for r in rows] == ["G1", "D2", "D1", "C1", "C2", "C3"]  # DMs with unread first, then last_ts desc
    assert "C4" not in by_id  # not a member
    assert (by_id["C1"].name, by_id["C1"].kind, by_id["C1"].topic, by_id["C1"].is_default) == ("#forge-team", "channel", "the build", True)
    assert (by_id["C3"].name, by_id["C3"].kind, by_id["C3"].is_default) == ("#secret", "private", False)
    assert (by_id["D1"].name, by_id["D1"].kind, by_id["D1"].unread, by_id["D1"].last_text) == ("hudson", "dm", 0, "seen already")
    assert (by_id["D2"].name, by_id["D2"].unread, by_id["D2"].mentions) == ("Jane Doe", 1, 1)   # no display name: real name
    assert (by_id["G1"].name, by_id["G1"].kind) == ("hudson, Jane Doe", "group-dm")              # the others, not me
    assert (by_id["C1"].unread, by_id["C1"].mentions, by_id["C1"].last_ts) == (1, 1, "1700000005.000100")
    assert (by_id["C2"].last_text, by_id["C2"].topic) == ("old news", None)
    assert set(rows[0].model_dump()) == {"id", "name", "kind", "unread", "mentions", "last_ts", "last_text", "topic", "is_default",
                                         "source"}


def test_channel_list_refresh_pushes_only_on_change(tmp_path: Path) -> None:
    svc, bus, _ = make_service(tmp_path, as_user=True)
    r = _workspace(svc)
    svc.channels_refresh_s = 0.02

    async def run() -> None:
        await svc.refresh_channels()
        svc._refresh = asyncio.create_task(svc._refresh_loop())
        await asyncio.sleep(0.05)                      # a couple of refreshes, nothing changed
        r.conversations.append({"id": "C5", "name": "new-room", "is_member": True})
        await asyncio.sleep(0.05)
        await settle(svc)
        await svc.stop()

    asyncio.run(run())
    pushes = [p for t, p in bus.events if t == "notify.channels"]
    assert len(pushes) == 1 and "C5" in [c.id for c in pushes[0].channels]


def test_notify_channels_is_debounced_and_sticky(tmp_path: Path) -> None:
    bus = EventBus()
    svc, _, _ = make_service(tmp_path, as_user=True, bus=bus)
    _workspace(svc)
    assert "notify.channels" in bus.sticky

    class Ws:
        def __init__(self) -> None:
            self.sent: list[dict[str, Any]] = []

        async def send_json(self, body: dict[str, Any]) -> None:
            self.sent.append(body)

    early, late = Ws(), Ws()

    async def run() -> None:
        await bus.attach(early)  # type: ignore[arg-type]
        await svc.refresh_channels()
        for i in range(5):
            await svc.on_event(event(ts=f"170000001{i}.000100", channel="D1", text=f"m{i}"))
        await settle(svc)
        await bus.attach(late)   # type: ignore[arg-type]

    asyncio.run(run())
    kinds = [m["type"] for m in early.sent]
    assert kinds.count("notify.message") == 5 and kinds.count("notify.channels") == 1  # five events, one list push
    assert kinds[-1] == "notify.channels"
    d1 = next(c for c in early.sent[-1]["payload"]["channels"] if c["id"] == "D1")
    assert (d1["unread"], d1["mentions"], d1["last_text"], d1["name"]) == (5, 5, "m4", "hudson")
    assert [m["type"] for m in late.sent] == ["notify.channels"]  # replayed on connect
    assert late.sent[0] == early.sent[-1]


# ------------------------------------------------------------------ history
def _c1_history(r: StubClient) -> None:
    r.history = {"C1": [
        _hist("1700000006.000100", "sixth", reply_count=2, edited={"user": "U1", "ts": "1"}),
        _hist("1700000005.000100", "mine", user="UME"),
        _hist("1700000004.500000", "a reply", thread_ts="1700000004.000100"),   # Slack never returns replies here
        _hist("1700000004.000100", "fourth"),
        _hist("1700000003.000100", "bot", bot_id="B1"),
        _hist("1700000002.000100", "second"),
        _hist("1700000001.000100", "first"),
    ]}


def test_history_pages_and_caches(tmp_path: Path) -> None:
    svc, bus, _ = make_service(tmp_path, as_user=True)
    r = reader(svc)
    _c1_history(r)

    async def run() -> list[Any]:
        first = await svc.history("#forge-team", 3)
        calls = len(r.called("conversations.history"))
        cached = await svc.history("C1", 3)                 # served from the store: no Slack call
        calls2 = len(r.called("conversations.history"))
        older = await svc.history("C1", 2, before="1700000004.000100")
        oldest = await svc.history("C1", 5, before="1700000002.000100")
        await settle(svc)
        return [first, calls, cached, calls2, older, oldest]

    (rec, page, more), calls, (_, cached, more2), calls2, (_, older, more3), (_, oldest, more4) = asyncio.run(run())
    assert (rec.id, rec.label, rec.kind) == ("C1", "#forge-team", "channel")
    assert [m.text for m in page] == ["fourth", "mine", "sixth"] and more is True   # oldest -> newest, no reply, no bot
    assert page[2].reply_count == 2 and page[2].edited is True and page[1].is_me is True and page[0].is_me is False
    assert page[0].user_initials == "HU" and page[1].user_initials == "AD"
    assert r.called("conversations.history")[0] == {"channel": "C1", "limit": 3, "latest": None, "inclusive": False}
    assert calls == 1 and calls2 == 1 and [m.text for m in cached] == ["fourth", "mine", "sixth"] and more2 is True
    assert [m.text for m in older] == ["second"] and more3 is True  # limit 2 of [bot, second, first]: the bot is filtered
    assert r.called("conversations.history")[-2]["latest"] == "1700000004.000100"
    assert [m.text for m in oldest] == ["first"] and more4 is False
    assert len(svc.store.history("C1", 20)) == 5 and svc.store.count("C1") == 5   # merged, no duplicates
    assert bus.types() == ["notify.channels"]


def test_history_errors(tmp_path: Path) -> None:
    svc, _, _ = make_service(tmp_path, as_user=True)
    r = reader(svc)
    r.history_errors = {"C1": [SlackApiError("ratelimited", retry_after="2"), SlackApiError("ratelimited", retry_after="9")]}

    async def run() -> list[Any]:
        out: list[Any] = []
        with pytest.raises(LookupError):
            await svc.history("C999", 5)        # channel_not_found from Slack
        with pytest.raises(RateLimited) as e:
            await svc.history("C1", 5)
        out.append(e.value.retry_after)
        with pytest.raises(LookupError):
            await svc.history("#nowhere", 5)
        return out

    assert asyncio.run(run()) == [9.0]


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
    assert (draft.channel, draft.channel_name, draft.thread_ts, draft.edited) == ("C1", "forge-team", "1700000010.000100", False)
    assert draft.reply_to is not None and draft.reply_to.text == "build ready by 3?"
    assert sent is draft and ts == "1700000099.000100" and svc.open_draft is None
    assert svc._user_client.posted == [{"channel": "C1", "text": "yes, by 3", "thread_ts": "1700000010.000100"}]
    assert svc._client.posted == []  # not as Bit
    assert bus.types() == ["notify.draft", "notify.sent"]
    assert bus.last("notify.sent").model_dump() == {"draft_id": draft.draft_id, "channel": "C1",
                                                    "channel_name": "forge-team", "text": "yes, by 3", "ts": ts,
                                                    "source": "slack"}


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


def test_draft_update_re_emits_with_edited(tmp_path: Path) -> None:
    svc, bus, _ = make_service(tmp_path, as_user=True)
    svc.store.add(msg("1700000010.000100", text="ready?"))
    svc.draft_expiry_s = 0.3

    async def run() -> list[Any]:
        d = await svc.create_draft("yes by 3", reply_to_ts="1700000010.000100")
        await asyncio.sleep(0.2)
        e = await svc.update_draft(d.draft_id, {"text": "  yes, by 3pm  "})
        await asyncio.sleep(0.2)                      # past the original expiry: the edit restarted the timer
        assert svc.open_draft is e
        with pytest.raises(DraftError):
            await svc.update_draft("nope", {"text": "x"})
        with pytest.raises(ValueError):
            await svc.update_draft(d.draft_id, {"text": "   "})
        moved = await svc.update_draft(d.draft_id, {"channel": "Hudson"})      # a person -> their DM; thread dropped
        cleared = await svc.update_draft(d.draft_id, {"thread_ts": None})
        _, ts = await svc.send_draft(d.draft_id)
        return [d, e, moved, cleared, ts]

    d, e, moved, cleared, ts = asyncio.run(run())
    assert (e.draft_id, e.text, e.edited, e.channel, e.thread_ts) == (d.draft_id, "yes, by 3pm", True, "C1", "1700000010.000100")
    assert e.reply_to is not None
    assert (moved.channel, moved.channel_name, moved.thread_ts, moved.reply_to) == ("DU1", "hudson", None, None)
    assert cleared.thread_ts is None and cleared.edited is True
    assert svc._user_client.posted == [{"channel": "DU1", "text": "yes, by 3pm", "thread_ts": None}]
    assert bus.types() == ["notify.draft"] * 4 + ["notify.sent"]
    assert [p.draft_id for t, p in bus.events if t == "notify.draft"] == [d.draft_id] * 4
    assert [p.edited for t, p in bus.events if t == "notify.draft"] == [False, True, True, True]


# ------------------------------------------------------------------ resolving targets
def test_resolve_channel_by_id_name_or_person(tmp_path: Path) -> None:
    svc, _, _ = make_service(tmp_path, as_user=True)
    r = _workspace(svc)

    async def run() -> list[Any]:
        out = [
            await svc.resolve_channel("C1"), await svc.resolve_channel("#random"), await svc.resolve_channel("Random"),
            await svc.resolve_channel("C0123ABCD"),            # an id we don't know: taken as is
            await svc.resolve_channel("Hudson"),               # person with an existing DM
            await svc.resolve_channel("@hudson"),
            await svc.resolve_channel("Jane Doe"),             # real name
            await svc.resolve_channel("jane"),                 # first name
            await svc.resolve_channel(None),                   # SLACK_DEFAULT_CHANNEL
        ]
        r.users["U3"] = {"id": "U3", "name": "dan", "real_name": "Dan K", "profile": {"display_name": "Dan"}}
        svc._users_loaded = False                              # (a new teammate: users.list runs again on a miss)
        out.append(await svc.resolve_channel("Dan"))           # no DM yet -> conversations.open
        out.append(await svc.resolve_channel("dan"))           # now known: no second open
        for bad in ("#hudson", "nobody", "#nowhere"):
            with pytest.raises(LookupError):
                await svc.resolve_channel(bad)
        return out

    out = asyncio.run(run())
    assert out == ["C1", "C2", "C2", "C0123ABCD", "D1", "D1", "D2", "D2", "C1", "DU3", "DU3"]
    assert r.called("conversations.open") == [{"users": "U3"}]
    assert svc._channels["DU3"].label == "Dan" and svc._channels["DU3"].kind == "dm"


def test_send_tool_to_a_person_drafts_into_their_dm(tmp_path: Path) -> None:
    svc, bus, _ = make_service(tmp_path, as_user=True)
    r = reader(svc)  # only #forge-team listed: the DM has to be opened

    async def run() -> list[Any]:
        drafted = await mtools.call_tool("messages_send", {"text": "lunch at 1?", "channel": "Hudson"}, svc)
        sent = await mtools.call_tool("messages_confirm", {"send": True}, svc)
        return [drafted, sent]

    drafted, sent = asyncio.run(run())
    assert drafted["status"] == "awaiting_confirmation" and drafted["channel_name"] == "hudson"
    assert r.called("conversations.open") == [{"users": "U1"}]
    assert svc._user_client.posted == [{"channel": "DU1", "text": "lunch at 1?", "thread_ts": None}]
    assert sent["channel_name"] == "hudson"
    assert bus.last("notify.draft").channel == "DU1"


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
    assert {t["name"] for t in mtools.TOOLS} == {"messages_read_latest", "messages_list_channels", "messages_read_channel",
                                                 "messages_send", "messages_confirm"}
    assert all(t["name"].startswith("messages_") for t in mtools.TOOLS)  # -> the `messages` panel (agent/tools.py PANELS)


def test_tools_list_channels_and_read_channel(tmp_path: Path) -> None:
    svc, _, _ = make_service(tmp_path, as_user=True)
    r = _workspace(svc)
    _c1_history(r)
    svc.store.add(msg("1700000003.000100", channel="D1", channel_name="dm", is_dm=True, mention=True, text="ping"))

    async def run() -> list[dict[str, Any]]:
        return [
            await mtools.call_tool("messages_list_channels", {}, svc),
            await mtools.call_tool("messages_read_channel", {"channel": "forge-team", "n": 2}, svc),
            await mtools.call_tool("messages_read_channel", {"channel": "Hudson"}, svc),
            await mtools.call_tool("messages_read_channel", {"channel": ""}, svc),
            await mtools.call_tool("messages_read_channel", {"channel": "#nowhere"}, svc),
        ]

    listed, forge, dm, empty, missing = asyncio.run(run())
    assert listed["channels"][0] == {"id": "D1", "name": "hudson", "kind": "dm", "unread": 1, "mentions": 1, "last_text": "ping",
                                     "source": "slack", "last_ts": "1700000003.000100"}
    assert {c["name"] for c in listed["channels"]} == {"hudson", "Jane Doe", "hudson, Jane Doe", "#forge-team", "#random", "#secret"}
    assert (forge["channel"], forge["channel_name"]) == ("C1", "#forge-team")
    assert [m["text"] for m in forge["messages"]] == ["mine", "sixth"] and forge["messages"][0]["is_me"] is True
    assert "reply_count" not in forge["messages"][0] and forge["messages"][1]["reply_count"] == 2  # nulls dropped for the model
    assert (dm["channel"], dm["kind"], [m["text"] for m in dm["messages"]]) == ("D1", "dm", ["ping"])
    assert "error" in empty and "error" in missing


def test_tools_disabled(tmp_path: Path) -> None:
    svc, _, _ = make_service(tmp_path, enabled=False)
    assert "error" in asyncio.run(mtools.call_tool("messages_read_latest", {}, svc))
    assert "error" in asyncio.run(mtools.call_tool("messages_list_channels", {}, svc))


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
    for path in ("/messages/latest", "/messages/channels", "/messages/history?channel=C1", "/messages/users/me"):
        assert c.get(path).status_code == 503, path
    assert c.post("/messages/drafts", json={"text": "hi"}).status_code == 503
    assert c.patch("/messages/drafts/abc", json={"text": "hi"}).status_code == 503
    assert c.post("/messages/drafts/abc/send").status_code == 503
    assert c.post("/messages/channels/C1/seen").status_code == 503
    assert c.post("/messages/send", json={"text": "hi"}).status_code in (404, 405)  # no direct send endpoint
    r = c.post("/messages/digest")
    assert r.status_code == 200
    assert r.json() == {"count": 0, "mentions": 0, "line": "You have no new messages today.", "messages": []}


def test_router_enabled_draft_flow(api) -> None:
    c = api(True)
    svc: SlackService = c.app.state.slack
    svc.store.add(msg("1700000001.000100", text="first", mention=True))
    r = c.get("/messages/latest?n=5")
    assert r.status_code == 200 and r.json()[0]["text"] == "first"
    assert set(r.json()[0]) == {"channel", "channel_name", "is_dm", "user", "user_name", "text", "ts", "thread_ts", "mentions_me",
                                "reply_count", "edited", "is_me", "user_initials", "source"}

    r = c.post("/messages/drafts", json={"text": "hey", "reply_to_ts": "1700000001.000100"})
    assert r.status_code == 200
    draft = r.json()
    assert set(draft) == {"draft_id", "channel", "channel_name", "text", "thread_ts", "reply_to", "edited", "source"}
    assert draft["channel"] == "C1" and draft["thread_ts"] == "1700000001.000100" and draft["reply_to"]["text"] == "first"
    assert draft["edited"] is False
    assert svc._user_client.posted == []

    assert c.post("/messages/drafts", json={"text": "   "}).status_code == 422
    assert c.post("/messages/drafts", json={"channel": "#missing", "text": "x"}).status_code == 404
    # that 404 did not disturb the open draft; the 422 neither
    assert svc.open_draft is not None and svc.open_draft.draft_id == draft["draft_id"]

    r = c.patch(f"/messages/drafts/{draft['draft_id']}", json={"text": "hey there"})
    assert r.status_code == 200 and r.json()["text"] == "hey there" and r.json()["edited"] is True
    assert r.json()["draft_id"] == draft["draft_id"] and r.json()["thread_ts"] == "1700000001.000100"
    assert c.patch(f"/messages/drafts/{draft['draft_id']}", json={"text": "  "}).status_code == 422
    assert c.patch("/messages/drafts/zzz", json={"text": "x"}).status_code == 404
    assert c.patch(f"/messages/drafts/{draft['draft_id']}", json={"channel": "#missing"}).status_code == 404

    assert c.post(f"/messages/drafts/{draft['draft_id']}/cancel").status_code == 200
    assert c.post(f"/messages/drafts/{draft['draft_id']}/send").status_code == 404  # gone
    assert c.patch(f"/messages/drafts/{draft['draft_id']}", json={"text": "x"}).status_code == 404

    d2 = c.post("/messages/drafts", json={"channel": "C1", "text": "take two"}).json()
    r = c.post(f"/messages/drafts/{d2['draft_id']}/send")
    assert r.status_code == 200 and r.json() == {"ts": "1700000099.000100"}
    assert svc._user_client.posted == [{"channel": "C1", "text": "take two", "thread_ts": None}]
    assert c.post(f"/messages/drafts/{d2['draft_id']}/cancel").status_code == 404

    d3 = c.post("/messages/drafts", json={"channel": "Hudson", "text": "dm by name"}).json()  # a person -> their DM
    assert (d3["channel"], d3["channel_name"]) == ("DU1", "hudson")
    c.post(f"/messages/drafts/{d3['draft_id']}/cancel")

    r = c.post("/messages/digest", json={"pet_id": "bit"})
    assert r.status_code == 200 and r.json()["line"] == "You have 1 new message, and it mentions you."

    r = c.post("/messages/focus", json={"pet_id": "bit", "channel": "C1", "ts": "1700000001.000100"})
    assert r.status_code == 200 and r.json()["text"] == "first" and svc.focused is not None
    assert c.post("/messages/focus", json={"channel": "C1", "ts": "nope"}).status_code == 404
    assert api(False).post("/messages/focus", json={"channel": "C1", "ts": "1"}).status_code == 503


def test_router_panel_endpoints(api) -> None:
    c = api(True)
    svc: SlackService = c.app.state.slack
    r = _workspace(svc)
    _c1_history(r)
    svc.store.add(msg("1700000003.000100", channel="D1", channel_name="dm", is_dm=True, mention=True, text="ping"))

    res = c.get("/messages/channels")
    assert res.status_code == 200
    rows = res.json()["channels"]
    assert [row["id"] for row in rows][:1] == ["D1"] and {row["id"] for row in rows} == {"C1", "C2", "C3", "D1", "D2", "G1"}
    assert rows[0] == {"id": "D1", "name": "hudson", "kind": "dm", "unread": 1, "mentions": 1, "last_ts": "1700000003.000100",
                       "last_text": "ping", "topic": None, "is_default": False, "source": "slack"}

    res = c.get("/messages/history?channel=C1&limit=2")
    assert res.status_code == 200
    body = res.json()
    assert (body["channel"], body["channel_name"], body["kind"], body["has_more"]) == ("C1", "#forge-team", "channel", True)
    assert [m["text"] for m in body["messages"]] == ["mine", "sixth"]
    assert body["messages"][1]["reply_count"] == 2 and body["messages"][0]["is_me"] is True
    res = c.get("/messages/history?channel=C1&limit=5&before=1700000004.000100")
    assert res.status_code == 200 and [m["text"] for m in res.json()["messages"]] == ["first", "second"]
    assert res.json()["has_more"] is False
    assert c.get("/messages/history?channel=C1&limit=0").status_code == 422
    assert c.get("/messages/history?channel=C1&limit=101").status_code == 422
    assert c.get("/messages/history").status_code == 422
    assert c.get("/messages/history?channel=C999").status_code == 404
    r.history_errors = {"C2": [SlackApiError("ratelimited", retry_after="1"), SlackApiError("ratelimited", retry_after="4")]}
    res = c.get("/messages/history?channel=C2")
    assert res.status_code == 503 and res.json()["detail"]["error"] == "ratelimited" and res.json()["detail"]["retry_after"] == 4.0
    r.history_errors = {"C2": [SlackApiError("internal_error")]}
    assert c.get("/messages/history?channel=C2").status_code == 502

    res = c.post("/messages/channels/D1/seen")
    assert res.status_code == 200 and res.json() == {"ok": True, "unread": 0}
    d1 = next(row for row in c.get("/messages/channels").json()["channels"] if row["id"] == "D1")
    assert d1["unread"] == 0 and d1["last_text"] == "ping"
    assert c.post("/messages/channels/nowhere/seen").status_code == 404

    res = c.get("/messages/users/me")
    assert res.status_code == 200 and res.json() == {"user_id": "UME", "name": "adrian", "display_name": "adrian"}
