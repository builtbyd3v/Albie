# CAD Mode Plan: open-source CAD + cad-vision

> Companion to `PROJECT_CONTEXT.md` section 8.5. Resolves decision 11.4 ("CAD engine: pending repo review").
> Contract changes below are **proposals** until the team agrees (section 12 rule).

**Status 2026-10-23:** Phase 1 (hub) **done** plus the non-Unity SHOULD/STRETCH items: undo, reset, script-save regen, explode/focus events, STL export, flight sim, pet tools, mocks. 15 tests pass (`cd hub && pytest`). Phase 2 (Unity) **done**: `CadViewer` + `CadParamsPanel` prefabs and `CADSandbox.unity`, working from mocks and live against the hub. Next: Hudson slots the prefabs into the CAD mode. How-tos: `hub/README.md`, `unity/Assets/_Forge/CAD/README.md`.

---

## 1. Decision

**CAD engine: build123d** (Python, OpenCascade kernel, Apache-2.0).
**Viewer foundation: lift the loader, shaders and explode/focus ideas from [cad-vision](https://github.com/BAAA-Hacks/cad-vision)** (MIT), feed it from our hub instead of SolidWorks.

cad-vision is a *viewer*: SolidWorks exports a GLB + metadata JSON, the Quest inspects it, and its AI (CADEN) can query and move parts but **cannot change the design**. We beat it by closing the loop: say a change, the part regenerates in under a second, and the room shows you whether the rocket will still fly.

---

## 2. Options we compared

| Engine | License / runs where | Parametric B-rep + assemblies | Named parts in GLB | Mass / CG | LLM writes it well? | Verdict |
|---|---|---|---|---|---|---|
| **build123d** | Apache-2.0, `pip install`, local | Yes (OCCT), joints, labels | **Yes, verified** (labels become glTF node names) | Yes (OCCT) | OK: better first-try pass rate than CadQuery in one study, more errors than OpenSCAD in another | **Pick** |
| CadQuery | Apache-2.0, local, same kernel | Yes | Assembly glTF export exists; names/colors reported unreliable | Yes | Most training/benchmark data | Close second. **Cannot coexist with build123d in one venv** (verified: OCP 7.8 vs 8.0 breaks imports) |
| FreeCAD 1.x | LGPL, big desktop app | Yes (+ spreadsheets) | GLB export exists; headless export historically unavailable | Yes | Weak, sprawling API | Too heavy to drive headless at a hackathon |
| OpenSCAD | GPL, local | CSG mesh only, no B-rep, no assembly tree | No (STL/3MF) | Volume only | **Best** (fewest LLM code errors) | Wrong output for part-level interaction |
| Zoo (KCL) | Language open, **engine is a proprietary cloud API** | Yes | n/a | n/a | Has its own agent | Cloud dependency during a live demo: no |
| SolidWorks (cad-vision path) | Paid, Windows | Yes | Yes (their exporter) | Yes | n/a | Keep **compatible**, don't depend on it |

Why build123d wins *for us*: the main demo path is the voice model setting **numbers** on a pre-written part, not writing CAD from scratch, so LLM code-gen accuracy only matters for the stretch goal. What matters is named parts in the GLB, real mass properties, speed, and no license, and build123d delivered all of those in the spike.

---

## 3. Verified results (2026-10-23, `hub/cad/`, covered by tests)

Parametric rocket (ogive/cone nose, body tube, motor mount with 2 centering rings, N fins) in build123d → GLB → cad-vision schema 1.0 metadata:

| Check | Result |
|---|---|
| GLB node tree | `rocket → nose_cone, body_tube, motor_mount → (mount_tube, 2 rings), fin_set → fin_1..N`, names match labels 1:1 |
| cad-vision `CadMetadata.cs` rules (ported to Python, run on every export) | **PASS** (unique IDs, node indices, tree connects to root, childIds consistent) |
| Axes / units | Root node carries Z-up → Y-up rotation; metres **only with `unit=Unit.MM`** (the default; it names the *source* unit). `Unit.M` gives a 1000x model |
| Speed (real uvicorn, over HTTP, incl. the WS event) | **About 400 ms per edit**, undo 9 ms, GLB about 300 KB |
| Stability math | Barrowman CP matches **RocketPy 1.13 to within 0.5 mm, CNα within 1%** on 4 designs, including 5 fins (fin-count interference factor from OpenRocket) |
| Fin geometry vs aero | Test measures the drawn fin and checks it against the numbers the stability math uses |
| Masses | Hollow nose (shell), paper tube, balsa fins, ply rings, **Estes C6 24.1 g** + 12 g recovery: about 77 g, under the C6's 113 g max liftoff weight |

Correction to my first report: the spike's "matches RocketPy to 0.1 mm" was real but meaningless. The spike drew fins with a 30 mm sweep and gave both calculations 5 mm, so they agreed with each other, not with the geometry. Fixed by defining sweep once (`FinSet`, standard rocketry definition) and testing it against the solid.

---

## 4. Architecture

```
 voice/Bit ─┐                                   ┌──────────── Unity (CAD/) ─────────────┐
 sliders  ──┼─► POST /cad/params ─┐             │ CadViewer prefab                      │
 VS Code save (rocket.py) ────────┤             │  ├ CadModelLoader  (from cad-vision)  │
                                  ▼             │  ├ CadMetadata     (from cad-vision)  │
                hub/cad  (warm worker process)  │  ├ edge/outline shaders (cad-vision)  │
                 ├ rocket.py   params → Compound│  ├ diff: changed parts glow + ghost   │
                 ├ export.py   GLB + metadata   │  ├ explode / focus                    │
                 ├ analysis.py mass, CG, CP,    │  └ CG/CP markers + margin gauge       │
                 │             margin (Barrowman)│ CadParamsPanel (ForgePanel)           │
                 └ versions/   v1, v2, ... kept │ CadTimeline (undo, A/B ghost)         │
                         │                      └───────────────────────────────────────┘
                         └── WS cad.model_updated ──► Unity fetches model.glb + metadata.json?v=N
```

Rules:
- **The hub owns the part.** Unity never edits geometry; it shows `version N`.
- **One rebuild path.** Sliders, voice and file saves all call `regenerate(params | script)`.
- **Run CAD in a warm worker subprocess.** Importing build123d is slow cold, and an OCCT crash or hang must not take down the hub. Kill after 8 s, respawn, keep the last good version.
- **A script save keeps the current parameter values.** Editing a default in `PARAMS` only matters on reset; new params get their default, removed ones are dropped.
- **Stay cad-vision compatible.** Our metadata is valid schema 1.0, so a SolidWorks user could send a design through their add-in and it loads in our viewer (read-only, no regen).

---

## 5. Proposed contract additions (section 7)

All implemented in `hub/cad/router.py` and `hub/events.py`. Omit `v` for the current version (`no-store`); with `v` the file is immutable and cacheable.

```
GET  /cad/params                      -> { part, version, params: [{ name, value, min, max, unit, step, options, label }] }
POST /cad/params  { name: value, ...} -> { version, clamped: string[], analysis }   (adds clamped + analysis)
                                         422 unknown name / bad value, 500 build failure (body = cad.regen_failed payload)
GET  /cad/model.glb?v={version}                                              (unchanged)
GET  /cad/metadata.json?v={version}   -> cad-vision schema 1.0 document       NEW
GET  /cad/analysis?v={version}        -> analysis block below                 NEW
GET  /cad/flight?v={version}          -> { apogee_m, time_to_apogee_s, max_speed_mps, rail_exit_speed_mps,
                                           rail_exit_margin_cal, liftoff_mass_g, warning, trajectory_m: [[east, up, north]] }  NEW
GET  /cad/model.stl?v={version}       -> STL download                         NEW
POST /cad/undo                        -> { version, clamped, analysis }       NEW (409 when nothing to undo)
POST /cad/reset                       -> { version, clamped, analysis }       NEW (back to defaults; undo still works)
POST /cad/view    { explode?, focus_id? } -> { ok }                           NEW (UI only, emits cad.view)
GET  /cad/versions                    -> [{ version, parent, source, head, params, margin_cal, stability, created_at }]  NEW
GET  /cad/latest                      -> { "cad.model_updated": payload, "cad.view": payload }   NEW (the WS state, for
                                         clients without a WebSocket; Unity polls it until ForgeNet lands)

cad.model_updated { version, params, previous_version, source,               (source: api|pet|script|undo|reset|startup)
                    changed_ids: string[], removed_ids: string[],             NEW: glow these / fade these out
                    analysis: { mass_g, cg_mm, cp_mm, margin_cal, stable, stability, length_mm, diameter_mm,
                                markers_gltf: { cg: [x,y,z], cp: [x,y,z] }, breakdown, assumptions } }   NEW
cad.regen_failed  { source, kind: params|build|timeout|crash, error, error_line?, kept_version }   NEW
cad.view          { explode?, focus_id? }                                     NEW
```

`stability` bands: `unstable` < 1.0 cal (red), `marginal` 1.0 to 1.5 (amber), `stable` 1.5 to 2.5 (green), `overstable` > 2.5 (amber).
`markers_gltf` are in the GLB's scene space (metres, Y-up) on the rocket axis, so handedness doesn't matter in Unity.
`cad.model_updated` and `cad.view` are replayed to every client when it connects (and served by `GET /cad/latest`), so Unity gets the current state after a reconnect.
Panel IDs: add `cad-params`, `cad-timeline` next to `cad-viewer`.
`changed_ids` is computed in the hub by comparing per-object volume and bounding box between versions (verified: shrinking fins lights only the fins; lengthening lights only nose + body).

---

## 6. What we take from cad-vision (and what we don't)

| Take into `unity/Assets/_Forge/CAD/ThirdParty/CadVision/` (keep MIT notice) | Effort |
|---|---|
| `CadMetadata.cs`, `CadModelLoader.cs` (+ its `IndexedInstantiator` node → ID mapping), `CadVisionObject.cs` | Small: self-contained, needs glTFast + Newtonsoft. Newtonsoft 3.2.2 is already in the project; glTFast is added by Core (Hudson), don't edit `manifest.json` yourself |
| Shaders: `CADEdgeLines`, `CADSelectionOutline`, `CADSelectionMask`, `CADWireframeSurface` | Small: we're on URP 17.6.0, same as theirs |
| Explode / focus / ghost **logic** from `CADVisionManipulationService*.cs` | Medium: rewrite against our input (DesktopRig mouse + Interaction SDK), don't port their pointer system |
| `.codex/skills/meta-xr-operator-*` notes, for our coding agents | Free |

Skip: CADEN (a second AI brain on Gemini; Bit already does this job), multiplayer + shared anchors, their HTTP receiver on 8085 (our hub REST/WS replaces it), the SolidWorks add-in.
Gotcha: cloning cad-vision on Windows needs `git config --global core.longpaths true`.
Unity version: **we're pinned to 6000.6.7f1** (PROJECT_CONTEXT 11.6), with Meta XR SDK 207.0.1 and OpenXR 1.18.0. cad-vision is on 6000.6.0f1, the same Unity 6.6 line, so their code lifts without changes. Don't open the project in 6000.6.0f1.

---

## 7. How we beat cad-vision (feature list)

1. **Edit by voice:** "make the body 20% longer and add a fourth fin." Regenerates in under 1 s; changed parts glow, and a translucent ghost of the previous version fades out.
2. **Live flight-worthiness:** CG (blue) and CP (red) markers on the rocket, with a stability-margin gauge (bands in section 5). This is real Barrowman aerodynamics, cross-checked against RocketPy.
3. **Bit catches bad edits:** "shrink the fins" drops the margin into red → Bit: "That'll fly unstable. Want me to add a fourth fin?" → yes → green.
4. **Code is the source of truth:** `rocket.py` on the main screen; save in VS Code → same regen path. A syntax error keeps the last good model and shows the error on the panel.
5. **Timeline:** undo, plus A/B ghost compare ("show me 3 fins vs 4").
6. **Explode / focus:** pull the rocket apart into nose, body and fins (cad-vision's best interaction, rebuilt on our input).
7. Stretch: **"How high will it go?"** RocketPy flight sim with a real motor curve, and the trajectory arc drawn in the room.
8. Stretch: **Bit edits the script itself** (new feature, not just params) via the `delegate` model with a run-validate-repair loop.
9. Stretch: **Export STL** for a 3D printer, or load any SolidWorks design via cad-vision's exporter.

---

## 8. Pet tools for CAD (section 8.7 additions)

Implemented in `hub/cad/tools.py` (JSON-schema definitions in `TOOLS`, handler `call_tool`), ready for Person 5 to register:
`cad_get_params` (includes analysis), `cad_set_params`, `cad_undo`, `cad_reset`, `cad_view(explode, focus_id)`, `cad_simulate_flight`, `cad_get_context` (params, analysis, history and the script source, for `delegate`).
Errors come back as `{error}` so the voice model can explain instead of failing.
Still the agent's job: emit `pet.state working, target_panel cad-viewer` around `cad_set_params` so Bit walks over and tinkers (section 8.6), and `ui_highlight` if wanted.

---

## 9. Build plan

Owner: Person 5 owns the hub side until CAD gets an owner (section 10); the Unity viewer goes to whoever frees up first (likely Hudson).

**Phase 1: hub (DONE 2026-10-23)**
- C1 done. `hub/cad/parts/rocket.py`: `PARAMS` (`Param` with min/max/unit) + `build(params) -> BuildResult`. Part scripts declare their own params, so editing the script can add a slider.
- C2 done. `hub/cad/export.py`: GLB + metadata, maps nodes by name, validates with the ported cad-vision rules before publishing.
- C3 done. `hub/cad/analysis.py`: Barrowman CP + CG with motor + recovery mass, tuned for the demo story (section 10), tested against RocketPy.
- C4 done. `hub/cad/router.py`, `service.py`, `worker.py`: section 5 endpoints, version tree on disk (`hub/.data/cad/`), warm worker with timeout and respawn, events on `/ws`.
- Extra: file watcher, undo/reset, STL, flight sim (`flight.py`), pet tools (`tools.py`), mocks (`make_mocks.py`).
- **Accept:** met. About 400 ms per edit end to end.

**Phase 2: Unity viewer from mocks (DONE 2026-10-23, `unity/Assets/_Forge/CAD/`, see its README)**
- U1 done. cad-vision's `CadMetadata.cs` + outline shaders in `CAD/ThirdParty/CadVision/` (unchanged, MIT); our glTFast loader `CadModel.cs`; mocks in `Mocks/cad/` (`model.glb.bytes` so Unity keeps raw bytes).
- U2 done. `CadViewer.prefab` (ForgeVolume variant): turntable, glow on `changed_ids`, fading ghost of the changed parts, fit once with headroom, explode/focus. Not done: grab to rotate (waits on Core's grab), dissolve swap.
- U3 done. `CadParamsPanel.prefab` (ForgePanel variant): rows from `GET /cad/params`, POST on slider release, Undo / Reset / Explode / Fly.
- U4 done. CG/CP collars that slide between versions, stability chip + gauge, apogee in the stats line.
- **Accept:** met in `CADSandbox.unity`, and live against the hub: slider, external `POST /cad/params` (as Bit would), undo and flight all verified.

**Phase 3: integrate (about 2 h)**
- Hudson slots the two prefabs into the CAD mode; Person 5 registers `cad/tools.py`; swap `CadSession`'s poll for ForgeNet's `/ws` events; Bit tinkering animation.
- Live hub wiring, file watcher on `rocket.py` and explode/focus are already done.
- **Accept:** demo beat 5 works by voice, with typed fallback.

Tiers: **MUST** C1 to C4, U1 to U3, voice params. **SHOULD** U4 + the unstable-catch moment, undo, file-watch regen, explode. **STRETCH** A/B ghost, apogee sim + trajectory arc, script editing by Bit, STL export, SolidWorks import.

---

## 10. Demo beat 5 (revised)

Real numbers from the hub (these are the mocks v1 to v4):

1. Rocket on the desk, CG/CP markers, gauge **green (1.57 cal)**.
2. "Bit, make the fins smaller." Bit tinkers (fins to about 60%); only the fins glow; the gauge goes **red (0.84 cal)**. Bit: "Careful, that won't fly straight. Want me to add a fourth fin?"
3. "Yeah, add a fourth fin." The gauge goes **amber (1.25 cal)**. Bit: "Better, but still marginal. Make the body 20% longer?" "Do it." Nose and body glow; the gauge goes **green (1.60 cal)**.
4. (Stretch, hub side done) "How high will it go?" An arc traces up to the ceiling: "About 195 metres, roughly 640 feet, on a C6." (RocketPy, no wind, Cd 0.6.)

Caveats: how much "smaller" is up to the voice model. Under about 0.7x the fins don't go red, so the prompt should suggest about 60% or "a lot smaller". The no-wind sim flies the red version fine too, which is why `/cad/flight` returns a `warning` under 1.0 cal. Don't run the flight sim on the red version on stage.

---

## 11. Risks and gotchas

| Risk | Mitigation |
|---|---|
| build123d + CadQuery in one venv break each other (verified) | Hub uses build123d only. Pin `build123d==0.13.0` in `hub/requirements.txt` |
| Wrong `unit` makes the model 1000x too big | Always `unit=Unit.MM`; the export test asserts bbox under 2 m |
| OCCT hangs or crashes on bad params | Worker subprocess with an 8 s timeout and respawn, clamp params to min/max, keep the last good version (tested with `while 1: pass` and `1/0` in the script) |
| Hub stops mid-build | Worker handle kills the process first, then closes the pipe, and never respawns while closing (tested) |
| Slider spam queues builds (about 400 ms each, one at a time) | Unity sends `POST /cad/params` on slider release, not on every drag tick |
| Spline nose profile must close exactly | First and last points pinned to `(r, 0)` and `(0, n)` |
| cad-vision loader is strict about mapping | The exporter re-reads the GLB and validates before publishing a version |
| Bit's script edits produce broken code (build123d's LLM error rate is real) | Stretch only; run, validate, repair up to 3 tries, otherwise revert and say so |
| Wi-Fi to the Quest | GLB is about 175 KB, so this is a non-issue |

---

## Sources
- cad-vision repo, read in full for this plan: https://github.com/BAAA-Hacks/cad-vision
- build123d glTF exporter: https://build123d.readthedocs.io/en/stable/_modules/exporters3d.html
- CadQuery glTF export: https://cadquery.readthedocs.io/en/stable/importexport.html
- LLM comparisons: https://grandpacad.com/en/blog/openscad-vs-cadquery-vs-build123d, CADDesigner https://arxiv.org/pdf/2508.01031, Text2CAD-Bench https://arxiv.org/html/2605.18430v1
- FreeCAD headless: https://wiki.freecad.org/Headless_FreeCAD, https://github.com/gbroques/freecad-to-gltf
- Zoo / KCL: https://zoo.dev/docs/kcl-lang, https://github.com/KittyCAD/modeling-app
- RocketPy: https://rocketpy.readthedocs.io/en/latest/usage.html; Barrowman rules of thumb: https://www.nasa.gov/wp-content/uploads/2023/09/nasa-sl-2024-arw-rocketry-by-the-numbers-508.pdf
- Estes C6 data (24.1 g, 18 x 70 mm, thrust curve `hub/cad/data/estes_c6.eng`): https://www.thrustcurve.org/motors/Estes/C6/; max liftoff weight 113 g: https://estesrockets.com/products/c6-5-engines
