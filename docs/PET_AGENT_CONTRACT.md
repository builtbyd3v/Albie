# Pet ⇄ Agent contract (Adrian's module)

Companion to `PROJECT_CONTEXT.md` §7/§8.6/§8.7. Everything here is additive to the
locked contracts; new event types are marked **proposed** and must be announced in chat
before the hub treats them as law.

## Where things live

| What | Path |
|---|---|
| Unity pet module (sandbox project for now: `~/GatorHack`) | `Assets/_Forge/Pet/` |
| Runtime scripts | `Assets/_Forge/Pet/Scripts/` (namespace `Forge.Pet`, no asmdef, Assembly-CSharp) |
| Editor scripts | `Assets/_Forge/Pet/Editor/` (namespace `Forge.Pet.Editor`) |
| Generated assets | `Assets/_Forge/Pet/{Animation,Prefabs,Materials,Scenes,Mocks}/` |
| Config | `Assets/StreamingAssets/pet_config.json` |
| Gator art (do not modify) | `Assets/Quirky Series/...` — model prefab `Ultimate Pack Vol.1/Mega Pack Vol.4/River Vol.2/Prefabs/Alligator.prefab` |
| Backend | repo `hub/agent/` (Python 3.11, FastAPI) + `hub/config/projects.yaml` |

Unity: 6000.6.7f1, URP 17.6, Input System only (no legacy Input), Newtonsoft JSON,
TextMeshPro, AI Navigation 2.0.16, Meta XR All-in-One SDK 207.0.1. **Pet scripts must not
reference Meta (`OVR*`/`Oculus.*`) or XRI types** — rig-agnostic (see `PetUser`).

## Gator asset facts (read from the imported files)

- Generic rig, in-place clips in `Alligator_Animations.fbx`:
  `Attack, Bounce, Clicked, Death, Eat, Fear, Fly, Hit, Idle_A, Idle_B, Idle_C, Jump, Lay, Roll, Run, Sit, Spin, Swim, Walk`.
  All are imported with loopTime=1 except Death — one-shots (Jump, Bounce, Clicked, Spin, Fear…)
  must be left via an exit-time transition, not by relying on the clip ending.
- 4 LOD skinned meshes under the model: `Mesh_LOD0..Mesh_LOD3` (LODGroup on the prefab root).
- Blendshape names (identical on every LOD mesh):
  `eyes.blink, eyes.happy, eyes.squint, eyes.sad, eyes.annoyed, eyes.excited-1, eyes.excited-2,
   eyes.lookUp, eyes.lookDown, eyes.lookIn, eyes.lookOut, eyes.sleep, eyes.dead, eyes.shrink,
   eyes.spin-1, eyes.spin-2, eyes.spin-3, eyes.rabid, eyes.trauma, eyes.cry-1, eyes.cry-2,
   sweat-1.L, sweat-1.R, sweat-2.L, sweat-2.R, teardrop-1.L, teardrop-1.R, teardrop-2.L, teardrop-2.R`
- Material `M_Alligator` uses the pack's built-in-RP `SoftSurface.shader` (`_MainTex`, `_Color`, `_Emission`).
  Pack ships a URP Shader Graph: `Assets/Quirky Series/_Shader/URP (Unity 2019.3.0f3 and above)/SoftSurfaceGraph.shadergraph`.

## Unity runtime API (namespace `Forge.Pet`)

```csharp
public enum GatorState { Intro, Wandering, Following, Listening, Thinking, Answering, Working, TaskComplete, Error }

// GatorController : MonoBehaviour (on prefab root, with NavMeshAgent)
public GatorState State { get; }
public event Action<GatorState> StateChanged;
public void SetState(GatorState s);
public void PlayEmote(string animatorTrigger);     // "Jump" | "Bounce" | "Clicked" | "Spin" | "Sit" | "Fear"
public void FaceTarget(Transform t);               // null = stop facing
public void ComeToUser();                          // path to ~1.2 m in front of PetUser.Head, then face them

// GatorExpressions : MonoBehaviour (same root; drives every LOD SkinnedMeshRenderer)
public enum Mood { Neutral, Happy, Attentive, Squint, Sad, Excited, Dizzy }
public void SetMood(Mood m, float fadeSeconds = 0.25f);
public void SetShape(string blendShapeName, float weight01, float fadeSeconds = 0.25f); // weight 0..1
public void Blink();
public bool AutoBlink { get; set; }

// GatorCaption : MonoBehaviour (world-space TMP bubble above the head, billboards to PetUser.Head)
public void SetText(string text, bool partial);   // partial = dimmed/italic, keeps updating
public void Clear();

// GatorInteraction : MonoBehaviour — poke/click → PlayEmote("Clicked") + Mood.Happy; public void Poke()
// PetUser (static) — Transform Head { get; }  : Camera.main → any enabled Camera → null
// DesktopRig : MonoBehaviour — WASD + mouse look CharacterController for the sandbox (Input System)
```

