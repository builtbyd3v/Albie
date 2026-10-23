# CLAUDE.md

Read `PROJECT_CONTEXT.md` first. It's the source of truth: the pitch, demo script, module owners, contracts (section 7), and current status (section 0).

## Rules (from PROJECT_CONTEXT section 12)
- Work only inside your module folder (section 6) unless told otherwise.
- Contracts in section 7 are law. Propose changes, never silently change them.
- Mock first. Every Unity module runs in its own sandbox scene with mocks from `Assets/_Forge/Mocks/` with the hub off.
- Never edit `Main.unity`, `Packages/manifest.json`, or Project Settings unless you are the Core owner (Hudson).
- Ship features as prefabs. No scene-only setup that someone else has to recreate.
- UI goes inside `ForgePanel` and must work with mouse (DesktopRig) and Interaction SDK ray/poke.
- Quest performance: no allocations or LINQ in `Update`, instancing for repeated meshes, textures 2048 max.
- Unity networking: NativeWebSocket for WS, `UnityWebRequest` for REST, Newtonsoft for JSON. Marshal WS callbacks to the main thread.
- Python: FastAPI routers per module, type hints, pydantic models mirroring `events.py`.
- Never send a real message or take an irreversible action from the pet without a user confirmation step.
- Small commits, pull often, never break `main`.

## Unity project
- Project is `unity/`: Unity 6000.6.7f1, URP, Meta XR SDK 207.0.1 over OpenXR 1.18.0. Setup in PROJECT_CONTEXT 6.1.
- Scripts in `Assets/` with no asmdef compile into Assembly-CSharp, which already references `Oculus.VR` and `meta.xr.mrutilitykit`.
- Drive an open editor with the `unity` CLI (skill: `.claude/skills/unity-cli`). Run from inside `unity/`. `unity status` can wrongly report "no instances" in a sandbox; `unity command` still works.
- Prefer editor scripts with a `[MenuItem]` that build scenes/prefabs in code over hand-editing `.unity`/`.prefab` YAML.
- Play-mode logs: `unity/Logs/Editor.log`. In the simulator, passthrough renders only in the simulator window; the Game view is black.
