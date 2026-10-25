# Desk extras (timers, notes, movie)

Things Bit adds on top of any mode. They stay open while you switch ML / CAD / CODE / MSG, fold away when you stand
up and come back when you sit down. The hub owns all of it (`hub/desk/`, contract in hub/README "Desk"); Unity is
the view.

| | Where | What |
|---|---|---|
| **Timers** (`TimerStack.prefab`) | pills just above the main panel (above ML's Training panel), growing upward | one pill per named timer, soonest at the bottom: name, time left, a rim of light draining; pause / resume, stop. Ringing: widens, pulses, chimes (`DeskChime`), +5 min / Stop. The − beside the bottom pill minimizes the stack into one dot: the soonest timer's minutes, its rim draining, a count when there are several |
| **Notes** (`NotesPanel.prefab`) | beside the open panels on the right | newest first; check ring, age, × (Undo for 5 s). Added by voice ("Ask Bit to add a note"). The header's − (with Face me / Straight) shrinks it into a dot with the open count |
| **Movie** (`MoviePanel.prefab`) | high over the desk, further back, tilted down at you | streams `GET /movie/files/<id>.mp4` from the hub. Point at it: title and × at the top; play / pause, ∓10 s, the scrubber, CC (subtitles), volume at the bottom. Quieter while Albie's voice plays. Move bar above it, resize corners like any panel |

**Dots** (`DeskOrb.prefab`, Core `PanelOrb`): tap to open the panel there again; hold (0.3 s, or start moving) to
carry it on the end of the pointer (scroll / thumbstick / reach pushes it back or pulls it in). The panel then opens
where you left the dot. Where you move things is kept for the session.

## How it fits

- `DeskExtras` (`Resources/DeskExtras.prefab`, one per scene under the Workspace: Main's builder adds it, and
  `DeskBootstrap` adds it at runtime to any scene with a Workspace that has none, so a Main rebuilt by another
  branch's builder still gets it) listens to `ui.widgets`, `timers.updated`, `notes.updated`,
  `movie.state` (classes in `Core/Net/ForgeEvents.cs`) and shows the panels through Core's `WorkspaceExtras`
  (anchors Top / Beside / Cinema; follows the open mode's layout until you move a panel).
- The panels' buttons call `DeskApi`: a POST to the hub when it's live; with the hub off, the same change on a local
  copy, raised as the event, so everything works (timers ring) without it.
- Nothing is in a mode's item list and nothing needs a slot: the hub decides what shows.

## Try it

- **Sandbox, hub off:** `DeskSandbox.unity`, Play. The mock events (`Mocks/desk/sequence.json`) start a focus and a
  tea timer, bring notes in and shrink them to a dot, put the movie on (it plays `hub/.data/media/obsession.mp4` if
  the hub's media folder is on this machine, else says so), and ring the tea timer. Keys: 1 a 25 s tea timer, 2 an
  hour of focus, 3 a note, 4 notes open / closed, 5 notes to a dot, 6 movie on / off, 0 all timers off. Mouse: the
  desktop rig (Esc frees the cursor).
- **Main, hub live:** sit at the desk and ask Bit, or straight to the hub:
  `curl -X POST localhost:8000/timers -H "Content-Type: application/json" -d "{\"minutes\": 25, \"label\": \"tea\"}"`,
  `curl -X POST localhost:8000/notes -d "{\"text\": \"try the dot\"}" -H "Content-Type: application/json"`,
  `curl -X POST localhost:8000/movie/play -d "{}" -H "Content-Type: application/json"`.

## Rebuild

`Forge > Desk > Build Desk Prefabs and Sandbox` (the panels build their UI at runtime, so the prefabs are small).
Mocks: `python -m desk.make_mocks` in `hub/`, then copy `.data/mocks/desk/Resources/Mocks/*` to
`Mocks/Resources/Mocks/` and `desk/sequence.json` to `Mocks/desk/`.

## Notes

- The pet's name in the UI ("Ask Bit to add a note", "Quieter while Bit talks") is `DeskExtras.assistantName`.
- Movie files: H.264 8-bit + stereo AAC, `+faststart` (hub/README "Adding a movie"). Not in git, not in the APK.
- The movie's VideoPlayer and the hub's URL need the Quest on the same network as the laptop (`hubUrl` on ForgeNet).
