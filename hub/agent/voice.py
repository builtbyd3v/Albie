"""Speech-to-speech voice for the pet: one voice session per Unity socket.

VOICE_PROVIDER (env):
* ``none`` (default) no voice session; ws.py keeps the stt.py + runner path.
* ``grok``  Grok Voice over raw websockets (``wss://api.x.ai/v1/realtime``), key ``XAI_API_KEY``.
* ``fake``  canned reply from a bundled PCM file, no key, no network (Unity testing).

Grok handles conversation itself. Quick actions (open a view, the CAD design and its demo, Slack drafts with files,
VS Code highlight/open) are direct tools from
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
from . import wake as wake_mod
from .crew import TYPES, lead_rules
from .bus import tidy_paths
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
    "function names plainly, and never read a folder path aloud: just the file name. Transcripts often turn \"ML\" into \"email\": Forge has no email panel, so "
    "\"email\" means ML (the machine learning desk)."
)
# Last in the instructions, after the tool rules, so it wins over "tell me everything".
BREVITY = (
    " Most important rule: every reply is one or two short sentences, 20 words at most, under eight seconds out "
    "loud. Lead with the answer, no warm-up, no recap. Even if the user asks for everything or a long answer, give "
    "only the key point, then offer to say more. For a why, how or in-depth question: two short sentences with the "
    "main reason and its key numbers (which neuron, which class, how much accuracy), then ask 'Want the details?'; go deeper only after the user says yes, and even then keep it to "
    "three sentences. If the user asks you to do something (open, play, pause, close, make, change, show, run, set, "
    "add), call the tool for it first and never say it is done without the tool call; then confirm in one short "
    "sentence, under 10 words."  # in the session prompt too: a turn's own reply is Grok's, without BREVITY_NOW
)
BREVITY_NOW = (" If the user asked you to do something (open, play, pause, close, make, change, show, run, set, add), call "
               "the tool for it in this reply first; never say it is done without the tool call. Then confirm in one "
               "short sentence, under 10 words. A question: at most 20 words; an in-depth question: the key point "
               "then 'Want the details?'. Reading a tool result: say only the one or two numbers that answer the "
               "user, never a list of values.")  # live 03:34, 03:38, 03:43: "Pause the movie" got a reply and no tool
# Grok ignores max_output_tokens / max_response_output_tokens (probed 2026-10-24: 100 s answers either way), so the
# hub cuts a reply after this much of its audio. Unity plays it 1.2x faster (pitch), and audio arrives in chunks
# (a cut at 12 s landed at 13.4 s), so 10 s here is ~8.5-9.5 s heard. 0 = no cut (default since 2026-10-25: Dev
# heard it cut answers mid-sentence; BREVITY keeps them short).
MAX_REPLY_S = float(os.getenv("VOICE_MAX_REPLY_S", "0"))
# Grok still talks 15-26 s for "tell me everything" (lab 2026-10-25). At response.done the hub has the whole text and
# audio, so playback ends at the last sentence break before this many seconds heard: never mid-sentence. 0 = off.
MAX_HEARD_S = float(os.getenv("VOICE_MAX_HEARD_S", "12"))
PLAYBACK_RATE = float(os.getenv("VOICE_PLAYBACK_RATE", "1.2"))  # Unity's StreamedPcmTextToSpeech.pitch: it plays this x faster
SAY_MAX = 150  # a verbatim line (runner answer, proactive line) is cut to whole sentences within this many characters
# A wake turn's audio can start with room talk before the user said "Albie" (and grok-transcribe repeats the last turn).
ADDRESSED_NOW = " Answer only what the user asked you after saying your name; ignore anything said before it."
DELEGATE_RULES = (
    " Your delegate tool is for anything needing real analysis of the user's code, repository, files or project, "
    "and for any request to write, fix or change code or to create a new file (except ML training: the ml tools "
    "handle that, never delegate it); use mode code "
    "for those, and the brain edits the files open in VS Code right where the user sees them. Never guess those "
    "details yourself. Give it a self-contained task that includes what the user asked and any context from the "
    "conversation. After calling it, say only a tiny 'on it' line: the answer is spoken for you when it's "
    "ready. Answer everything else yourself. When a system note says what broke and you just offered to fix it or "
    "look at it, a yes means call delegate with mode code at once, with the file, line and error from that note; a "
    "no means say okay and drop it."
)
TOOL_RULES = (
    " Use your other tools yourself for quick actions: call them instead of guessing, then say in one short line what "
    "you did. You run the whole workspace. ui_open_view opens a view at the desk (cad is 3D modeling, code is VS Code, "
    "ml, messages is the Slack messenger); messages_open opens one conversation in it (a Slack channel, someone's "
    "DM, or an iMessage chat). The CAD tools open the CAD view themselves: cad_open_part opens a design "
    "(rocket, quadcopter, gearbox), cad_set_params and cad_apply_preset change it, cad_view expands it big in front of "
    "the user, explodes it or shows markers and dimensions, cad_take_part hands the user one part or puts it back, cad_demo shows it working (the rocket flies, the "
    "quadcopter hovers, the gearbox turns), and cad_undo or cad_restore_version go back. When one request asks for "
    "several things, do every one, one tool after another, in the order asked. If a tool returns an error or a "
    "view_error, say it plainly. "
    "Open, highlight or check code in VS Code yourself: to open or show a file that exists, call vscode_open_file "
    "(vscode_get_context first if they say 'this' or 'my code'), to point at lines call vscode_highlight. Never use "
    "the crew (spawn_agent, assign_task) to open, show, create or edit a file: the crew works on its own branch, "
    "which the user can't see in VS Code. Use the crew only when the user names a crew member or asks for help in "
    "parallel. "
    "ML (machine learning) requests always go to your ml tools, never to delegate: opening the ML project or 'the "
    "ML stuff' is ml_open (not ui_open_view), pulling up or importing a repo from GitHub is ml_github, 'how is my model doing' or the last score is ml_status, 'what can I do "
    "better' or 'improve it' is ml_improve, a yes or 'apply it' after an ml_improve suggestion is ml_apply, 'undo "
    "that' is ml_undo, and 'train it again' is ml_train. The Neural Net Lab (a small network learning 2D data, "
    "TensorFlow Playground style) is nn_open, nn_control, nn_neuron and nn_status; 'why did the accuracy drop' or "
    "'what does this neuron do' there is nn_status, then answer with the neuron, what it detects and the numbers; "
    "'turn them all back on' is nn_control restore_all; change only what the user named in nn_control; "
    "'why is it bad', 'fix it' or 'improve it' there is nn_improve, and a yes after it is nn_apply. Opening the "
    "neural net (lab, playground, 'neural net training') is nn_open, never ui_open_view; going back to the ML "
    "project or the training dashboard from it is nn_close. "
    "Set the focus timer, keep the user's notes (one notes_add call per item: 'add X and Y' is two calls), and play "
    "the movie on the big screen with your desk tools. "
    "Slack and iMessage replies go out under the user's own account and are never sent directly: messages_send only "
    "writes a draft that pops up for them to confirm, and its attach option puts the open design's files on a Slack "
    "draft (STEP model, STL, drawing PDF, parts list; never delegate that). messages_edit_draft rewords the open draft "
    "and keeps its files. Say what you wrote and ask them to tap Send or say 'send it'; messages_confirm is for "
    "their yes or no."
)
IGNORED_NOTE = ("The user's last message was not said to you (background audio, a film, or talk to someone else). "
                "Ignore it completely: never act on it, answer it or mention it.")
CREW_OFF_CODE_SCREEN = {"error": "crew tools are only available on the code screen; ask the user to open CODE"}
VIEW_NOTES = {
    "code": "The user switched to the CODE view (VS Code on the main screen); crew tools are available now.",
    "cad": "The user switched to the CAD view (the open design on the desk); crew tools are off until they open CODE.",
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
# The rest of the CAD library and what Albie does with it (views, demos, the files he sends).
DESIGN_TERMS = ["quadcopter", "gearbox", "pinion", "shaft", "props", "prop clearance", "hexacopter", "explode",
                "STEP file", "STL", "Slack", "CAD"]
# The ML desk and the Neural Net Lab (lab 2026-10-25: "pytorch cifar" came back as "Python cipher", "ReLU" as "VLU").
ML_TERMS = ["PyTorch", "CIFAR", "pytorch-cifar", "GitHub", "neural net lab", "ReLU", "tanh", "sigmoid", "XOR", "spiral",
            "epochs", "validation accuracy", "overfitting", "humanoid", "Iron Man"]
# "Stop" over Albie should just stop him, not start a reply ("Okay, quiet!").
STOP_CORE = {"stop", "wait", "quiet", "shh", "shush", "hush", "enough", "nevermind", "shutup", "cancel"}  # "shut up" -> shutup
STOP_FILLER = ALIASES["bit"] | {SPLIT_FIRST} | SPLIT_SECOND | {"okay", "ok", "please", "hey", "be", "that", "it", "never",
                                                                "mind", "now", "right"}
HOLD_S = 6.0  # a held half-thought (addressed.unfinished) with no next segment by then is answered as it is
NO_REPLY_S = 8.0  # committed turn with no transcript and no response from Grok by then: treat it as empty (4 s gave up on late replies on the hotspot, live 01:40)
DRAIN_TIMEOUT_S = 20.0  # longest we wait for Grok to finish talking before speaking a delegate answer
UNDOABLE_TOOLS = {"cad_set_params", "cad_apply_preset", "cad_restore_version", "cad_reset"}  # undone by cad_undo
DUPLICATE_S = 20.0  # the same transcript again within this is the same turn (live 22:26: one sentence, three turns)
WAKE_FINAL_TIMEOUT_S = 8.0  # a wake turn with no transcription.completed by then is decided on the last partial
# (5 s decided on partials over the phone hotspot, live 19:29-19:30; Unity's PushToTalk finalTimeout is 10 s)
GROK_PING_TIMEOUT_S = 60.0  # the hotspot delayed keepalive pongs past websockets' 20 s default: 4 drops in 11 min


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
        self._prev_raw = ""  # the last turn's transcript as Grok sent it (it shows up again at the start of the next)
        self._partial_raw = ""
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
        self._partial_raw = ""
        self._final_sent = False
        self._committed = False
        self._cancelled = False
        self._wake = wake and self.wake_router is not None
        self._gated = False

    def _fresh(self, text: str) -> str:
        """This turn's words only. grok-transcribe starts a new item's transcript with the previous turn's text (probed
        2026-10-24: after "Can you run the rocket file...", "Albie, what is two plus two?" came back as both), so one
        "Albie" late in a turn made earlier room chatter count. The carried-over prefix is cut off."""
        prev = _TOKENS.findall(self._prev_raw.lower())
        words = list(_TOKENS.finditer(text))
        if len(prev) >= 2 and len(words) > len(prev) and [w.group().lower() for w in words[:len(prev)]] == prev:
            return text[words[len(prev) - 1].end():].lstrip(" ,.!?;:-")
        return text

    async def _transcript(self, text: str) -> None:
        if self._final_sent or not text:
            return
        self._partial_raw, text = text, self._fresh(text)
        if not text or text == self._partial:
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

    async def addressed(self, text: str, facing: bool = False) -> bool:
        """WakeRouter.judge: was this unnamed sentence said to Albie? His last line (and how long ago) is the context."""
        age = time.monotonic() - self.last_reply_t if self.last_reply_t else None
        if wake_mod.name_needed() and time.monotonic() - getattr(self, "_held_t", 0.0) < HOLD_S + 3:
            return True  # the rest of a half-thought he is holding
        if wake_mod.name_needed():  # the movie is on: a desk request is for him; otherwise ask whether it was the film
            return bool(wake_mod.DESK_REQUEST.search(text)) or await addressed.said_to_albie(text, self.last_reply or None)
        return await addressed.judge(text, self.last_reply or None, age, facing=facing)

    def _spoke(self, text: str) -> None:
        if text.strip():
            self.last_reply, self.last_reply_t = text.strip(), time.monotonic()


def _just_the_name(text: str) -> bool:
    words = [w.lower() for w in re.findall(r"[A-Za-z']+", text)]
    return bool(words) and set(words) <= wake_mod.GREETINGS | wake_mod.ALIASES["bit"] and \
        bool(set(words) & wake_mod.ALIASES["bit"])


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
        # Replies the hub asked for (response.create, or a say() force_message) and hasn't seen start yet. Grok starts a
        # reply on its own after every commit whatever turn_detection says (probed 2026-10-24: null, {"type": null}
        # and server_vad + create_response false all did), so a response.created nobody asked for is cancelled.
        self._expected = 0
        self._dropped_items: list[str] = []  # output items of a cancelled reply, deleted at its response.done
        self.on_stop: Callable[[], Awaitable[None]] | None = None  # ws.py: "Albie, stop" also cancels his running task
        self._last_heard, self._last_heard_t = "", 0.0  # the same transcript twice within DUPLICATE_S is one turn
        # After "Stopped. Undo, continue, or leave it?": until choice_until a bare "undo" / "continue" / "leave it"
        # is the answer (ws.py on_choice runs it), no name needed.
        self.choice_until = 0.0
        self._held_t, self._hold_keep = 0.0, False  # last held half-thought (addressed.unfinished); keep its wake item
        self.on_choice: Callable[[str], Awaitable[None]] | None = None
        self.last_change: tuple[str, float] | None = None  # (tool, when) of the last CAD change Grok made: Undo-able
        self.last_ml: tuple[str, dict[str, Any]] | None = None  # the last training run Grok started (ml_apply / ml_train)
        self._response_suppressed = False
        self._response_requested = False  # response.create sent, response.created not seen yet
        self._create_after_drop: bool | None = None  # a response.create waiting for a cancelled reply to end
        self._dropping_id: str | None = None  # Grok's id of the reply being cancelled
        self._idle_close_s = float(os.getenv("VOICE_IDLE_CLOSE_S", "120"))  # 20 s cost a reconnect (up to 6 s on the hotspot) after every quiet spell
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
        self._timed_out_answered = False  # ...and answered it on the partial: its late transcript must not answer again
        self._wake_answered = False  # the last wake turn was routed to a reply (for the turn log)
        # current response
        self._utt: str | None = None
        self._audio_started = False
        self._utt_s = 0.0  # audio seconds of the current reply (MAX_REPLY_S)
        self._utt_t0 = 0.0  # when its first audio went to Unity
        self._last_utt_s = 0.0  # audio seconds of the last finished reply (Unity may still be playing it)
        self._commit_t = 0.0  # when the open turn was committed (latency log)
        # Grok sends speech faster than real time, so Unity is often still playing after response.done.
        self._playing_utt: str | None = None
        self._play_until = 0.0  # monotonic time Unity's queued speech runs out
        self._played_t = 0.0  # when the last reply stopped playing (stop_speech), for the age of "Yeah." after it
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
        self._dropping, self._create_after_drop = False, None  # no response.done will come for a cancel on the old one
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
        self._commit_t = time.monotonic()
        r = self.wake_router
        flags = (r.target, bool(r.facing), bool(r.gated)) if self._wake else (None, False, False)
        self._commits.append(_Commit(self._turn_seq, self._wake, self._gated, flags))
        if self._wake:  # wait for the transcript (wake_router decides in _final)
            job = asyncio.create_task(self._wake_timeout(self._turn_seq))
            self._jobs.add(job)
            job.add_done_callback(self._jobs.discard)
            return
        # Grok answers every commit by itself, from the committed audio: that reply is the answer. A response.create
        # sent with the commit could be generated before Grok added the new audio item, so it answered the previous
        # turn again with no tool (live 03:56, 03:58: "Play Iron Man" replayed the ML desk line).
        self._response_requested = True
        self._expected += 1
        await self._emit(ev.thinking(self.pet_id))
        guard = asyncio.create_task(self._no_reply_guard(self._turn_seq))
        self._jobs.add(guard)
        guard.add_done_callback(self._jobs.discard)

    async def _no_reply_guard(self, seq: int) -> None:
        """Grok drops a turn it hears no speech in (noise, a bump): no transcript, no response, no error.
        Without this the turn never ends and Unity stays in Thinking, not listening."""
        await asyncio.sleep(NO_REPLY_S / 2)
        if seq == self._turn_seq and not self._final_sent and not self._response_active and self._committed:
            # Grok confirmed the audio, then sent nothing (lab: 2 of ~2,000 turns, "How's my model doing?" lost): ask
            # once, halfway, so the turn still ends after NO_REPLY_S (Unity is deaf while it waits).
            log.info("grok sent nothing for %.0f s after a confirmed commit: asking for the reply again", NO_REPLY_S / 2)
            await self._request_response()
        await asyncio.sleep(NO_REPLY_S / 2)
        if seq == self._turn_seq and not self._final_sent and not self._response_active:
            log.info("grok sent nothing for %.0f s after commit: empty turn", NO_REPLY_S)
            self._response_requested = False
            self._expected = max(0, self._expected - 1)  # its ask is abandoned: a late reply must not answer the next turn
            await self._final("")

    async def _wake_timeout(self, seq: int) -> None:
        await asyncio.sleep(WAKE_FINAL_TIMEOUT_S)
        if seq == self._turn_seq and self._wake and not self._final_sent:  # a later segment's turn isn't ours to end
            self._timed_out_seq = seq
            log.info("wake turn: no transcript %.0f s after commit, deciding on %r", WAKE_FINAL_TIMEOUT_S, self._partial)
            await self._final()
            self._timed_out_answered = self._wake_answered

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
            await self._forget(c.item)
        log.info("turn (late transcript): %r facing=%s gated=%s judged=%s %s", text[:120], c.flags[1],
                 c.gated or c.flags[2], getattr(self.wake_router, "judged", None), "answered" if respond else "dropped")

    async def _wake_decided(self, respond: bool) -> None:
        self._wake_answered = respond
        if respond:
            await self.stop_speech("barge_in")
            await self._request_response(addressed=True)
            await self._emit(ev.thinking(self.pet_id))
        elif self._last_item and not self._hold_keep:
            await self._forget(self._last_item)
            self._last_item = None
        self._hold_keep = False

    async def _forget(self, item: str) -> None:
        """Drop a turn that was not for Albie. Grok answers item.delete of a user audio item with "Item not found" and
        keeps it (probed 2026-10-25: a deleted "my secret word is pineapple" was still known), and in the lab Albie then
        acted on the film's "I need a suit". So he is also told, in a system note, to ignore it."""
        await self._send({"type": "conversation.item.delete", "item_id": item})
        await self._send(_note_item(IGNORED_NOTE))

    async def cancel_turn(self) -> None:
        self._cancelled = True
        self._fed = 0
        await self._send({"type": "input_audio_buffer.clear"})

    async def stop_speech(self, reason: str = "cancel") -> None:
        """Stop forwarding first, then tell Grok and Unity (no stale chunk can follow tts.stop)."""
        heard = self._heard_so_far() if reason in ("barge_in", "cancel") else None
        was_active, self._forwarding = self._response_active, False
        if was_active and self._ws is not None:
            await self._send({"type": "response.cancel"})
        if heard is not None:
            # Grok keeps the whole cut reply in its conversation and picked it up again in the next answer (live
            # 2026-10-25). conversation.item.truncate gets no ack from Grok (probed), so tell it what was heard.
            await self._send(_note_item(f"The user cut you off. They heard only: \"{heard} ...\". Do not repeat or "
                                        "continue that answer unless they ask for it; answer what they say next."))
        if self._audio_started and self._utt is not None:
            await self._send_event(ev.tts_stop(self.pet_id, self._utt, reason))
        elif self._playing_utt is not None and time.monotonic() < self._play_until:
            await self._send_event(ev.tts_stop(self.pet_id, self._playing_utt, reason))
        self._played_t = max(self._played_t, min(time.monotonic(), self._play_until))  # when the user last heard him
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
                if c.seq == self._timed_out_seq and self._timed_out_answered:
                    return  # answered on its partial already (live 19:29: "...new room" answered twice)
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
            unasked = self._expected == 0
            self._expected = max(0, self._expected - 1)
            if self._drop_reply or unasked:
                # Live 22:25-22:30: Grok's own replies to unaddressed turns ran delegate / cad_open_part, talked at
                # length (no BREVITY_NOW) and ignored "Albie, stop". Cancel it; its tool calls are skipped (_dropping).
                if unasked:
                    log.info("grok started a reply nobody asked for: cancelled")
                self._drop_reply = False
                self._response_active, self._response_requested, self._forwarding = True, False, False
                self._dropping = True
                self._dropping_id = (msg.get("response") or {}).get("id")
                await self._send({"type": "response.cancel"})
                return
            self._utt = uuid.uuid4().hex[:12]
            self._audio_started = False
            self._utt_s = 0.0
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
        elif t in ("response.output_item.added", "conversation.item.added") and self._dropping:
            item = (msg.get("item") or {}).get("id")
            if item:
                self._dropped_items.append(item)  # its function call / message must not steer later turns
        elif t == "response.done" and self._dropping and (not self._dropping_id or not _resp_id(msg)
                                                           or _resp_id(msg) == self._dropping_id):
            # (only the cancelled reply's own done: lab 08:25, a tool reply's done taken for it lost the tool results)
            self._dropping = self._response_active = False
            self._pending_outputs.clear()
            items, self._dropped_items = self._dropped_items, []
            for item in items:
                await self._send({"type": "conversation.item.delete", "item_id": item})
            pending, self._create_after_drop = self._create_after_drop, None
            if pending is not None:
                await self._request_response(addressed=pending)
        elif t == "response.done":
            if self._forwarding:
                if self._audio_started and self._utt is not None:
                    self._last_utt_s = self._utt_s
                    log.info("reply: %.1f s of audio", self._utt_s)  # how long-winded he was
                    cut = sentence_cut(self._answer, self._utt_s / PLAYBACK_RATE, MAX_HEARD_S)
                    if cut is not None:
                        self._spawn(self._trim(self._utt, self._utt_t0 + cut[0], cut[1]))
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
            low = (message or "").lower()
            if any(m in low for m in ("item not found", "no active response")):
                log.info("grok: %s (harmless)", message)
                return
            if "response" in low and ("active" in low or "in progress" in low):
                # Our response.create landed while Grok's own reply to the commit was running: that one is the answer.
                self._expected = max(0, self._expected - 1)
                self._response_requested = False
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

    async def _request_response(self, addressed: bool = False) -> None:
        self._response_requested = True
        if self._dropping:
            # A reply Grok started on its own is still being cancelled: a create now is rejected ("active response")
            # and the turn got no answer at all (lab 07:34, "Close everything" while the movie played). After its done.
            self._create_after_drop = addressed
            return
        self._expected += 1
        # Per-response instructions (xAI: overrides the session prompt for this reply) restate the length rule
        # right before Grok answers; in the session prompt alone it ignored it for "tell me everything".
        extra = BREVITY_NOW + (ADDRESSED_NOW if addressed else "")
        await self._send({"type": "response.create", "response": {"instructions": self._instructions() + extra}})

    # ------------------------------------------------------------ tools
    async def _on_function_call(self, name: str, call_id: str, arguments: str) -> None:
        if self._dropping:
            return  # the reply to a "stop" turn is cancelled: run nothing
        try:
            args = json.loads(arguments) if isinstance(arguments, str) else dict(arguments)
        except (json.JSONDecodeError, TypeError, ValueError):
            args = {}
        await self._emit(ev.tool_call(self.pet_id, call_id, name, args))
        log.info("tool: %s %s", name, json.dumps(args)[:200])  # which tool a request was routed to (VS Code vs crew)
        if name in UNDOABLE_TOOLS:
            self.last_change = (name, time.monotonic())
        if name in ("ml_apply", "ml_train"):
            self.last_ml = (name, args)  # "Albie, stop" while that run trains: undo (ml_undo) or train again
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
        await self.say(tidy_paths(answer))  # spoken verbatim: "/Users/dev/.../ml/train.py" is read out as "train.py"

    async def note(self, text: str) -> None:
        """One line of context Grok reads but doesn't answer (a system message). While the socket is idle-closed
        the note is kept (last five) and sent after the next session.update, so a view change never reopens a
        paid session on its own."""
        if self._ws is None:
            self._pending_notes = (self._pending_notes + [text])[-5:]
            return
        await self._send(_note_item(text))

    async def say(self, text: str, context: str | None = None) -> None:
        """Speak text verbatim once Grok (and the user) are done talking; captions come from the runner.
        `context` is a system note sent right before it: Grok reads it with the line but never says it."""
        deadline = time.monotonic() + DRAIN_TIMEOUT_S
        while (self._response_active or self._response_requested or self._pending_outputs or not self._final_sent) \
                and time.monotonic() < deadline:
            await asyncio.sleep(0.1)
        await self.start()
        if context:
            await self._send(_note_item(context))
        text = short_line(text)
        self._suppress_transcript = True
        self._spoke(text)
        self._expected += 1  # a force_message also starts a response
        await self._send({"type": "conversation.item.create", "item": {
            "type": "force_message", "role": "assistant", "interruptible": True,
            "content": [{"type": "output_text", "text": text}]}})

    async def _final(self, text: str | None = None) -> None:
        if self._final_sent:
            return
        raw = text if text is not None else self._partial_raw
        final = (self._fresh(text) if text is not None else self._partial).strip()
        if raw.strip():
            self._prev_raw = raw.strip()
        if text is not None:
            text = final  # the router and Unity get this turn's words only
        crew_pid = (self.wake_router.crew_target(final)
                    if not self._wake and self.wake_router is not None else None)
        if crew_pid is not None:
            # "Tester, run the hub tests" in one of Bit's own turns (hands-free, follow-up window, push-to-talk; the
            # WakeListener is paused then): the crew gator gets it, Bit says nothing and goes back to idle.
            self._drop_reply = True
            await super()._final("")
            await self.wake_router.final(final)
            return
        if final and final == self._last_heard and time.monotonic() - self._last_heard_t < DUPLICATE_S \
                and not (is_stop(final) or has_named_stop(final)):  # "Albie, stop" twice is meant twice
            # Live 22:26: one sentence came back as three turns 3-14 s apart; act on it once.
            self._drop_reply = True
            log.info("turn: duplicate of the last transcript, ignored")
            await super()._final("")
            return
        if final:
            self._last_heard, self._last_heard_t = final, time.monotonic()
        choice = stop_choice(final) if final and time.monotonic() < self.choice_until else None
        if choice is not None and self.on_choice is not None:
            self.choice_until = 0.0
            self._drop_reply = True  # the hub answers it (undo / continue / leave), not Grok
            log.info("turn: %r -> stop choice %s", final[:80], choice)
            await super()._final("")
            await self.on_choice(choice)
            return
        stop = is_stop(final) or has_named_stop(final)
        named = "partial" if self._named else "final" if has_name(final) else "no"
        # An empty finished transcript: noise, or words grok-transcribe lost. Grok still answered such turns from the
        # audio, and guessed wrong both times in the lab ("I'm Albie, not MegaGrok"), so it is dropped.
        drop = stop or (self._require_name and named == "no") or (text is not None and not final and not self._wake)
        held = None
        if not drop and (not self._wake or wake_mod.name_needed()) and not wake_mod.NAME_REQUIRED:
            # (movie on: wake turns are judged, and a half-thought is still held, lab 07:0x "Close the ... movie.")
            # Counted from when Unity finished playing it: Grok streams a 13 s offer in ~1 s, so "Yeah." after it was
            # 14 s "old" and held as filler (live 2026-10-25 11:06).
            end = max(self.last_reply_t, self._played_t, min(time.monotonic(), self._play_until))
            age = time.monotonic() - end if self.last_reply_t else None
            held = addressed.unfinished(final, self.last_reply or None, age, complete=text is not None)
            if held and _just_the_name(final):
                held = None  # "Hey Albie": he answers "yes?" now
        if held:
            # Not answered yet, but its audio item stays in Grok's conversation: the next segment's reply covers both.
            drop = True
            self._held_t, self._hold_keep = time.monotonic(), self._wake  # a held wake turn is kept, not forgotten
            if self._response_active:
                await self.stop_speech("cancel")  # Grok started before the transcript came in
            if held == "partial":
                job = asyncio.create_task(self._answer_held(self._turn_seq))
                self._jobs.add(job)
                job.add_done_callback(self._jobs.discard)
        if drop:
            self._drop_reply = True
            text = ""  # Unity treats an empty final as "nothing to answer" and goes back to idle
        wake = self._wake
        gated = self._gated or (wake and bool(self.wake_router.gated))  # read before the router resets it in final()
        facing = wake and bool(self.wake_router.facing)  # read before the router resets it in final()
        if stop and (not wake or named != "no" or facing or wake_mod.name_needed()):  # movie on: a bare "stop" still works
            # A wake / gated turn never barges in by itself (side talk must not cut Albie off): only "Albie, stop"
            # or "stop" while looking at him does. A plain "stop" to a teammate leaves him talking. A stop also ends
            # the code task he is running (live 22:30: "Albie, stop" and the runner kept editing).
            await self.stop_speech("barge_in")
            self._pending_outputs.clear()
            if self.on_stop is not None:
                await self.on_stop()
        await super()._final(text)
        answered = self._wake_answered if wake else bool(final)
        judged = self.wake_router.judged if wake else None
        after = f" ({time.monotonic() - self._commit_t:.1f} s after commit)" if self._commit_t else ""
        log.info("turn: %r require_name=%s named=%s stop=%s wake=%s facing=%s gated=%s judged=%s %s%s", final[:120],
                 self._require_name, named, stop, wake, facing, gated, judged,
                 f"held ({held})" if held else "answered" if answered and not drop else "dropped", after)

    async def _answer_held(self, seq: int) -> None:
        """Nobody finished the half-thought: answer it after all (unless a new turn began)."""
        await asyncio.sleep(HOLD_S)
        if seq == self._turn_seq and self._ws is not None and not self._response_active:
            log.info("held turn: nothing followed in %.0f s, answering it", HOLD_S)
            self._drop_reply = False  # the hold's own drop flag must not cancel this reply
            await self._request_response()
            await self._emit(ev.thinking(self.pet_id))

    async def _audio(self, pcm: bytes) -> None:
        if not self._forwarding or not pcm:
            return
        await self._final()
        if not self._audio_started:
            self._audio_started = True
            if self._commit_t:  # latency: end of speech (commit) to Albie's first audio
                log.info("first audio %.1f s after commit", time.monotonic() - self._commit_t)
                self._commit_t = 0.0
            self._utt = self._utt or uuid.uuid4().hex[:12]
            self._utt_t0 = time.monotonic()
            await self._send_event(ev.tts_start(self.pet_id, self._utt, self.sample_rate))
        await self._send_bytes(pcm)
        self._playing_utt = self._utt
        self._play_until = max(self._play_until, time.monotonic()) + len(pcm) / 2 / self.sample_rate
        self._utt_s += len(pcm) / 2 / self.sample_rate
        if MAX_REPLY_S and self._utt_s >= MAX_REPLY_S:  # long-winded: end it here (Unity plays what it has, then tts.end)
            log.info("reply cut at %.0f s of audio", self._utt_s)
            self._forwarding = False
            await self._send({"type": "response.cancel"})
            await self._send_event(ev.tts_end(self.pet_id, self._utt))
            if self._answer and not self._answer_done:
                await self._answer_finished(self._answer)

    def _spawn(self, coro) -> None:
        job = asyncio.create_task(coro)
        self._jobs.add(job)
        job.add_done_callback(self._jobs.discard)

    async def _trim(self, utt: str, at: float, said: str) -> None:
        """End a long reply at a sentence break (see MAX_HEARD_S), unless it was already stopped or replaced."""
        await asyncio.sleep(max(0.0, at - time.monotonic()))
        if self._playing_utt != utt or time.monotonic() >= self._play_until:
            return
        log.info("reply trimmed at a sentence break: %r", said[-60:])
        await self._send_event(ev.tts_stop(self.pet_id, utt, "cancel"))
        self._played_t = time.monotonic()
        self._playing_utt, self._play_until = None, 0.0
        await self.note(f"Your last reply was too long and was cut after: \"{said[-120:]}\". Keep the next ones shorter.")

    def _heard_so_far(self) -> str | None:
        """The words of Albie's current reply the user has heard (Unity plays PLAYBACK_RATE x faster than the stream),
        or None if nothing is playing or it was nearly all heard."""
        now = time.monotonic()
        if self._audio_started and self._utt is not None:
            text, total = self._answer, self._utt_s
        elif self._playing_utt is not None and now < self._play_until:
            text, total = self.last_reply, self._last_utt_s
        else:
            return None
        played = (now - self._utt_t0) * PLAYBACK_RATE
        words = text.split()
        if not words or total <= 0 or played >= 0.9 * total:
            return None
        return " ".join(words[:max(1, int(len(words) * played / total))])

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

    async def _forget(self, item: str) -> None:
        """Drop a turn that was not for Albie. Grok answers item.delete of a user audio item with "Item not found" and
        keeps it (probed 2026-10-25: a deleted "my secret word is pineapple" was still known), and in the lab Albie then
        acted on the film's "I need a suit". So he is also told, in a system note, to ignore it."""
        await self._send({"type": "conversation.item.delete", "item_id": item})
        await self._send(_note_item(IGNORED_NOTE))

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


