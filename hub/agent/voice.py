"""Speech-to-speech voice for the pet: one voice session per Unity socket.

VOICE_PROVIDER (env):
* ``none`` (default) no voice session; ws.py keeps the stt.py + runner path.
* ``grok``  Grok Voice over raw websockets (``wss://api.x.ai/v1/realtime``), key ``XAI_API_KEY``.
* ``fake``  canned reply from a bundled PCM file, no key, no network (Unity testing).

Grok handles conversation itself. Quick actions (CAD params, VS Code highlight/open) are direct tools from
agent/tools.py (incl. the crew tools: spawn_agent, assign_task, ... from agent/crew.py). ``delegate`` sends anything about the repo, runs or a code change to the Claude runner
(the "brain"), whose answer is then spoken verbatim with ``force_message``. Push-to-talk is a manual turn:

    Unity stt.start     -> begin_turn()   (barge-in: cancel speech, clear the input buffer)
    Unity binary PCM16  -> feed()         (16 kHz mono, forwarded as input_audio_buffer.append)
    Unity stt.stop      -> commit_turn()  (input_audio_buffer.commit + response.create)

Grok -> our events (docs/PET_AGENT_CONTRACT.md, Addendum B):

    ...input_audio_transcription.updated/.delta   -> stt.partial (cumulative)
    ...input_audio_transcription.completed        -> stt.final {voice: true}
    response.output_audio.delta (base64)          -> tts.start (first chunk) + binary PCM16 frames
    response.output_audio_transcript.delta/.done  -> agent.partial_answer (cumulative) / agent.answer_done
    response.done                                 -> tts.end
    response.function_call_arguments.done         -> agent.tool_call; delegate -> runner (see _on_function_call)
    error                                         -> agent.error

Context without a reply: ``note(text)`` adds a one-line system message (the workspace view the user opened,
the Slack message in focus; ws.py and messaging call it through agent/speech.py). ``workspace_mode`` (set by ws.py
from ``workspace.mode``) gates Albie's crew tools: they answer with an error unless the CODE view is open, since
the crew's gators live on that screen. None (the client never said) leaves them available.

Wake turns (``begin_turn(wake=True)``, Unity's always-on WakeListener, see agent/wake.py): no barge-in, no stt.*
and no response until ``wake_router`` has read the transcript; it answers only if a gator was named (or the user
faced Albie and agent/addressed.py's word rules don't rule it out), otherwise the committed item is deleted so room
chatter never reaches the conversation. A gated turn (``begin_turn(require_name=True)``: a look-to-talk / follow-up
reply) takes the same route and needs the name; when dropped it still ends with an empty stt.final, since Unity's
hands-free session waits for one. "Stop" on these turns stops Albie only with his name or while facing him.

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
import re
import time
import uuid
from collections import deque
from collections.abc import Awaitable, Callable
from dataclasses import dataclass
from pathlib import Path
from typing import Any, Protocol

from . import addressed
from . import events as ev
from . import tools
from .crew import TYPES, lead_rules
from .events import Event
from .wake import ALIASES, SPLIT_FIRST, SPLIT_SECOND

log = logging.getLogger(__name__)

SendEvent = Callable[[Event], Awaitable[Any]]
SendBytes = Callable[[bytes], Awaitable[Any]]
Delegate = Callable[[str, str], Awaitable[str]]  # (task, mode) -> spoken answer / summary

INPUT_RATE = 16000
SUPPORTED_RATES = {8000, 16000, 22050, 24000, 32000, 44100, 48000}
MIN_COMMIT_BYTES = INPUT_RATE * 2 // 10  # 100 ms of PCM16: less than this is a tap, not speech
ECHO_WINDOW_S = 10.0  # agent.ask repeating the last voice transcript within this window is ignored
GROK_URL = "wss://api.x.ai/v1/realtime?model={model}"
FAKE_PCM = Path(__file__).resolve().parent / "assets" / "fake_voice_24k.pcm"
FAKE_RATE = 24000
FAKE_TRANSCRIPT = "Hey Albie, are you there?"
FAKE_ANSWER = "Hi! I'm Albie. I'm right here on your desk, ready to help."

PERSONA = (
    "You are Albie (said AL-bee), a small, cute and very competent alligator who lives on the user's desk in Forge, "
    "a mixed-reality workspace for engineers. Talk like a cheerful cartoon sidekick: warm, bouncy and always "
    "helpful. Never use interjections or filler sounds such as 'ooh', 'hehe', 'wow' or 'yay'; show "
    "personality through word choice instead. No lists, no markdown, never read code aloud; say file and "
    "function names plainly. Transcripts often turn \"ML\" into \"email\": Forge has no email panel, so "
    "\"email\" means ML (the machine learning desk)."
)
# Last in the instructions, after the tool rules, so it wins over "tell me everything".
BREVITY = (
    " Most important rule: every reply is one or two short sentences, about 20 words at most. Lead with the "
    "answer, no warm-up. Even if the user asks for everything or a long answer, give only the key point, "
    "then offer to say more."
)
BREVITY_NOW = " Reply now in at most two short sentences."
DELEGATE_RULES = (
    " Your delegate tool is for anything needing real analysis of the user's code, repository, files, project "
    "or training runs, and for any request to write, fix or change code; never guess those details "
    "yourself. Give it a self-contained task that includes what the user asked and any context from the "
    "conversation. After calling it, say only a tiny 'on it' line: the answer is spoken for you when it's "
    "ready. Answer everything else yourself."
)
TOOL_RULES = (
    " Use your other tools yourself for quick actions: read or change the rocket design, open, highlight "
    "or check code in VS Code, set the focus timer, keep the user's notes, and play the movie on the big screen. "
    "Call them instead of guessing, then say in one short line what you did. "
    "Slack and iMessage replies go out under the user's own account and are never sent directly: messages_send only writes a draft "
    "that pops up for them to confirm, so say what you wrote and ask them to tap Send or say 'send it'; "
    "messages_confirm is for their yes or no."
)
CREW_OFF_CODE_SCREEN = {"error": "crew tools are only available on the code screen; ask the user to open CODE"}
VIEW_NOTES = {
    "code": "The user switched to the CODE view (VS Code on the main screen); crew tools are available now.",
    "cad": "The user switched to the CAD view (the rocket design); crew tools are off until they open CODE.",
    "ml": "The user switched to the ML view (training data and runs); crew tools are off until they open CODE.",
    "messages": "The user switched to the MESSAGES view (their Slack inbox); crew tools are off until they open CODE.",
    "": "The user closed the workspace views; crew tools are off until they open CODE.",
}
DELEGATE_TOOL = {
    "type": "function",
    "name": "delegate",
    "description": "Hand off anything needing real analysis of the user's repo, run results or CAD script, "
                   "or any code change, to the engineering brain. Its answer is spoken to the user when ready.",
    "parameters": {
        "type": "object",
        "properties": {
            "task": {"type": "string", "description": "self-contained instruction incl. context from the conversation"},
            "mode": {"type": "string", "enum": ["qa", "code", "auto"],
                     "description": "qa = answer a question, code = change code, auto = decide"},
        },
        "required": ["task"],
    },
}
# Albie's own vocabulary first in grok-transcribe's hints ("fins" came back as "Fizz" in a live test).
ROCKET_TERMS = ["Albie", "ML", "ML panel", "ML desk", "fins", "fin", "nose cone", "body tube", "static margin", "apogee", "center of pressure",
                "center of gravity", "calibers", "Estes", "ogive", "fin span", "fin sweep"]
# "Stop" over Albie should just stop him, not start a reply ("Okay, quiet!").
STOP_CORE = {"stop", "wait", "quiet", "shh", "shush", "hush", "enough", "nevermind"}
STOP_FILLER = ALIASES["bit"] | {SPLIT_FIRST} | SPLIT_SECOND | {"okay", "ok", "please", "hey", "be", "that", "it", "never",
                                                                "mind", "now", "right"}
NO_REPLY_S = 4.0  # committed turn with no transcript and no response from Grok by then: treat it as empty
DRAIN_TIMEOUT_S = 20.0  # longest we wait for Grok to finish talking before speaking a delegate answer
WAKE_FINAL_TIMEOUT_S = 5.0  # a wake turn with no transcription.completed by then is decided on the last partial


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

    wake_router: Any

    async def start(self) -> None: ...
    async def begin_turn(self, require_name: bool = False, wake: bool = False) -> None: ...
    async def feed(self, pcm16: bytes) -> None: ...
    async def commit_turn(self) -> None: ...
    async def cancel_turn(self) -> None: ...
    async def stop_speech(self, reason: str = "cancel") -> None: ...
    async def close(self) -> None: ...
    def is_echo(self, text: str) -> bool: ...


@dataclass
class _Commit:
    """One committed turn, until its transcript arrives. WakeListener sends segments back to back, so a segment's
    transcript can land after the next one began (live 18:49: five real sentences came back as '' turns)."""

    seq: int
    wake: bool
    gated: bool  # Unity's gated turn (its hands-free session wants a final)
    flags: tuple[str | None, bool, bool]  # the WakeRouter's (target, facing, gated) for this turn
    item: str | None = None


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
        self._committed = False  # Grok confirmed the turn's audio (input_audio_buffer.committed)
        self._turn_seq = 0
        self._require_name = False  # look-to-talk: user wasn't facing Albie, so only a turn naming him counts
        self._named = False  # an interim transcript of this turn named him (the final can drop a leading "Albie")
        self._cancelled = False  # stt.cancel: drop audio, commit nothing, empty final on stt.stop
        self._last_final = ""
        self._last_final_t = 0.0
        self.wake_router: Any = None  # agent/wake.py WakeRouter, set by ws.py
        self._wake = False  # the open turn is a wake turn
        self._gated = False  # ...that Unity opened as a gated turn (require_name): a dropped one still needs a final
        self.last_reply, self.last_reply_t = "", 0.0  # Albie's last spoken line: context for addressed.judge
        self.workspace_mode: str | None = None  # workspace.mode from Unity; None = never told (ws.py keeps it current)

    def is_echo(self, text: str) -> bool:
        """True if an agent.ask just repeats the transcript this session is already answering."""
        return (
            bool(self._last_final)
            and text.strip().lower() == self._last_final.strip().lower()
            and time.monotonic() - self._last_final_t < ECHO_WINDOW_S
        )

    def _open_turn(self, wake: bool = False) -> None:
        self._turn_seq += 1
        self._named = False
        self._partial = ""
        self._final_sent = False
        self._committed = False
        self._cancelled = False
        self._wake = wake and self.wake_router is not None
        self._gated = False

    async def _transcript(self, text: str) -> None:
        if self._final_sent or not text or text == self._partial:
            return
        self._partial = text
        self._named = self._named or has_name(text, last=False)  # a growing partial's last word isn't a vocative yet
        if self._wake:
            await self.wake_router.partial(text)  # captions only once a gator is named
            return
        if self._require_name and not self._named:
            return  # maybe side conversation: no captions until he's named
        await self._emit(ev.stt_partial(self.pet_id, text))

    async def _final(self, text: str | None = None) -> None:
        if self._final_sent:
            return
        self._final_sent = True
        final = (text if text is not None else self._partial).strip()
        self._last_final, self._last_final_t = final, time.monotonic()
        if self._wake:
            self._wake = False
            decision = await self.wake_router.final(final)
            await self._wake_decided(decision.respond)
            if self._gated and not decision.respond:
                await self._emit(ev.stt_final(self.pet_id, "", voice=True))  # ends Unity's hands-free turn
            return
        await self._emit(ev.stt_final(self.pet_id, final, voice=True))

    async def _wake_decided(self, respond: bool) -> None:
        """Wake turn routed: answer it (respond) or forget it."""

    async def addressed(self, text: str, gated: bool = False) -> bool:
        """WakeRouter.judge: was this unnamed sentence said to Albie? His last line is the context."""
        return await addressed.judge(text, self.last_reply or None, gated=gated)

    def _spoke(self, text: str) -> None:
        if text.strip():
            self.last_reply, self.last_reply_t = text.strip(), time.monotonic()


class GrokVoiceSession(_Base):
    provider = "grok"

    def __init__(self, *, pet_id: str, sample_rate: int, send_event: SendEvent, send_bytes: SendBytes,
                 emit: SendEvent, api_key: str | None = None, model: str | None = None,
                 voice: str | None = None, keyterms: list[str] | None = None,
                 connect: Callable[[str, str], Awaitable[Any]] | None = None,
                 delegate: Delegate | None = None) -> None:
        super().__init__(pet_id=pet_id, sample_rate=sample_rate, send_event=send_event,
                         send_bytes=send_bytes, emit=emit)
        self._api_key = api_key if api_key is not None else os.getenv("XAI_API_KEY", "")
        self._model = model or os.getenv("GROK_VOICE_MODEL", "grok-voice-3.0")
        self._voice = voice if voice is not None else os.getenv("GROK_VOICE", "")
        self._keyterms = (keyterms or [])[:100]
        self._connect = connect or _ws_connect
        self._delegate = delegate
        self._speed = os.getenv("GROK_VOICE_SPEED", "").strip()
        self._pending_outputs: list[dict[str, Any]] = []  # function_call_output items sent after response.done
        self._pending_notes: list[str] = []  # note() while the socket was idle-closed: sent right after session.update
        self._jobs: set[asyncio.Task[None]] = set()
        self._suppress_transcript = False  # next response speaks a delegate answer the runner already captioned
        self._drop_reply = False  # the turn was only "stop": cancel Grok's reply to it, play and run nothing
        self._dropping = False  # that reply is being cancelled (until its response.done)
        self._response_suppressed = False
        self._response_requested = False  # response.create sent, response.created not seen yet
        self._idle_close_s = float(os.getenv("VOICE_IDLE_CLOSE_S", "20"))
        self._ws: Any = None
        self._reader: asyncio.Task[None] | None = None
        self._watchdog: asyncio.Task[None] | None = None
        self._send_lock = asyncio.Lock()
        self._connect_lock = asyncio.Lock()
        self._last_activity = time.monotonic()
        self._fed = 0
        self._last_item: str | None = None  # item id of the last committed user turn (wake turns may delete it)
        self._commits: deque[_Commit] = deque()  # sent input_audio_buffer.commit, waiting for .committed (in order)
        self._by_item: dict[str, _Commit] = {}  # committed, waiting for the transcript
        self._timed_out_seq = -1  # the turn _wake_timeout decided without a transcript
        self._wake_answered = False  # the last wake turn was routed to a reply (for the turn log)
        # current response
        self._utt: str | None = None
        self._audio_started = False
        # Grok sends speech faster than real time, so Unity is often still playing after response.done.
        self._playing_utt: str | None = None
        self._play_until = 0.0  # monotonic time Unity's queued speech runs out
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
            notes, self._pending_notes = self._pending_notes, []
            for text in notes:
                await self._send(_note_item(text))
            self._reader = asyncio.create_task(self._read_loop(self._ws))
            if self._watchdog is None and self._idle_close_s > 0:
                self._watchdog = asyncio.create_task(self._idle_loop())
            self._last_activity = time.monotonic()
            log.info("grok voice connected model=%s out=%d Hz in %.0f ms", self._model,
                     self.sample_rate, (time.monotonic() - t0) * 1000)

    async def close(self) -> None:
        for job in list(self._jobs):
            job.cancel()
        for task in (self._watchdog, self._reader):
            if task is not None and not task.done():
                task.cancel()
        self._watchdog = None
        await self._drop_socket()

    async def _drop_socket(self) -> None:
        ws, self._ws = self._ws, None
        self._commits.clear()  # a new socket starts a new conversation: nothing pending carries over
        self._by_item.clear()
        if self._reader is not None and self._reader is not asyncio.current_task() and not self._reader.done():
            self._reader.cancel()
        self._reader = None
        if ws is not None:
            try:
                await ws.close()
            except Exception:
                pass

    def _instructions(self) -> str:
        return (PERSONA + (TOOL_RULES if tools.definitions(lead=False) else "") + (DELEGATE_RULES if self._delegate else "")
                + (lead_rules() if tools.has("spawn_agent") else "") + BREVITY)

    def _session_update(self) -> dict[str, Any]:
        transcription: dict[str, Any] = {"model": "grok-transcribe"}
        if self._keyterms:
            transcription["keyterms"] = self._keyterms
        output: dict[str, Any] = {"format": {"type": "audio/pcm", "rate": self.sample_rate}}
        if self._speed:
            try:
                output["speed"] = max(0.7, min(1.5, float(self._speed)))
            except ValueError:
                log.warning("GROK_VOICE_SPEED=%r is not a number", self._speed)
        session: dict[str, Any] = {
            "instructions": self._instructions(),
            "turn_detection": None,  # push-to-talk: we commit the turn ourselves
            "reasoning": {"effort": "none"},
            "audio": {
                "input": {"format": {"type": "audio/pcm", "rate": INPUT_RATE}, "transcription": transcription},
                "output": output,
            },
        }
        if self._voice:
            session["voice"] = self._voice
        offered = ([DELEGATE_TOOL] if self._delegate else []) + tools.grok_definitions()
        if offered:
            session["tools"] = offered
        return {"type": "session.update", "session": session}

    # ------------------------------------------------------------ turn API (called by ws.py)
    async def begin_turn(self, require_name: bool = False, wake: bool = False) -> None:
        await self.start()
        gated = require_name and not wake and self.wake_router is not None  # routed on its transcript, like a wake turn
        wake = wake or gated
        if not wake:  # room chatter must not cut Albie off; a named turn barges in once it's routed
            await self.stop_speech("barge_in")
        await self._send({"type": "input_audio_buffer.clear"})
        self._fed = 0
        self._drop_reply = False
        self._open_turn(wake)
        self._require_name = require_name and not self._wake  # a wake turn routes by name itself (agent/wake.py)
        if gated:
            self._gated = True
            self.wake_router.begin(None, gated=True)

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
        r = self.wake_router
        flags = (r.target, bool(r.facing), bool(r.gated)) if self._wake else (None, False, False)
        self._commits.append(_Commit(self._turn_seq, self._wake, self._gated, flags))
        if self._wake:  # wait for the transcript (wake_router decides in _final)
            job = asyncio.create_task(self._wake_timeout(self._turn_seq))
            self._jobs.add(job)
            job.add_done_callback(self._jobs.discard)
            return
        await self._request_response()
        await self._emit(ev.thinking(self.pet_id))
        guard = asyncio.create_task(self._no_reply_guard(self._turn_seq))
        self._jobs.add(guard)
        guard.add_done_callback(self._jobs.discard)

    async def _no_reply_guard(self, seq: int) -> None:
        """Grok drops a turn it hears no speech in (noise, a bump): no transcript, no response, no error.
        Without this the turn never ends and Unity stays in Thinking, not listening."""
        await asyncio.sleep(NO_REPLY_S)
        if seq == self._turn_seq and not self._final_sent and not self._response_active:
            log.info("grok sent nothing for %.0f s after commit: empty turn", NO_REPLY_S)
            self._response_requested = False
            await self._final("")

    async def _wake_timeout(self, seq: int) -> None:
        await asyncio.sleep(WAKE_FINAL_TIMEOUT_S)
        if seq == self._turn_seq and self._wake and not self._final_sent:  # a later segment's turn isn't ours to end
            self._timed_out_seq = seq
            log.info("wake turn: no transcript %.0f s after commit, deciding on %r", WAKE_FINAL_TIMEOUT_S, self._partial)
            await self._final()

    async def _late_transcript(self, c: _Commit, text: str) -> None:
        """A wake turn's transcript after its turn was superseded or timed out: route it now with its own flags."""
        text = text.strip()
        if is_stop(text):
            if has_name(text) or c.flags[1]:  # "Albie, stop", or "stop" while looking at him
                await self.stop_speech("barge_in")
            respond = False
        elif text and self.wake_router is not None:
            respond = (await self.wake_router.final(text, c.flags)).respond
        else:
            respond = False
        if respond:
            await self.stop_speech("barge_in")
            await self._request_response()
            await self._emit(ev.thinking(self.pet_id))
        elif c.item:
            await self._send({"type": "conversation.item.delete", "item_id": c.item})
        log.info("turn (late transcript): %r facing=%s gated=%s judged=%s %s", text[:120], c.flags[1],
                 c.gated or c.flags[2], getattr(self.wake_router, "judged", None), "answered" if respond else "dropped")

    async def _wake_decided(self, respond: bool) -> None:
        self._wake_answered = respond
        if respond:
            await self.stop_speech("barge_in")
            await self._request_response()
            await self._emit(ev.thinking(self.pet_id))
        elif self._last_item:
            await self._send({"type": "conversation.item.delete", "item_id": self._last_item})
            self._last_item = None

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
        elif self._playing_utt is not None and time.monotonic() < self._play_until:
            await self._send_event(ev.tts_stop(self.pet_id, self._playing_utt, reason))
        self._playing_utt, self._play_until = None, 0.0
        self._audio_started = False
        self._response_active = False

    # ------------------------------------------------------------ Grok I/O
    async def _send(self, obj: dict[str, Any]) -> None:
        ws = self._ws
        if ws is None:
            return
        self._last_activity = time.monotonic()
        try:
            async with self._send_lock:
                await ws.send(json.dumps(obj))
        except Exception as exc:  # noqa: BLE001 - the socket died under us (Wi-Fi blip): reconnect on the next turn
            # Live 18:19: one failed send used to switch voice off for the socket (ws.py _voice_call) until the
            # headset reconnected, and every turn after that came back empty from the stt.py fallback.
            log.warning("grok send failed (%s): dropping the socket, the next turn reconnects", exc)
            if self._ws is ws:
                await self._drop_socket()

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
        elif t == "input_audio_buffer.committed":
            item = msg.get("item_id")
            c = self._commits.popleft() if self._commits else None
            if c is not None and item:
                c.item = item
                self._by_item[item] = c
            if c is None or c.seq == self._turn_seq:
                self._committed = True
                self._last_item = item or self._last_item  # a dropped wake turn deletes it
        elif t == "conversation.item.input_audio_transcription.completed":
            c = self._by_item.pop(msg.get("item_id") or "", None)
            if c is not None and (c.seq != self._turn_seq or (self._final_sent and c.seq == self._timed_out_seq)):
                if c.wake:  # an earlier segment's words: never this turn's, and not lost either
                    await self._late_transcript(c, msg.get("transcript") or "")
                return
            # Grok also sends .completed per segment while the user is still talking; only the one after commit is final.
            if self._committed:
                await self._final(msg.get("transcript"))
            else:
                await self._transcript(msg.get("transcript") or "")
        elif t == "response.created":
            if self._partial:
                await self._final()  # no .completed yet: the last cumulative transcript is final
            if self._drop_reply:
                self._drop_reply = False
                self._response_active, self._response_requested, self._forwarding = True, False, False
                self._dropping = True
                await self._send({"type": "response.cancel"})
                return
            self._utt = uuid.uuid4().hex[:12]
            self._audio_started = False
            self._answer, self._answer_done = "", False
            self._forwarding = self._response_active = True
            self._response_requested = False
            self._response_suppressed, self._suppress_transcript = self._suppress_transcript, False
        elif t in ("response.output_audio.delta", "response.audio.delta"):
            delta = msg.get("delta")
            if delta:
                await self._audio(base64.b64decode(delta))
        elif t in ("response.output_audio_transcript.delta", "response.audio_transcript.delta"):
            if self._forwarding and msg.get("delta") and not self._response_suppressed:
                await self._final()
                self._answer += msg["delta"]
                await self._emit(ev.partial_answer(self.pet_id, self._answer))
        elif t in ("response.output_audio_transcript.done", "response.audio_transcript.done"):
            if self._forwarding and not self._response_suppressed:
                await self._answer_finished(msg.get("transcript") or self._answer)
        elif t == "response.done" and self._dropping:
            self._dropping = self._response_active = False
            self._pending_outputs.clear()
        elif t == "response.done":
            if self._forwarding:
                if self._audio_started and self._utt is not None:
                    await self._send_event(ev.tts_end(self.pet_id, self._utt))
                if self._answer and not self._answer_done:
                    await self._answer_finished(self._answer)
            self._forwarding = self._response_active = False
            self._audio_started = False
            self._response_suppressed = False
            if self._pending_outputs:  # tool results go back after the calling response, then ONE response.create
                outputs, self._pending_outputs = self._pending_outputs, []
                for item in outputs:
                    await self._send(item)
                await self._request_response()
        elif t == "response.function_call_arguments.done":
            await self._on_function_call(msg.get("name", ""), msg.get("call_id", ""), msg.get("arguments") or "{}")
        elif t == "error":
            err = msg.get("error") or {}
            message = err.get("message") if isinstance(err, dict) else str(err)
            # A dropped turn's item.delete, or a cancel of a reply that just ended, raced Grok: nothing lost. Shown as
            # "Voice error: ..." these made Albie look broken (live 18:48 "Cancellation failed: no active response").
            if any(m in (message or "").lower() for m in ("item not found", "no active response")):
                log.info("grok: %s (harmless)", message)
                return
            log.warning("grok error: %s", msg)
            self._response_requested = False
            if not self._final_sent:
                await self._final("")
            await self._emit(ev.error(self.pet_id, f"Voice error: {message or 'unknown'}"))
        elif t in ("session.created", "session.updated"):
            log.debug("grok %s", t)
        else:
            log.debug("grok: unhandled %s", t)

    async def _request_response(self) -> None:
        self._response_requested = True
        # Per-response instructions (xAI: overrides the session prompt for this reply) restate the length rule
        # right before Grok answers; in the session prompt alone it ignored it for "tell me everything".
        await self._send({"type": "response.create", "response": {"instructions": self._instructions() + BREVITY_NOW}})

    # ------------------------------------------------------------ tools
    async def _on_function_call(self, name: str, call_id: str, arguments: str) -> None:
        if self._dropping:
            return  # the reply to a "stop" turn is cancelled: run nothing
        try:
            args = json.loads(arguments) if isinstance(arguments, str) else dict(arguments)
        except (json.JSONDecodeError, TypeError, ValueError):
            args = {}
        await self._emit(ev.tool_call(self.pet_id, call_id, name, args))
        if name == "delegate" and self._delegate is not None and str(args.get("task", "")).strip():
            mode = args.get("mode") if args.get("mode") in ("qa", "code", "auto") else "auto"
            # Grok has no "pending" results: acknowledge now so it can say "on it", speak the answer later.
            result = {"status": "started", "note": "The answer will be spoken to the user when it is ready."}
            job = asyncio.create_task(self._run_delegate(str(args["task"]), mode))
            self._jobs.add(job)
            job.add_done_callback(self._jobs.discard)
        elif tools.is_lead_only(name) and self.workspace_mode is not None and self.workspace_mode != "code":
            result = CREW_OFF_CODE_SCREEN  # the crew lives on the CODE screen; Unity told us another view is open
        elif tools.has(name):
            # ponytail: awaited inline, so the reader waits on the tool (CAD regen about 1 s, VS Code under 5 s).
            result = await tools.call(name, args, self.pet_id)
        else:
            result = {"error": f"unknown tool {name!r}"}
        self._pending_outputs.append({"type": "conversation.item.create", "item": {
            "type": "function_call_output", "call_id": call_id, "output": json.dumps(result)}})

    async def _run_delegate(self, task: str, mode: str) -> None:
        assert self._delegate is not None
        try:
            answer = (await self._delegate(task, mode)).strip()
        except asyncio.CancelledError:
            raise
        except Exception:
            log.exception("delegate failed")
            answer = "Oops, my brain hiccuped on that one. Can you ask again?"
        if not answer:
            return  # cancelled / superseded: nothing to say
        await self.say(answer)

    async def note(self, text: str) -> None:
        """One line of context Grok reads but doesn't answer (a system message). While the socket is idle-closed
        the note is kept (last five) and sent after the next session.update, so a view change never reopens a
        paid session on its own."""
        if self._ws is None:
            self._pending_notes = (self._pending_notes + [text])[-5:]
            return
        await self._send(_note_item(text))

    async def say(self, text: str) -> None:
        """Speak text verbatim once Grok (and the user) are done talking; captions come from the runner."""
        deadline = time.monotonic() + DRAIN_TIMEOUT_S
        while (self._response_active or self._response_requested or self._pending_outputs or not self._final_sent) \
                and time.monotonic() < deadline:
            await asyncio.sleep(0.1)
        await self.start()
        self._suppress_transcript = True
        self._spoke(text)
        await self._send({"type": "conversation.item.create", "item": {
            "type": "force_message", "role": "assistant", "interruptible": True,
            "content": [{"type": "output_text", "text": text}]}})

    async def _final(self, text: str | None = None) -> None:
        if self._final_sent:
            return
        final = (text if text is not None else self._partial).strip()
        crew_pid = (self.wake_router.crew_target(final)
                    if not self._wake and self.wake_router is not None else None)
        if crew_pid is not None:
            # "Tester, run the hub tests" in one of Bit's own turns (hands-free, follow-up window, push-to-talk; the
            # WakeListener is paused then): the crew gator gets it, Bit says nothing and goes back to idle.
            self._drop_reply = True
            await super()._final("")
            await self.wake_router.final(final)
            return
        stop = is_stop(final)
        named = "partial" if self._named else "final" if has_name(final) else "no"
        drop = stop or (self._require_name and named == "no")
        if drop:
            self._drop_reply = True
            text = ""  # Unity treats an empty final as "nothing to answer" and goes back to idle
        wake = self._wake
        gated = self._gated or (wake and bool(self.wake_router.gated))  # read before the router resets it in final()
        facing = wake and bool(self.wake_router.facing)  # read before the router resets it in final()
        if stop and wake and (named != "no" or facing):
            # A wake / gated turn never barges in by itself (side talk must not cut Albie off): only "Albie, stop"
            # or "stop" while looking at him does. A plain "stop" to a teammate leaves him talking.
            await self.stop_speech("barge_in")
        await super()._final(text)
        answered = self._wake_answered if wake else bool(final)
        judged = self.wake_router.judged if wake else None
        log.info("turn: %r require_name=%s named=%s stop=%s wake=%s facing=%s gated=%s judged=%s %s", final[:120],
                 self._require_name, named, stop, wake, facing, gated, judged,
                 "answered" if answered and not drop else "dropped")

    async def _audio(self, pcm: bytes) -> None:
        if not self._forwarding or not pcm:
            return
        await self._final()
        if not self._audio_started:
            self._audio_started = True
            self._utt = self._utt or uuid.uuid4().hex[:12]
            await self._send_event(ev.tts_start(self.pet_id, self._utt, self.sample_rate))
        await self._send_bytes(pcm)
        self._playing_utt = self._utt
        self._play_until = max(self._play_until, time.monotonic()) + len(pcm) / 2 / self.sample_rate

    async def _answer_finished(self, text: str) -> None:
        self._answer_done = True
        self._spoke(text)
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

    async def begin_turn(self, require_name: bool = False, wake: bool = False) -> None:
        await self.start()
        if not wake:
            await self.stop_speech("barge_in")
        self._open_turn(wake)

    async def feed(self, pcm16: bytes) -> None:
        return None

    async def cancel_turn(self) -> None:
        self._cancelled = True

    async def commit_turn(self) -> None:
        if self._cancelled:
            await self._final("")
            return
        wake = self._wake
        await self._transcript(FAKE_TRANSCRIPT)
        await self._final(FAKE_TRANSCRIPT)
        if not wake:  # a wake turn answers from _wake_decided
            await self._wake_decided(True)

    async def _wake_decided(self, respond: bool) -> None:
        if respond:
            await self.stop_speech("barge_in")
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