Animator (`Assets/_Forge/Pet/Animation/AC_Gator.controller`), base layer:
- param `Speed` (float, 0 = idle … 1 = walk … 2 = run) → 1D blend tree Idle_A / Walk / Run at thresholds 0 / 1 / 2
- param `IdleIndex` (int): sub-states Idle_B / Idle_C played once when `IdleIndex` == 1 / 2, then back to tree, controller resets it to 0
- triggers `Jump, Bounce, Clicked, Spin, Fear, Sit` (Sit loops until `Speed > 0.1` or any other trigger)
- one-shots return to the tree via exit time 0.95, transition 0.15 s
- `Speed` written by the controller from `agent.velocity.magnitude / runSpeed * 2`, damped.

Ratios the controller must keep so feet don't slide: walkSpeed ≈ 0.6 m/s at Speed=1, runSpeed ≈ 1.8 m/s at Speed=2
(the setup script measures the clip root-motion-free stride and may override via serialized fields).

## Speech-to-text (Unity)

```csharp
public interface ISpeechToText {
  bool IsListening { get; }
  event Action<string> PartialTranscript;
  event Action<string> FinalTranscript;
  event Action<string> Error;
  void StartListening();
  void StopListening();     // emits FinalTranscript
}
```
Implementations: `TypedSpeechToText` (on-screen input field, Enter = final), `HubSpeechToText`
(mic → PCM16 mono 16 kHz → `stt.audio` binary frames over the `AgentClient` socket; hub answers `stt.partial`/`stt.final`).
`PushToTalk` holds: Keyboard **Space** and XR controller primary button (Input System binding
`<XRController>{RightHand}/primaryButton`). Held → `GatorState.Listening`; released → `Thinking` + send `agent.ask`.
Android: request `Permission.Microphone` before first mic use.

## Text-to-speech hook

`ITextToSpeech { void Speak(string text); void Stop(); event Action SpeechStarted; event Action SpeechEnded; }`
— `NullTextToSpeech` (Unity) is the stub; on the hub speech comes from the Grok voice session (Addendum B; the old
`agent/tts.py` stub is gone). `GatorController` has a clearly marked `// TODO(lipsync)` hook in the Answering state.

## Config

`Assets/StreamingAssets/pet_config.json`:
```json
{ "hubUrl": "ws://127.0.0.1:8000/ws", "petId": "bit", "projectId": "gatorhack", "eventSource": "mock" }
```
`eventSource`: `"mock"` (in-Unity scripted `MockEventSource`, no network) | `"hub"` (live `AgentClient`).
Quest on LAN: replace 127.0.0.1 with the laptop IP, or `adb reverse tcp:8000 tcp:8000`.

## WebSocket protocol (`/ws`, JSON text frames `{ "type": "...", "payload": { ... } }`, binary frames = audio)

Path is `/ws`, the hub's only socket (Addendum C): the CAD `EventBus` (`hub/events.py`) publishes on it and the agent
session (`hub/agent/ws.py`) reads it. The agent-only `/ws/agent` was removed (Addendum F).

Client → hub:
```
hello            { client: "unity", pet_id, project_id }
agent.ask        { pet_id, project_id, text, mode: "qa" | "code" }     # mode "auto" lets the hub pick
agent.cancel     { pet_id }
stt.start        { pet_id, sample_rate: 16000, encoding: "pcm16" }
<binary>         raw PCM16 mono chunks (~100 ms) while listening
stt.stop         { pet_id }
```

