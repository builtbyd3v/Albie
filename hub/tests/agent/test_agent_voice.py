"""Voice session: Grok event mapping against a mocked Grok socket, and the fake provider over /ws."""

from __future__ import annotations

import asyncio
import base64
import json
from typing import Any

import pytest
from fastapi.testclient import TestClient

from agent import voice as voice_mod
from agent.events import Event
from agent.voice import GrokVoiceSession, pick_out_rate


class MockGrok:
    """Stands in for the websockets client: records sent JSON, yields queued server events."""

    def __init__(self) -> None:
        self.sent: list[dict[str, Any]] = []
        self.url = ""
        self.key = ""
        self._inbox: asyncio.Queue[str | None] = asyncio.Queue()

    async def connect(self, url: str, key: str) -> MockGrok:
        self.url, self.key = url, key
        return self

    async def send(self, raw: str) -> None:
        self.sent.append(json.loads(raw))

    async def close(self) -> None:
        await self._inbox.put(None)

    def push(self, **msg: Any) -> None:
        self._inbox.put_nowait(json.dumps(msg))

    def __aiter__(self) -> MockGrok:
        return self

    async def __anext__(self) -> str:
        item = await self._inbox.get()
        if item is None:
            raise StopAsyncIteration
        return item

    def types(self) -> list[str]:
        return [m["type"] for m in self.sent]


class Sink:
    """Collects everything the session sends, in wire order."""

    def __init__(self) -> None:
        self.log: list[tuple[str, Any]] = []

    async def owner(self, e: Event) -> None:
        self.log.append(("owner", e))

    async def audio(self, data: bytes) -> None:
        self.log.append(("bytes", data))

    async def emit(self, e: Event) -> None:
        self.log.append(("emit", e))

    def flat(self) -> list[str]:
        return [f"{k}:{v.type}" if k != "bytes" else f"bytes:{len(v)}" for k, v in self.log]


def _session(grok: MockGrok, sink: Sink, rate: int = 48000) -> GrokVoiceSession:
    return GrokVoiceSession(pet_id="bit", sample_rate=rate, send_event=sink.owner, send_bytes=sink.audio,
                            emit=sink.emit, api_key="xai-test", model="grok-voice-3.0", voice="",
                            connect=grok.connect)


async def _settle() -> None:
    for _ in range(20):
        await asyncio.sleep(0)


def _b64(data: bytes) -> str:
    return base64.b64encode(data).decode()


def test_session_update_config() -> None:
    async def run() -> None:
        grok, sink = MockGrok(), Sink()
        s = _session(grok, sink)
        await s.start()
        await s.start()  # idempotent
        assert grok.url == "wss://api.x.ai/v1/realtime?model=grok-voice-3.0"
        assert grok.key == "xai-test"
        assert grok.types() == ["session.update"]
        sess = grok.sent[0]["session"]
        assert sess["turn_detection"] is None
        assert sess["audio"]["input"]["format"] == {"type": "audio/pcm", "rate": 16000}
        assert sess["audio"]["output"]["format"] == {"type": "audio/pcm", "rate": 48000}
        assert sess["audio"]["input"]["transcription"]["model"] == "grok-transcribe"
        assert "Albie" in sess["instructions"]
        await s.close()

    asyncio.run(run())


