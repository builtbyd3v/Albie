"""Voice session: Grok event mapping against a mocked Grok socket, and the fake provider over /ws/agent."""

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
        assert "Bit" in sess["instructions"]
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
        grok.push(type=f"{T}.updated", transcript="Hey Bit")
        grok.push(type=f"{T}.completed", transcript="Hey Bit!")
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
        assert partials == ["Hey", "Hey Bit"]
        final = next(e for e in events if e.type == "stt.final")
        assert final.payload == {"pet_id": "bit", "text": "Hey Bit!", "voice": True}
        start = next(e for e in events if e.type == "tts.start")
        assert start.payload["sample_rate"] == 48000 and start.payload["encoding"] == "pcm16"
        end = next(e for e in events if e.type == "tts.end")
        assert end.payload["utterance_id"] == start.payload["utterance_id"]
        answers = [e.payload["text"] for e in events if e.type.startswith("agent.") and "text" in e.payload]
        assert answers == ["Hi", "Hi there!", "Hi there!"]
        assert s.is_echo("hey bit!") and not s.is_echo("something else")
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
    with client.websocket_connect("/ws/agent") as ws:
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
    with client.websocket_connect("/ws/agent") as ws:
        ws.send_json({"type": "hello", "payload": {"client": "unity", "pet_id": "bit", "project_id": "demo"}})
        ws.send_json({"type": "stt.start", "payload": {"pet_id": "bit"}})
        ws.send_json({"type": "stt.stop", "payload": {"pet_id": "bit"}})
        evts = _recv_until(ws, "stt.final")
    assert "voice.ready" not in [t for t, _ in evts]
    assert evts[-1] == ("stt.final", {"pet_id": "bit", "text": ""})
