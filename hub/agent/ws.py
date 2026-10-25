"""Pet agent session on the hub's shared /ws socket (main.py accepts it, attaches the EventBus, then calls us).

    await handle_socket(ws, accept=False)

Per socket: hello -> (pet_id, project_id); agent.ask -> background turn streaming
events over the bus; agent.cancel -> interrupt; stt.start / binary PCM16 / stt.stop
-> stt.partial / stt.final. With VOICE_PROVIDER=grok|fake the same stt.* frames drive a
voice session instead (agent/voice.py: speech back as tts.* + binary, owner socket only).
Wake turns (stt.start {wake: true}, agent/wake.py): always-on speech segments, answered only when they name a
gator ("Hey Albie ...", "Tester, ...") or the user faces Albie (facing: true). Crew (agent/crew.py): crew.roster -> membership; agent.ask / agent.cancel whose pet_id is a crew member
run that member's own background turn instead of the socket's turn. workspace.mode {mode}: which view Unity
shows; kept on the session, told to the voice model as a one-line note, and gates Bit's crew tools (CODE view only).
Other modules own their client -> hub messages on this socket through ``client_handlers`` (main.py registers
``messages.focus`` for hub/messaging). Unknown message types are ignored and logged.
"""

from __future__ import annotations

import asyncio
import json
import logging
import time
from collections.abc import Awaitable, Callable
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any

from fastapi import WebSocket, WebSocketDisconnect
from pydantic import ValidationError

from . import events as ev
from . import runner as runner_mod
from . import speech, tools
from .bus import Emitter, bus
from .config import ProjectNotFound, resolve_project
from .crew import Member, crew, spoken
from .events import (
    AgentAsk, AgentCancel, CrewRoster, Event, Hello, PetCardAction, SttCancel, SttStart, SttStop, TtsStopRequest,
    WorkspaceMode,
)
from .runner import get_runner
from .stt import SpeechToText, make_stt
from .turn import TurnControl, classify_mode
from .voice import VoiceSession, make_voice, view_note
from . import wake as wake_mod
from .wake import WakeRouter
from .watcher import watcher
from .workspace import workspace

log = logging.getLogger(__name__)

CANCEL_GRACE_S = 3.0

ClientHandler = Callable[["Session", dict[str, Any]], Awaitable[None]]
client_handlers: dict[str, ClientHandler] = {}  # type -> handler(session, payload); the handler validates the payload
CardHandler = Callable[["Session", str], Awaitable[None]]
card_handlers: dict[str, CardHandler] = {}  # action_id prefix before ":" -> handler(session, action_id); "ml" (main.py)


@dataclass
class Session:
    ws: WebSocket
    pet_id: str = "bit"
    project_id: str | None = None
    turn: asyncio.Task[str] | None = None
    turn_mode: str | None = None
    turn_text: str | None = None  # the running turn's task (Continue after "Albie, stop" restarts it)
    stopped: dict | None = None  # what "Albie, stop" interrupted: {"task", "edits", "cad"} for Undo / Continue
    control: TurnControl | None = None
    stt: SpeechToText | None = None
    stt_busy: bool = False
    stt_cancelled: bool = False  # stt.cancel: ignore audio until the next stt.start
    voice: VoiceSession | None = None
    crew_listener: object | None = None  # announces finished crew work through this socket's voice
    wake: WakeRouter | None = None
    wake_turn: bool = False  # the open stt turn is a wake turn (decided by s.wake on its transcript)
    mode: str | None = None  # workspace.mode: the view Unity shows ("code", "cad", ...; "" = none); None until it says
    tasks: set[asyncio.Task[None]] = field(default_factory=set)

    def spawn(self, coro) -> None:  # fire-and-forget helper that keeps a reference
        t = asyncio.create_task(coro)
        self.tasks.add(t)
        t.add_done_callback(self.tasks.discard)

    async def reply_error(self, message: str, pet_id: str | None = None) -> None:
        """agent.error to the whole bus (keeps the pet in sync with other viewers)."""
        await Emitter(pet_id or self.pet_id)(ev.error(pet_id or self.pet_id, message))