Hub → client (existing contract, unchanged):
```
pet.state        { pet_id, mode: "follow"|"idle"|"listening"|"thinking"|"working"|"speaking", target_panel? }
pet.say          { pet_id, text, card?: { title, bullets: string[], actions?: [{ label, action_id }] } }
```

Hub → client (**proposed** additions, emitted by `hub/agent`):
```
stt.partial          { pet_id, text }
stt.final            { pet_id, text }
agent.thinking       { pet_id }
agent.partial_answer { pet_id, text }          # cumulative text so far
agent.answer_done    { pet_id, text }
agent.task_started   { pet_id, task_id, title }
agent.task_progress  { pet_id, task_id, text, pct? }
agent.task_complete  { pet_id, task_id, summary, branch? }
agent.error          { pet_id, message }
```

Event → gator state mapping (`GatorAgentBridge`):

| event | GatorState | extras |
|---|---|---|
| `stt.partial/final` | Listening | caption (partial/final) |
| `agent.thinking` / `pet.state thinking` | Thinking | Mood.Squint |
| `agent.partial_answer` | Answering | caption partial |
| `agent.answer_done` / `pet.say` | Answering | caption final, Bounce, Mood.Happy, `ITextToSpeech.Speak` |
| `agent.task_started/progress` / `pet.state working` | Working | caption progress |
| `agent.task_complete` | TaskComplete (Spin) → Wandering after the spin | caption summary |
| `agent.error` | Error | Fear + Mood.Sad, caption message |
| `pet.state follow` / `idle` | Following / Wandering | |
| `pet.state listening` / `speaking` | Listening / Answering | |

Hub REST: `GET /agent/projects`. A typed question is `agent.ask` on the socket like a spoken one; the
`POST /agent/text` typed fallback was removed (Addendum F).
Hub config `hub/config/projects.yaml`: `projects: { gatorhack: { path: "~/Documents/gatorhack-dubz", default_mode: "qa" } }`.
Env: `ANTHROPIC_API_KEY`, `FAKE_AGENT=1` (scripted events, no model calls), `STT_PROVIDER=none|whisper`.

## Addendum A (2026-10-23): team project, desk hop, runtime NavMesh

The pet module now lives in the team project `unity/Assets/_Forge/Pet/` (GatorHack was only the staging sandbox).
Gator art moved to `unity/Assets/_Forge/Pet/ThirdParty/Quirky Series/...` (same internal layout; GUIDs unchanged).
The team manifest has **no AI Navigation package** and only Core (Hudson) may add packages → the pet uses only the
built-in `UnityEngine.AI` module: `NavMeshAgent`, `NavMesh`, and runtime `NavMeshBuilder` with **box sources**
(no scene geometry needed, no baked assets, no `NavMeshSurface`).

```csharp
// GatorNavSurface : MonoBehaviour (one per walkable surface; on the prefab root there is none — created by whoever owns the surface)
public static int GatorAgentTypeId { get; }      // created once via NavMesh.CreateSettings(): radius 0.15, height 0.4, step 0.1, slope 45
public void BuildBox(Vector3 topCenter, Quaternion yaw, Vector2 size, IList<Bounds> holes = null); // walkable box = top surface, holes = unwalkable boxes
public bool IsBuilt { get; }   public Bounds WorldBounds { get; }   public void Clear();
public static bool TryGetSurfaceAt(Vector3 worldPoint, out GatorNavSurface s);   // which surface contains a point (registry)

// GatorController additions
public GatorNavSurface CurrentSurface { get; }                     // null = plain scene NavMesh / unknown
public void SetWanderArea(GatorNavSurface surface);                // wander points sampled inside surface.WorldBounds (XZ), falls back to radius-around-user when null
public void HopTo(GatorNavSurface target, Vector3 landing, Action onLanded = null); // Jump anim + parabolic arc (agent disabled), then agent.Warp + SetWanderArea(target)
public void HopToUserDesk();  // convenience: HopTo(DeskSurface, point on desk nearest the user) if a desk surface is registered
// The NavMeshAgent on the prefab uses agentTypeID = GatorNavSurface.GatorAgentTypeId (set in Awake).

// Adapters/GatorMrukAdapter : MonoBehaviour (the ONLY pet script allowed to reference Meta types; lives in Adapters/)
// Wire DeskLocator.deskFound (UnityEvent<MRUKAnchor>) → OnDeskFound(MRUKAnchor). It:
//   1. builds a desk GatorNavSurface from anchor.PlaneRect (top face, anchor rotation), registered as the "desk" surface
//   2. builds a floor GatorNavSurface from the room's FLOOR anchor plane rect, with holes for every anchor that has VolumeBounds
//   3. (serialized) hopOnFound = true → controller.HopToUserDesk() after a short delay; else just SetWanderArea(floor)
// Sandbox (no MRUK): GatorSetup builds a 20×20 floor surface + a desk surface over the Desk cube; hotkey H = HopToUserDesk, G = hop back to floor.
```

