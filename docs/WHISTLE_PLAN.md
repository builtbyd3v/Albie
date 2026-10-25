# Whistle to summon, then listen (plan)

> **2026-10-24:** Space/A push-to-talk is gone and the whistle is now a floating dock ring beside the desk dock (`WhistleButton`, Core `DockRing`). The flow below is otherwise how it works; read "PTT takes over" as history.

Owner: Adrian (pet). Status: plan, nothing built yet. Companion to `PROJECT_CONTEXT.md` §2 (demo step 4, "Ask the pet"), §8.6, §8.7, §12 and `docs/PET_AGENT_CONTRACT.md` Addendum A.

**What changes:** the user sits at the desk while the gator wanders on it. They press a whistle button, hear a whistle, and the gator trots over. It walks around the laptop/keyboard zone, never across it, and stops at the side of the laptop nearest the user. It turns to face them and starts listening on its own, with no held button. The STT provider decides when the user has stopped talking (server VAD). After that the usual Thinking/Answering flow runs. Poking the gator now only plays "Clicked". Push-to-talk (Space / A) stays as a second way in, mainly for the simulator.

## 1. UX flow and state transitions

Approaching does **not** get its own `GatorState`. Adding a 10th value would break the 1-9 sandbox hotkeys and the contract enum. While it approaches, the gator stays in `Wandering`, and `GatorSummon` owns the trip (the controller's `approachingUser` flag already stops wander trips).

```
Wandering / Following / Intro(after greet) / TaskComplete / Error / Answering
   │ whistle (button poke, ray select, mouse click, F, controller B)
   ▼  SFX + Mood.Excited (+ TTS.Stop() if Answering)
 ┌─ summon ───────────────────────────────────────────────────────────────┐
 │ seated? (head within 1.0 m of desk rect XZ) and desk built:            │
 │   gator on floor → HopToUserDesk(onLanded: listen) (lands at approach  │
 │                    point, never inside the keep-out)                   │
 │   gator on desk  → walk to approach point (side of the keep-out zone)  │
 │                    path partial (desk split by the hole) → hop there   │
 │ not seated / no desk → ComeToUser() on the floor (1.2 m in front)      │
 │ no NavMesh at all → turn in place                                      │
 │ ends on: arrived | 6 s approach timeout | path failure → listen anyway │
 │ whistle again → cancel → Wandering                                     │
 └────────────────────────────────────────────────────────────────────────┘
   ▼  FaceTarget(head), SetState(Listening), PushToTalk.BeginHandsFree()
 Listening  (ISpeechToText.StartListening, mic permission asked on whistle)
   ├─ stt.final, non-empty ───────────► Thinking → bridge.Ask → Answering/Working (unchanged)
   ├─ stt.final empty / Error ────────► Wandering (caption cleared)
   ├─ 8 s with no speech detected ────► cancel (discard final) → Wandering
   ├─ 20 s hard cap ──────────────────► StopListening → final → Thinking
   ├─ whistle again ──────────────────► cancel (discard final) → Wandering
   └─ Space/A pressed ────────────────► PTT takes over the session (hold mode)
 Thinking / Working + whistle ─► ignored: Bounce + caption "one sec!" (no implicit agent.cancel)
```

## 2. Where the whistle goes

**Decision: a small world-space button lying on the desk at the near edge, to the user's right.** It is a 7 cm puck tilted 25° toward the user, 0.08 m in from the edge, placed outside the keep-out zone (about 0.55 m right of the head's projection; set `handedness` for left-handed users).

| Option | Verdict |
|---|---|
| **Desk near edge (chosen)** | Within reach for a poke while seated. Easy to hit with a ray. In desktop mode a plain raycast against its collider is enough. It is anchored to the desk like the rest of the unfolding workspace, and it doesn't depend on hand tracking. |
| Wrist / palm button | Needs tracked hands (or controllers) and doesn't exist in desktop mode. The SDK's PalmMenu sample is `[Obsolete(..., true)]` in ISDK 207 ("HandOnHand sample has been removed"), so we would build it from `HandJoint` ourselves. Too much for the payoff. |
| HUD spot near the main screen | That is Hudson's panel space and `ForgePanel` doesn't exist yet. At 0.6-0.8 m it is too far to poke. |
| 3D whistle object | Cute, but a grab-and-blow gesture is a project of its own. Stretch: use a whistle **mesh** as the puck visual and keep the same press logic. |