def _resp_id(msg: dict[str, Any]) -> str | None:
    return (msg.get("response") or {}).get("id")


def _note_item(text: str) -> dict[str, Any]:
    return {"type": "conversation.item.create", "item": {
        "type": "message", "role": "system", "content": [{"type": "input_text", "text": text}]}}


def view_note(mode: str) -> str:
    """The one-line note Grok gets when Unity switches workspace view (workspace.mode)."""
    return VIEW_NOTES.get(mode) or f"The user switched to the {mode.upper()} view; crew tools are off until they open CODE."


async def _ws_connect(url: str, api_key: str) -> Any:
    from websockets.asyncio.client import connect  # only needed with VOICE_PROVIDER=grok

    return await connect(url, additional_headers={"Authorization": f"Bearer {api_key}"},
                         max_size=None, open_timeout=10, ping_timeout=GROK_PING_TIMEOUT_S)


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


_TOKENS = re.compile(r"[A-Za-z0-9']+")


CHOICE_UNDO = {"undo", "revert", "reverse", "back"}
CHOICE_GO = {"continue", "resume", "going", "finish"}  # "keep going", "go on" is matched below


_NO_UNDO = re.compile(r"\b(?:don'?t|do not|never|no need to)\s+(?:\w+\s+)?(?:" + "|".join(sorted(CHOICE_UNDO)) + r")\b")