async def handle_socket(ws: WebSocket, *, accept: bool = True) -> None:
    """Run the agent session on ws until it disconnects. accept=False: the caller already accepted it
    (main.py's shared /ws, where the CAD EventBus also publishes to the same socket)."""
    if accept:
        await ws.accept()
    bus.add(ws)
    s = Session(ws=ws)

    async def send_owner(event: Event) -> None:
        await bus.send(ws, event)

    # Wake turns carry no pet.state companions: room chatter must not make a gator look like it is listening. The
    # router sends pet.state listening itself once a segment is addressed to a gator.
    s.wake = WakeRouter(lead_pet_id=s.pet_id, emit_for=lambda pid: Emitter(pid, companion=False), send_owner=send_owner,
                        spawn=s.spawn)
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
        if bus.count == 0:
            workspace.forget()
        if s.crew_listener is not None:
            crew.remove_listener(s.crew_listener)  # type: ignore[arg-type]
        if s.voice is not None:
            speech.unregister(s.pet_id, s.voice)
            watcher.drop(s.voice)
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
            if s.wake is not None:
                s.wake.lead = h.pet_id
            if h.project_id:
                crew.project_id = h.project_id
            log.info("hello client=%s pet=%s project=%s", h.client, h.pet_id, h.project_id)
            await _open_voice(s, h)
        elif event.type == "agent.ask":
            await _on_ask(s, AgentAsk.model_validate(event.payload))
        elif event.type == "agent.cancel":
            cancel = AgentCancel.model_validate(event.payload)
            if cancel.pet_id and cancel.pet_id != s.pet_id:  # a crew gator's task (never Bit's turn)
                member = crew.get(cancel.pet_id)  # resolved now: a roster that follows may drop it
                if member is not None:
                    s.spawn(crew.cancel_member(member))
            else:
                s.spawn(_cancel(s))
        elif event.type == "crew.roster":
            roster = CrewRoster.model_validate(event.payload)
            crew.set_roster([m.model_dump() for m in roster.members])
        elif event.type == "workspace.mode":
            await _on_mode(s, WorkspaceMode.model_validate(event.payload))
        elif event.type == "stt.start":
            start = SttStart.model_validate(event.payload)
            if start.sample_rate != 16000:
                log.warning("stt.start sample_rate=%s; expected 16000", start.sample_rate)
            s.stt_cancelled = False
            named = wake_mod.NAME_REQUIRED  # free speech (default): a hands-free segment is a plain turn for Albie
            s.wake_turn = start.wake and s.wake is not None and (named or start.pet_id not in (None, "", s.pet_id))
            require_name = start.require_name and named
            if s.wake_turn:
                s.wake.begin(start.pet_id if start.pet_id != s.pet_id else None, facing=start.facing)  # type: ignore[union-attr]
            if s.voice is not None:
                await _voice_call(s, lambda: s.voice.begin_turn(require_name=require_name, wake=s.wake_turn))
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
        elif event.type == "pet.card_action":
            await _on_card_action(s, PetCardAction.model_validate(event.payload))
        elif event.type in client_handlers:
            await client_handlers[event.type](s, event.payload)
        else:
            log.debug("ignoring ws message type %s", event.type)
    except ValidationError as exc:
        log.warning("invalid %s payload: %s", event.type, exc)
        await s.reply_error(f"Bad {event.type} message.")