The button places itself once the desk exists (`WhistleButton.PlaceOnDesk(desk, head)`). It is called from `GatorMrukAdapter` / `GatorSandboxNav` and again when the keep-out zone changes. It stays hidden until a desk is known, but the hotkey works regardless. Its footprint is also added as a **small hole** in the desk NavMesh, so the gator never parks on top of it.

**Desktop fallback:**
- **Mouse:** left click raycasts against the button's `BoxCollider`, the same pattern as `GatorInteraction`.
- **Key: `F`**. It doesn't collide with WASD, Space, Esc, Shift, J/B/K/P/C/M/T/H/G, 1-9, or Core's `E` (sit).
- **XR controller fallback:** `<XRController>{RightHand}/secondaryButton` (B). A is already push-to-talk.
- **Locked cursor:** with `DesktopRig` the first click locks the cursor and `Mouse.position` stops meaning anything. Add `PetUser.TryGetPointerRay(out Ray)`, which uses the screen-center ray while locked and the cursor ray otherwise. Use it in both `WhistleButton` and `GatorInteraction`.
- **Typing:** the `F` key is ignored while `TypedSpeechToText` is open, so you can type "f".

## 3. Keep-out zone

**Where the zone comes from, highest priority first:**
1. **Hudson's workspace layout.** After unfold, Core raises the keyboard + trackpad glow footprint (one oriented box, laptop included) and Main wires it to `GatorKeepOutZone.SetKeepOut(...)`. Core calls into Pet through a UnityEvent wired in Main, so Pet never references Core types.
2. **MRUK.** In `GatorMrukAdapter`, any anchor with `VolumeBounds` that stands on the desk counts: labels `SCREEN`, `OTHER` or `UNKNOWN`, with the volume bottom within 0.1 m of the desk top and overlapping it in XZ. Each one becomes a static desk hole. If one lies between the head and the desk center, it also seeds the keep-out zone (the zone is grown to cover it).
3. **Head fallback.** Project the head onto the desk's near edge. Build a desk-aligned box (desk yaw, not head yaw) **0.6 m wide × 0.45 m deep**, centered laterally on the head. It starts 5 cm past the near edge so the gator can't squeeze between the user and the keyboard, which puts its center about 0.22 m in front of the head projection. Height: from the desk top −0.05 m to +0.25 m, tall enough to act as an obstacle. Recompute only on sit and on the first whistle if unset; never per frame.

**Clamp:** keep each side strip at least `2·agentRadius + 0.05` = 0.35 m wide, otherwise radius erosion leaves nothing walkable beside the laptop. On the 1.2 m sandbox desk this clamps the width to about 0.44 m, which still covers a 13" laptop (0.31 m). Also grow the sandbox desk to 1.6 × 0.8 m and add a laptop prop.

**`GatorNavSurface` changes (Core/GatorNavSurface.cs):**
- Holes are currently world AABBs only, and an MRUK desk is usually rotated. Add `public struct OrientedBox { Vector3 center; Quaternion rotation; Vector3 size; }` and an overload `BuildBox(topCenter, yaw, size, IList<Bounds> holes, IList<OrientedBox> orientedHoles)`. A box source already takes a TRS matrix, so this is trivial.
- Add `public void SetHoles(IList<Bounds>, IList<OrientedBox>)`. It re-bakes with the stored top/yaw/size through `NavMeshBuilder.UpdateNavMeshData` on the **existing** `NavMeshData` instead of `Clear()` + `AddNavMeshData`, so agents stay bound. Fallback: rebuild, then `agent.Warp`.
- Add `public event Action<GatorNavSurface> Rebuilt`.

**New `Scripts/Core/GatorKeepOutZone.cs`** (one per scene, on the gator prefab, no Meta types):

