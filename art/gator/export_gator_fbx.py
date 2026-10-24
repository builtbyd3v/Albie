"""Exports the gator from gator_anims.blend to two FBX files next to this script, matching the pack's FBX setup
(Blender default axes: -Z forward / Y up, FBX_SCALE_NONE, no leaf bones, Y/X bone axes):

  Gator_Model.fbx       mesh + rig (with the "jaw" bone), no animation
  Gator_Animations.fbx  rig only, one take per "Gator_*" action, baked every frame. No mesh on purpose:
                        with the mesh, every take also gets constant curves for the 29 eye shape keys,
                        which would override GatorExpressions while a clip plays.

Takes are named "Rig|<action>" (Blender's "all actions" mode) and start at 0; the Unity import settings
give the clips their plain names. That mode puts the pose back after each take, so the file's static
pose stays the rest pose (NLA-strip mode leaves the last take's pose there instead). Run inside
gator_anims.blend.
"""
import os

import bpy

HERE = os.path.dirname(bpy.data.filepath)
RIG = bpy.data.objects["Rig"]
CLIP_PREFIX = "Gator_"

COMMON = dict(
    use_selection=False,
    object_types={'ARMATURE', 'MESH'},
    use_mesh_modifiers=True,
    mesh_smooth_type='FACE',
    use_tspace=False,
    use_custom_props=False,
    add_leaf_bones=False,
    primary_bone_axis='Y',
    secondary_bone_axis='X',
    use_armature_deform_only=False,
    armature_nodetype='NULL',
    axis_forward='-Z',
    axis_up='Y',
    apply_scale_options='FBX_SCALE_NONE',
    global_scale=1.0,
    apply_unit_scale=True,
    bake_space_transform=False,
    path_mode='STRIP',
    embed_textures=False,
)


def reset_pose():
    for pb in RIG.pose.bones:
        pb.rotation_mode = 'QUATERNION'
        pb.rotation_quaternion = (1, 0, 0, 0)
        pb.location = (0, 0, 0)
        pb.scale = (1, 1, 1)


def clip_actions():
    return sorted((a for a in bpy.data.actions if a.name.startswith(CLIP_PREFIX)), key=lambda a: a.name)


def export_model(path):
    ad = RIG.animation_data
    prev = ad.action if ad else None
    if ad:
        ad.action = None
    reset_pose()
    bpy.context.view_layer.update()
    try:
        bpy.ops.export_scene.fbx(filepath=path, bake_anim=False, **COMMON)
    finally:
        if ad:
            ad.action = prev


def export_animations(path):
    # "all actions" mode exports every action that fits the rig, so only clips may be in the file
    others = [a.name for a in bpy.data.actions if not a.name.startswith(CLIP_PREFIX)]
    if others:
        raise RuntimeError(f"Remove or rename non-clip actions before exporting: {others}")
    ad = RIG.animation_data or RIG.animation_data_create()
    prev = ad.action
    ad.action = None
    reset_pose()
    try:
        bpy.ops.export_scene.fbx(
            filepath=path,
            bake_anim=True,
            bake_anim_use_all_bones=True,
            bake_anim_use_nla_strips=False,
            bake_anim_use_all_actions=True,
            bake_anim_force_startend_keying=True,
            bake_anim_step=1.0,
            bake_anim_simplify_factor=1.0,
            **{**COMMON, "object_types": {'ARMATURE'}},
        )
    finally:
        ad.action = prev
        reset_pose()
    return [a.name for a in clip_actions()]


def export_all():
    export_model(os.path.join(HERE, "Gator_Model.fbx"))
    clips = export_animations(os.path.join(HERE, "Gator_Animations.fbx"))
    print("exported Gator_Model.fbx and Gator_Animations.fbx with", clips)


if __name__ == "__main__":
    export_all()
