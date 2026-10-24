"""Speech-to-speech voice for the pet: one voice session per Unity socket.

VOICE_PROVIDER (env):
* ``none`` (default) no voice session; ws.py keeps the stt.py + runner path.
* ``grok``  Grok Voice over raw websockets (``wss://api.x.ai/v1/realtime``), key ``XAI_API_KEY``.
* ``fake``  canned reply from a bundled PCM file, no key, no network (Unity testing).

Conversational path only (no tools / delegate yet). Push-to-talk is a manual turn:

    Unity stt.start     -> begin_turn()   (barge-in: cancel speech, clear the input buffer)
    Unity binary PCM16  -> feed()         (16 kHz mono, forwarded as input_audio_buffer.append)
    Unity stt.stop      -> commit_turn()  (input_audio_buffer.commit + response.create)

Grok -> our events (docs/PET_AGENT_CONTRACT.md, Addendum B):

    ...input_audio_transcription.updated/.delta   -> stt.partial (cumulative)
    ...input_audio_transcription.completed        -> stt.final {voice: true}
    response.output_audio.delta (base64)          -> tts.start (first chunk) + binary PCM16 frames
    response.output_audio_transcript.delta/.done  -> agent.partial_answer (cumulative) / agent.answer_done
    response.done                                 -> tts.end
    error                                         -> agent.error

Transcripts and agent.* go through the broadcast Emitter (other viewers stay in sync); tts.* and
audio go to the owning socket only. The reader is the only task that sends tts.*/audio, so the
order tts.start -> frames -> tts.end/tts.stop is preserved on the wire.
"""

from __future__ import annotations

import asyncio
import base64
import json
import logging
import os
import time
import uuid
from collections.abc import Awaitable, Callable
from pathlib import Path
from typing import Any, Protocol

from . import events as ev
from .events import Event

log = logging.getLogger(__name__)

SendEvent = Callable[[Event], Awaitable[Any]]
SendBytes = Callable[[bytes], Awaitable[Any]]

INPUT_RATE = 16000
SUPPORTED_RATES = {8000, 16000, 22050, 24000, 32000, 44100, 48000}
MIN_COMMIT_BYTES = INPUT_RATE * 2 // 10  # 100 ms of PCM16: less than this is a tap, not speech
ECHO_WINDOW_S = 10.0  # agent.ask repeating the last voice transcript within this window is ignored
GROK_URL = "wss://api.x.ai/v1/realtime?model={model}"
FAKE_PCM = Path(__file__).resolve().parent / "assets" / "fake_voice_24k.pcm"
FAKE_RATE = 24000
FAKE_TRANSCRIPT = "Hey Bit, are you there?"
FAKE_ANSWER = "Hi! I'm Bit. I'm right here on your desk, ready to help."

PERSONA = (
    "You are Bit, a small, cute and very competent alligator who lives on the user's desk in Forge, "
    "a mixed-reality workspace for engineers. Speak in one to three short sentences, warm and a little "
    "playful, never longer. No lists, no markdown, never read code aloud; say file and function names "
    "plainly. If you don't know something about their project, say so briefly; you can't look things "
    "up yet."
)


def provider() -> str:
    return os.getenv("VOICE_PROVIDER", "none").strip().lower() or "none"


def pick_out_rate(client_rate: int | None) -> int:
    """The client's output rate when Grok supports it (no resampling in Unity), else GROK_VOICE_OUT_RATE."""
    if client_rate in SUPPORTED_RATES:
        return int(client_rate)  # type: ignore[arg-type]
    default = int(os.getenv("GROK_VOICE_OUT_RATE", "48000"))
    return default if default in SUPPORTED_RATES else 48000


class VoiceSession(Protocol):
    provider: str
    sample_rate: int

    async def start(self) -> None: ...
    async def begin_turn(self) -> None: ...
    async def feed(self, pcm16: bytes) -> None: ...
    async def commit_turn(self) -> None: ...
    async def cancel_turn(self) -> None: ...
    async def stop_speech(self, reason: str = "cancel") -> None: ...
    async def close(self) -> None: ...
    def is_echo(self, text: str) -> bool: ...