```csharp
public sealed class GatorKeepOutZone : MonoBehaviour {
  public static GatorKeepOutZone Instance { get; }
  public GatorNavSurface Surface { get; set; }            // default: GatorNavSurface.Desk
  public bool HasKeepOut { get; }  public OrientedBox KeepOut { get; }
  public event Action Changed;
  public void SetKeepOut(Bounds worldBox);                // AABB (UnityEvent-friendly)
  public void SetKeepOut(Vector3 center, Quaternion yaw, Vector3 size);
  public void SetKeepOutFromHead(Transform head);         // fallback 0.6 x 0.45 (clamped)
  public void ClearKeepOut();
  public void SetStaticHoles(IList<Bounds> holes);        // MRUK desk-top volumes + whistle pad
  public bool Contains(Vector3 worldPoint, float margin = 0f);
  public bool TryGetApproachPoint(Vector3 head, Vector3 from, out Vector3 point);
}
```

Every setter goes through the same path: compose holes → `Surface.SetHoles(...)` → `Changed`.

**Approach point:** two candidates on the desk's near-edge line, at `±(keepOut.halfWidth + agentRadius + 0.05)` laterally and `deskEdgeInset` (0.15 m) inward. Each is snapped with `NavMesh.SamplePosition` (r 0.2) and costed with `NavMesh.CalculatePath` into a preallocated `NavMeshPath` (path length via `GetCornersNonAlloc`). The shortest complete path wins; ties go to the handedness side. If neither candidate works, use `SamplePosition(desk.ClosestPoint(head), 0.6)`. The NavMesh never contains hole cells, so that point is always outside the zone.

**If the gator is standing inside a new hole:** on `Rebuilt`, if `Contains(gator, agentRadius)`, the controller hops out with a short `HopTo(CurrentSurface, exit)`. `exit` is the nearest NavMesh point on the side of the zone away from the screen (same sampling as the approach point). A hop disables the agent, so there is no off-mesh glitch, and on screen it looks like the gator scooting out of the way. Otherwise, if it had a path, it re-issues the destination (`needRepath = true`). Wander sampling also rejects points inside the keep-out, so the gator doesn't hug the laptop edge.

## 4. Components to add / change

