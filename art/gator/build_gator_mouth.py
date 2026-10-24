"""Gives the pack's gator (Alligator_LOD0) an opening mouth: a jaw bone, a dark mouth cavity and a tongue.

Run inside gator_anims.blend (Text Editor > Run Script, or exec this file). It always starts again from
the untouched mesh copy "Alligator_LOD0_orig", so it can be re-run after tweaking the numbers below.

What it does:
  * splits the skin along the mouth line (where the green upper snout meets the cream lower jaw),
    from the snout tip back to a corner under each eye; the corners stay joined, like a hinge
  * adds a cavity: domed roof (palate, on "head"), floor (on "jaw"), a back wall at the throat and
    inner cheeks along the sides, coloured with the palette's dark navy so the open mouth reads as a gap
  * adds a pink tongue on the floor (on "jaw")
  * adds the "jaw" bone under "head", hinged at the mouth corners, and moves the lower jaw onto it
Kept as is: the 9 original bones, the 29 eye/tear/sweat shape keys, the original normals.
"""
import math

import bmesh
import bpy
from mathutils import Matrix, Vector

MESH_OBJ, RIG_OBJ = "Mesh", "Rig"
SOURCE_MESH = "Alligator_LOD0_orig"     # untouched copy of the pack mesh
RESULT_MESH = "Alligator_LOD0_mouth"

MOUTH_BACK_Y = -0.30          # the cut stops in front of this (mouth corners under the eyes)
HINGE = (0.0, -0.299, 0.189)  # jaw bone head; the jaw rotates about X through here
JAW_FADE_END_Y = -0.215       # behind the corners the jaw weight fades out by here (throat)
PALATE_HEIGHT = 0.06          # how far the roof of the mouth domes up into the head
FLOOR_DEPTH = 0.045           # how far the floor dips into the lower jaw
THROAT_BACK = 0.03            # how far the back wall bulges behind the corners
COLUMNS = 6                   # cavity columns across the mouth
CHEEK_FRONT_Y = -0.70         # the cheek webs taper away by here
TONGUE_CENTER = (0.0, -0.52)  # (x, y); height follows the floor
TONGUE_SIZE = (0.07, 0.17, 0.028)   # half extents x, y, z

UV_NAVY = (0.875, 0.375)      # T_Alligator palette cells
UV_PINK = (0.375, 0.125)


def smoothstep(t):
    t = max(0.0, min(1.0, t))
    return t * t * (3 - 2 * t)


def ensure_jaw_bone(rig):
    if "jaw" in rig.data.bones:
        return
    prev_active = bpy.context.view_layer.objects.active
    bpy.context.view_layer.objects.active = rig
    bpy.ops.object.mode_set(mode='EDIT')
    eb = rig.data.edit_bones
    head = eb["head"]
    jaw = eb.new("jaw")
    jaw.head = HINGE
    jaw.tail = Vector(HINGE) + Vector((0.0, -head.length, 0.0))   # parallel to "head"
    jaw.roll = head.roll
    jaw.parent = head
    jaw.use_connect = False
    jaw.use_deform = True
    bpy.ops.object.mode_set(mode='OBJECT')
    bpy.context.view_layer.objects.active = prev_active


def palette_classifier(img):
    w, h = img.size
    px = list(img.pixels)

    def is_cream(face, uvl):       # cream lower jaw: #ffe694
        u = sum(l[uvl].uv.x for l in face.loops) / len(face.loops)
        v = sum(l[uvl].uv.y for l in face.loops) / len(face.loops)
        x, y = min(w - 1, int(u % 1 * w)), min(h - 1, int(v % 1 * h))
        r, g, b = px[(y * w + x) * 4:(y * w + x) * 4 + 3]
        return r > 0.98 and g > 0.85 and b < 0.65
    return is_cream


