"""/ws/agent session handler for the pet agent.

Mount it from any FastAPI app:

    @app.websocket("/ws/agent")
    async def ws_endpoint(ws: WebSocket) -> None:
        await agent.ws.handle_socket(ws)

Per socket: hello -> (pet_id, project_id); agent.ask -> background turn streaming
events over the bus; agent.cancel -> interrupt; stt.start / binary PCM16 / stt.stop
-> stt.partial / stt.final. With VOICE_PROVIDER=grok|fake the same stt.* frames drive a
voice session instead (agent/voice.py: speech back as tts.* + binary, owner socket only).
Unknown message types are ignored (other modules may share the socket) and logged.
"""

from __future__ import annotations

import asyncio
import json
import logging
from dataclasses import dataclass, field

from fastapi import WebSocket, WebSocketDisconnect
from pydantic import ValidationError

from . import events as ev
from .bus import Emitter, bus
from .config import ProjectNotFound, resolve_project
from .events import AgentAsk, AgentCancel, Event, Hello, SttCancel, SttStart, SttStop, TtsStopRequest
from .runner import get_runner
from .stt import SpeechToText, make_stt
from .turn import TurnControl, classify_mode
from .voice import VoiceSession, make_voice

log = logging.getLogger(__name__)

CANCEL_GRACE_S = 3.0


@dataclass
class Session:
    ws: WebSocket
    pet_id: str = "bit"
    project_id: str | None = None
    turn: asyncio.Task[str] | None = None
    turn_mode: str | None = None
    control: TurnControl | None = None
    stt: SpeechToText | None = None
    stt_busy: bool = False
    stt_cancelled: bool = False  # stt.cancel: ignore audio until the next stt.start
    voice: VoiceSession | None = None
    tasks: set[asyncio.Task[None]] = field(default_factory=set)

    def spawn(self, coro) -> None:  # fire-and-forget helper that keeps a reference
        t = asyncio.create_task(coro)
        self.tasks.add(t)
        t.add_done_callback(self.tasks.discard)

    async def reply_error(self, message: str, pet_id: str | None = None) -> None:
        """agent.error to the whole bus (keeps the pet in sync with other viewers)."""
        await Emitter(pet_id or self.pet_id)(ev.error(pet_id or self.pet_id, message))


async def handle_socket(ws: WebSocket) -> None:
    await ws.accept()
    bus.add(ws)
    s = Session(ws=ws)
    log.info("ws connected (%d total)", bus.count)
    try:
        while True:
            msg = await ws.receive()
            if msg.get("type") == "websocket.disconnect":
                break
            if msg.get("bytes") is not None:
                await _on_audio(s, msg["bytes"])
            elif msg.get("text") is not None:
                await _on_text(s, msg["text"])
    except WebSocketDisconnect:
        pass
    finally:
        bus.remove(ws)
        if s.voice is not None:
            await s.voice.close()
        # Drop a running Q&A (nobody is listening), but let a code task finish on its
        # branch: a flaky Quest Wi-Fi reconnect must not abort half-done edits.
        if s.turn and not s.turn.done() and s.turn_mode != "code":
            await _cancel(s, announce=False)
        log.info("ws disconnected (%d left)", bus.count)


async def _on_text(s: Session, raw: str) -> None:
    try:
        event = Event.model_validate(json.loads(raw))
    except (json.JSONDecodeError, ValidationError):
        log.warning("bad frame: %.200s", raw)
        await s.reply_error("I didn't understand that message.")
        return
    try:
        if event.type == "hello":
            h = Hello.model_validate(event.payload)
            s.pet_id, s.project_id = h.pet_id, h.project_id
            log.info("hello client=%s pet=%s project=%s", h.client, h.pet_id, h.project_id)
            await _open_voice(s, h)
        elif event.type == "agent.ask":
            await _on_ask(s, AgentAsk.model_validate(event.payload))
        elif event.type == "agent.cancel":
            AgentCancel.model_validate(event.payload)
            s.spawn(_cancel(s))
        elif event.type == "stt.start":
            start = SttStart.model_validate(event.payload)
            if start.sample_rate != 16000:
                log.warning("stt.start sample_rate=%s; expected 16000", start.sample_rate)
            s.stt_cancelled = False
            if s.voice is not None:
                await _voice_call(s, s.voice.begin_turn)
            else:
                s.stt = await asyncio.to_thread(make_stt, _project_path(s))
        elif event.type == "stt.cancel":
            SttCancel.model_validate(event.payload)
            if s.voice is not None:
                await _voice_call(s, s.voice.cancel_turn)
            else:
                s.stt, s.stt_cancelled = None, True
        elif event.type == "stt.stop":
            SttStop.model_validate(event.payload)
            if s.voice is not None:
                await _voice_call(s, s.voice.commit_turn)
            else:
                await _on_stt_stop(s)
        elif event.type == "tts.stop":
            TtsStopRequest.model_validate(event.payload)
            if s.voice is not None:
                await _voice_call(s, s.voice.stop_speech)
        else:
            log.debug("ignoring ws message type %s", event.type)
    except ValidationError as exc:
        log.warning("invalid %s payload: %s", event.type, exc)
        await s.reply_error(f"Bad {event.type} message.")