| File | Change / public API |
|---|---|
| **new** `Scripts/UI/WhistleButton.cs` | `public void Press()`, `public void SetHovered(bool)` (also `HoverOn()`/`HoverOff()` for UnityEvents), `public void PlaceOnDesk(GatorNavSurface desk, Transform head)`, `public enum Visual { Idle, Active, Busy }` via `SetVisual(...)`. `event Action Pressed`. It also has a serialized `InputAction whistleAction` (`<Keyboard>/f`, `<XRController>{RightHand}/secondaryButton`), a mouse raycast fallback, 0.3 s debounce, and the SFX. No Meta types. |
| **new** `Scripts/Core/GatorSummon.cs` | `public bool IsActive { get; }`, `public void Toggle()` (what the button calls), `public void Summon()`, `public void Cancel()`, `event Action<SummonEnd> Ended` (`Listening, Cancelled, Ignored`). Serialized: `approachTimeout 6`, `seatedRadius 1.0`. Runs the §1 machine on events and timers only; no allocations in `Update`. Drives the button's visual: Active while approaching or listening ("tap to cancel"), Busy while Thinking/Working. |
| **new** `Scripts/Core/GatorKeepOutZone.cs` | See §3. |
| `Core/GatorNavSurface.cs` | `OrientedBox`, oriented-hole overload, `SetHoles`, `Rebuilt` event (§3). |
| `Core/GatorController*.cs` | `public bool OnDesk`, `public bool IsHopping`. `HopToUserDesk(Action onLanded = null)`, which lands at the approach point. `public bool ComeTo(Vector3 goal, bool run)`. `event Action<bool> ApproachFinished` (raised from `UpdateApproach`; true = arrived). `UserGoal()` on the desk uses `GatorKeepOutZone.TryGetApproachPoint`, so Thinking/Working "come closer" also respects the zone. `OnSurfaceRebuilt` handles hop-out and repath. Wander points inside the zone are rejected. |
| `Voice/PushToTalk.cs` | Stays the **only** subscriber to STT events and the only caller of `bridge.Ask`, so transcripts never get asked twice. Adds `public bool BeginHandsFree()` (true if started), `public void CancelSession()` (sets a discard-next-final flag; works for Hub and Typed), `public bool SessionActive`, `event Action<ListenEnd> SessionEnded`. Hands-free timeouts: `noSpeechTimeout 8`, `maxUtterance 20`. Space/A behave as before. |
| `Voice/HubSpeechToText.cs` | If `stt.final` arrives while still listening (server VAD), stop the mic locally and send `stt.stop` (idempotent). Compute a per-chunk RMS with no allocations, exposed as `float Level`. Implement optional **new** `ISpeechActivity { event Action SpeechStarted; }`: first partial or RMS above threshold. This is a separate interface so `ISpeechToText` stays as is. Optional `clientEndpointing` (1.0 s of silence after speech → `StopListening`) as a safety net if the hub has no VAD yet. |
| `Core/GatorInteraction.cs` | Behavior already matches: poke only plays `Clicked` + Mood.Happy. Change: use `PetUser.TryGetPointerRay`, never change `GatorState`, update the doc comment. Poking during Listening restores `Attentive` afterwards (existing revert logic). |
| `Core/PetUser.cs` | `public static bool TryGetPointerRay(out Ray ray)` (handles the locked cursor). |
| `Adapters/GatorMrukAdapter.cs` | After `BuildDesk`, collect desk-top volumes (§3.2) → `GatorKeepOutZone.SetStaticHoles`, seed the keep-out zone, call `WhistleButton.PlaceOnDesk`. Still the only pet file with Meta types. |
| `Sandbox/GatorSandboxNav.cs`, `PetSandboxHotkeys.cs` | Sandbox desk 1.6 × 0.8 m with a laptop prop that seeds the keep-out. Hotkeys: `F` lives in WhistleButton itself (so it also works in Main); `L` toggles the keep-out (debug). |
| `Editor/GatorSetup.cs` | Adds `GatorSummon` + `GatorKeepOutZone` to the gator prefab. New `BuildWhistlePrefab()` → `Prefabs/WhistleButton.prefab` (§5) and SVG import settings (§6). Puts the button in both sandboxes; adds the ISDK interaction rig to `PetSandboxXR`. |

**Whistle SFX:** a two-tone rising chirp (about 1.8 → 2.6 kHz, 0.45 s) built once in `Awake` with `AudioClip.Create`. No asset, no LFS. A CC0 `.wav` can replace it later through the serialized clip field.

## 5. Interaction SDK wiring (verified against `com.meta.xr.sdk.interaction` in PackageCache, SDK 207)

**Prefab layout:**
```
WhistleButton            WhistleButton, BoxCollider (0.08×0.03×0.08), AudioSource
├─ Visual                puck mesh + icon quad (URP Unlit, alpha), pressed-depth anim
├─ Poke                  PokeInteractable  ─ _surfacePatch → ClippedPlaneSurface
│  └─ Surface            PlaneSurface (normal = button up) + BoundsClipper (0.07×0.07) + ClippedPlaneSurface
│                        InteractableUnityEventWrapper (_interactableView = PokeInteractable)
└─ Ray                   RayInteractable   ─ _surface → ColliderSurface (→ root BoxCollider)
                         InteractableUnityEventWrapper (_interactableView = RayInteractable)
```