def test_push_to_talk_turn_maps_to_our_events() -> None:
    async def run() -> None:
        grok, sink = MockGrok(), Sink()
        s = _session(grok, sink)
        await s.begin_turn()
        await s.feed(b"\x00\x00" * 1600)  # 100 ms
        await s.feed(b"\x00\x00" * 1600)
        await s.commit_turn()
        assert grok.types() == ["session.update", "input_audio_buffer.clear", "input_audio_buffer.append",
                                "input_audio_buffer.append", "input_audio_buffer.commit", "response.create"]
        assert base64.b64decode(grok.sent[2]["audio"]) == b"\x00\x00" * 1600

        T = "conversation.item.input_audio_transcription"
        grok.push(type=f"{T}.updated", transcript="Hey")
        grok.push(type=f"{T}.updated", transcript="Hey Albie")
        grok.push(type="input_audio_buffer.committed")
        grok.push(type=f"{T}.completed", transcript="Hey Albie!")
        grok.push(type="response.created", response={"id": "r1"})
        grok.push(type="response.output_audio.delta", delta=_b64(b"\x01\x00" * 10))
        grok.push(type="response.output_audio_transcript.delta", delta="Hi")
        grok.push(type="response.output_audio_transcript.delta", delta=" there!")
        grok.push(type="response.output_audio.delta", delta=_b64(b"\x02\x00" * 5))
        grok.push(type="response.output_audio_transcript.done", transcript="Hi there!")
        grok.push(type="response.done", response={"id": "r1", "status": "completed"})
        await _settle()

        assert sink.flat() == [
            "emit:agent.thinking",
            "emit:stt.partial", "emit:stt.partial", "emit:stt.final",
            "owner:tts.start", "bytes:20",
            "emit:agent.partial_answer", "emit:agent.partial_answer",
            "bytes:10",
            "emit:agent.answer_done",
            "owner:tts.end",
        ]
        events = [v for k, v in sink.log if k != "bytes"]
        partials = [e.payload["text"] for e in events if e.type == "stt.partial"]
        assert partials == ["Hey", "Hey Albie"]
        final = next(e for e in events if e.type == "stt.final")
        assert final.payload == {"pet_id": "bit", "text": "Hey Albie!", "voice": True}
        start = next(e for e in events if e.type == "tts.start")
        assert start.payload["sample_rate"] == 48000 and start.payload["encoding"] == "pcm16"
        end = next(e for e in events if e.type == "tts.end")
        assert end.payload["utterance_id"] == start.payload["utterance_id"]
        answers = [e.payload["text"] for e in events if e.type.startswith("agent.") and "text" in e.payload]
        assert answers == ["Hi", "Hi there!", "Hi there!"]
        assert s.is_echo("hey albie!") and not s.is_echo("something else")
        await s.close()

    asyncio.run(run())


def test_final_falls_back_to_last_partial_and_answer_without_done() -> None:
    async def run() -> None:
        grok, sink = MockGrok(), Sink()
        s = _session(grok, sink)
        await s.begin_turn()
        await s.feed(b"\x00\x00" * 4000)
        await s.commit_turn()
        grok.push(type="conversation.item.input_audio_transcription.delta", delta="what's ")
        grok.push(type="conversation.item.input_audio_transcription.delta", delta="up")
        grok.push(type="response.created")
        grok.push(type="response.audio_transcript.delta", delta="Not much!")  # older event names work too
        grok.push(type="response.done")
        await _settle()
        flat = sink.flat()
        assert flat == ["emit:agent.thinking", "emit:stt.partial", "emit:stt.partial", "emit:stt.final",
                        "emit:agent.partial_answer", "emit:agent.answer_done"]  # no audio -> no tts.*
        final = [v for k, v in sink.log if k == "emit" and v.type == "stt.final"][0]
        assert final.payload["text"] == "what's up"
        await s.close()

    asyncio.run(run())


def test_barge_in_stops_speech_and_drops_stale_audio() -> None:
    async def run() -> None:
        grok, sink = MockGrok(), Sink()
        s = _session(grok, sink)
        await s.begin_turn()
        await s.feed(b"\x00\x00" * 4000)
        await s.commit_turn()
        grok.push(type="response.created")
        grok.push(type="response.output_audio.delta", delta=_b64(b"\x01\x00" * 8))
        await _settle()
        utt = next(v for k, v in sink.log if k == "owner" and v.type == "tts.start").payload["utterance_id"]

        await s.begin_turn()  # user pressed talk again while Bit was speaking
        assert grok.types()[-2:] == ["response.cancel", "input_audio_buffer.clear"]
        stop = sink.log[-1][1]
        assert stop.type == "tts.stop" and stop.payload == {"pet_id": "bit", "utterance_id": utt, "reason": "barge_in"}

        n = len(sink.log)
        grok.push(type="response.output_audio.delta", delta=_b64(b"\x01\x00" * 8))  # in flight before the cancel
        grok.push(type="response.done", response={"status": "cancelled"})
        await _settle()
        assert sink.log[n:] == []
        await s.close()

    asyncio.run(run())