class _Base:
    """Shared turn bookkeeping: transcript -> stt.*, utterance ids, echo guard."""

    provider = "none"

    def __init__(self, *, pet_id: str, sample_rate: int, send_event: SendEvent,
                 send_bytes: SendBytes, emit: SendEvent) -> None:
        self.pet_id = pet_id
        self.sample_rate = sample_rate
        self._send_event = send_event  # owner socket only
        self._send_bytes = send_bytes  # owner socket only
        self._emit = emit  # broadcast Emitter (adds pet.state companions)
        self._partial = ""
        self._final_sent = True  # no turn open yet
        self._cancelled = False  # stt.cancel: drop audio, commit nothing, empty final on stt.stop
        self._last_final = ""
        self._last_final_t = 0.0

    def is_echo(self, text: str) -> bool:
        """True if an agent.ask just repeats the transcript this session is already answering."""
        return (
            bool(self._last_final)
            and text.strip().lower() == self._last_final.strip().lower()
            and time.monotonic() - self._last_final_t < ECHO_WINDOW_S
        )

    def _open_turn(self) -> None:
        self._partial = ""
        self._final_sent = False
        self._cancelled = False

    async def _transcript(self, text: str) -> None:
        if self._final_sent or not text or text == self._partial:
            return
        self._partial = text
        await self._emit(ev.stt_partial(self.pet_id, text))

    async def _final(self, text: str | None = None) -> None:
        if self._final_sent:
            return
        self._final_sent = True
        final = (text if text is not None else self._partial).strip()
        self._last_final, self._last_final_t = final, time.monotonic()
        await self._emit(ev.stt_final(self.pet_id, final, voice=True))


