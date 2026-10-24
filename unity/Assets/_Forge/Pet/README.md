# Pet module (Bit, the gator)

Bit lives in **Main.unity** through one prefab: `Prefabs/PetRuntime.prefab`. Core's `MainSceneBuilder` instantiates it by
that path (a contract), so nothing pet-related is set up by hand in Main. The sandbox scenes (`Scenes/PetSandbox.unity`,
`Scenes/PetSandboxXR.unity`) are for working on Bit in isolation; `PetMain.unity` (a stale copy of Main) is gone.
Specs: `PROJECT_CONTEXT.md` 8.6 / 8.7 / 8.8, `docs/PET_AGENT_CONTRACT.md`, test guide `docs/PET_TESTING.md`.

## What is in PetRuntime.prefab

```
PetRuntime
├─ GatorAssistant        nested Prefabs/GatorAssistant.prefab: GatorController, GatorExpressions, GatorInteraction,
│   │                    GatorCaption, GatorAgentBridge, PushToTalk, HubSpeechToText, AgentClient, GatorMrukAdapter
│   │                    + WakeListener, GatorTalkAnimator, GatorSummon, GatorFollowUp
│   └─ Voice             AudioSource + StreamedPcmTextToSpeech (the hub's streamed speech), wired into GatorAgentBridge.tts
├─ WhistleButton         desk puck (poke / ray / mouse / F / B); GatorMrukAdapter clones it onto every table
├─ GatorCrew             lead = GatorAssistant, crewPrefab = GatorAssistant.prefab
├─ GatorHatchery         the egg + carousel picker (poke / ray / mouse)
└─ ArrivalDirector       (root) the desk choreography below
```

## How Bit behaves in Main

* **Desk.** `GatorMrukAdapter` builds the desk NavMesh from **`DeskPresence.Table`**, the table the user actually walked up
  to (re-built when it changes); `DeskLocator.Desk` only stands in until then. Without a `DeskPresence` (sandbox XR) it
  falls back to the locator / nearest table and hops by itself, as before. The desk keep-out covers the laptop + `DeskDock`
  area and, while the Messages cards float beside Bit, `MessagesDeskZone.Footprint`.
* **Arrival** (`ArrivalDirector`, one `[Arrival]` log line per step, every step is an inspector toggle):
  Near → hop onto the desk. Seated → wait for the landing (2.5 s cap) → `POST /messages/digest` once per sit
  (`DigestTrigger.RequestDigest`) → wait for Bit to finish the digest line (12 s cap, 1.5 s when there is nothing new)
  → wait up to 3 s for `screen.status` `sharing` → `Workspace.Open("code")` (falls back to `"screens"` until Core renames
  it). Away → re-arm; Bit hops down and follows (or wanders off: `followOnAway`). It also sends
  `workspace.mode {pet_id, mode}` on `Workspace.modeChanged` and after every (re)connect (`""` when closed).
* **Voice** (live hub only, no mock mode outside the sandboxes): `WakeListener` keeps the mic on and streams wake turns
  (`stt.start {wake: true}`, the hub decides by name / look-to-talk); `GatorFollowUp` keeps listening after an answer;
  `GatorSummon` (whistle) brings Bit over and opens a hands-free turn; `PushToTalk` (Space / controller primary) is the
  session manager with `HubSpeechToText`. The typed text box is sandbox-only.
* **Crew** is gated to the code screen: the egg and the N / X keys work while the `code` mode is open; when it closes
  the crew stays on the desk, keeps its antics, but its bubbles are muted (`GatorCaption.Muted`). Bit is never muted and
  the hub's `crew.spawn` / `crew.dismiss` are not gated.
* **`pet.state` with `target_panel`** (`screen-main`, `cad-viewer`, `messages`, ...): Thinking / Working walk or hop to
  the desk spot in front of that open panel (beside a volume such as the rocket, under / nearest a floating panel),
  face it and play the working emote. Panels are resolved through `PetWorkspace.TryGetItemTransform`, which calls
  Core's `Workspace.TryGetItemTransform` by reflection when it exists (PR C) and scans `ForgePanel` / `ForgeVolume` ids
  otherwise. Unknown or closed panel = the old come-to-user behaviour.
* **Mouth.** `GatorTalkAnimator` opens a `jaw` bone by the speech loudness (20° at full, local +X) on top of the head
  nod and body pulse. The bone comes with **PR 26** (`Models/Gator_Model.fbx`, `pet/gator-anims`); until that is merged
  `GatorSetup` builds the prefab from the pack model and the mouth stays shut (everything is null-safe). After merging
  PR 26, re-run **Forge ▸ Pet ▸ Build All** so the prefab picks the new model.

## Editor steps (in this order)

1. **Forge ▸ Pet ▸ Build All** — material, animator, `GatorAssistant.prefab`, both sandbox scenes, and (last)
   `PetRuntime.prefab`. Console ends with `[GatorSetup] BuildAll summary: ... RESULT: OK`.
2. **Forge ▸ Pet ▸ 8. Build PetRuntime Prefab** — rebuilds just the runtime prefab (after changing what Bit carries).
3. **Forge ▸ Messages ▸ Build Prefabs** — the Messages panel / card prefabs the digest uses.
4. **Forge ▸ Build Main Scene** (Core) — instantiates `Prefabs/PetRuntime.prefab`.

The "Add ... To Open Scene" menus (6. Whistle, 7. Agent Crew) still exist for the sandbox scenes; they share their
helpers with the PetRuntime builder.

## Config

`Assets/StreamingAssets/pet_config.json`: `{ "petId": "bit", "projectId": "gatorhack", "eventSource": "hub" }`.
The hub address is ForgeNet's (`hubUrl` on the ForgeNet object or PlayerPrefs `Forge.HubUrl`). `"mock"` only takes
effect on a gator that carries a `MockEventSource`, which only the sandbox scene builders add (and the mock overrides
the config there while it is enabled: untick the component to test the live hub from a sandbox).

## Files

| Folder | What |
|---|---|
| `Scripts/Core/` | `GatorController.*` (state machine, motion, surfaces, keep-out, antics, territory), `ArrivalDirector`, `PetWorkspace`, `GatorCaption`, `GatorExpressions`, `GatorInteraction`, `GatorNavSurface`, `PetUser`, `DeskCorner` |
| `Scripts/Agent/` | `GatorAgentBridge`, `AgentClient` (ForgeNet), `PetConfig`, `PetEvent`, `MockEventSource` (sandbox) |
| `Scripts/Voice/` | `WakeListener`, `PushToTalk`, `HubSpeechToText`, `StreamedPcmTextToSpeech`, `GatorTalkAnimator`, `GatorFollowUp`, `TypedSpeechToText` (sandbox), `NullTextToSpeech` |
| `Scripts/Whistle/` | `WhistleButton`, `GatorSummon` |
| `Scripts/Crew/` | `GatorCrew`, `GatorCrewMember`, `GatorHatchery`, `GatorNameTag`, `GatorAgentTypes` |
| `Scripts/Adapters/` | `GatorMrukAdapter` (the only pet script that references Meta types) |
| `Scripts/Sandbox/` | `GatorSandboxNav`, `PetSandboxHotkeys`, `DesktopRig`, `SandboxRigSelector` |
| `Editor/` | `GatorSetup` (Build All), `PetRuntimeSetup` (PetRuntime.prefab), `WhistleSetup`, `CrewSetup`, `GatorSetupCli` |
