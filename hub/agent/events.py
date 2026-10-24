"""WebSocket message models for the pet <-> agent contract.

Source of truth: docs/PET_AGENT_CONTRACT.md. Every frame is
``{"type": "...", "payload": {...}}``; binary frames are raw PCM16 audio.

Hub -> client events come in two flavours:
* existing contract: ``pet.state``, ``pet.say``
* proposed (hub/agent): ``stt.*``, ``agent.*``, ``tts.*`` and ``voice.ready``

Speech audio (``tts.start`` -> binary PCM16 frames -> ``tts.end``) goes only to the
socket that owns the voice session, never broadcast (see agent/voice.py).

Every proposed ``agent.*`` event is preceded by a companion ``pet.state``
(see ``PET_STATE_FOR_EVENT``) so the pet still animates if the proposed events
are rejected. The companion is only sent when the mode changes (see
``agent.bus.Emitter``) so partial answers don't spam it.
"""

from __future__ import annotations

from typing import Any, Literal

from pydantic import BaseModel, ConfigDict, Field

AskMode = Literal["qa", "code", "auto"]
PetMode = Literal["follow", "idle", "listening", "thinking", "working", "speaking"]


class Event(BaseModel):
    """Envelope for every JSON frame in both directions."""

    type: str
    payload: dict[str, Any] = Field(default_factory=dict)

    def to_json(self) -> str:
        return self.model_dump_json()


class _Payload(BaseModel):
    # Ignore unknown fields from clients so additive contract changes don't break us.
    model_config = ConfigDict(extra="ignore")


# ---------------------------------------------------------------- client -> hub
class Hello(_Payload):
    client: str = "unity"
    pet_id: str = "bit"
    project_id: str | None = None
    audio_out_rate: int | None = None  # proposed: the client's output sample rate (voice mode)


class AgentAsk(_Payload):
    pet_id: str | None = None  # falls back to the session's hello values
    project_id: str | None = None
    text: str
    mode: AskMode | None = None  # None -> project default_mode; "auto" -> hub picks


class AgentCancel(_Payload):
    pet_id: str | None = None


class SttStart(_Payload):
    pet_id: str | None = None
    sample_rate: int = 16000
    encoding: Literal["pcm16"] = "pcm16"
    require_name: bool = False  # proposed: look-to-talk turn opened while the user wasn't facing Bit


class SttStop(_Payload):
    pet_id: str | None = None


class SttCancel(_Payload):  # client -> hub: drop the open turn (no answer); its stt.stop gets an empty final
    pet_id: str | None = None


class TtsStopRequest(_Payload):  # client -> hub: stop the current utterance
    pet_id: str | None = None


CLIENT_MESSAGES: dict[str, type[_Payload]] = {
    "hello": Hello,
    "agent.ask": AgentAsk,
    "agent.cancel": AgentCancel,
    "stt.start": SttStart,
    "stt.stop": SttStop,
    "stt.cancel": SttCancel,
    "tts.stop": TtsStopRequest,
}


# ---------------------------------------------------------------- hub -> client
class CardAction(_Payload):
    label: str
    action_id: str


class Card(_Payload):
    title: str
    bullets: list[str] = Field(default_factory=list)
    actions: list[CardAction] | None = None


class PetState(_Payload):
    pet_id: str
    mode: PetMode
    target_panel: str | None = None


class PetSay(_Payload):
    pet_id: str
    text: str
    card: Card | None = None


class SttText(_Payload):  # stt.partial / stt.final
    pet_id: str
    text: str
    voice: bool | None = None  # stt.final only: true = the voice session answers this turn, don't agent.ask it


class AgentThinking(_Payload):
    pet_id: str


class AgentText(_Payload):  # agent.partial_answer (cumulative) / agent.answer_done
    pet_id: str
    text: str


class TaskStarted(_Payload):
    pet_id: str
    task_id: str
    title: str


class TaskProgress(_Payload):
    pet_id: str
    task_id: str
    text: str
    pct: float | None = None


class TaskComplete(_Payload):
    pet_id: str
    task_id: str
    summary: str
    branch: str | None = None


class AgentError(_Payload):
    pet_id: str
    message: str


class AgentToolCall(_Payload):  # the voice model called a tool (debug caption / pet animation)
    pet_id: str
    call_id: str
    name: str
    args: dict[str, Any] = Field(default_factory=dict)


class VoiceReady(_Payload):  # hub -> owning socket after hello when VOICE_PROVIDER != none
    pet_id: str
    provider: str
    sample_rate: int


class TtsStart(_Payload):
    pet_id: str
    utterance_id: str
    sample_rate: int
    encoding: Literal["pcm16"] = "pcm16"
    channels: int = 1


class TtsEnd(_Payload):
    pet_id: str
    utterance_id: str


class TtsStop(_Payload):
    pet_id: str
    utterance_id: str
    reason: Literal["barge_in", "cancel", "error"] = "cancel"