class GrokVoiceSession(_Base):
    provider = "grok"

    def __init__(self, *, pet_id: str, sample_rate: int, send_event: SendEvent, send_bytes: SendBytes,
                 emit: SendEvent, api_key: str | None = None, model: str | None = None,
                 voice: str | None = None, keyterms: list[str] | None = None,
                 connect: Callable[[str, str], Awaitable[Any]] | None = None) -> None:
        super().__init__(pet_id=pet_id, sample_rate=sample_rate, send_event=send_event,
                         send_bytes=send_bytes, emit=emit)
        self._api_key = api_key if api_key is not None else os.getenv("XAI_API_KEY", "")
        self._model = model or os.getenv("GROK_VOICE_MODEL", "grok-voice-3.0")
        self._voice = voice if voice is not None else os.getenv("GROK_VOICE", "")
        self._keyterms = (keyterms or [])[:100]
        self._connect = connect or _ws_connect
        self._idle_close_s = float(os.getenv("VOICE_IDLE_CLOSE_S", "120"))
        self._ws: Any = None
        self._reader: asyncio.Task[None] | None = None
        self._watchdog: asyncio.Task[None] | None = None
        self._send_lock = asyncio.Lock()
        self._connect_lock = asyncio.Lock()
        self._last_activity = time.monotonic()
        self._fed = 0
        # current response
        self._utt: str | None = None
        self._audio_started = False
        self._answer = ""
        self._answer_done = False
        self._forwarding = False
        self._response_active = False

    # ------------------------------------------------------------ lifecycle
    async def start(self) -> None:
        """Connect (idempotent) and configure the session. Raises on failure."""
        async with self._connect_lock:
            if self._ws is not None:
                return
            if not self._api_key:
                raise RuntimeError("XAI_API_KEY is not set")
            t0 = time.monotonic()
            self._ws = await self._connect(GROK_URL.format(model=self._model), self._api_key)
            await self._send(self._session_update())
            self._reader = asyncio.create_task(self._read_loop(self._ws))
            if self._watchdog is None and self._idle_close_s > 0:
                self._watchdog = asyncio.create_task(self._idle_loop())
            self._last_activity = time.monotonic()
            log.info("grok voice connected model=%s out=%d Hz in %.0f ms", self._model,
                     self.sample_rate, (time.monotonic() - t0) * 1000)

    async def close(self) -> None:
        for task in (self._watchdog, self._reader):
            if task is not None and not task.done():
                task.cancel()
        self._watchdog = None
        await self._drop_socket()

    async def _drop_socket(self) -> None:
        ws, self._ws = self._ws, None
        if self._reader is not None and self._reader is not asyncio.current_task() and not self._reader.done():
            self._reader.cancel()
        self._reader = None
        if ws is not None:
            try:
                await ws.close()
            except Exception:
                pass

    def _session_update(self) -> dict[str, Any]:
        transcription: dict[str, Any] = {"model": "grok-transcribe"}
        if self._keyterms:
            transcription["keyterms"] = self._keyterms
        session: dict[str, Any] = {
            "instructions": PERSONA,
            "turn_detection": None,  # push-to-talk: we commit the turn ourselves
            "reasoning": {"effort": "none"},
            "audio": {
                "input": {"format": {"type": "audio/pcm", "rate": INPUT_RATE}, "transcription": transcription},
                "output": {"format": {"type": "audio/pcm", "rate": self.sample_rate}},
            },
        }
        if self._voice:
            session["voice"] = self._voice
        return {"type": "session.update", "session": session}

    # ------------------------------------------------------------ turn API (called by ws.py)
    async def begin_turn(self) -> None:
        await self.start()
        await self.stop_speech("barge_in")
        await self._send({"type": "input_audio_buffer.clear"})
        self._fed = 0
        self._open_turn()

    async def feed(self, pcm16: bytes) -> None:
        if self._ws is None or self._cancelled:
            return  # no turn open, cancelled, or connection dropped: drop the chunk
        self._fed += len(pcm16)
        await self._send({"type": "input_audio_buffer.append", "audio": base64.b64encode(pcm16).decode("ascii")})

    async def commit_turn(self) -> None:
        if self._ws is None or self._cancelled:
            await self._final("")
            return
        if self._fed < MIN_COMMIT_BYTES:  # a tap: nothing to answer
            await self._send({"type": "input_audio_buffer.clear"})
            await self._final("")
            return
        await self._send({"type": "input_audio_buffer.commit"})
        await self._send({"type": "response.create"})
        await self._emit(ev.thinking(self.pet_id))

    async def cancel_turn(self) -> None:
        self._cancelled = True
        self._fed = 0
        await self._send({"type": "input_audio_buffer.clear"})

    async def stop_speech(self, reason: str = "cancel") -> None:
        """Stop forwarding first, then tell Grok and Unity (no stale chunk can follow tts.stop)."""
        was_active, self._forwarding = self._response_active, False
        if was_active and self._ws is not None:
            await self._send({"type": "response.cancel"})
        if self._audio_started and self._utt is not None:
            await self._send_event(ev.tts_stop(self.pet_id, self._utt, reason))
        self._audio_started = False
        self._response_active = False

    # ------------------------------------------------------------ Grok I/O
    async def _send(self, obj: dict[str, Any]) -> None:
        ws = self._ws
        if ws is None:
            return
        self._last_activity = time.monotonic()
        async with self._send_lock:
            await ws.send(json.dumps(obj))

    async def _read_loop(self, ws: Any) -> None:
        try:
            async for raw in ws:
                self._last_activity = time.monotonic()
                if isinstance(raw, (bytes, bytearray)):  # binary transport: raw output audio
                    await self._audio(bytes(raw))
                    continue
                try:
                    msg = json.loads(raw)
                except json.JSONDecodeError:
                    log.warning("grok: bad frame %.200s", raw)
                    continue
                await self.handle(msg)
        except asyncio.CancelledError:
            raise
        except Exception as exc:
            log.warning("grok voice connection lost: %s", exc)
        finally:
            if self._ws is ws:
                self._ws = None
                await self.stop_speech("error")

    async def _idle_loop(self) -> None:
        while True:
            await asyncio.sleep(5.0)
            if self._ws is not None and not self._response_active \
                    and time.monotonic() - self._last_activity > self._idle_close_s:
                log.info("grok voice idle for %.0f s: closing (reconnects on next turn)", self._idle_close_s)
                await self._drop_socket()

    async def handle(self, msg: dict[str, Any]) -> None:
        """Translate one Grok server event into ours."""
        t = msg.get("type", "")
        if t in ("conversation.item.input_audio_transcription.updated",
                 "conversation.item.input_audio_transcription.delta"):
            text = msg.get("transcript")
            if text is None:
                text = self._partial + (msg.get("delta") or "")
            await self._transcript(text)
        elif t == "conversation.item.input_audio_transcription.completed":
            await self._final(msg.get("transcript"))
        elif t == "response.created":
            if self._partial:
                await self._final()  # no .completed yet: the last cumulative transcript is final
            self._utt = uuid.uuid4().hex[:12]
            self._audio_started = False
            self._answer, self._answer_done = "", False
            self._forwarding = self._response_active = True
        elif t in ("response.output_audio.delta", "response.audio.delta"):
            delta = msg.get("delta")
            if delta:
                await self._audio(base64.b64decode(delta))
        elif t in ("response.output_audio_transcript.delta", "response.audio_transcript.delta"):
            if self._forwarding and msg.get("delta"):
                await self._final()
                self._answer += msg["delta"]
                await self._emit(ev.partial_answer(self.pet_id, self._answer))
        elif t in ("response.output_audio_transcript.done", "response.audio_transcript.done"):
            if self._forwarding:
                await self._answer_finished(msg.get("transcript") or self._answer)
        elif t == "response.done":
            if self._forwarding:
                if self._audio_started and self._utt is not None:
                    await self._send_event(ev.tts_end(self.pet_id, self._utt))
                if self._answer and not self._answer_done:
                    await self._answer_finished(self._answer)
            self._forwarding = self._response_active = False
            self._audio_started = False
        elif t == "error":
            err = msg.get("error") or {}
            message = err.get("message") if isinstance(err, dict) else str(err)
            log.warning("grok error: %s", msg)
            if not self._final_sent:
                await self._final("")
            await self._emit(ev.error(self.pet_id, f"Voice error: {message or 'unknown'}"))
        elif t in ("session.created", "session.updated"):
            log.debug("grok %s", t)
        else:
            log.debug("grok: unhandled %s", t)

    async def _audio(self, pcm: bytes) -> None:
        if not self._forwarding or not pcm:
            return
        await self._final()
        if not self._audio_started:
            self._audio_started = True
            await self._send_event(ev.tts_start(self.pet_id, self._utt or uuid.uuid4().hex[:12], self.sample_rate))
        await self._send_bytes(pcm)

    async def _answer_finished(self, text: str) -> None:
        self._answer_done = True
        await self._emit(ev.answer_done(self.pet_id, text.strip()))