Event mapping unchanged. `pet.state follow` = floor following; `pet.state idle` while on the desk = desk wandering (controller decides by CurrentSurface).

## Addendum B (2026-10-23): Grok voice + whistle (all events **proposed**, additive)

Plans: `docs/VOICE_TTS_PLAN.md`, `docs/WHISTLE_PLAN.md`. Conversational path only: no tools, no `delegate`,
no keep-out zone yet.

Hub env: `VOICE_PROVIDER=none|grok|fake` (default `none` = everything above, unchanged), `XAI_API_KEY`,
`GROK_VOICE_MODEL=grok-voice-3.0`, `GROK_VOICE`, `GROK_VOICE_OUT_RATE=48000`, `VOICE_IDLE_CLOSE_S=120`,
`FAKE_VOICE_PACE`. One Grok session per `/ws` socket (`hub/agent/voice.py`), push-to-talk = manual turn.

Client → hub:
```
hello        { ..., audio_out_rate?: 48000 }    # AudioSettings.outputSampleRate; Grok speaks at it when supported
stt.start / <binary PCM16 16 kHz> / stt.stop    # unchanged; voice mode: stt.start = barge-in + new turn, stt.stop = commit + response.create
stt.start.require_name: bool = false             # proposed (look-to-talk): user wasn't facing Bit; the hub answers only if the
                                                  # transcript names him (first two words or last: "Bit, ...", "..., Bit?"), else empty stt.final, no captions
stt.start.facing: bool = false                   # proposed (wake turns): user faced Bit at speech onset; the hub answers with no name
stt.cancel   { pet_id }                          # drop the open turn: audio after it is ignored, its stt.stop yields an empty stt.final
tts.stop     { pet_id }                          # stop the current reply (ITextToSpeech.Stop)
```
Hub → the voice session's own socket only (never broadcast, no pet.state companion):
```
voice.ready  { pet_id, provider: "grok"|"fake", sample_rate }   # after hello
tts.start    { pet_id, utterance_id, sample_rate, encoding: "pcm16", channels: 1 }
<binary>     PCM16 LE mono @ sample_rate          # untagged; only tts audio flows hub → client
tts.end      { pet_id, utterance_id }             # Unity raises SpeechEnded when its buffer drains
tts.stop     { pet_id, utterance_id, reason: "barge_in"|"cancel"|"error" }   # flush immediately
```
Broadcast (existing events, new sources): Grok's user transcript → `stt.partial` (cumulative) / `stt.final { ..., voice: true }`;
the spoken reply's transcript → `agent.partial_answer` / `agent.answer_done` (captions unchanged); commit → `agent.thinking`;
Grok `error` → `agent.error`. `stt.final.voice = true` means "already being answered, don't `agent.ask` it"; the hub also
ignores an `agent.ask` that repeats the last voice transcript within 10 s.

