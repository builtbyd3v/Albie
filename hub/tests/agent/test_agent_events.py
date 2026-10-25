from __future__ import annotations

import pytest

from agent import events as ev
from agent.events import Event
from agent.turn import classify_mode


@pytest.mark.parametrize(
    "event",
    [
        ev.pet_state("bit", "thinking"),
        ev.partial_answer("bit", "The handler is in"),
        ev.task_progress("bit", "t1", "Editing hub/agent/ws.py", pct=0.5),
        ev.task_complete("bit", "t1", "Done", branch="pet/x-1"),
        ev.task_complete("bit", "t1", "Done"),
        ev.error("bit", "nope"),
        ev.stt_final("bit", "hello"),
    ],
)
def test_round_trip(event: Event) -> None:
    back = Event.model_validate_json(event.to_json())
    assert back == event
    ev.parse_payload(back)  # validates against the payload model


def test_optional_fields_omitted() -> None:
    assert ev.task_complete("bit", "t1", "Done").payload == {"pet_id": "bit", "task_id": "t1", "summary": "Done"}
    assert "target_panel" not in ev.pet_state("bit", "idle").payload


def test_client_messages_parse() -> None:
    ask = ev.parse_payload(Event(type="agent.ask", payload={"pet_id": "bit", "project_id": "p", "text": "hi", "mode": "qa", "extra": 1}))
    assert isinstance(ask, ev.AgentAsk) and ask.mode == "qa"
    with pytest.raises(ValueError):
        ev.parse_payload(Event(type="nope.nope"))


def test_every_proposed_event_has_pet_state() -> None:
    proposed = [t for t in ev.HUB_MESSAGES if not t.startswith("pet.")]
    assert set(proposed) == set(ev.PET_STATE_FOR_EVENT)


@pytest.mark.parametrize(
    ("text", "mode"),
    [("fix the build", "code"), ("Can you add a test for ws", "code"), ("where is the bus?", "qa"),
     ("what does make_stt do", "qa"), ("please refactor runner", "code")],
)
def test_classify(text: str, mode: str) -> None:
    assert classify_mode(text, "auto") == mode


class _Target:
    def __init__(self) -> None:
        self.sent: list[Event] = []

    async def broadcast(self, e: Event) -> None:
        self.sent.append(e)


def test_emitter_companion_follows_the_mode_but_not_an_empty_final() -> None:
    """pet.state listening goes out with a transcript, once per mode change; a dropped turn's empty stt.final
    (look-to-talk, a tap, stt.cancel) changes nothing."""
    import asyncio

    from agent.bus import Emitter

    target = _Target()
    emit = Emitter("bit", target=target)

    async def go() -> None:
        await emit(ev.stt_final("bit", ""))
        await emit(ev.stt_partial("bit", "hey"))
        await emit(ev.stt_partial("bit", "hey bit"))
        await emit(ev.stt_final("bit", "hey bit"))
        await emit(ev.thinking("bit"))
        await emit(ev.stt_final("bit", "   "))

    asyncio.run(go())
    assert [(e.type, e.payload.get("mode")) for e in target.sent] == [
        ("stt.final", None),
        ("pet.state", "listening"), ("stt.partial", None), ("stt.partial", None), ("stt.final", None),
        ("pet.state", "thinking"), ("agent.thinking", None),
        ("stt.final", None),
    ]


def test_emitter_without_companions_sends_only_the_event() -> None:
    import asyncio

    from agent.bus import Emitter

    target = _Target()
    emit = Emitter("bit", target=target, companion=False)
    asyncio.run(emit(ev.stt_partial("bit", "hey bit")))
    asyncio.run(emit(ev.pet_state("bit", "listening")))
    assert [e.type for e in target.sent] == ["stt.partial", "pet.state"]


def test_a_voice_transcript_after_the_commit_keeps_him_thinking() -> None:
    """Lab 2026-10-25: thinking (commit) -> listening (late stt.final) -> thinking flickered on every voice turn."""
    import asyncio

    from agent.bus import Emitter

    target = _Target()
    emit = Emitter("bit", target=target)

    async def go() -> None:
        await emit(ev.stt_partial("bit", "pause the"))
        await emit(ev.thinking("bit"))
        await emit(ev.stt_final("bit", "Pause the movie.", voice=True))

    asyncio.run(go())
    assert [(e.type, e.payload.get("mode")) for e in target.sent if e.type == "pet.state"] == [
        ("pet.state", "listening"), ("pet.state", "thinking")]