class FakeVoiceSession(_Base):
    """VOICE_PROVIDER=fake: on stt.stop, a canned transcript + reply spoken from FAKE_PCM (24 kHz)."""

    provider = "fake"

    def __init__(self, *, pet_id: str, sample_rate: int, send_event: SendEvent,
                 send_bytes: SendBytes, emit: SendEvent, pcm: bytes | None = None) -> None:
        super().__init__(pet_id=pet_id, sample_rate=FAKE_RATE, send_event=send_event,
                         send_bytes=send_bytes, emit=emit)
        self._pcm = pcm
        self._reply: asyncio.Task[None] | None = None
        self._utt: str | None = None
        self._speaking = False

    def _pace(self) -> float:  # 1.0 = real time; tests set FAKE_VOICE_PACE=0
        return float(os.getenv("FAKE_VOICE_PACE", "1.0"))

    async def start(self) -> None:
        if self._pcm is None:
            try:
                self._pcm = FAKE_PCM.read_bytes()
            except OSError:
                log.warning("fake voice: %s missing, speaking silence", FAKE_PCM)
                self._pcm = bytes(FAKE_RATE * 2)

    async def begin_turn(self) -> None:
        await self.start()
        await self.stop_speech("barge_in")
        self._open_turn()

    async def feed(self, pcm16: bytes) -> None:
        return None

    async def cancel_turn(self) -> None:
        self._cancelled = True

    async def commit_turn(self) -> None:
        if self._cancelled:
            await self._final("")
            return
        await self._transcript(FAKE_TRANSCRIPT)
        await self._final(FAKE_TRANSCRIPT)
        await self._emit(ev.thinking(self.pet_id))
        self._reply = asyncio.create_task(self._speak())

    async def _speak(self) -> None:
        pcm = self._pcm or b""
        self._utt = uuid.uuid4().hex[:12]
        await asyncio.sleep(0.3 * self._pace())
        self._speaking = True
        await self._send_event(ev.tts_start(self.pet_id, self._utt, FAKE_RATE))
        chunk = FAKE_RATE * 2 // 10  # 100 ms
        words = FAKE_ANSWER.split(" ")
        n = max(1, (len(pcm) + chunk - 1) // chunk)
        for i in range(n):
            await self._send_bytes(pcm[i * chunk:(i + 1) * chunk])
            shown = len(words) * (i + 1) // n
            if shown and (i + 1) % 3 == 0:
                await self._emit(ev.partial_answer(self.pet_id, " ".join(words[:shown])))
            await asyncio.sleep(0.1 * self._pace())
        await self._emit(ev.answer_done(self.pet_id, FAKE_ANSWER))
        await self._send_event(ev.tts_end(self.pet_id, self._utt))
        self._speaking = False

    async def stop_speech(self, reason: str = "cancel") -> None:
        task, self._reply = self._reply, None
        if task is not None and not task.done():
            task.cancel()
            try:
                await task
            except asyncio.CancelledError:
                pass
        if self._speaking and self._utt is not None:
            self._speaking = False
            await self._send_event(ev.tts_stop(self.pet_id, self._utt, reason))

    async def close(self) -> None:
        task, self._reply = self._reply, None
        if task is not None and not task.done():
            task.cancel()


async def _ws_connect(url: str, api_key: str) -> Any:
    from websockets.asyncio.client import connect  # only needed with VOICE_PROVIDER=grok

    return await connect(url, additional_headers={"Authorization": f"Bearer {api_key}"},
                         max_size=None, open_timeout=10)


def make_voice(*, pet_id: str, client_rate: int | None, send_event: SendEvent, send_bytes: SendBytes,
               emit: SendEvent, repo: Path | None = None) -> VoiceSession | None:
    """The session for VOICE_PROVIDER, or None (``none``, or ``grok`` without XAI_API_KEY)."""
    p = provider()
    if p == "fake":
        return FakeVoiceSession(pet_id=pet_id, sample_rate=FAKE_RATE, send_event=send_event,
                                send_bytes=send_bytes, emit=emit)
    if p == "grok":
        if not os.getenv("XAI_API_KEY"):
            log.warning("VOICE_PROVIDER=grok but XAI_API_KEY is empty: voice off")
            return None
        return GrokVoiceSession(pet_id=pet_id, sample_rate=pick_out_rate(client_rate), send_event=send_event,
                                send_bytes=send_bytes, emit=emit, keyterms=vocab_terms(repo))
    if p != "none":
        log.warning("unknown VOICE_PROVIDER=%r: voice off", p)
    return None


def vocab_terms(repo: Path | None, limit: int = 100) -> list[str]:
    """Repo file/symbol names to bias grok-transcribe (same source as the whisper prompt)."""
    if repo is None:
        return []
    from .stt import build_vocab_prompt

    try:
        body = build_vocab_prompt(str(repo)).removeprefix("Code terms: ").rstrip(".")
    except OSError:
        return []
    return [t for t in body.split(", ") if t][:limit]