def _note_item(text: str) -> dict[str, Any]:
    return {"type": "conversation.item.create", "item": {
        "type": "message", "role": "system", "content": [{"type": "input_text", "text": text}]}}


def view_note(mode: str) -> str:
    """The one-line note Grok gets when Unity switches workspace view (workspace.mode)."""
    return VIEW_NOTES.get(mode) or f"The user switched to the {mode.upper()} view; crew tools are off until they open CODE."


async def _ws_connect(url: str, api_key: str) -> Any:
    from websockets.asyncio.client import connect  # only needed with VOICE_PROVIDER=grok

    return await connect(url, additional_headers={"Authorization": f"Bearer {api_key}"},
                         max_size=None, open_timeout=10)


def make_voice(*, pet_id: str, client_rate: int | None, send_event: SendEvent, send_bytes: SendBytes,
               emit: SendEvent, repo: Path | None = None, delegate: Delegate | None = None) -> VoiceSession | None:
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
                                send_bytes=send_bytes, emit=emit, keyterms=keyterms_for(repo), delegate=delegate)
    if p != "none":
        log.warning("unknown VOICE_PROVIDER=%r: voice off", p)
    return None


def is_stop(text: str) -> bool:
    """Only stop words and filler ("Wait, stop, Albie.", "Never mind."): the user wants silence, not an answer."""
    words = re.findall(r"[a-z]+", text.lower().replace("never mind", "nevermind"))
    return bool(words) and set(words) <= STOP_CORE | STOP_FILLER and bool(set(words) & STOP_CORE)


NAME_WORDS = ALIASES["bit"]  # albie, albee, alby, elbie (agent/wake.py)


def has_name(text: str, last: bool = True) -> bool:
    """Albie is addressed: "Albie, ...", "Hey Albie ...", "Al bee, ...", "..., Albie?". Only the first two words,
    plus the last one when ``last`` (final transcripts only: a growing partial always ends on its newest word)."""
    words = re.findall(r"[a-z]+", text.lower())
    for i in (0, 1):  # "Al bee" at the start is the name heard as two words
        if words[i:i + 1] == [SPLIT_FIRST] and words[i + 1:i + 2] and words[i + 1] in SPLIT_SECOND:
            words[i:i + 2] = ["albie"]
            break
    return any(w in NAME_WORDS for w in words[:2] + (words[-1:] if last else []))


def keyterms_for(repo: Path | None) -> list[str]:
    # The crew's names right after Bit's, so "Hey Scout" isn't transcribed as "Hey Scott" (agent/wake.py routes by name).
    crew_names = [t.name for t in TYPES.values()]
    return list(dict.fromkeys(ROCKET_TERMS[:1] + crew_names + ROCKET_TERMS[1:] + vocab_terms(repo)))[:100]


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