HUB_MESSAGES: dict[str, type[_Payload]] = {
    "pet.state": PetState,
    "pet.say": PetSay,
    "stt.partial": SttText,
    "stt.final": SttText,
    "agent.thinking": AgentThinking,
    "agent.partial_answer": AgentText,
    "agent.answer_done": AgentText,
    "agent.task_started": TaskStarted,
    "agent.task_progress": TaskProgress,
    "agent.task_complete": TaskComplete,
    "agent.error": AgentError,
    "agent.tool_call": AgentToolCall,
}

# Sent to the voice session's own socket only (bus.send, not the Emitter), so no pet.state companion:
# the agent.* events that accompany speech already carry it.
VOICE_MESSAGES: dict[str, type[_Payload]] = {
    "voice.ready": VoiceReady,
    "tts.start": TtsStart,
    "tts.end": TtsEnd,
    "tts.stop": TtsStop,
}

# Companion pet.state mode for each proposed event (existing-contract fallback).
PET_STATE_FOR_EVENT: dict[str, PetMode] = {
    "stt.partial": "listening",
    "stt.final": "listening",
    "agent.thinking": "thinking",
    "agent.partial_answer": "speaking",
    "agent.answer_done": "speaking",
    "agent.task_started": "working",
    "agent.task_progress": "working",
    "agent.task_complete": "idle",
    "agent.error": "idle",
    "agent.tool_call": "thinking",
}


def make(type_: str, payload: _Payload) -> Event:
    """Build an Event from a typed payload, dropping None-valued optionals."""
    return Event(type=type_, payload=payload.model_dump(exclude_none=True))


def parse_payload(event: Event) -> _Payload:
    """Validate an incoming or outgoing event against its payload model."""
    model = CLIENT_MESSAGES.get(event.type) or HUB_MESSAGES.get(event.type) or VOICE_MESSAGES.get(event.type)
    if model is None:
        raise ValueError(f"unknown event type: {event.type}")
    return model.model_validate(event.payload)


# ------------------------------------------------------------- builder helpers
def pet_state(pet_id: str, mode: PetMode, target_panel: str | None = None) -> Event:
    return make("pet.state", PetState(pet_id=pet_id, mode=mode, target_panel=target_panel))


def pet_say(pet_id: str, text: str, card: Card | None = None) -> Event:
    return make("pet.say", PetSay(pet_id=pet_id, text=text, card=card))


def stt_partial(pet_id: str, text: str) -> Event:
    return make("stt.partial", SttText(pet_id=pet_id, text=text))


def stt_final(pet_id: str, text: str, voice: bool | None = None) -> Event:
    return make("stt.final", SttText(pet_id=pet_id, text=text, voice=voice))


def thinking(pet_id: str) -> Event:
    return make("agent.thinking", AgentThinking(pet_id=pet_id))


def partial_answer(pet_id: str, text: str) -> Event:
    return make("agent.partial_answer", AgentText(pet_id=pet_id, text=text))


def answer_done(pet_id: str, text: str) -> Event:
    return make("agent.answer_done", AgentText(pet_id=pet_id, text=text))


def task_started(pet_id: str, task_id: str, title: str) -> Event:
    return make("agent.task_started", TaskStarted(pet_id=pet_id, task_id=task_id, title=title))


def task_progress(pet_id: str, task_id: str, text: str, pct: float | None = None) -> Event:
    return make(
        "agent.task_progress", TaskProgress(pet_id=pet_id, task_id=task_id, text=text, pct=pct)
    )


def task_complete(pet_id: str, task_id: str, summary: str, branch: str | None = None) -> Event:
    return make(
        "agent.task_complete",
        TaskComplete(pet_id=pet_id, task_id=task_id, summary=summary, branch=branch),
    )


def error(pet_id: str, message: str) -> Event:
    return make("agent.error", AgentError(pet_id=pet_id, message=message))


def tool_call(pet_id: str, call_id: str, name: str, args: dict[str, Any]) -> Event:
    return make("agent.tool_call", AgentToolCall(pet_id=pet_id, call_id=call_id, name=name, args=args))


def voice_ready(pet_id: str, provider: str, sample_rate: int) -> Event:
    return make("voice.ready", VoiceReady(pet_id=pet_id, provider=provider, sample_rate=sample_rate))


def tts_start(pet_id: str, utterance_id: str, sample_rate: int) -> Event:
    return make("tts.start", TtsStart(pet_id=pet_id, utterance_id=utterance_id, sample_rate=sample_rate))


def tts_end(pet_id: str, utterance_id: str) -> Event:
    return make("tts.end", TtsEnd(pet_id=pet_id, utterance_id=utterance_id))


def tts_stop(pet_id: str, utterance_id: str, reason: str = "cancel") -> Event:
    return make("tts.stop", TtsStop(pet_id=pet_id, utterance_id=utterance_id, reason=reason))
