"""Say a gator's name (agent/wake.py): parsing, the Grok wake turn, and wake turns over /ws."""

from __future__ import annotations

import asyncio
import json
from typing import Any

import pytest
from fastapi.testclient import TestClient

from agent.crew import crew
from agent.events import Event
from agent.wake import WakeRouter, parse

from .test_agent_voice import MockGrok, Sink, _session, _settle


@pytest.fixture(autouse=True)
def fresh_crew() -> None:
    crew.members.clear()
    crew.project_id = None
    crew._roster_seen = False
    crew._changed = asyncio.Event()
    crew._listeners.clear()


@pytest.mark.parametrize("text, who, rest", [
    ("Hey Albie, what's in ws.py?", "bit", "what's in ws.py?"),
    ("albee what time is it", "bit", "what time is it"),
    ("Al bee, what's in ws.py?", "bit", "what's in ws.py?"),
    ("Hey al be what time is it", "bit", "what time is it"),
    ("ok Al B, run it", "bit", "run it"),
    ("Hey Elbie", "bit", ""),
    ("thanks, Alby", "bit", "thanks"),
    ("Hey Bit, what's in ws.py?", None, "Hey Bit, what's in ws.py?"),
    ("a bit more", None, "a bit more"),
    ("Okay Tester, run the hub tests.", "tester", "run the hub tests."),
    ("Hey Scout", "scout", ""),
    ("Skipper, write the changelog", "shipper", "write the changelog"),
    ("can you check the diff, Reviewer?", "reviewer", "can you check the diff"),
    ("wait a bit", None, "wait a bit"),
    ("I'm a bit tired, Albie", "bit", "I'm a bit tired"),
    ("let's grab lunch", None, "let's grab lunch"),
    ("", None, ""),
])
def test_parse(text: str, who: str | None, rest: str) -> None:
    assert parse(text) == (who, rest)


class Recorder:
    def __init__(self) -> None:
        self.events: list[Event] = []
        self.owner: list[Event] = []
        self.jobs: list[Any] = []

    def emit_for(self, pid: str):
        async def emit(e: Event) -> None:
            self.events.append(e)
        return emit

    async def send_owner(self, e: Event) -> None:
        self.owner.append(e)

    def router(self) -> WakeRouter:
        return WakeRouter(lead_pet_id="bit", emit_for=self.emit_for, send_owner=self.send_owner,
                          spawn=lambda coro: self.jobs.append(coro))


def test_router_keeps_room_chatter_silent() -> None:
    r = Recorder()
    router = r.router()

    async def go() -> None:
        await router.partial("so anyway the")
        d = await router.final("so anyway the demo is at five")
        assert d.pet_id is None and not d.respond

    asyncio.run(go())
    assert r.events == [] and r.owner == []


def test_router_hey_albie_alone_opens_a_listen() -> None:
    r = Recorder()
    d = asyncio.run(r.router().final("Hey Albie"))
    assert d.pet_id == "bit" and not d.respond
    assert [(e.type, e.payload) for e in r.owner] == [("wake.listen", {"pet_id": "bit"})]


def test_router_crew_target_from_a_previous_listen() -> None:
    r = Recorder()
    router = r.router()
    router.begin("tester")

    async def go() -> None:
        await router.partial("run the")
        d = await router.final("run the tests")
        assert d.pet_id == "tester" and d.rest == "run the tests" and not d.respond
        for job in r.jobs:
            job.close()  # the assign coroutine (no hub here)

    asyncio.run(go())
    assert [(e.type, e.payload["pet_id"]) for e in r.events] == [("stt.partial", "tester"), ("pet.state", "tester"), ("stt.final", "tester")]
    assert r.events[1].payload["mode"] == "listening"


def _facing_final(text: str, facing: bool) -> tuple[Any, Recorder]:
    r = Recorder()
    router = r.router()
    router.begin(None, facing=facing)

    async def go() -> Any:
        d = await router.final(text)
        for job in r.jobs:
            job.close()  # crew coroutines (no hub here)
        return d

    return asyncio.run(go()), r


def test_router_facing_without_name_goes_to_the_lead() -> None:
    d, r = _facing_final("And we can open the email panel.", facing=True)
    assert d.pet_id == "bit" and d.respond and d.rest == "And we can open the email panel."
    assert [(e.type, e.payload.get("mode") or e.payload.get("text")) for e in r.events] == \
        [("pet.state", "listening"), ("stt.final", "And we can open the email panel.")]


def test_router_looking_away_without_name_is_dropped() -> None:
    d, r = _facing_final("And we can open the email panel.", facing=False)
    assert d.pet_id is None and not d.respond and r.events == []


def test_router_facing_named_routes_as_before() -> None:
    d, _ = _facing_final("Hey Albie, how's the build?", facing=True)
    assert d.pet_id == "bit" and d.respond and d.rest == "how's the build?"
    d, r = _facing_final("Hey Albie", facing=True)
    assert d.pet_id == "bit" and not d.respond
    assert [e.type for e in r.owner] == ["wake.listen"]


def test_router_facing_crew_name_goes_to_the_crew() -> None:
    d, _ = _facing_final("Tester, run the hub tests", facing=True)
    assert d.pet_id == "tester" and d.rest == "run the hub tests" and not d.respond


def test_router_facing_empty_turn_is_dropped() -> None:  # a "stop" turn reaches the router as an empty final
    d, r = _facing_final("", facing=True)
    assert d.pet_id is None and not d.respond and r.events == [] and r.owner == []