Unity (namespace `Forge.Pet`, still no Meta types outside `Adapters/`):
```csharp
// AgentClient additions
public event Action<byte[], int> BinaryReceived;   // main thread, ordered with EventReceived; buffer reused after the call
public bool VoiceActive { get; }  public int VoiceSampleRate { get; }   // from voice.ready, reset on disconnect

// Voice/StreamedPcmTextToSpeech : MonoBehaviour, ITextToSpeech, ITalkLevel   (own GameObject: "Voice" child + AudioSource)
//   tts.* + binary → SPSC ring → OnAudioFilterRead (linear resample to the device rate, 120 ms pre-buffer, carrier-clip 3D)
//   Speak() is a no-op while VoiceActive (fakes a speaking period otherwise); Stop() flushes + sends tts.stop
public interface ITalkLevel { float Level { get; } bool IsTalking { get; } }   // smoothed loudness 0..1
public interface IMicLevel { float Level { get; } }                            // HubSpeechToText: RMS of the last chunk

// Voice/GatorTalkAnimator — while speaking: head-bone nod (additive, LateUpdate) + model squash pulse from Level, Mood.Happy

// PushToTalk additions (still the only transcript reader)
public bool BeginHandsFree();      // listen without a held key: ends on 0.9 s silence after speech, 8 s no speech (cancel), 20 s cap
public void CancelSession();       // sends stt.cancel (hub provider), discards the final, → Wandering
public bool SessionActive { get; }  public bool HandsFree { get; }  public event Action<bool> SessionEnded;  // true = answered
// Voice mode: the final is captioned + Thinking, but not sent as agent.ask.

// Whistle/WhistleButton — desk puck: Press(), HoverOn/Off(), SetVisual(Idle|Active|Busy), PlaceOnDesk(desk, head), event Pressed
//   inputs: ISDK Poke/Ray via InteractableUnityEventWrapper (wired by the editor menu), mouse click, F, right controller B
//   self-places at the desk's near edge to the user's right once GatorNavSurface.Desk is built; else 0.3 m right of the gator spawn
// Whistle/GatorSummon — Toggle() (button), Summon(), Cancel(), IsActive, event Action<SummonEnd> Ended (Listening|Cancelled|Ignored)
//   whistle SFX → HopToUserDesk() if on the floor with a desk, else ComeToUser() → arrived or 2.5 s → PushToTalk.BeginHandsFree()
//   Thinking/Working: ignored ("One sec!"); Answering: TTS stopped first; any foreign state change during the trip cancels it
```
Bridge mapping additions: `tts.start` → Answering; `tts.end` / `tts.stop` / `voice.ready` are handled by the TTS (no state change).
Editor: **Forge/Pet/6. Add Whistle To Open Scene** (`Editor/WhistleSetup.cs`) adds Voice + GatorTalkAnimator + GatorSummon to the
gator and a WhistleButton with Poke/Ray interactables to the open scene (idempotent, doesn't save). Hand/controller presses need
`OVRComprehensiveInteractionRig` in the scene's camera rig.

## Addendum C (2026-10-23): one socket through ForgeNet, per-table whistles, follow-up, bubble

- **Transport:** Unity talks to the hub only through Core's `ForgeNet` (team rule, PROJECT_CONTEXT §12). `AgentClient` now sits on
  ForgeNet's `/ws` socket: `ForgeNet.On(type)` per agent event type, `ForgeNet.Send` for JSON, `ForgeNet.SendBinary` for mic audio,
  `ForgeNet.BinaryReceived` for speech (the Send/SendBinary/BinaryReceived additions are their own PR, `core/forgenet-send`).
  `hello` is re-sent whenever ForgeNet goes Live. The hub's `/ws` runs the agent session on every socket while the CAD `EventBus`
  keeps publishing to it (`/ws/agent`, the agent-only endpoint, was removed in Addendum F). The hub address is ForgeNet's
  (`hubUrl` on the ForgeNet object / PlayerPrefs `Forge.HubUrl`); `pet_config.json` `hubUrl` is unused. `pet.state` / `pet.say`
  published on `/ws` by anyone now reach the gator too.
- **Delegate:** Grok's only tool. `agent.tool_call { pet_id, call_id, name, args }` (proposed, broadcast, companion `pet.state thinking`);
  the bridge maps it to Thinking. The runner's answer is spoken with `force_message` and not re-captioned.
- **Whistles:** `GatorMrukAdapter.whistleTemplate` is cloned onto every MRUK `TABLE` (`WhistleButton.SetTable`); each sits on its table's
  near-right corner and re-places when the user walks up from another side. Pressing one makes that table the desk (keep-out baked)
  before `GatorSummon` (subscribed to the static `WhistleButton.AnyPressed`) hops the gator there.
- **Follow-up:** `GatorFollowUp` keeps listening hands-free 15 s after an answer (`PushToTalk.BeginHandsFree(15)`); no reply →
  `GatorController.WanderAway()` (back turned, walks to a far wander point, idles). A whistle summon that ends without a question does the same.
- **Bubble:** `GatorCaption` draws a rounded pastel-green bubble with a green outline and green text in Fredoka
  (`Resources/PetFonts/Fredoka-Medium.ttf`, OFL, dynamic TMP font).
- **Cartoon voice:** `StreamedPcmTextToSpeech.pitch` (default 1.2) plays speech higher and faster; hub `GROK_VOICE_SPEED` ≈ 1/pitch keeps the pace.

## Addendum D (2026-10-24): Bit's crew (extra gators, one per agent type), areas, restful idling

**Agent types** (ids are pet_ids; at most one gator per type). The hub owns the prompt and tools
(`hub/agent/crew.py` `TYPES`), Unity the look (`Crew/GatorAgentTypes.cs`). Keep ids, names and roles in sync.

| id | role | kit | size vs Bit | hat |
|---|---|---|---|---|
| scout | maps the codebase and finds where things live | read-only | 0.88 | antenna, teal |
| builder | builds new features end to end | code | 1.12 | hard hat, orange |
| fixer | hunts down bugs and fixes the root cause | code | 0.90 | bandana, red |
| tester | writes and runs tests (test files only) | code | 0.86 | visor, yellow |
| reviewer | reviews branches and diffs for bugs | read-only + git read | 1.10 | beanie, purple |
| scribe | writes docs, READMEs and comments | edit/write, no shell | 0.87 | top hat, blue |
| shipper | handles git, changelogs and PR write-ups (never pushes) | code | 1.14 | captain cap, white |
| designer | tweaks the rocket CAD design | read + CAD tools | 0.89 | bow, pink |
| sentinel | audits the code for security problems | read-only + git read | 1.15 | helmet, navy |

**Messages** (additive; old clients ignore them):

| direction | type | payload |
|---|---|---|
| client → hub | `crew.roster` | `{members: [{pet_id, agent_type}]}`: every crew gator in Unity, sent after each change and on every (re)connect |
| hub → clients | `crew.spawn` | `{agent_type}`: hatch it (Bit's `spawn_agent` / `assign_task`) |
| hub → clients | `crew.dismiss` | `{pet_id}`: send it home (Bit's `dismiss_agent`) |

* `agent.ask` / `agent.cancel` with a crew `pet_id` run or stop **that member's** background turn. Bit's turn is never
  touched. All `agent.*` / `pet.state` events of that turn carry the member's `pet_id`.
* Every `GatorAgentBridge` now ignores events whose `pet_id` isn't its own (Bit = `pet_config.json` petId).
* Crew code tasks run in a git worktree (`<repo>-crew/<agent>-<id>`, branch `pet/<agent>/<slug>-<id>`) and are
  committed there; `git push`, `checkout`, `switch`, `reset`, `merge`, `rebase`, `stash` and `worktree` are denied.
* Bit's lead-only tools: `spawn_agent {type}`, `assign_task {agent, task, mode?}`, `dismiss_agent {agent}`,
  `crew_status {}`. In voice mode, Bit speaks "<Name> says: <summary>" when a member finishes.

**Unity** (`Forge/Pet/7. Add Agent Crew To Open Scene`):

* `GatorCrew`: `Spawn(type, at?)`, `Dismiss(petIdOrName)`, `DismissLast()`, `Members`, `Changed`. A crew gator is a
  copy of `crewPrefab` (GatorAssistant) with the Bit-only parts stripped before any Awake runs (mic, whistle, voice,
  AgentClient, MRUK adapter...). Its bridge shares Bit's event source (`UseSharedSource`). Keys: N hatch, X send
  the last one home.
* `GatorHatchery`: egg on the desk's near-left corner (the whistle has the right one). Press the egg to hatch the
  type on the label, or to send it home if it's already out; press the arrow for the next type.
* Areas: with a crew, every gator (Bit included) owns a strip of the desk with **equal walkable area** (the
  keep-out doesn't count; `GatorTerritories.Split`), nearest the user first: Bit, then the crew in hatch order.
  Strips narrower than `minStripWidth` (0.16 m) overflow onto the floor. `scope = Floor` splits the floor instead.
  `GatorController.SetTerritory` / `ClearTerritory`: wander points and WanderAway stay inside it, and a gator
  elsewhere walks or hops back. Crew gators think and work at the front of their own area
  (`StayInTerritoryWhenBusy`); Bit still comes to the user. Solo Bit (no crew) behaves exactly as before.
* Restful idling while a crew exists (all gators): long rests 85 % of pauses for 6-16 s, runs 8 %, short pauses
  2.5-6 s (`GatorController.SetIdleProfile`). Bit's own profile comes back when the crew is gone.

## Addendum E (2026-10-24): say a gator's name and it listens (always-on VAD)

* Unity `WakeListener` (on Bit, added by `Forge/Pet/7`): the mic stays on and an energy VAD (adaptive noise floor)
  cuts speech into segments. Each segment is a normal turn marked `stt.start {wake: true, pet_id?}`: 0.5 s of
  pre-roll, PCM16 16 kHz, then `stt.stop`. A segment with less than 0.35 s of speech gets `stt.cancel` before
  `stt.stop`. The listener pauses and releases the mic while push-to-talk or hands-free owns it
  (`HubSpeechToText.MicNeeded`), while Bit is speaking (so he can't wake his crew by saying their names) and while
  the hub is offline.
* Hub (`agent/wake.py`): a wake turn's `stt.*` is held back until the transcript names a gator, either at the start
  (after an optional "hey / ok / hi / yo") or at the end after a comma ("thanks, Bit"). "wait a bit" wakes nobody.
  * `"Hey Bit, <request>"` → Bit's `stt.final`, then Bit answers (voice: Grok responds to the turn; text: agent.ask).
  * `"<Crew name>, <request>"` → that gator's `stt.final` + `crew.assign` (hatched first if needed).
  * `"Hey <name>"` alone → **`wake.listen {pet_id}`** (hub → that socket only). Bit: Unity opens a hands-free turn.
    A crew gator turns to Listening, and the next segment is sent with `pet_id` = that gator and needs no name (8 s).
  * nobody named → nothing is emitted. In voice mode the committed item is deleted (`conversation.item.delete`), so
    room chatter never reaches Bit's conversation. A wake turn never barges in on Bit's speech.
* Privacy and cost: in voice mode (`VOICE_PROVIDER=grok`) every speech segment the headset hears is sent to xAI to
  be transcribed, so the Grok session stays open while people talk. With `VOICE_PROVIDER=none` and
  `STT_PROVIDER=whisper` the segments are transcribed locally instead.

## Addendum F (2026-10-24): one hub process, workspace view, "Reply with Bit", screen status

**One process.** `uvicorn main:app` is everything on the laptop: CAD worker, Slack Socket Mode, the pet, and now
the screen bridge (`hub/screens/`), which the hub spawns as `npm start` in `screen-bridge/` (VS Code extension
install, signaling.py :8766, stream.py :8765, Chrome sharing the VS Code window) and kills with itself. The agent
has no standalone app any more: `python -m agent`, `agent/app.py`'s `create_agent_app`, `/ws/agent` and
`POST /agent/text` are gone; `agent/tts.py` (dead stub) too. Everything the pet says or hears goes over `/ws`.
Env: `SCREEN_BRIDGE=1|0` (default on; off automatically with a warning when `node` or `screen-bridge/start.js`
is missing), `SCREEN_BRIDGE_DIR`. `GET /health` has `screen {enabled, state, source}`; `GET /screens/status` the long form.

**Messages** (all **proposed**, additive; old clients ignore them):

| direction | type | payload |
|---|---|---|
| client → hub | `workspace.mode` | `{pet_id, mode}`: the workspace view Unity shows, `"code"`, `"cad"`, `"ml"`, `"messages"`, or `""` when closed. Send on every change. |
| client → hub | `messages.focus` | `{pet_id, channel, ts}`: "Reply with Bit" on a message card (also `POST /messages/focus {pet_id, channel, ts}` → the `SlackMessage`, 404 if it isn't one of the last 50). |
| hub → clients | `screen.status` | `{state: "starting"\|"sharing"\|"stopped"\|"error", source?, detail?}`: the screen bridge. **Sticky**: replayed on connect like `cad.model_updated`. `source` is what Chrome shares, `detail` the last status line (`signaling: connected`, `video: connected h264`, the error when restarts are exhausted). |

* `workspace.mode`: kept on the hub session. The Grok voice session gets a one-line system note ("The user
  switched to the CAD view ..."; no reply is triggered). Bit's crew tools (`spawn_agent`, `assign_task`,
  `dismiss_agent`, `crew_status`) stay in Grok's tool list but answer
  `{"error": "crew tools are only available on the code screen; ask the user to open CODE"}` unless the mode is
  `"code"`. Chosen over editing the tool list mid-session: it is one branch in the tool-call path and does not depend on
  Grok re-reading `session.update`. A client that never sends `workspace.mode` keeps them available. `delegate` and the
  Claude runner are unaffected.
* `messages.focus`: the hub finds the message, Bit says `"<user_name> in <#channel | a DM> says: <text, ~200 chars>.
  Want me to answer?"` through the live voice session (`pet.say` with the same line when there is none), the voice
  session gets a one-line note naming the message, and the next `messages_send` that names no `reply_to_ts`,
  `thread_ts` or `channel` replies in that thread. The focus is spent once that reply is sent, or replaced by the next focus.
* Bit's read-only Slack tools, next to `messages_read_latest`: `messages_list_channels {}` (every conversation the
  user is in, with unread / mention counts and the last line; DMs named after the person) and
  `messages_read_channel {channel, n?}` (the last n of one conversation, oldest first, the user's own posts included as
  `is_me`; `channel` is a `#name`, an id, or a person's name for their DM). `messages_send`'s `channel` takes a person's
  name too ("tell Hudson ...") and drafts into their DM. All `messages_*`, so Bit walks to the `messages` panel.
* Crew runners (every agent type except Bit) now get only `vscode_*` hub tools, plus `cad_*` for the Designer whose
  kit is the CAD tools; never `messages_*` or the crew tools. Bit keeps everything.
* Slack: on connect the hub backfills every conversation the user is in (since `last_seen_ts`, else the last two
  hours; 20 per conversation) into the store **without** `notify.message`, so the sit-down digest counts what arrived
  while the hub was off and Unity gets no flood.

## Addendum G (2026-10-24): the arrival moment, listening is explicit (feat/arrival-start)

No new event types. Behaviour changes on `/ws`:

* **Wake turns send no `pet.state` until decided.** `stt.start {wake: true}` segments (Unity's always-on `WakeListener`)
  produce no `pet.state listening` companion for their `stt.partial` / `stt.final`; once the hub decides a segment is a
  request for a gator it sends `pet.state listening` for that gator explicitly, then the `stt.final` (then `agent.thinking`
  follows as before). Room chatter: nothing at all.
* **An empty `stt.final` has no companion.** Dropped (look-to-talk, `stt.cancel`), silent or ignored turns end with
  `stt.final {text: ""}` and no `pet.state`; Unity treats it as "nothing heard" (no caption, no state change).
* **Unity:** `GatorAgentBridge` switches to `Listening` on `stt.*` only while `PushToTalk.SessionActive` (a turn this gator
  opened) or on `pet.state listening`; never for an empty final. `PushToTalk` drops a stopped turn whose final doesn't
  arrive within 6 s.
* **`notify.digest.line` is never empty.** `digest_line(0, 0)` = `"You have no new messages today."`, and `/messages/digest`
  emits `pet.say` and speaks the line for the zero case too. The pet's greeting card shows that same `line`.

Unity (Pet, `Forge.Pet`):
```csharp
// GatorController additions
public void StationAt(Vector3 point, Transform face = null);  // Wandering, parked at a spot facing `face` / the user, until ReleaseStation()
public void ReleaseStation();   public bool Stationed { get; }
public void WanderTo(Vector3 point);                           // Wandering with the next trip chosen (clamped onto the surface, keep-out safe)
// Core/ArrivalGreeting : MonoBehaviour (PetRuntime/ArrivalGreeting, built by ArrivalSetup, inactive until used)
public void Show(DeskPresence presence, Vector3 greetingSpot, string line);  public void SetLine(string line);  public void Fold(bool immediate = false);
public event Action StartPressed;   public DockRing StartRing { get; }
```
Core (Hudson agreed): `Workspace.HoldDock(bool)` / `DockHeld`; `DockRing.Create(Transform parent, string label, Color hue,
float diameterMm, UnityAction onClick)` (+ the static `Layer` / `Label` / `SetLook` / `Pointers` DeskDock is built from);
`DeskDock.Show(frame, width, depth, bool reposition = false)` always enters as frame slide-in + ring plops.