def stop_choice(text: str) -> str | None:
    """The answer to "Undo, continue, or leave it?": "undo" / "continue" / "leave", else None. A negated undo ("don't
    undo, keep going") is never undo: undo rewrites files."""
    low = text.lower().replace("\u2019", "'")
    words = set(re.findall(r"[a-z]+", low))
    negated = bool(_NO_UNDO.search(low))
    if words & CHOICE_UNDO and not negated:
        return "undo"
    if words & CHOICE_GO or "go on" in low or "pick up" in low:
        return "continue"
    if negated or "leave" in words or words & {"nothing", "nope"} or low.strip(" .!") in ("no", "never mind"):
        return "leave"
    return None


def sentence_cut(text: str, heard_s: float, cap_s: float) -> tuple[float, str] | None:
    """(seconds into playback, text up to there) of the last sentence end before cap_s heard, for a reply of heard_s
    seconds; None if it fits. Speech time is taken as proportional to characters (TTS speaks at an even pace)."""
    if not cap_s or heard_s <= cap_s or not text.strip():
        return None
    ends = [m.end() for m in re.finditer(r"[.!?](?=\s|$)", text)]
    fits = [e for e in ends if heard_s * e / len(text) + 0.3 <= cap_s and e < len(text.rstrip())]
    if not fits:
        return None
    end = fits[-1]
    return heard_s * end / len(text) + 0.3, text[:end].strip()  # 0.3 s: the pause after the full stop