def test_router_facing_is_per_turn() -> None:
    r = Recorder()
    router = r.router()
    router.begin(None, facing=True)

    async def go() -> None:
        assert (await router.final("open the email panel")).respond
        router.begin(None)
        assert not (await router.final("open the email panel")).respond

    asyncio.run(go())


def test_grok_wake_turn_answers_only_when_named() -> None:
    async def run() -> None:
        grok, sink = MockGrok(), Sink()
        s = _session(grok, sink)
        r = Recorder()
        s.wake_router = r.router()
        T = "conversation.item.input_audio_transcription"

        # Room chatter: no stt.*, no response, the item is deleted.
        await s.begin_turn(wake=True)
        await s.feed(b"\x00\x00" * 3200)
        await s.commit_turn()
        grok.push(type="input_audio_buffer.committed", item_id="item_1")
        grok.push(type=f"{T}.updated", transcript="lunch at")
        grok.push(type=f"{T}.completed", transcript="lunch at noon?")
        await _settle()
        assert "response.create" not in grok.types()
        assert grok.sent[-1] == {"type": "conversation.item.delete", "item_id": "item_1"}
        assert sink.log == [] and r.events == []

        # Named: Albie's stt.final goes out, then Grok is asked to respond.
        await s.begin_turn(wake=True)
        await s.feed(b"\x00\x00" * 3200)
        await s.commit_turn()
        grok.push(type="input_audio_buffer.committed", item_id="item_2")
        grok.push(type=f"{T}.completed", transcript="Hey Albie, how's the build?")
        await _settle()
        assert grok.types()[-1] == "response.create"
        assert [(e.type, e.payload.get("text") or e.payload.get("mode")) for e in r.events] == \
            [("pet.state", "listening"), ("stt.final", "Hey Albie, how's the build?")]
        assert sink.flat() == ["emit:agent.thinking"]
        await s.close()

    asyncio.run(run())


def _hello(ws: Any) -> None:
    ws.send_json({"type": "hello", "payload": {"client": "unity", "pet_id": "bit", "project_id": "demo"}})


def test_fake_voice_wake_turn_over_ws(client: TestClient, monkeypatch: pytest.MonkeyPatch) -> None:
    """The fake transcript is "Hey Albie, are you there?": a wake turn that names Albie gets the spoken reply."""
    monkeypatch.setenv("VOICE_PROVIDER", "fake")
    monkeypatch.setenv("FAKE_VOICE_PACE", "0")
    with client.websocket_connect("/ws") as ws:
        _hello(ws)
        assert ws.receive_json()["type"] == "voice.ready"
        ws.send_json({"type": "stt.start", "payload": {"pet_id": "bit", "wake": True}})
        ws.send_bytes(b"\x00\x00" * 3200)
        ws.send_json({"type": "stt.stop", "payload": {"pet_id": "bit"}})
        seen: list[str] = []
        while "tts.end" not in seen and len(seen) < 200:
            msg = ws.receive()
            if msg.get("text"):
                seen.append(json.loads(msg["text"])["type"])
    assert seen.index("stt.partial") < seen.index("stt.final") < seen.index("tts.start")  # named, so captioned


def test_text_path_wake_turn_routes_to_the_crew(client: TestClient, monkeypatch: pytest.MonkeyPatch) -> None:
    """No voice session: the stt.py transcript is routed the same way (here: to a crew member's runner turn)."""
    from agent import stt as stt_mod

    class Canned:
        def feed(self, pcm16: bytes) -> None: ...
        def partial(self) -> None: return None
        def finish(self) -> str: return "Scout, where does greet live?"

    monkeypatch.setenv("VOICE_PROVIDER", "none")
    monkeypatch.setattr("agent.ws.make_stt", lambda repo=None: Canned())
    assert stt_mod  # imported for the monkeypatch target's module
    with client.websocket_connect("/ws") as ws:
        _hello(ws)
        ws.send_json({"type": "crew.roster", "payload": {"members": [{"pet_id": "scout", "agent_type": "scout"}]}})
        ws.send_json({"type": "stt.start", "payload": {"pet_id": "bit", "wake": True}})
        ws.send_bytes(b"\x00\x00" * 3200)
        ws.send_json({"type": "stt.stop", "payload": {"pet_id": "bit"}})
        got: list[tuple[str, str]] = []
        while ("scout", "agent.answer_done") not in got and len(got) < 60:
            m = ws.receive_json()
            got.append((m["payload"].get("pet_id"), m["type"]))
    assert ("scout", "stt.final") in got
    assert not any(p == "bit" and t.startswith(("agent.", "stt.")) for p, t in got)


def test_ws_wake_turn_sends_no_pet_state_before_it_is_decided() -> None:
    """Over ws.py the router's Emitter has no companions: a partial for a named gator is just stt.partial, and
    pet.state listening appears only with the decided final."""
    from agent.bus import Emitter

    class Target:
        def __init__(self) -> None:
            self.sent: list[Event] = []

        async def broadcast(self, e: Event) -> None:
            self.sent.append(e)

    target = Target()
    owner: list[Event] = []

    async def send_owner(e: Event) -> None:
        owner.append(e)

    router = WakeRouter(lead_pet_id="bit", emit_for=lambda pid: Emitter(pid, target=target, companion=False),
                        send_owner=send_owner, spawn=lambda coro: coro.close())

    async def go() -> None:
        await router.partial("Hey Albie, how")
        assert [e.type for e in target.sent] == ["stt.partial"]
        d = await router.final("Hey Albie, how's the build?")
        assert d.respond

    asyncio.run(go())
    assert [(e.type, e.payload.get("mode")) for e in target.sent] == \
        [("stt.partial", None), ("pet.state", "listening"), ("stt.final", None)]
