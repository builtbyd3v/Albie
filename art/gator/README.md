# Gator art: mouth + custom clips

Source of the gator's opening mouth and our own clips. The third-party pack (`unity/Assets/_Forge/Pet/ThirdParty/Quirky Series/...`) is never edited; everything here starts from a copy of its `Alligator_LOD0.fbx`.

| File | What it is |
|---|---|
| `gator_anims.blend` | Blender 4.1 working file: rig (+ `jaw` bone), mouth mesh, clip actions. Also keeps an untouched copy of the pack mesh (`Alligator_LOD0_orig`) that the mouth script rebuilds from. |
| `build_gator_mouth.py` | Cuts the mouth open along the lip line, adds the dark mouth cavity, cheek webs and tongue, adds the `jaw` bone and weights the lower jaw to it. Re-runnable. |
| `build_gator_clips.py` | Keyframes for every clip, as readable code. Re-runnable. |
| `export_gator_fbx.py` | Writes the two FBX files below with the pack's FBX settings. |
| `Gator_Model.fbx` | Mesh + rig with the jaw (LOD0 only). Copied to `unity/Assets/_Forge/Pet/Models/`. |
| `Gator_Animations.fbx` | Rig + one take per clip. Copied to `unity/Assets/_Forge/Pet/Animation/Custom/`. |

## Rig

The pack's 9 bones are unchanged (same names, same rest pose), so the pack's 19 clips play on the new model as they are; they leave the jaw closed. New bone: `Rig/root/body/head/jaw`, hinged at the mouth corners. Rotating it about its local X axis opens the mouth (positive = open in Blender).

## Clips (24 fps, like the pack)

| Clip | Frames | Loop | Moves |
|---|---|---|---|
| `Gator_MouthOpen` | 0-8 (0.33 s) | no | jaw: closed, open 22°, close |
| `Gator_MouthOpen_Hold` | 0-1 | no | jaw held open 25° (pose for blending / loudness) |
| `Gator_Talk_Loop` | 0-24 (1 s) | yes | jaw flaps 11-18°, small head lift and tilt |

Every clip holds the bones it doesn't animate at the rest pose, so play them on a layer masked to `jaw` (+ `head` for the talk loop) to keep the body animation from the base layer.

## Rebuild / re-export

1. Open `gator_anims.blend` in Blender 4.1.
2. In the Text Editor (or the Python console) run, as needed: `build_gator_mouth.py` (mesh), `build_gator_clips.py` (clips), then `export_gator_fbx.py`.
3. Copy the two FBX files over the ones in `unity/Assets/...` (paths above). Keep the `.meta` files: they hold the clip names (takes are exported as `Rig|<clip>`), the loop settings, and **Preserve Hierarchy** on the animations FBX. That file's only top-level node is `Rig`; without Preserve Hierarchy Unity makes `Rig` the root and the clips lose their `Rig/` path prefix, so they stop binding to the gator.

`Gator_Think_Loop` is drafted in `build_gator_clips.py` but not built or exported yet (not reviewed).
