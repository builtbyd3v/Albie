# Voice / TTS plan: Grok speech-to-speech for the gator

Status: **proposal** (2026-10-23). It adds to `PROJECT_CONTEXT.md` §7/§8.7 and `docs/PET_AGENT_CONTRACT.md`.
New events are marked **proposed**. Announce them in chat before treating them as law. No code has been changed yet.

---

## 1. Findings (verified against docs.x.ai on 2026-10-23)

Primary source: xAI Speech to Speech guide, https://docs.x.ai/developers/model-capabilities/audio/speech-to-speech
(the old `docs.x.ai/docs/guides/voice/agent` link redirects here; the page was last updated 2026-10-15).

| Topic | Fact |
|---|---|
| **Model id** | `grok-voice-latest` is an alias for **`grok-voice-3.0`** (flagship, released 2026-10-14). `grok-voice-2.0` is **deprecated**. Same endpoint, events and price. **`grok-voice-think-fast-1.0` (§11 decision 2) is no longer listed** in the docs, on the models page, or in the rate-limit table. Pipecat's docs still use it as a pinning example (https://docs.pipecat.ai/server/services/s2s/grok). → Pin `grok-voice-3.0` through an env var and update §11. |
| Transport | WebSocket `wss://api.x.ai/v1/realtime?model=<id>`. The protocol is an **OpenAI Realtime clone** (the openai SDK also works with `base_url=https://api.x.ai/v1`). |
| Auth | `Authorization: Bearer $XAI_API_KEY` from the server. Ephemeral tokens (`xai-client-secret.<tok>` subprotocol) are for browser and mobile clients. We don't need them because the hub holds the key. |
| Session config | `session.update` with `instructions`, `voice`, `tools`, `turn_detection {type:"server_vad"\|null, threshold, silence_duration_ms, prefix_padding_ms, idle_timeout_ms}`, `reasoning.effort "high"(default)\|"none"`, `audio.input/output.format {type, rate}`, `audio.*.transport "json"\|"binary"`, `audio.input.transcription {model, language_hint, keyterms[≤100]}`, `audio.output.speed 0.7–1.5`, `replace {phrase: spoken}`, `resumption.enabled`. |
| Audio formats | `audio/pcm` (PCM16 LE) at 8000/16000/22050/**24000 (default)**/32000/44100/**48000**. `audio/pcmu`, `audio/pcma` at 8 kHz. `audio/opus` at 24 kHz. **Input and output are configured separately.** `transport:"binary"` sends raw codec bytes as WS binary frames with no header, and lifecycle and transcript events stay JSON. |
| User transcript (replaces our STT) | `conversation.item.input_audio_transcription.updated` delivers the **cumulative** transcript (it can revise earlier text). It is only emitted when `audio.input.transcription.model = "grok-transcribe"`. Pipecat's event model also lists `...transcription.completed` (https://reference-server.pipecat.ai/en/stable/_modules/pipecat/services/xai/realtime/events.html). Verify that event live. `keyterms` biases recognition toward repo vocabulary. |
| Assistant speech | `response.output_audio.delta` (base64 in JSON transport, or binary frames). `response.output_audio_transcript.delta/.done` carries the spoken text (for captions). The lifecycle is `response.created` … `response.done`. |
| Turn detection / barge-in | `server_vad` detects the end of speech and handles barge-in automatically. Pipecat reports `input_audio_buffer.speech_started/stopped`. With manual turns (`turn_detection:null`) you send `input_audio_buffer.commit` (or `.clear`) and then `response.create`. Pipecat also lists the client events `response.cancel` and `conversation.item.truncate`. |
| Function calling | Tools are `{type:"function", name, description, parameters(JSON Schema)}`. The round trip: the server sends `response.function_call_arguments.done {name, call_id, arguments}`. The client replies with `conversation.item.create {item:{type:"function_call_output", call_id, output:"<json string>"}}` and then **one** `response.create` after *all* parallel outputs. Tip from the docs: audio deltas arrive *before* the call events, so **wait for playback to drain before `response.create`** or the two turns overlap. Server-side tools (`web_search`, `x_search`, `mcp`) need no handling. |
| **Text in → speech out** | Yes, two ways. (a) **`force_message`** (xAI extension): `conversation.item.create {item:{type:"force_message", role:"assistant", interruptible, content:[{type:"output_text", text}]}}` speaks that text **verbatim** through TTS, without the model, and with no `response.create`. **This is how Claude's answer gets spoken.** (b) A user `input_text` message plus `response.create` (the model rephrases it). There is also a standalone streaming TTS socket, `wss://api.x.ai/v1/tts` (`text.delta` → `audio.delta`, `codec=pcm`, rates 8–48 kHz, `optimize_streaming_latency 0-2`): https://docs.x.ai/developers/model-capabilities/audio/text-to-speech |
| Voices | Speech to Speech and TTS share one roster (`GET /v1/tts/voices`). It includes `eve` (default, energetic and upbeat), `ara`, `leo`, `rex`, `sal`, and also `sirius` ("quick-witted, clever, playful"), `cosmo`, `iris`, `helios`, among others. Custom voice cloning works too. For the pet, try `sirius`, `cosmo`, `iris` and `eve` with `audio.output.speed ≈ 1.1`. |
| Latency | No numbers are published. The docs' advice: connect the socket early, send 100 ms chunks, stream deltas straight to the speaker, and match input/output rates to avoid resampling. `reasoning.effort:"high"` is the default, so set it to **`"none"`** for snappy tool calls and keep the heavy reasoning in `delegate`. |
| Pricing | Speech to speech costs **$0.08/min ($4.80/h)** plus $0.004 per text input. Streaming STT costs $0.20/h and TTS costs $15 per 1M characters. Source: https://docs.x.ai/developers/models |
| Rate limits | Speech to speech is limited by **concurrent sessions** only: 10 / 20 / 50 / 100 / 200 for tiers T0 to T4. Streaming TTS allows 50 concurrent sessions per team. Source: https://docs.x.ai/developers/rate-limits |
| Resumption | Opt in with `resumption.enabled`, then reconnect with `?conversation_id=` and the history replays (it expires after 30 min idle). Useful when the Quest's Wi-Fi drops. |

**Python clients**
- The official `xai-sdk` is gRPC (chat, image, video) and has **no realtime voice support** (https://pypi.org/project/xai_sdk).
- **Raw `websockets`** is what xAI's own examples use. It is about 300 lines, there's no framework, and every xAI extension (`force_message`, `replace`) is trivial to send.
- **Pipecat** `GrokRealtimeLLMService` (`pipecat-ai[grok]`, default model `grok-voice-latest`) is good if Pipecat owns the audio I/O. We would have to write a custom transport and serializer for our own `/ws` protocol plus adopt its pipeline model, which is too much glue for a hackathon.
- The **openai SDK** realtime client works too, but typed events may fight the xAI-only items.
- **→ Recommendation: raw `websockets`**, in one module.

**Unity playback** (https://docs.unity3d.com/ScriptReference/MonoBehaviour.OnAudioFilterRead.html)
- **`OnAudioFilterRead` + ring buffer: recommended.**
  - It runs on the audio thread about every 20 ms with interleaved floats at `AudioSettings.outputSampleRate`.
  - On an AudioSource **without a clip** it acts as the sound source.
  - Latency is lowest and the buffer never ends. Underruns just output silence.
- **`AudioClip.Create(..., stream:true, PCMReaderCallback)`: not recommended.**
  - Unity pulls large blocks ahead of time on its own schedule, so you get extra and unpredictable latency.
  - It needs a fixed fake length and looping hacks.
  - Barge-in flushes are awkward.
- **Quest output is 48 kHz.** Ask Grok for output **at the device rate** (48000 on Quest, usually 44100 or 48000 on Mac; both supported), so Unity never resamples. Fallback is a 24 kHz stream with a linear 2× upsample.

**ElevenLabs fallback**
- **Agents** (`wss://api.elevenlabs.io/v1/convai/conversation?agent_id=`, https://elevenlabs.io/docs/agents-platform/api-reference/agents-platform/websocket):
  - Mapping: `user_audio_chunk` (base64) in. Out: `audio`, `user_transcript`, `agent_response`, `interruption`. `client_tool_call` → `client_tool_result {tool_call_id, result}`. `contextual_update` to inject state. `ping`/`pong` keepalive.
  - Formats run from `pcm_16000` to `pcm_48000`.
  - Tools and persona are configured on the **agent in the dashboard** (or API), not per session.
  - There's no `force_message` equivalent. Speak Claude's text by sending `user_message` with an instruction, or use ElevenLabs **TTS stream-input WebSocket** (`output_format=pcm_24000/pcm_48000`), which is the cleanest "text → speech" path.
- **What changes:** only `hub/agent/voice.py` gets a second `VoiceBackend` implementation (event names, base64 framing, tool round trip). **The Unity side and our `/ws` contract do not change at all**, which is the point of keeping the vendor session in the hub.

---

## 2. Recommended architecture

```
Unity (Quest / editor)                        Hub (FastAPI, laptop)                        xAI
HubSpeechToText ──stt.start + binary PCM16 16k──► ws.py ──► VoiceSession (voice.py) ──WS──► grok-voice-3.0
StreamedPcmTextToSpeech ◄─tts.start/binary PCM16 @device rate/tts.end─┘   │  ▲ in 16k / out = device rate
GatorTalkAnimator ◄─ amplitude                                            │  │ tools → tools.py (REST fns)
GatorAgentBridge ◄─ stt.partial/final, agent.*, speech.*, pet.state ──────┘  └ delegate → runner.run_turn
```

- **The hub owns one Grok session per Unity socket.** It opens on `hello` (pre-warm) and closes on disconnect or after `VOICE_IDLE_CLOSE_S=120` with no audio. The key never leaves the laptop. `VOICE_PROVIDER=grok|elevenlabs|none`, where `none` keeps today's `stt.py` + `runner` path.
- **Grok session config:**
  - Input `audio/pcm@16000` binary (Unity already sends exactly this).
  - Output `audio/pcm@<client rate>` binary.
  - `transcription.model:"grok-transcribe"`, with `keyterms` from `stt.build_vocab_prompt`.
  - `reasoning.effort:"none"`, `voice` from `GROK_VOICE`, and model from `GROK_VOICE_MODEL=grok-voice-3.0`.
- **Turn-taking: push-to-talk with manual commit** (`turn_detection:null`) is the default:
  - **Press** sends `stt.start`. If speech is playing, the hub also sends `response.cancel` + `tts.stop` (manual barge-in).
  - **Release** sends `stt.stop`, and the hub sends `input_audio_buffer.commit` + `response.create`.
  - Why manual: a noisy hackathon floor, laptop or Quest speaker echo re-triggering VAD, and text input needs `turn_detection:null` anyway.
  - `VOICE_TURN=server_vad` switches to §8.7's original "server VAD ends the turn". **This is a proposed change to §8.7; confirm with person 5.**
- **Binary framing: untagged binary frames bracketed by JSON events (no channel byte).** Reasons:
  1. Each direction has exactly **one** binary stream (up = mic, down = speech), so direction already identifies the channel.
  2. Grok's `transport:"binary"` frames forward to Unity **byte-for-byte** with no copy and no re-tagging, and `HubSpeechToText` needs no change.
  3. Text and binary frames share one ordered TCP stream behind one per-socket lock, so `tts.start` → chunks → `tts.end`/`tts.stop` cannot reorder.
  4. To make barge-in safe, the hub stops forwarding *before* it sends `tts.stop`, so no stale chunk can follow.
  - If a second downstream binary stream ever appears (e.g. a screen thumbnail), move to a 1-byte prefix in a `v2` handshake (`hello.binary_v: 2`).

## 3. Contract additions (all **proposed**, additive, and `extra="ignore"` keeps old peers working)

Client → hub:
```
hello        { ..., audio_out_rate?: 48000 }       # AudioSettings.outputSampleRate; hub picks Grok output rate
stt.start    { pet_id, sample_rate:16000, encoding:"pcm16" }   # unchanged; also = PTT press (barge-in)
<binary>     PCM16 mono 16 kHz, ~100 ms                         # unchanged
stt.stop     { pet_id }                                         # unchanged; = PTT release → commit
tts.stop     { pet_id }                                         # user tapped the pet / ITextToSpeech.Stop()
```
Hub → client:
```
speech.started   { pet_id }                       # input_audio_buffer.speech_started (server_vad mode only)
speech.ended     { pet_id }                       # input_audio_buffer.speech_stopped
stt.partial      { pet_id, text }                 # ← ...input_audio_transcription.updated (cumulative; maps 1:1 to our cumulative partial)
stt.final        { pet_id, text }                 # ← ...transcription.completed, else last .updated at commit/response.created
tts.start        { pet_id, utterance_id, sample_rate, encoding:"pcm16", channels:1, source:"voice"|"delegate"|"typed" }
<binary>         PCM16 LE mono @ sample_rate      # ← response.output_audio binary frames, forwarded verbatim
tts.end          { pet_id, utterance_id }          # ← response.done (Unity raises SpeechEnded when its buffer drains)
tts.stop         { pet_id, utterance_id, reason:"barge_in"|"cancel"|"error" }   # Unity flushes immediately
agent.partial_answer / agent.answer_done           # ← response.output_audio_transcript.delta/.done (caption; existing events)
agent.tool_call  { pet_id, call_id, name, args }   # every function call (debug caption + pet animation)
agent.tool_result{ pet_id, call_id, name, ok, summary? }
pet.state        # every tool call also emits it (§8.7): fast tool → "thinking", delegate → "working"
```
Unity mapping additions (`GatorAgentBridge`): `speech.started` → Listening. `tts.start` → Answering, and `ITextToSpeech` raises SpeechStarted. `agent.tool_call` with name `delegate` → Working, other tools → a short Thinking + caption. `tts.stop` → flush buffer and go back to Listening (barge-in) or Wandering.
**Double-speak rule:** with voice on, `agent.answer_done` still reaches `_tts.Speak(text)`, but `StreamedPcmTextToSpeech.Speak` is a **no-op**, because the hub alone decides what is spoken.

## 4. Tools (fast tools on the voice model; all call the same in-process functions as REST)

```jsonc
[
 {"type":"function","name":"ml_get_run_summary","description":"Latest training run: status, epoch, val acc/loss, worst classes.","parameters":{"type":"object","properties":{"run_id":{"type":"string"}}}},
 {"type":"function","name":"ml_relabel","description":"Change a sample's label.","parameters":{"type":"object","properties":{"sample_id":{"type":"string"},"label":{"type":"string","description":"class name, e.g. 'cat'"}},"required":["sample_id","label"]}},
 {"type":"function","name":"ml_remove","description":"Remove a sample from the dataset.","parameters":{"type":"object","properties":{"sample_id":{"type":"string"}},"required":["sample_id"]}},
 {"type":"function","name":"ml_start_training","description":"Start a training run.","parameters":{"type":"object","properties":{"epochs":{"type":"integer","minimum":1,"maximum":50}},"required":["epochs"]}},
 {"type":"function","name":"cad_get_params","description":"Current CAD part parameters with min/max/unit.","parameters":{"type":"object","properties":{}}},
 {"type":"function","name":"cad_set_params","description":"Set one or more CAD parameters (numbers, within min/max).","parameters":{"type":"object","properties":{"params":{"type":"object","additionalProperties":{"type":"number"}}},"required":["params"]}},
 {"type":"function","name":"ui_highlight","description":"Highlight a panel and optionally items/class.","parameters":{"type":"object","properties":{"panel":{"type":"string","enum":["screen-main","ml-datastack","ml-model3d","ml-results","ml-training","cad-viewer"]},"item_ids":{"type":"array","items":{"type":"string"}},"class_idx":{"type":"integer"}},"required":["panel"]}},
 {"type":"function","name":"pet_go_to","description":"Walk/hop the pet to a panel, the user, or the desk.","parameters":{"type":"object","properties":{"target":{"type":"string","enum":["user","desk","screen-main","ml-datastack","ml-model3d","ml-results","ml-training","cad-viewer"]}},"required":["target"]}},
 {"type":"function","name":"messages_read_latest","description":"Read the latest WhatsApp messages.","parameters":{"type":"object","properties":{"count":{"type":"integer","default":3}}}},
 {"type":"function","name":"messages_send","description":"Send a WhatsApp message. FIRST call with confirmed=false, read the draft back to the user, and only call again with confirmed=true after they say yes.","parameters":{"type":"object","properties":{"to":{"type":"string"},"text":{"type":"string"},"confirmed":{"type":"boolean"}},"required":["to","text","confirmed"]}},
 {"type":"function","name":"delegate","description":"Hand off anything needing real analysis of the repo, run results or CAD script, or any code change. Say a short 'give me a sec' first.","parameters":{"type":"object","properties":{"task":{"type":"string","description":"self-contained instruction incl. context from the conversation"},"mode":{"type":"string","enum":["qa","code","auto"],"default":"auto"}},"required":["task"]}}
]
```
`messages_send` with `confirmed=false` never sends, and the hub enforces this, not the prompt (§12 rule).

**`delegate(task, mode)` → `runner.run_turn`:**
1. On `function_call_arguments.done`, emit `agent.tool_call` + `pet.state working`. Then **immediately** return `function_call_output {"status":"started"}` and send `response.create`. Grok has no "pending" tool results, so this acknowledgement lets it say "on it, give me a sec".
2. Spawn `runner.run_turn(project, task, mode, emit, pet_id=…, control=TurnControl())` as a background task (the same project resolution and `Emitter` as `ws._on_ask`, stored on `Session.turn` so `agent.cancel` still works). It streams `agent.partial_answer` / `agent.task_*` to Unity as it does today.
3. When it returns the answer or summary string, **wait until the current utterance has drained** (`tts.end` sent plus a small slack). Then send `force_message {interruptible:true, text: answer}`. Claude's QA prompt already outputs 1–3 spoken sentences, so it is spoken verbatim. Then add a context note so Grok can discuss it: `conversation.item.create` user `input_text` "[delegate result] …", **without** `response.create`.
4. Tag the forced response as `source:"delegate"` and suppress its `output_audio_transcript` → `agent.answer_done` mapping, because the runner already emitted `answer_done`.
5. Typed `agent.ask` keeps its current path (runner). If a voice session is open, the hub speaks the result with the same `force_message` call (`source:"typed"`).

## 5. Modules and classes

**Hub (person 5):**
- `hub/agent/voice.py` holds `VoiceSession`:
  - `connect()`, `feed(pcm)`, `begin_turn()` / `commit_turn()`, `say(text, source)`, `cancel()`, `close()`.
  - A reader task translates Grok events into ours and forwards binary.
  - A `VoiceBackend` Protocol sits underneath for the ElevenLabs swap.
- `hub/agent/tools.py`: JSON schemas plus `async def dispatch(name, args, ctx) -> dict`, which calls the ml/cad/messaging functions in process.
- `ws.py`: `Session.voice`. `_on_audio` → `voice.feed`. `stt.start/stop` → begin/commit. Add a `tts.stop` handler.
- `bus.py`: `send_bytes(ws, data)` behind the same per-socket lock. **Speech audio is sent to the owning socket only, never broadcast.**
- `events.py`: new payload models. `tts.py`: replace the stub with `GrokTTS` (the standalone `wss://api.x.ai/v1/tts`) for when no voice session is open.

**Unity (Adrian, `Assets/_Forge/Pet/Scripts/Voice/`):**
- `AgentClient`: add `event Action<byte[], int> BinaryReceived`, raised **on the socket thread** (it ignores binary today).
- `StreamedPcmTextToSpeech : MonoBehaviour, ITextToSpeech, ITalkLevel`:
  - Uses an AudioSource with no clip and spatialBlend 1 on the gator head, with `OnAudioFilterRead` pulling from a lock-free SPSC float ring (about 4 s, allocated once).
  - The producer is the socket thread (PCM16 → float, resampled only if `sample_rate != outputSampleRate`).
  - Pre-buffers about 120 ms before un-muting. On underrun it outputs zeros.
  - `tts.start` → SpeechStarted. When the ring is empty after `tts.end` → SpeechEnded. On `tts.stop`, flush.
  - `Speak()` is a no-op. `Stop()` flushes and sends `tts.stop`.
  - Each audio block computes RMS into a `volatile float Level`.
- `HubSpeechToText` is the "GrokSpeechToText via hub". **It needs no code change.** Transcripts arrive as `stt.partial/final` whatever the backend. Only `hello.audio_out_rate` is added in `AgentClient`.
- `ITalkLevel { float Level {get;} bool IsTalking {get;} }`. `NullTextToSpeech` implements it with a fake syllable envelope (about 6 Hz noise) so the animation works in mock mode.
- `GatorTalkAnimator`:
  - Reads `ITalkLevel` and smooths it with an envelope follower (attack about 30 ms, release about 120 ms).
  - In `LateUpdate`, after the Animator, adds a head-bone pitch nod plus a root Y squash-bounce proportional to the level. A threshold crossing triggers an extra "syllable" bob.
  - Drives `eyes.happy` 0.3–0.8 from the level (direct weight, no per-frame coroutine), with an occasional `eyes.excited-1` on peaks.
  - The head bone is a serialized field (Generic rig, picked by name).
  - It starts and stops from `ITextToSpeech.SpeechStarted/Ended` at the `TODO(lipsync)` hook. **The gator has no jaw blendshape. Don't look for one.**

**Interface between person 5 and Adrian = §3 of this file, plus two fixtures:**
- Hub `scripts/fake_tts.py` streams a WAV as `tts.start`/binary/`tts.end` (and `FAKE_AGENT=1` gains a voice script).
- Unity `MockEventSource` can play a WAV through the same ring.
- Either side can be built and tested without the other or without an xAI key.

## 6. Risks

- **Latency budget** (PTT release → first sound, target < 1.2 s):
  - 100 ms last mic chunk
  - about 20 ms Wi-Fi
  - about 80 ms hub↔xAI
  - model time-to-first-audio unknown (measure it on day 1 with `reasoning.effort:"none"`)
  - 120 ms Unity pre-buffer
  - Log the timestamps in `voice.py`.
- **Quest 48 kHz:** solved by requesting output at `audio_out_rate`. The mic may not give 16 kHz natively, but `HubSpeechToText` already resamples. `OnAudioFilterRead` together with the Meta spatializer plugin needs checking on device. Fallback: `spatialBlend=0` (2D).
- **Mac / simulator:**
  - Unity Editor needs macOS mic permission (System Settings → Privacy → Microphone → Unity).
  - Laptop speakers feeding the laptop mic cause self-barge-in in `server_vad` mode, so use headphones or manual PTT.
  - Fallback is the hub-side laptop mic (`sounddevice`) if the editor mic fails.
- **Model id drift:** `think-fast-1.0` is gone from the docs. Pin `grok-voice-3.0`. If the voices don't fit the persona, try custom voice cloning before switching to ElevenLabs.
- **Tool and audio overlap:** follow xAI's "drain playback, then `response.create`" advice, and track a `playing_until` estimate per utterance on the hub.
- **Cost:** $4.80/h per open session, so close idle sessions. Concurrency on T0 is 10 sessions, which is plenty.
- **Wi-Fi drops:** use `resumption.enabled` with a saved `conversation_id`, and reconnect the Grok socket in the hub without dropping the Unity socket.

## 7. Task list (rough hours)

| # | Owner | Task | h |
|---|---|---|---|
| 1 | P5 | `voice.py` spike: connect, `session.update`, laptop-mic loop through `sounddevice`, hear Grok, log event types and latency | 1.5 |
| 2 | P5 | Bridge into `ws.py`: Unity PCM16 in, binary out, `tts.start/end/stop`, transcripts → `stt.*`, `bus.send_bytes` | 2 |
| 3 | P5 | `tools.py`: 10 fast tools + schemas + `pet.state` / `agent.tool_call` emission, `messages_send` confirmation guard | 2 |
| 4 | P5 | `delegate` → `run_turn` background, drain-then-`force_message`, transcript suppression, cancel | 1.5 |
| 5 | P5 | `scripts/fake_tts.py` + `FAKE_AGENT` voice script + events.py models/tests | 1 |
| 6 | Adrian | `AgentClient.BinaryReceived` + `hello.audio_out_rate` | 0.5 |
| 7 | Adrian | `StreamedPcmTextToSpeech` (SPSC ring, resample fallback, pre-buffer, flush, RMS) | 2 |
| 8 | Adrian | `ITalkLevel`, `GatorTalkAnimator` (head nod/bounce + eyes.happy), hook at `TODO(lipsync)`, Null envelope | 1.5 |
| 9 | Adrian | `GatorAgentBridge` mapping for `speech.*`, `tts.*`, `agent.tool_call`, barge-in flow | 1 |
| 10 | both | Editor end-to-end on Mac, persona/voice pick, latency pass | 1 |
| 11 | Hudson+Adrian | Quest pass: mic permission, 48 kHz, spatializer check, Wi-Fi reconnect | 1.5 |

Total is about 16 h, split roughly 8 h for person 5 and 6 h for Adrian, plus shared integration. Tasks 1, 5 and 6–8 can run in parallel from hour 0.