- **Events:** each wrapper wires `WhenSelect → WhistleButton.Press()`, `WhenHover → HoverOn()`, `WhenUnhover → HoverOff()`. GatorSetup builds this reproducibly: it instantiates `Runtime/Prefabs/Poke/PokeInteractable.prefab` (it already carries `PlaneSurface` / `BoundsClipper` / `ClippedPlaneSurface`; drop its `ButtonVisual`) and `Runtime/Prefabs/Ray/RayInteractable.prefab` (`ColliderSurface`). It adds the wrappers by type name and sets `_interactableView` / `_surface` through `SerializedObject`, then adds persistent listeners with `UnityEventTools`. Doing it by hand matches the menu items `GameObject > Interaction SDK > Add Poke Interaction` / `Add Ray Interaction`. Pet runtime scripts stay free of Meta types.
- **Rig requirement:** nothing is pokeable without interactors. **Main currently has a bare `OVRCameraRig`.** Hudson needs to add `OVRComprehensiveInteractionRig` (or `OVRComprehensiveInteractors`) from `com.meta.xr.sdk.interaction.ovr/Runtime/Prefabs/`, which brings Hand/Controller `PokeInteractor` and `RayInteractor`. Adrian does the same in `PetSandboxXR`.
- **Why not a uGUI canvas:** a canvas button needs `Canvas`(World) + `GraphicRaycaster` + `PointableCanvas` + `PokeInteractable`/`RayInteractable` on clipped plane surfaces. The Quick Actions "Add Poke/Ray Interaction to Canvas" or `QuickActionsAPI.AddPokeCanvasInteraction` set that up. It also needs exactly **one** `PointableCanvasModule` on the EventSystem (it asserts on a second), and its `ExclusiveMode` (on by default via `Reset`) **disables `InputSystemUIInputModule`**, which breaks mouse clicks in DesktopRig unless RigSelector swaps modules. That EventSystem setup belongs to Hudson's `ForgePanel` work. A collider-based button works in both rigs today, with no EventSystem. If the whistle later moves into a ForgePanel, `Press()` is the only hook it needs.
- **§12 note:** the whistle is a desk prop like the gator itself, not panel UI. It works with the mouse in DesktopRig and with ISDK ray/poke, as §12 requires.

## 6. Icon / SVG pipeline

**Finding:** Unity 6.6's built-in `com.unity.modules.vectorgraphics`, already in the manifest, **includes the SVG importer**. `UnityEditor.VectorGraphicsModule.dll` in 6000.6.7f1 contains `Unity.VectorGraphics.Editor.SVGImporter` (a `ScriptedImporter`) with `SVGType` = `VectorSprite | TexturedSprite | Texture2D | UISVGImage | VectorImage`. So the `com.unity.vectorgraphics` package is not needed, and nobody has to touch the manifest.

**Decision:**
- Commit the designer-editable source as `Assets/_Forge/Pet/UI/whistle.svg` (single color, 24×24 viewBox).
- GatorSetup sets **SvgType = Texture2D, 256 px, Bilinear, alpha**. That gives a plain texture for the unlit icon quad, which also works in a uGUI `RawImage` later.
- **Check once in the editor (5 min):** the Inspector should show "SVG Importer". If it doesn't, or it renders wrong on Quest, move the SVG to `docs/art/whistle.svg` and commit a 256 px PNG at `Assets/_Forge/Pet/UI/whistle_256.png` through LFS.
- Drawing the icon procedurally was rejected: it costs more and is less editable.

## 7. Contract / event impact

- **No locked contract changes are required.** Unity keeps sending `stt.start` → audio → `stt.stop`, and the bridge already maps `pet.state listening` (no-op when already Listening).
- **Proposed** additive hub behavior (announce in chat):
  - `stt.start { ..., vad: "server", silence_ms: 700 }`: the hub sends `stt.final` unprompted at end of utterance.
  - Optional `stt.speech_started { pet_id }`, which arms the no-speech timer more precisely than the first partial.
  - On `stt.start`, the hub broadcasts `pet.state { mode: "listening" }` so other clients stay in sync. Unity does **not** need a client → hub `pet.state`.
- Mock mode (hub off): `TypedSpeechToText` opens; Enter plays the role of VAD.

## 8. Edge cases