async def _on_mode(s: Session, wm: WorkspaceMode) -> None:
    """Unity switched workspace view (or answered ui.mode): remember it, tell the voice model in one line (no reply),
    gate crew tools. Every report also goes to agent/workspace.py, which may be waiting for it."""
    mode = wm.mode.strip().lower()
    workspace.report(mode, wm.reason)
    if mode == s.mode:
        return
    s.mode = mode
    log.info("workspace mode: %s", mode or "(closed)")
    voice = s.voice
    if voice is None:
        return
    voice.workspace_mode = mode
    note = getattr(voice, "note", None)
    if note is not None:
        s.spawn(_voice_call(s, lambda: note(view_note(mode))))


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
    if pet_id != s.pet_id and crew.get(pet_id) is not None:  # asked a crew gator directly
        if ask.project_id:
            crew.project_id = ask.project_id
        started = await crew.assign(pet_id, ask.text, ask.mode)
        if "error" in started:
            await s.reply_error(str(started["error"]), pet_id)
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
    s.turn_mode, s.turn_text = mode, ask.text
    emit = Emitter(pet_id, redact=[str(project.path)])
    run = get_runner()
    coro = run(project, ask.text, mode, emit, pet_id=pet_id, control=s.control)
    s.turn = asyncio.create_task(_guarded(coro, emit, pet_id))


# Background work "Albie, stop" can end, by name; each returns True if something was running (main.py: "ml").
task_stoppers: dict[str, Callable[[], Awaitable[bool]]] = {}
STOP_CHOICE_S = 14.0  # the question takes ~4 s to say, then a 10 s window for a bare "undo" / "continue" / "leave it"
STOP_CHOICES = {"undo": "Undo", "continue": "Continue", "leave": "Leave it"}


async def _stop_and_ask(s: Session, voice) -> None:
    """ "Albie, stop": end the running code task (or note a CAD change from the last 30 s), then ask one question.
    Only talking (no task, no change): stop just silences him."""
    running = s.turn is not None and not s.turn.done()
    code = running and s.turn_mode == "code"
    task = s.turn_text
    await _cancel(s, announce=False)
    edits = dict(runner_mod.TASK_EDITS.get(s.pet_id) or {}) if code else {}
    change = getattr(voice, "last_change", None)
    cad = bool(change and time.monotonic() - change[1] < 30.0) and not code
    ml = getattr(voice, "last_ml", None) if not code and not cad else None
    stopper = task_stoppers.get("ml")
    ml_stopped = bool(ml and stopper is not None and await stopper())
    if not code and not cad and not ml_stopped:
        return
    applied = ml_stopped and ml[0] == "ml_apply"  # type: ignore[index]
    s.stopped = {"task": task if code else None, "edits": edits, "cad": cad, "ml": ml if ml_stopped else None}
    choices = (["undo"] if edits or cad or applied else []) + (["continue"] if code or ml_stopped else []) + ["leave"]
    if ml_stopped:
        said = ("Stopped the training. Want me to undo the change, start it again, or leave it?" if applied
                else "Stopped the training. Want me to start it again, or leave it?")
    else:
        said = {("undo", "continue", "leave"): "Stopped. Want me to undo what I changed, pick up where I left off, or leave it?",
                ("continue", "leave"): "Stopped. Want me to pick up where I left off, or leave it?",
                ("undo", "leave"): "Stopped. Want me to undo that change, or leave it?"}[tuple(choices)]
    card = ev.Card(title="Stopped", actions=[ev.CardAction(label=STOP_CHOICES[c], action_id=f"stop:{c}") for c in choices])
    await Emitter(s.pet_id)(ev.pet_say(s.pet_id, said, card))
    voice.choice_until = time.monotonic() + STOP_CHOICE_S
    say = getattr(voice, "say", None)
    if say is not None:
        s.spawn(say(said))  # after this turn's final: say() waits for it