def test_tap_without_speech_sends_empty_final_and_no_commit() -> None:
    async def run() -> None:
        grok, sink = MockGrok(), Sink()
        s = _session(grok, sink)
        await s.begin_turn()
        await s.feed(b"\x00\x00" * 100)
        await s.commit_turn()
        assert "input_audio_buffer.commit" not in grok.types()
        assert sink.flat() == ["emit:stt.final"]
        assert sink.log[0][1].payload == {"pet_id": "bit", "text": "", "voice": True}
        await s.close()

    asyncio.run(run())


def test_grok_error_becomes_agent_error() -> None:
    async def run() -> None:
        grok, sink = MockGrok(), Sink()
        s = _session(grok, sink)
        await s.begin_turn()
        grok.push(type="error", error={"message": "rate limited"})
        await _settle()
        assert sink.flat() == ["emit:stt.final", "emit:agent.error"]
        assert "rate limited" in sink.log[-1][1].payload["message"]
        await s.close()

    asyncio.run(run())


def test_item_not_found_error_is_harmless() -> None:
    """A dropped turn's conversation.item.delete can race Grok, which answers 'Item not found'. No bubble, and the
    open turn keeps going."""
    async def run() -> None:
        grok, sink = MockGrok(), Sink()
        s = _session(grok, sink)
        await s.begin_turn()
        grok.push(type="error", error={"type": "invalid_request_error", "message": "Item not found: item_1"})
        await _settle()
        assert sink.flat() == [] and not s._final_sent
        await s.close()

    asyncio.run(run())