| Case | Behavior |
|---|---|
| Gator on the floor, user seated | `HopToUserDesk(onLanded)` lands at the approach point, then listens right away. |
| User standing / away from desk | Floor `ComeToUser()`, then listen. No hop onto a desk the user isn't at. |
| Mid-hop when whistled | Queue until `!IsHopping`. |
| Desk not found (no MRUK table, sandbox without desk) | Floor approach. No NavMesh at all: turn and listen in place. Button hidden; `F`/B still work. |
| Keep-out covers both side candidates (narrow desk) | Use the nearest NavMesh point to the head (always outside holes). Still fails: listen in place. |
| Desk split into islands by the hole | `CalculatePath` gives `PathPartial` → hop straight to the approach point (an arc over the laptop). |
| Zone appears with the gator inside | Hop out (§3). |
| Whistle during Thinking/Working | Ignored with Bounce + "one sec!". Answering: stop TTS and listen again. |
| Space held during a summon | PTT takes over; summon cancelled; one session owner (PushToTalk). |
| Mic permission denied | Caption "I need microphone permission."; back to Wandering. Permission is requested on whistle, in parallel with the walk. |
| Hub disconnected | `Error("hub not connected")` → Wandering with caption; use Typed STT in the sandbox. |
| `pet.state idle` arrives mid-summon | Summon cancels quietly. Hub events win. |

## 9. Tasks

**Owner key:** A = Adrian (pet), A/hub = Adrian or person 5 in `hub/agent`, H = Hudson (core). Tasks marked **MVP** give a demoable feature in about 10.5 h.

| # | Task | Owner | h |
|---|---|---|---|
| 1 | `GatorNavSurface`: `OrientedBox` holes, `SetHoles` via `UpdateNavMeshData`, `Rebuilt` **MVP** | A | 1.5 |
| 2 | `GatorKeepOutZone` + head fallback + clamp + approach point + gizmos **MVP** | A | 2 |
| 3 | `GatorController`: keep-out-aware `UserGoal`, `HopToUserDesk(onLanded)`, `ComeTo`, `ApproachFinished`, hop-out on rebuild, wander rejection **MVP** | A | 2 |
| 4 | `PushToTalk` hands-free session (`BeginHandsFree` / `CancelSession` / timeouts / discard) **MVP** | A | 1.5 |
| 5 | `GatorSummon` orchestrator + Answering/Thinking rules **MVP** | A | 1.5 |
| 6 | `WhistleButton` (mouse/F/B fallback, procedural SFX, visual states, `PlaceOnDesk`, pad hole) + `PetUser.TryGetPointerRay` **MVP** (ISDK part in #7) | A | 2 |
| 7 | ISDK wiring in `GatorSetup.BuildWhistlePrefab` + interaction rig in `PetSandboxXR` | A | 1.5 |
| 8 | `HubSpeechToText`: unsolicited `stt.final`, RMS `Level`, `ISpeechActivity`, client endpointing fallback | A | 1.5 |
| 9 | Hub server VAD (`vad` flag on `stt.start`, silero/webrtcvad, 700 ms silence → `stt.final`, `pet.state listening` broadcast) | A/hub | 2 |
| 10 | `GatorMrukAdapter`: desk-top volumes → static holes + keep-out seed + button placement | A | 1 |
| 11 | Whistle SVG + import settings / PNG fallback check | A | 0.5 |
| 12 | Sandbox: 1.6 × 0.8 desk, laptop prop, `L` debug key; contract doc update | A | 0.5 |
| 13 | Test pass: desktop, simulator, Quest (poke height, ray, mic, VAD latency) | A + H | 1.5 |
| H1 | Add `OVRComprehensiveInteractionRig` to Main (needed for **any** poke/ray UI) | H | 0.5 |
| H2 | Workspace unfold raises the keyboard/trackpad zone (UnityEvent with center/yaw/size) → wire to `GatorKeepOutZone.SetKeepOut` in Main | H | 0.5 |
| H3 | Drop `WhistleButton.prefab` + gator prefab into Main (they self-place from the desk) | H | 0.25 |
| H4 | DesktopRig in Core: a crosshair while the cursor is locked (the center ray is what clicks) | H | 0.25 |
| H5 | Later, with `ForgePanel`: EventSystem policy (PointableCanvasModule in XR vs InputSystemUIInputModule on desktop) | H | 0.5 |

**Totals:** Adrian about 19 h (MVP about 10.5 h with #1-6), Hudson about 2 h.

**Main risk:** agent radius 0.15 m against a real desk. Measure the MRUK simulator table first; if the side strips are too thin, scale the gator down on the desk and give it a second agent type with radius 0.1.