async def _stop_choice(s: Session, answer: str) -> None:
    """Undo / Continue / Leave it, spoken ("undo") or tapped (stop:undo on the card)."""
    stopped, s.stopped = s.stopped, None
    if s.voice is not None:
        s.voice.choice_until = 0.0
    if stopped is None:
        return
    ml = stopped.get("ml")
    if answer == "undo":
        restored = await asyncio.to_thread(_restore, stopped["edits"])
        if stopped["cad"]:
            await tools.call("cad_undo", {}, s.pet_id)
        if ml and ml[0] == "ml_apply":
            await tools.call("ml_undo", {}, s.pet_id)
        undone = restored or stopped["cad"] or bool(ml and ml[0] == "ml_apply")
        said = "Undone." if undone else "There was nothing to undo."
    elif answer == "continue" and ml:
        epochs = (ml[1] or {}).get("epochs")
        await tools.call("ml_train", {"epochs": epochs} if epochs else {}, s.pet_id)
        said = "Training again."
    elif answer == "continue" and stopped["task"]:
        said = "Picking up where I left off."
        task = stopped["task"] + "\n\nYou were stopped partway through this task. Some files may already have your changes: continue from there."
        s.spawn(_on_ask(s, AgentAsk(pet_id=s.pet_id, text=task, mode="code")))
    else:
        said = "Okay, I'll leave it."
    log.info("stop choice %s: %s", answer, said)
    await Emitter(s.pet_id)(ev.pet_say(s.pet_id, said))
    say = getattr(s.voice, "say", None)
    if say is not None:
        s.spawn(say(said))


def _restore(edits: dict[str, str | None]) -> int:
    """Put back what the stopped task changed: original contents, and files it created removed."""
    done = 0
    for path, original in edits.items():
        p = Path(path)
        try:
            if original is None:
                if p.exists():
                    p.unlink()
            else:
                p.write_text(original)
            done += 1
        except OSError:
            log.exception("undo: could not restore %s", p.name)
    return done


async def _on_card_action(s: Session, tap: PetCardAction) -> None:
    """Fix it on a proactive card: the same code fix a spoken "yes" starts, run as a typed ask. Not now: forgotten.
    stop:undo / stop:continue / stop:leave answer "Stopped. Want me to undo ...?" """
    if tap.action_id.startswith("stop:"):
        await _stop_choice(s, tap.action_id.removeprefix("stop:"))
        return
    handler = card_handlers.get(tap.action_id.partition(":")[0])
    if handler is not None:  # another module's card (ML proposal: Apply / Not now)
        await handler(s, tap.action_id)
        return
    task = watcher.take_offer(tap.action_id)
    if task is None:
        return
    if s.voice is not None:
        note = getattr(s.voice, "note", None)  # Grok: it asked the question, so it must not start the fix again
        if note is not None:
            await note("The user tapped Fix it on your last offer; the fix is already running. Don't delegate it.")
    await _on_ask(s, AgentAsk(pet_id=tap.pet_id, text=task, mode="code"))


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

    async def delegate(task: str, mode: str) -> str:
        return await _run_delegate(s, task, mode)

    voice = make_voice(pet_id=s.pet_id, client_rate=h.audio_out_rate, send_event=send_event,
                       send_bytes=send_bytes, emit=Emitter(s.pet_id), repo=_project_path(s), delegate=delegate)
    if voice is None:
        return
    voice.wake_router = s.wake
    async def stop_task() -> None:  # "Albie, stop": his running task ends too, then he asks what to do with it
        s.spawn(_stop_and_ask(s, voice))  # in the background: ending a runner can take a few seconds

    async def choice(answer: str) -> None:
        await _stop_choice(s, answer)

    voice.on_stop, voice.on_choice = stop_task, choice
    judge = getattr(voice, "addressed", None)  # unnamed facing / follow-up turns: judged with Albie's last line
    if s.wake is not None and judge is not None:
        s.wake.judge = judge
    voice.workspace_mode = s.mode  # a workspace.mode that arrived before voice opened still counts
    s.voice = voice
    speech.register(s.pet_id, voice)  # lets /messages/digest make Bit speak unprompted
    watcher.add(voice)  # proactive lines are spoken too
    await bus.send(ws, ev.voice_ready(s.pet_id, voice.provider, voice.sample_rate))
    s.spawn(_voice_call(s, voice.start))

    say = getattr(voice, "say", None)
    if say is not None:
        async def announce(member: Member, result: str) -> None:  # Bit relays finished crew work out loud
            if s.voice is voice:
                s.spawn(say(f"{member.type.name} says: {spoken(result)}"))

        s.crew_listener = announce
        crew.add_listener(announce)