def test_pick_out_rate(monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.delenv("GROK_VOICE_OUT_RATE", raising=False)
    assert pick_out_rate(44100) == 44100
    assert pick_out_rate(None) == 48000
    assert pick_out_rate(96000) == 48000
    monkeypatch.setenv("GROK_VOICE_OUT_RATE", "24000")
    assert pick_out_rate(None) == 24000


def test_grok_without_key_means_no_voice(monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.setenv("VOICE_PROVIDER", "grok")
    monkeypatch.delenv("XAI_API_KEY", raising=False)
    sink = Sink()
    assert voice_mod.make_voice(pet_id="bit", client_rate=48000, send_event=sink.owner,
                                send_bytes=sink.audio, emit=sink.emit) is None


# ------------------------------------------------------------------ fake provider over the real socket
def _recv_until(ws: Any, until: str, limit: int = 200) -> list[tuple[str, Any]]:
    out: list[tuple[str, Any]] = []
    for _ in range(limit):
        msg = ws.receive()
        if msg.get("bytes") is not None:
            out.append(("bytes", msg["bytes"]))
            continue
        data = json.loads(msg["text"])
        out.append((data["type"], data["payload"]))
        if data["type"] == until:
            return out
    raise AssertionError(f"never saw {until}: {[t for t, _ in out]}")


def test_fake_voice_over_ws(client: TestClient, monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.setenv("VOICE_PROVIDER", "fake")
    monkeypatch.setenv("FAKE_VOICE_PACE", "0")
    with client.websocket_connect("/ws") as ws:
        ws.send_json({"type": "hello", "payload": {"client": "unity", "pet_id": "bit", "project_id": "demo",
                                                    "audio_out_rate": 48000}})
        ready = _recv_until(ws, "voice.ready")[-1]
        assert ready == ("voice.ready", {"pet_id": "bit", "provider": "fake", "sample_rate": 24000})

        ws.send_json({"type": "stt.start", "payload": {"pet_id": "bit", "sample_rate": 16000, "encoding": "pcm16"}})
        ws.send_bytes(b"\x00\x00" * 1600)
        ws.send_json({"type": "stt.stop", "payload": {"pet_id": "bit"}})
        evts = _recv_until(ws, "tts.end")

        types = [t for t, _ in evts]
        final = next(p for t, p in evts if t == "stt.final")
        assert final["voice"] is True and final["text"]
        assert types.index("stt.final") < types.index("agent.thinking") < types.index("tts.start")
        i0, i1 = types.index("tts.start"), types.index("tts.end")
        audio = b"".join(p for t, p in evts[i0:i1] if t == "bytes")
        assert audio == voice_mod.FAKE_PCM.read_bytes()
        assert "agent.answer_done" in types[i0:i1]
        start = evts[i0][1]
        assert start["sample_rate"] == 24000 and evts[i1][1]["utterance_id"] == start["utterance_id"]

        # PushToTalk-style echo of the transcript as agent.ask is ignored (the voice already answered).
        ws.send_json({"type": "agent.ask", "payload": {"text": final["text"], "mode": "qa"}})
        ws.send_json({"type": "agent.ask", "payload": {"text": "error", "mode": "qa"}})
        after = _recv_until(ws, "agent.error")
        assert [t for t, _ in after].count("agent.thinking") == 1  # only the second ask ran


def test_no_voice_by_default(client: TestClient, monkeypatch: pytest.MonkeyPatch) -> None:
    monkeypatch.setenv("VOICE_PROVIDER", "none")
    with client.websocket_connect("/ws") as ws:
        ws.send_json({"type": "hello", "payload": {"client": "unity", "pet_id": "bit", "project_id": "demo"}})
        ws.send_json({"type": "stt.start", "payload": {"pet_id": "bit"}})
        ws.send_json({"type": "stt.stop", "payload": {"pet_id": "bit"}})
        evts = _recv_until(ws, "stt.final")
    assert "voice.ready" not in [t for t, _ in evts]
    assert evts[-1] == ("stt.final", {"pet_id": "bit", "text": ""})


def test_cancel_turn_drops_audio_and_commits_nothing() -> None:
    async def run() -> None:
        grok, sink = MockGrok(), Sink()
        s = _session(grok, sink)
        await s.begin_turn()
        await s.feed(b"\x00\x00" * 4000)
        await s.cancel_turn()
        await s.feed(b"\x00\x00" * 4000)  # mic flush after the cancel
        await s.commit_turn()
        assert grok.types() == ["session.update", "input_audio_buffer.clear", "input_audio_buffer.append",
                                "input_audio_buffer.clear"]
        assert sink.flat() == ["emit:stt.final"] and sink.log[0][1].payload["text"] == ""
        await s.close()

    asyncio.run(run())


@pytest.mark.parametrize("provider", ["none", "fake"])
def test_stt_cancel_over_ws(client: TestClient, monkeypatch: pytest.MonkeyPatch, provider: str) -> None:
    monkeypatch.setenv("VOICE_PROVIDER", provider)
    monkeypatch.setenv("FAKE_VOICE_PACE", "0")
    with client.websocket_connect("/ws") as ws:
        ws.send_json({"type": "hello", "payload": {"client": "unity", "pet_id": "bit", "project_id": "demo"}})
        ws.send_json({"type": "stt.start", "payload": {"pet_id": "bit"}})
        ws.send_bytes(b"\x00\x00" * 1600)
        ws.send_json({"type": "stt.cancel", "payload": {"pet_id": "bit"}})
        ws.send_bytes(b"\x00\x00" * 1600)
        ws.send_json({"type": "stt.stop", "payload": {"pet_id": "bit"}})
        evts = _recv_until(ws, "stt.final")
        assert evts[-1][1]["text"] == ""
        ws.send_json({"type": "agent.ask", "payload": {"text": "error", "mode": "qa"}})
        after = _recv_until(ws, "agent.error")
    assert "tts.start" not in [t for t, _ in after]


def test_delegate_round_trip() -> None:
    async def run() -> None:
        grok, sink = MockGrok(), Sink()
        asked: list[tuple[str, str]] = []

        async def delegate(task: str, mode: str) -> str:
            asked.append((task, mode))
            await asyncio.sleep(0)
            return "The socket handler lives in hub/agent/ws.py."

        s = GrokVoiceSession(pet_id="bit", sample_rate=48000, send_event=sink.owner, send_bytes=sink.audio,
                             emit=sink.emit, api_key="k", connect=grok.connect, delegate=delegate)
        await s.begin_turn()
        sess = grok.sent[0]["session"]
        assert sess["tools"][0]["name"] == "delegate" and "delegate" in sess["instructions"]
        await s.feed(b"\x00\x00" * 4000)
        await s.commit_turn()
        grok.push(type="input_audio_buffer.committed")
        grok.push(type="conversation.item.input_audio_transcription.completed", transcript="where's the ws handler?")
        grok.push(type="response.created")
        grok.push(type="response.output_audio_transcript.delta", delta="On it!")
        grok.push(type="response.function_call_arguments.done", name="delegate", call_id="c1",
                  arguments=json.dumps({"task": "Where is the websocket handler?", "mode": "qa"}))
        grok.push(type="response.done")
        await _settle()

        assert asked == [("Where is the websocket handler?", "qa")]
        tool = next(v for k, v in sink.log if k == "emit" and v.type == "agent.tool_call")
        assert tool.payload["name"] == "delegate" and tool.payload["call_id"] == "c1"
        kinds = [(m["type"], (m.get("item") or {}).get("type")) for m in grok.sent]
        i_out = kinds.index(("conversation.item.create", "function_call_output"))
        assert kinds[i_out + 1] == ("response.create", None)  # the "on it" ack turn
        assert json.loads(grok.sent[i_out]["item"]["output"])["status"] == "started"

        # Grok speaks its ack; the answer waits until that response is done, then goes out verbatim.
        grok.push(type="response.created")
        await _settle()
        assert ("conversation.item.create", "force_message") not in kinds
        grok.push(type="response.done")
        for _ in range(30):
            await asyncio.sleep(0.02)
            if any((m.get("item") or {}).get("type") == "force_message" for m in grok.sent):
                break
        forced = [m for m in grok.sent if (m.get("item") or {}).get("type") == "force_message"]
        assert forced and forced[0]["item"]["content"][0]["text"] == "The socket handler lives in hub/agent/ws.py."

        # The forced response's transcript is not re-captioned (the runner already sent answer_done); audio still plays.
        n = len(sink.log)
        grok.push(type="response.created")
        grok.push(type="response.output_audio.delta", delta=_b64(b"\x01\x00" * 4))
        grok.push(type="response.output_audio_transcript.delta", delta="The socket")
        grok.push(type="response.output_audio_transcript.done", transcript="The socket handler...")
        grok.push(type="response.done")
        await _settle()
        assert [x for x in sink.flat()[n:]] == ["owner:tts.start", "bytes:8", "owner:tts.end"]
        await s.close()

    asyncio.run(run())


def test_unknown_tool_gets_error_output() -> None:
    async def run() -> None:
        grok, sink = MockGrok(), Sink()
        s = _session(grok, sink)
        await s.begin_turn()
        grok.push(type="response.created")
        grok.push(type="response.function_call_arguments.done", name="launch_rocket", call_id="x", arguments="{}")
        grok.push(type="response.done")
        await _settle()
        out = [m for m in grok.sent if (m.get("item") or {}).get("type") == "function_call_output"]
        assert out and "unknown tool" in json.loads(out[0]["item"]["output"])["error"]
        assert "delegate" not in [t["name"] for t in grok.sent[0]["session"]["tools"]]  # no delegate wired
        await s.close()

    asyncio.run(run())


def test_delegate_over_ws_uses_the_runner(client: TestClient, monkeypatch: pytest.MonkeyPatch) -> None:
    """ws wires delegate -> the (fake) Claude runner, which streams its usual agent.* events."""
    import agent.ws as ws_mod

    seen: dict[str, Any] = {}

    def fake_make_voice(**kw: Any) -> Any:
        seen.update(kw)
        return None  # no voice session; we only want the delegate callable ws builds

    monkeypatch.setattr(ws_mod, "make_voice", fake_make_voice)
    with client.websocket_connect("/ws") as ws:
        ws.send_json({"type": "hello", "payload": {"client": "unity", "pet_id": "bit", "project_id": "demo"}})
        ws.send_json({"type": "agent.cancel", "payload": {}})  # round-trip so hello is processed
        assert callable(seen.get("delegate"))


def test_fast_tool_runs_inline_and_returns_its_result(monkeypatch: pytest.MonkeyPatch) -> None:
    """A registry tool (agent/tools.py) is offered to Grok and its real result goes back as function_call_output."""
    from agent import tools

    async def call(name: str, args: dict[str, Any], pet_id: str) -> dict[str, Any]:
        return {"highlighted": args["path"]}

    monkeypatch.setattr(tools, "call", call)

    async def run() -> None:
        grok, sink = MockGrok(), Sink()
        s = _session(grok, sink)
        await s.begin_turn()
        sess = grok.sent[0]["session"]
        assert "vscode_highlight" in [t["name"] for t in sess["tools"]] and "quick actions" in sess["instructions"]
        grok.push(type="response.created")
        grok.push(type="response.function_call_arguments.done", name="vscode_highlight", call_id="h1",
                  arguments=json.dumps({"path": "train.py", "start": 40}))
        grok.push(type="response.done")
        await _settle()
        out = [m for m in grok.sent if (m.get("item") or {}).get("type") == "function_call_output"]
        assert json.loads(out[0]["item"]["output"]) == {"highlighted": "train.py"}
        assert grok.sent[-1]["type"] == "response.create"  # Grok then says what it did
        await s.close()

    asyncio.run(run())


def test_barge_in_stops_speech_unity_is_still_playing(monkeypatch: pytest.MonkeyPatch) -> None:
    """Grok sends a 3 s answer in a burst and finishes (tts.end) while Unity is still playing it.
    Talking 1 s later must still send tts.stop; talking after playback ended must not."""
    clock = [100.0]
    monkeypatch.setattr(voice_mod.time, "monotonic", lambda: clock[0])

    async def run() -> None:
        grok, sink = MockGrok(), Sink()
        s = _session(grok, sink, rate=24000)
        for later, expect_stop in ((1.0, True), (5.0, False)):
            await s.begin_turn()
            await s.feed(b"\x00\x00" * 4000)
            await s.commit_turn()
            grok.push(type="response.created")
            grok.push(type="response.output_audio.delta", delta=_b64(b"\x01\x00" * 24000 * 3))  # 3 s of speech
            grok.push(type="response.done")
            await _settle()
            utt = next(v for k, v in reversed(sink.log) if k == "owner" and v.type == "tts.end").payload["utterance_id"]
            clock[0] += later
            n = len(sink.log)
            await s.begin_turn()  # the user talks
            stops = [v for k, v in sink.log[n:] if k == "owner" and v.type == "tts.stop"]
            assert bool(stops) == expect_stop, f"{later} s after tts.end"
            if stops:
                assert stops[0].payload == {"pet_id": "bit", "utterance_id": utt, "reason": "barge_in"}
            await s.cancel_turn()
        await s.close()

    asyncio.run(run())


def test_interim_completed_transcripts_are_not_final() -> None:
    """Grok sends transcription.completed per segment while the user still talks (captured from grok-voice-3.0).
    Only the one after input_audio_buffer.committed is the final; earlier ones are partials."""
    async def run() -> None:
        grok, sink = MockGrok(), Sink()
        s = _session(grok, sink)
        await s.begin_turn()
        await s.feed(b"\x00\x00" * 4000)
        T = "conversation.item.input_audio_transcription"
        grok.push(type=f"{T}.updated", transcript="... alby. How's")
        grok.push(type=f"{T}.completed", transcript="... alby. How's")
        grok.push(type=f"{T}.completed", transcript="... Albie. How stable is my rocket")
        await _settle()
        await s.commit_turn()
        grok.push(type="input_audio_buffer.committed")
        grok.push(type=f"{T}.completed", transcript="Albie, how stable is my rocket right now?")
        grok.push(type="response.created")
        await _settle()
        finals = [v.payload["text"] for k, v in sink.log if k == "emit" and v.type == "stt.final"]
        assert finals == ["Albie, how stable is my rocket right now?"]
        partials = [v.payload["text"] for k, v in sink.log if k == "emit" and v.type == "stt.partial"]
        assert partials == ["... alby. How's", "... Albie. How stable is my rocket"]
        await s.close()

    asyncio.run(run())


def test_turn_grok_ignores_ends_as_empty(monkeypatch: pytest.MonkeyPatch) -> None:
    """Noise-only turn (captured from grok-voice-3.0): Grok commits, then sends nothing at all, not even with
    response.create. Without a timeout the hub waits forever and Unity sits in Thinking, deaf."""
    monkeypatch.setattr(voice_mod, "NO_REPLY_S", 0.05)

    async def run() -> None:
        grok, sink = MockGrok(), Sink()
        s = _session(grok, sink)
        await s.begin_turn()
        await s.feed(b"\x10\x00" * 9600)
        await s.commit_turn()
        grok.push(type="input_audio_buffer.committed")
        await asyncio.sleep(0.2)
        assert [v.payload["text"] for k, v in sink.log if k == "emit" and v.type == "stt.final"] == [""]

        # A turn Grok does answer is untouched by the timeout.
        n = len(sink.log)
        await s.begin_turn()
        await s.feed(b"\x10\x00" * 9600)
        await s.commit_turn()
        grok.push(type="input_audio_buffer.committed")
        grok.push(type="conversation.item.input_audio_transcription.completed", transcript="Okay.")
        grok.push(type="response.created")
        await asyncio.sleep(0.2)
        assert [v.payload["text"] for k, v in sink.log[n:] if k == "emit" and v.type == "stt.final"] == ["Okay."]
        await s.close()

    asyncio.run(run())


@pytest.mark.parametrize("text,stop", [
    ("Stop.", True), ("Wait, stop, Albie.", True), ("Al bee, stop.", True), ("Okay, quiet.", True), ("Never mind.", True), ("Stop that.", True),
    ("Shh!", True), ("Wait, what does static margin mean?", False), ("Explain fins.", False), ("", False),
])
def test_stop_words(text: str, stop: bool) -> None:
    assert voice_mod.is_stop(text) is stop


def test_rocket_terms_lead_the_transcription_hints() -> None:
    """'fins' was transcribed as 'Fizz' in a live test; the CAD vocabulary now biases grok-transcribe first."""
    terms = voice_mod.keyterms_for(None)
    assert {"fins", "nose cone", "static margin", "apogee"} <= set(terms) and len(terms) <= 100


def test_stop_word_ends_the_turn_without_a_reply() -> None:
    """'Stop' over Albie: he goes quiet without answering 'Okay, quiet', and no tool runs."""
    async def run() -> None:
        grok, sink = MockGrok(), Sink()
        s = _session(grok, sink)
        await s.begin_turn()
        await s.feed(b"\x10\x00" * 9600)
        await s.commit_turn()
        grok.push(type="input_audio_buffer.committed")
        grok.push(type="conversation.item.input_audio_transcription.completed", transcript="Stop.")
        grok.push(type="response.created")
        grok.push(type="response.output_audio.delta", delta=_b64(b"\x01\x00" * 100))
        grok.push(type="response.output_audio_transcript.delta", delta="Okay, quiet.")
        grok.push(type="response.function_call_arguments.done", name="vscode_run_command", call_id="x", arguments="{}")
        grok.push(type="response.done")
        await _settle()
        assert [v.payload["text"] for k, v in sink.log if k == "emit" and v.type == "stt.final"] == [""]
        assert not [x for x in sink.flat() if x.startswith(("owner:tts", "bytes", "emit:agent.answer", "emit:agent.partial", "emit:agent.tool"))]
        assert "response.cancel" in grok.types()
        assert not [m for m in grok.sent if (m.get("item") or {}).get("type") == "function_call_output"]
        await s.close()

    asyncio.run(run())


@pytest.mark.parametrize("text,named", [
    ("Albie, what's my static margin?", True), ("Hey Albie what is apogee", True), ("Okay Albee, explain fins.", True),
    ("How stable is it, Alby?", True), ("... elbie. How's my rocket?", True), ("Al bee, what's apogee?", True),
    ("Hey al be what is apogee", True), ("Al b, explain fins.", True), ("I'm a bit tired", False),
    ("Bit, what's my static margin?", False), ("Sit down, please.", False), ("What did you think of the talk?", False),
    ("I told Al be quiet", False), ("", False),
])
def test_has_name(text: str, named: bool) -> None:
    assert voice_mod.has_name(text) is named


@pytest.mark.parametrize("said,answered", [("So what did you think of the demo?", False), ("Albie, what is apogee?", True)])
def test_turn_that_needs_the_name(said: str, answered: bool) -> None:
    """Look-to-talk: a turn opened while the user wasn't facing Albie only counts if they say his name.
    Side conversation is dropped silently: no captions, no reply."""
    async def run() -> None:
        grok, sink = MockGrok(), Sink()
        s = _session(grok, sink)
        await s.begin_turn(require_name=True)
        await s.feed(b"\x10\x00" * 9600)
        T = "conversation.item.input_audio_transcription"
        grok.push(type=f"{T}.updated", transcript=said)
        await _settle()
        await s.commit_turn()
        grok.push(type="input_audio_buffer.committed")
        grok.push(type=f"{T}.completed", transcript=said)
        grok.push(type="response.created")
        grok.push(type="response.output_audio.delta", delta=_b64(b"\x01\x00" * 100))
        grok.push(type="response.done")
        await _settle()
        finals = [v.payload["text"] for k, v in sink.log if k == "emit" and v.type == "stt.final"]
        partials = [v for k, v in sink.log if k == "emit" and v.type == "stt.partial"]
        assert finals == ([said] if answered else [""])
        assert bool(partials) is answered
        assert ("owner:tts.start" in sink.flat()) is answered
        await s.begin_turn()  # the next turn doesn't need the name
        assert s._require_name is False
        await s.close()

    asyncio.run(run())


def test_name_heard_only_in_an_interim_transcript_counts() -> None:
    """Captured live (as 'Bit'): 'Albie, what's a fin for?' came in with the name, then was re-transcribed without it."""
    async def run() -> None:
        grok, sink = MockGrok(), Sink()
        s = _session(grok, sink)
        await s.begin_turn(require_name=True)
        await s.feed(b"\x10\x00" * 9600)
        T = "conversation.item.input_audio_transcription"
        grok.push(type=f"{T}.updated", transcript="Al bee")
        grok.push(type=f"{T}.completed", transcript="Al bee")
        grok.push(type=f"{T}.updated", transcript="What's a fin for?")
        await _settle()
        await s.commit_turn()
        grok.push(type="input_audio_buffer.committed")
        grok.push(type=f"{T}.completed", transcript="What's a fin for?")
        await _settle()
        assert [v.payload["text"] for k, v in sink.log if k == "emit" and v.type == "stt.final"] == ["What's a fin for?"]
        await s.close()

    asyncio.run(run())


@pytest.mark.parametrize("text", ["wait a bit", "a bit more", "Wait a bit, Albie is busy", "can you wait a bit"])
def test_a_bit_never_names_him(text: str) -> None:
    assert voice_mod.has_name(text, last=False) is False
    assert voice_mod.has_name(text) is False


def test_partials_only_count_the_name_up_front() -> None:
    """A growing partial always ends on its newest word: 'I told Alby' must not mark the turn named for good."""
    assert voice_mod.has_name("I told Alby", last=False) is False
    assert voice_mod.has_name("Hey Albie what", last=False) is True
    assert voice_mod.has_name("How stable is it, Albie?") is True  # the last word counts on the final


def test_name_as_last_word_of_a_partial_does_not_count() -> None:
    async def run() -> None:
        grok, sink = MockGrok(), Sink()
        s = _session(grok, sink)
        await s.begin_turn(require_name=True)
        await s.feed(b"\x10\x00" * 9600)
        T = "conversation.item.input_audio_transcription"
        grok.push(type=f"{T}.updated", transcript="I told Alby")
        grok.push(type=f"{T}.updated", transcript="I told Alby's mom we'd come")
        await _settle()
        assert s._named is False
        await s.commit_turn()
        grok.push(type="input_audio_buffer.committed")
        grok.push(type=f"{T}.completed", transcript="I told Alby's mom we'd come")
        grok.push(type="response.created")
        await _settle()
        assert [v.payload["text"] for k, v in sink.log if k == "emit" and v.type == "stt.final"] == [""]
        assert not [k for k, v in sink.log if k == "emit" and v.type == "stt.partial"]
        await s.close()

    asyncio.run(run())


def test_each_turn_logs_one_line(caplog: pytest.LogCaptureFixture) -> None:
    async def run() -> None:
        grok, sink = MockGrok(), Sink()
        s = _session(grok, sink)
        await s.begin_turn(require_name=True)
        await s.feed(b"\x10\x00" * 9600)
        T = "conversation.item.input_audio_transcription"
        grok.push(type=f"{T}.updated", transcript="Albie, what")
        await s.commit_turn()
        grok.push(type="input_audio_buffer.committed")
        grok.push(type=f"{T}.completed", transcript="What is apogee?")
        await _settle()
        await s.close()

    with caplog.at_level("INFO", logger="agent.voice"):
        asyncio.run(run())
    turns = [r.getMessage() for r in caplog.records if r.getMessage().startswith("turn:")]
    assert turns == ["turn: 'What is apogee?' require_name=True named=partial stop=False wake=False answered"]