def short_line(text: str) -> str:
    """Whole sentences within SAY_MAX characters (at least the first, cut at a word): what Albie says verbatim."""
    text = " ".join(text.split())
    if len(text) <= SAY_MAX:
        return text
    out = ""
    for sentence in re.findall(r"[^.!?]+[.!?]*", text):
        if out and len(out) + len(sentence) > SAY_MAX:
            break
        out += sentence
    out = out.strip()
    return out if len(out) <= SAY_MAX else out[:SAY_MAX].rsplit(" ", 1)[0] + "..."


_RETRACT = re.compile(r"\b(?:let it (?:run|go|finish|keep|train)|keep (?:going|it|training|running)|don'?t stop|do not stop|"
                      r"continue|carry on|go on|it'?s fine)\b")


def has_named_stop(text: str) -> bool:
    """A short sentence with his name and a stop word anywhere in the turn: "No, no. Albie, stop. You ..." (live 22:27).
    Not when a later sentence takes it back: "Albie, stop the training. Actually, never mind, let it run." (lab 04:3x)."""
    sentences = re.split(r"[.?!]+(?=\s|$)", text.lower())
    for k, sentence in enumerate(sentences):
        words = _stop_words(sentence)
        if 0 < len(words) <= 4 and set(words) & STOP_CORE and set(words) & NAME_WORDS:
            return not _RETRACT.search(" ".join(sentences[k + 1:]).replace("\u2019", "'"))
    return False


