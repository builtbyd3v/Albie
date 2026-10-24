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