def _project_path(s: Session):
    try:
        return resolve_project(s.project_id).path
    except ProjectNotFound:
        return None


async def _on_ask(s: Session, ask: AgentAsk) -> None:
    pet_id = ask.pet_id or s.pet_id
    if not ask.text.strip():
        await s.reply_error("I didn't catch that.", pet_id)
        return
    if s.voice is not None and s.voice.is_echo(ask.text):
        log.info("ignoring agent.ask that repeats the voice transcript (voice answers it)")
        return
    if s.turn and not s.turn.done():
        if s.turn_mode == "code":
            await s.reply_error("I'm still working on the last task. Cancel it first.", pet_id)
            return
        await _cancel(s, announce=False)  # a new question supersedes a running answer
    try:
        project = resolve_project(ask.project_id or s.project_id)
    except ProjectNotFound as exc:
        await s.reply_error(str(exc), pet_id)
        return
    # omitted mode -> project default; "auto" -> imperative-verb heuristic
    mode = classify_mode(ask.text, ask.mode or project.default_mode)
    s.control = TurnControl()
    s.turn_mode = mode
    emit = Emitter(pet_id, redact=[str(project.path)])
    run = get_runner()
    coro = run(project, ask.text, mode, emit, pet_id=pet_id, control=s.control)
    s.turn = asyncio.create_task(_guarded(coro, emit, pet_id))


async def _guarded(coro, emit: Emitter, pet_id: str) -> str:
    """Last-resort error boundary: log server-side, send a short message to the client."""
    try:
        return await coro
    except asyncio.CancelledError:
        return ""
    except Exception:
        log.exception("turn crashed (pet=%s)", pet_id)
        await emit(ev.error(pet_id, "Sorry, something went wrong."))
        return ""


async def _cancel(s: Session, announce: bool = True) -> None:
    turn, control = s.turn, s.control
    if turn is None or turn.done():
        return
    log.info("cancelling turn for pet=%s", s.pet_id)
    if control is not None:
        try:
            await control.cancel()  # ClaudeSDKClient.interrupt()
        except Exception:
            log.exception("interrupt failed; hard-cancelling")
    try:
        await asyncio.wait_for(asyncio.shield(turn), CANCEL_GRACE_S)
    except (asyncio.TimeoutError, asyncio.CancelledError):
        turn.cancel()
    if announce:
        await bus.broadcast(ev.pet_state(s.pet_id, "idle"))


# ------------------------------------------------------------------ voice
async def _open_voice(s: Session, h: Hello) -> None:
    """Create the voice session for VOICE_PROVIDER (once per socket), pre-warm it, announce voice.ready."""
    if s.voice is not None:
        return
    ws = s.ws

    async def send_event(event: Event) -> None:
        await bus.send(ws, event)

    async def send_bytes(data: bytes) -> None:
        await bus.send_bytes(ws, data)

    voice = make_voice(pet_id=s.pet_id, client_rate=h.audio_out_rate, send_event=send_event,
                       send_bytes=send_bytes, emit=Emitter(s.pet_id), repo=_project_path(s))
    if voice is None:
        return
    s.voice = voice
    await bus.send(ws, ev.voice_ready(s.pet_id, voice.provider, voice.sample_rate))
    s.spawn(_voice_call(s, voice.start))


async def _voice_call(s: Session, fn) -> None:
    """Run a voice-session call; on failure fall back to the stt.py path for this socket."""
    voice = s.voice
    try:
        await fn()
    except Exception as exc:
        log.exception("voice session failed")
        if s.voice is voice and voice is not None:
            s.voice = None
            await voice.close()
            await s.reply_error(f"Voice is offline ({exc}). Push-to-talk still works without it.")


# ------------------------------------------------------------------ audio / STT
async def _on_audio(s: Session, chunk: bytes) -> None:
    if s.voice is not None:
        await _voice_call(s, lambda: s.voice.feed(chunk))
        return
    if s.stt_cancelled:
        return
    if s.stt is None:  # tolerate audio without stt.start
        s.stt = await asyncio.to_thread(make_stt, _project_path(s))
    s.stt.feed(chunk)
    if not s.stt_busy:  # at most one partial transcription in flight
        s.stt_busy = True
        s.spawn(_stt_partial(s, s.stt))


async def _stt_partial(s: Session, stt: SpeechToText) -> None:
    try:
        text = await asyncio.to_thread(stt.partial)
        if text and s.stt is stt:
            await Emitter(s.pet_id)(ev.stt_partial(s.pet_id, text))
    except Exception:
        log.exception("stt partial failed")
    finally:
        s.stt_busy = False


async def _on_stt_stop(s: Session) -> None:
    stt, s.stt = s.stt, None
    text = ""
    if stt is not None:
        try:
            text = await asyncio.to_thread(stt.finish)
        except Exception:
            log.exception("stt finish failed")
            await s.reply_error("I couldn't hear that, try again?")
            return
    await Emitter(s.pet_id)(ev.stt_final(s.pet_id, text))