def _stop_words(text: str) -> list[str]:
    return re.findall(r"[a-z]+", text.lower().replace("never mind", "nevermind").replace("shut up", "shutup"))


def is_stop(text: str) -> bool:
    """Only stop words and filler ("Wait, stop, Albie.", "Never mind."): the user wants silence, not an answer."""
    words = _stop_words(text)
    return bool(words) and set(words) <= STOP_CORE | STOP_FILLER and bool(set(words) & STOP_CORE)


NAME_WORDS = ALIASES["bit"]  # albie, albee, alby, elbie (agent/wake.py)


def has_name(text: str, last: bool = True) -> bool:
    """Albie is addressed: "Albie, ...", "Hey Albie ...", "Al bee, ...", "..., Albie?". The first two words of any
    sentence ("Switch to code. Albie, switch to code."), plus the last word when ``last`` (final transcripts only: a
    growing partial always ends on its newest word)."""
    sentences = [re.findall(r"[a-z]+", s) for s in re.split(r"[.?!]+(?=\s|$)", text.lower())]
    sentences = [s for s in sentences if s]
    for words in sentences:
        for i in (0, 1):  # "Al bee" at the start is the name heard as two words
            if words[i:i + 1] == [SPLIT_FIRST] and words[i + 1:i + 2] and words[i + 1] in SPLIT_SECOND:
                words[i:i + 2] = ["albie"]
                break
        if any(w in NAME_WORDS for w in words[:2]):
            return True
    return bool(last and sentences and sentences[-1][-1] in NAME_WORDS)


def keyterms_for(repo: Path | None) -> list[str]:
    # The crew's names right after Albie's, so "Hey Scout" isn't transcribed as "Hey Scott" (agent/wake.py routes by name).
    crew_names = [t.name for t in TYPES.values()]
    return list(dict.fromkeys(ROCKET_TERMS[:1] + crew_names + ROCKET_TERMS[1:] + DESIGN_TERMS + ML_TERMS
                              + vocab_terms(repo)))[:100]


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