async def _run_delegate(s: Session, task: str, mode: str) -> str:
    """The voice model's delegate tool: one runner turn (same as agent.ask, so agent.cancel works); returns its answer."""
    if s.turn and not s.turn.done():
        if s.turn_mode == "code":
            return "I'm still working on the last task. Give me a moment!"
        await _cancel(s, announce=False)
    try:
        project = resolve_project(s.project_id)
    except ProjectNotFound as exc:
        return str(exc)
    concrete = classify_mode(task, mode or project.default_mode)
    s.control = TurnControl()
    s.turn_mode, s.turn_text = concrete, task
    emit = _QuietSteps(Emitter(s.pet_id, redact=[str(project.path)]), s.pet_id)
    coro = get_runner()(project, task, concrete, emit, pet_id=s.pet_id, control=s.control)
    s.turn = asyncio.create_task(_guarded(coro, emit, s.pet_id))
    return await s.turn


class _QuietSteps:
    """Voice delegate runs: the bubble shows what Albie says, not each runner step. Live 20:0x the task title and
    every "Editing / Running: ..." step (with full paths) showed in his bubble. Steps become one pet.state working
    (he walks to the VS Code screen); the summary and his spoken answer still come through."""

    def __init__(self, emit: Emitter, pet_id: str) -> None:
        self._emit, self._pet_id, self._working = emit, pet_id, False

    async def __call__(self, event: Event) -> None:
        if event.type in ("agent.task_started", "agent.task_progress"):
            if not self._working:
                self._working = True
                await self._emit(ev.pet_state(self._pet_id, "working", target_panel="screen-main"))
            return
        await self._emit(event)


async def _voice_call(s: Session, fn) -> None:
    """Run a voice-session call; on failure fall back to the stt.py path for this socket."""
    voice = s.voice
    try:
        await fn()
    except Exception as exc:
        if isinstance(exc, (OSError, asyncio.TimeoutError)) or type(exc).__module__.startswith("websockets"):
            # A network blip (Grok unreachable for a moment): keep voice, its next turn reconnects. Switching it
            # off left the socket on the stt.py fallback, which hears nothing (STT_PROVIDER=none), until a rehello.
            log.warning("voice call failed (%s): keeping voice, the next turn reconnects", exc)
            return
        log.exception("voice session failed")
        if s.voice is voice and voice is not None:
            s.voice = None
            speech.unregister(s.pet_id, voice)
            watcher.drop(voice)
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
            if s.wake_turn and s.wake is not None:
                await s.wake.partial(text)  # captions only once a gator is named
            else:
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
            if not s.wake_turn:
                await s.reply_error("I couldn't hear that, try again?")
            return
    if s.wake_turn and s.wake is not None:  # text path: Bit answers through the runner like agent.ask
        s.wake_turn = False
        if s.stt_cancelled:
            return
        decision = await s.wake.final(text)
        if decision.respond:
            await _on_ask(s, AgentAsk(pet_id=s.pet_id, text=decision.rest or text))
        return
    if s.wake is not None and s.wake.crew_target(text):  # "Tester, ..." in Bit's own turn: the crew gator's
        await Emitter(s.pet_id)(ev.stt_final(s.pet_id, ""))  # Bit: nothing to answer
        await s.wake.final(text)
        return
    await Emitter(s.pet_id)(ev.stt_final(s.pet_id, text))
