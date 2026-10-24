"""Builds the custom gator clips on the "Rig" armature in gator_anims.blend.

Run inside Blender (Text Editor > Run Script, or exec this file). Re-running replaces the clips.
Each clip is one Blender action keyed only on the bones it moves; the FBX export bakes the rest.

Angles are degrees about armature (world) axes. The gator faces -Y, its left side is +X:
  about X: + tips the bone's front end down (jaw open, snout down); - lifts it
  about Y: rolls / tilts (head bone points along -Y)
  about Z: + turns the front end toward the gator's left (+X)
"""
import math

import bpy
from mathutils import Quaternion, Vector

RIG = bpy.data.objects["Rig"]
FPS = 24

X, Y, Z = (1, 0, 0), (0, 1, 0), (0, 0, 1)


def pose_quat(bone, *rotations):
    """Local pose rotation for `bone` from (world_axis, degrees) pairs, applied in order."""
    rest = RIG.data.bones[bone].matrix_local.to_3x3()
    q = Quaternion()
    for axis, deg in rotations:
        local_axis = (rest.transposed() @ Vector(axis)).normalized()
        q = Quaternion(local_axis, math.radians(deg)) @ q
    return q


def reset_pose():
    for pb in RIG.pose.bones:
        pb.rotation_mode = 'QUATERNION'
        pb.rotation_quaternion = (1, 0, 0, 0)
        pb.location = (0, 0, 0)
        pb.scale = (1, 1, 1)


def new_action(name, last_frame, loop):
    old = bpy.data.actions.get(name)
    if old:
        bpy.data.actions.remove(old)
    act = bpy.data.actions.new(name)
    act.use_fake_user = True
    act.id_root = 'OBJECT'
    act.use_frame_range = True
    act.frame_start, act.frame_end = 0, last_frame
    act.use_cyclic = loop
    if RIG.animation_data is None:
        RIG.animation_data_create()
    RIG.animation_data.action = act
    return act


def key_rot(bone, keys):
    """keys: {frame: [(world_axis, degrees), ...]}"""
    pb = RIG.pose.bones[bone]
    for frame, rotations in sorted(keys.items()):
        pb.rotation_quaternion = pose_quat(bone, *rotations)
        pb.keyframe_insert("rotation_quaternion", frame=frame, group=bone)
    pb.rotation_quaternion = (1, 0, 0, 0)


def hold_rest_elsewhere(act):
    """Key every bone channel the clip doesn't animate at rest, on the first and last frame. Blender leaves
    unkeyed channels wherever the last clip put them, so without this the FBX bake of one take can pick up
    another take's pose."""
    keyed = {(fc.data_path.split('"')[1], fc.data_path.rsplit('.', 1)[1])
             for fc in act.fcurves if fc.data_path.startswith('pose.bones')}
    start, end = act.frame_range
    for pb in RIG.pose.bones:
        for prop in ("location", "rotation_quaternion", "scale"):
            if (pb.name, prop) not in keyed:
                for frame in (start, end):
                    pb.keyframe_insert(prop, frame=frame, group=pb.name)


def finish(act, loop):
    reset_pose()
    hold_rest_elsewhere(act)
    for fc in act.fcurves:
        if loop and not any(m.type == 'CYCLES' for m in fc.modifiers):
            fc.modifiers.new('CYCLES')  # smooth tangents across the loop seam
        fc.update()


def jaw(deg):
    return [(X, deg)]


# ---------------------------------------------------------------- clips

def build_mouth_open():
    """Open, short hold, close. 8 frames (0.33 s)."""
    act = new_action("Gator_MouthOpen", 8, loop=False)
    key_rot("jaw", {0: jaw(0), 3: jaw(22), 5: jaw(20), 8: jaw(0)})
    finish(act, loop=False)


def build_mouth_open_hold():
    """Held wide-open pose (25 deg) for blending or driving by loudness."""
    act = new_action("Gator_MouthOpen_Hold", 1, loop=False)
    key_rot("jaw", {0: jaw(25), 1: jaw(25)})
    finish(act, loop=False)


def build_talk_loop():
    """1 s loop: uneven 'syllable' jaw flaps, small snout lift and a slow side-to-side tilt."""
    act = new_action("Gator_Talk_Loop", 24, loop=True)
    key_rot("jaw", {0: jaw(0), 3: jaw(16), 5: jaw(4), 8: jaw(12), 10: jaw(2),
                    13: jaw(18), 16: jaw(5), 19: jaw(11), 21: jaw(2), 24: jaw(0)})
    key_rot("head", {
        0: [(X, -2), (Y, -3)],
        6: [(X, -5), (Y, 0)],
        12: [(X, -1), (Y, 3)],
        18: [(X, -5), (Y, 0)],
        24: [(X, -2), (Y, -3)],
    })
    finish(act, loop=True)


def build_think_loop():
    """2 s loop: head tilted and raised, slowly rocking; gentle body sway; lazy tail with a tip flick."""
    act = new_action("Gator_Think_Loop", 48, loop=True)
    key_rot("head", {
        0: [(X, -6), (Y, 8), (Z, 0)],
        24: [(X, -10), (Y, 12), (Z, 5)],
        48: [(X, -6), (Y, 8), (Z, 0)],
    })
    key_rot("body", {0: [(Z, -3)], 24: [(Z, 3)], 48: [(Z, -3)]})
    key_rot("tail.01", {f: [(Z, 12 * math.sin(2 * math.pi * f / 48))] for f in range(0, 49, 6)})
    tip = {f: 18 * math.sin(2 * math.pi * (f - 6) / 48) for f in range(0, 49, 6)}
    tip[33] = tip[30] + 22  # quick flick of the tail tip
    tip[37] = tip[36]
    key_rot("tail.02", {f: [(Z, d)] for f, d in tip.items()})
    finish(act, loop=True)


BUILDERS = {
    "Gator_MouthOpen": build_mouth_open,
    "Gator_MouthOpen_Hold": build_mouth_open_hold,
    "Gator_Talk_Loop": build_talk_loop,
}
# Not reviewed yet, so not built (or exported) by default: build(["Gator_Think_Loop"]) to try it.
DRAFTS = {
    "Gator_Think_Loop": build_think_loop,
}


def build(names=None):
    reset_pose()
    for name in names or BUILDERS:
        (BUILDERS.get(name) or DRAFTS[name])()
    reset_pose()


if __name__ == "__main__":
    build()