def zip_strip(a, b):
    """Triangles/quads joining two polylines that share their first and last points."""
    faces = []
    i = j = 0
    while i < len(a) - 1 or j < len(b) - 1:
        ta = (i + 1) / (len(a) - 1) if i < len(a) - 1 else 2.0
        tb = (j + 1) / (len(b) - 1) if j < len(b) - 1 else 2.0
        if abs(ta - tb) < 1e-6:
            faces.append([a[i], a[i + 1], b[j + 1], b[j]]); i += 1; j += 1
        elif ta < tb:
            faces.append([a[i], a[i + 1], b[j]]); i += 1
        else:
            faces.append([a[i], b[j + 1], b[j]]); j += 1
    return faces


def build_mouth():
    obj = bpy.data.objects[MESH_OBJ]
    rig = bpy.data.objects[RIG_OBJ]
    src = bpy.data.meshes[SOURCE_MESH]
    ensure_jaw_bone(rig)

    # fresh copy of the untouched mesh
    old = obj.data
    me = src.copy()
    obj.data = me
    if old is not src and old.users == 0:
        bpy.data.meshes.remove(old)
    me.name = RESULT_MESH
    me.use_fake_user = False
    # vertex group names live on the mesh, so add "jaw" after the swap
    if "jaw" not in obj.vertex_groups:
        obj.vertex_groups.new(name="jaw")
    G = {g.name: g.index for g in obj.vertex_groups}

    is_cream = palette_classifier(bpy.data.images["T_Alligator.png"])
    bm = bmesh.new()
    bm.from_mesh(me)
    uvl = bm.loops.layers.uv.active
    dl = bm.verts.layers.deform.verify()
    shapes = list(bm.verts.layers.shape.values())
    col = bm.loops.layers.color.get("Col")
    bm.faces.ensure_lookup_table()
    n_old_faces = len(bm.faces)
    cream = {f.index for f in bm.faces if is_cream(f, uvl)}

    # ---- mouth line: green/cream boundary in front of the corners, ordered corner -> tip -> corner
    line = [e for e in bm.edges if len(e.link_faces) == 2
            and (e.link_faces[0].index in cream) != (e.link_faces[1].index in cream)
            and all(v.co.y < MOUTH_BACK_Y and v.co.z > 0.15 for v in e.verts)]
    ends = [v for v in {v for e in line for v in e.verts} if sum(v in e.verts for e in line) == 1]
    assert len(ends) == 2, "mouth line should be one open chain"
    chain, prev = [min(ends, key=lambda v: v.co.x)], None
    while True:
        nxt = [e for e in line if chain[-1] in e.verts and e is not prev]
        if not nxt:
            break
        prev = nxt[0]
        chain.append(prev.other_vert(chain[-1]))
    n = len(chain)
    P = [v.co.copy() for v in chain]
    for k in range(n // 2):
        assert abs(P[k].x + P[n - 1 - k].x) < 1e-3 and abs(P[k].y - P[n - 1 - k].y) < 1e-3, "mouth not symmetric"

    # ---- split the lips apart (the two corner verts stay shared)
    bmesh.ops.split_edges(bm, edges=line)
    U, L = [], []
    for k, pos in enumerate(P):
        vs = [v for v in bm.verts if (v.co - pos).length < 1e-6]
        if k in (0, n - 1):
            U.append(vs[0]); L.append(vs[0]); continue
        U.append(next(v for v in vs if any(f.index not in cream for f in v.link_faces)))
        L.append(next(v for v in vs if all(f.index in cream for f in v.link_faces)))

    # ---- jaw weights on the lower jaw skin
    def jaw_share(y):
        return 1.0 if y <= HINGE[1] else 1.0 - smoothstep((y - HINGE[1]) / (JAW_FADE_END_Y - HINGE[1]))
    upper_side = {v for f in bm.faces if f.index < n_old_faces and f.index not in cream for v in f.verts}
    for v in bm.verts:
        if v in upper_side or v.co.y >= JAW_FADE_END_Y:
            continue
        if not any(f.index in cream for f in v.link_faces):
            continue
        hw = v[dl].get(G["head"], 0.0)
        jw = hw * jaw_share(v.co.y)
        if jw > 1e-4:
            v[dl][G["jaw"]] = jw
            v[dl][G["head"]] = hw - jw

    new_verts = []

    def add_vert(co, weights):
        v = bm.verts.new(co)
        for s in shapes:
            v[s] = v.co.copy()
        for g, w in weights.items():
            v[dl][G[g]] = w
        new_verts.append(v)
        return v

    groups = {"cavity": [], "cheek": [], "tongue": []}

    def add_face(verts, part, uv, facing=None):
        verts = [x for i, x in enumerate(verts) if x not in verts[:i]]
        if len(verts) < 3:
            return
        f = bm.faces.new(verts)
        f.normal_update()
        if facing is not None and f.normal.dot(facing) < 0:
            f.normal_flip()
        f.smooth = True
        for lp in f.loops:
            lp[uvl].uv = uv
            if col:
                lp[col] = (1.0, 1.0, 1.0, 1.0)
        groups[part].append(f)
        return f

    # ---- cavity: rows across the mouth, pairing mirrored lip points; the tip is zipped closed
    half = n // 2                       # chain[half] is the tip centre
    last_row = half - 2                 # rows 0..last_row span the mouth; the rest is the tip cap
    max_w = (P[0] - P[n - 1]).length

    def row(lips, k, lift, weights):
        a, b = lips[k], lips[n - 1 - k]
        width = (b.co - a.co).length
        pts = [a]
        for c in range(1, COLUMNS):
            s = c / COLUMNS
            co = a.co.lerp(b.co, s) + Vector((0, 0, lift * min(1.0, width / (0.5 * max_w)) * math.sin(math.pi * s)))
            pts.append(add_vert(co, weights))
        pts.append(b)
        return pts

    for lips, lift, weights, facing in ((U, PALATE_HEIGHT, {"head": 1.0}, Vector((0, 0, -1))),
                                        (L, -FLOOR_DEPTH, {"jaw": 1.0}, Vector((0, 0, 1)))):
        rows = [row(lips, k, lift, weights) for k in range(last_row + 1)]
        for r0, r1 in zip(rows, rows[1:]):
            for c in range(COLUMNS):
                add_face([r0[c], r0[c + 1], r1[c + 1], r1[c]], "cavity", UV_NAVY, facing)
        tip = lips[last_row:n - last_row]
        for f in zip_strip(rows[-1], tip):
            add_face(f, "cavity", UV_NAVY, facing)
        if lips is U:
            palate_rows = rows
        else:
            floor_rows = rows
    palate_back, floor_back = palate_rows[0], floor_rows[0]

    # back wall (throat): palate back edge -> middle row (half on each bone) -> floor back edge
    throat = [palate_back[0]]
    for c in range(1, COLUMNS):
        s = c / COLUMNS
        co = (palate_back[c].co + floor_back[c].co) / 2 + Vector((0, THROAT_BACK * math.sin(math.pi * s), 0))
        throat.append(add_vert(co, {"head": 0.5, "jaw": 0.5}))
    throat.append(palate_back[-1])
    for c in range(COLUMNS):
        add_face([palate_back[c], palate_back[c + 1], throat[c + 1], throat[c]], "cavity", UV_NAVY, Vector((0, -1, 0)))
        add_face([throat[c], throat[c + 1], floor_back[c + 1], floor_back[c]], "cavity", UV_NAVY, Vector((0, -1, 0)))

    # inner cheeks: dark webs just inside the lips at the mouth corners. At the back they join roof and
    # floor; going forward their lower edge rises to the roof, so the tongue shows in the front half.
    cheek_rows = [k for k in range(last_row + 1) if P[k].y >= CHEEK_FRONT_Y]
    for c, facing in ((1, Vector((1, 0, 0))), (COLUMNS - 1, Vector((-1, 0, 0)))):
        strip = []
        for k in cheek_rows:
            top, floor_v = palate_rows[k][c], floor_rows[k][c]
            lift = smoothstep((P[k].y - P[0].y) / (CHEEK_FRONT_Y - P[0].y))   # 0 at the corner, 1 at the front
            if k == 0:
                mid, bottom = throat[c], floor_v
            elif lift > 0.999:
                mid = bottom = top
            else:
                bottom = floor_v if lift < 1e-3 else add_vert(floor_v.co.lerp(top.co, lift), {"head": lift, "jaw": 1 - lift})
                mid = add_vert((top.co + bottom.co) / 2, {"head": (1 + lift) / 2, "jaw": (1 - lift) / 2})
            strip.append((top, mid, bottom))
        for (t0, m0, b0), (t1, m1, b1) in zip(strip, strip[1:]):
            add_face([t0, t1, m1, m0], "cheek", UV_NAVY, facing)
            add_face([m0, m1, b1, b0], "cheek", UV_NAVY, facing)

    # ---- tongue: flattened ellipsoid resting on the floor, slightly upturned tip
    floor_z = HINGE[2] - FLOOR_DEPTH          # floor height along the middle of the mouth
    sx, sy, sz = TONGUE_SIZE
    centre = Vector((TONGUE_CENTER[0], TONGUE_CENTER[1], floor_z + sz * 0.55))   # underside sinks into the floor
    sphere = bmesh.ops.create_uvsphere(bm, u_segments=12, v_segments=8, radius=1.0)
    t_verts = sphere["verts"]
    for v in t_verts:
        x, y, z = v.co
        z = z * (0.6 if z < 0 else 1.0)                  # flatter underside
        front = max(0.0, -y)                             # 0 at middle, 1 at the tip
        v.co = centre + Vector((x * sx * (1 - 0.25 * front), y * sy, z * sz + 0.012 * front * front))
        for s in shapes:
            v[s] = v.co.copy()
        v[dl][G["jaw"]] = 1.0
    t_faces = list({f for v in t_verts for f in v.link_faces})
    for f in t_faces:
        f.normal_update()
        if f.normal.dot(f.calc_center_median() - centre) < 0:
            f.normal_flip()
        f.smooth = True
        for lp in f.loops:
            lp[uvl].uv = UV_PINK
            if col:
                lp[col] = (1.0, 1.0, 1.0, 1.0)
    groups["tongue"] = t_faces

    # remember which faces are new, by position in the face list after to_mesh
    bm.faces.index_update()
    part_of = {f.index: part for part, fs in groups.items() for f in fs}
    bm.to_mesh(me)
    bm.free()
    me.update()

    # ---- normals: original faces keep theirs; new faces get smooth normals within their own part
    loop_normals = [None] * len(me.loops)
    for i in range(n_old_faces):
        for li, ls in zip(me.polygons[i].loop_indices, src.polygons[i].loop_indices):
            loop_normals[li] = tuple(src.corner_normals[ls].vector)
    acc = {}
    for p in me.polygons:
        part = part_of.get(p.index)
        if part:
            for vi in p.vertices:
                acc[(part, vi)] = acc.get((part, vi), Vector()) + p.normal * p.area
    for p in me.polygons:
        part = part_of.get(p.index)
        if part:
            for li in p.loop_indices:
                loop_normals[li] = tuple(acc[(part, me.loops[li].vertex_index)].normalized())
    me.normals_split_custom_set(loop_normals)

    print(f"mouth: chain {n} verts, +{len(me.vertices) - len(src.vertices)} verts, "
          f"+{len(me.polygons) - len(src.polygons)} faces "
          f"(cavity {len(groups['cavity'])}, cheeks {len(groups['cheek'])}, tongue {len(groups['tongue'])}), "
          f"shape keys {len(me.shape_keys.key_blocks)}")


if __name__ == "__main__":
    build_mouth()
