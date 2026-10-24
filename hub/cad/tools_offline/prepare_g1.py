"""Offline, once: turn Unitree's G1 robot description into the humanoid design's mesh pack (hub/cad/data/unitree_g1/).

What it does:
  1. Takes `robots/g1_description/` from github.com/unitreerobotics/unitree_ros (BSD-3-Clause) at COMMIT: the
     URDF, the MuJoCo XML (for Unitree's own two-tone colours) and the binary STL of every link the URDF draws.
     Pass --source <a local g1_description folder>, or it downloads the files it needs into --cache.
  2. Splits each link's mesh into its separate pieces (a shin is a cover, a motor, caps...), folding slivers into the
     biggest piece, so the robot comes apart into real parts and each piece can take its own tone.
  3. Simplifies every piece twice with quadric decimation: a display mesh (about DISPLAY_TRIS for the robot, the GLB)
     and a light outline mesh (about OUTLINE_TRIS, only for the engineering drawing). Triangles go where the surface
     area is, so big covers keep their curves and tiny fingertip textures don't eat the budget.
  4. Splits vertices at creases and stores smooth normals per piece, so Unity shades flat faces flat and curves smooth.
  5. Writes display.glb (one node per piece, named by its key, with normals: OCCT's glTF reader loads it in a tenth
     of a second), outline.npz (the drawing's meshes) and parts.json (the pieces of each link, tones, area shares),
     all in millimetres in each link's own frame (the URDF's visual origin applied, no glTF axis swap), and copies
     the URDF and the licence next to them. NOTICE.md there is written by hand.

The hub only reads the outputs (cad/meshes.py); nothing here runs in the hub.

Dev-only requirements, in a separate venv (not the hub's):
    python -m venv .venv-tools && .venv-tools/Scripts/pip install numpy scipy trimesh networkx fast-simplification
Run (from hub/):
    .venv-tools/Scripts/python -I cad/tools_offline/prepare_g1.py --cache <a scratch folder>
"""
from __future__ import annotations

import argparse
import json
import shutil
import urllib.request
import xml.etree.ElementTree as ET
from pathlib import Path

import fast_simplification
import numpy as np
import trimesh

REPO = "unitreerobotics/unitree_ros"
COMMIT = "15ebe53f4cd77e7d745ae83b9d7ff90e685e8998"   # master on 2026-10-24
FOLDER = "robots/g1_description"
URDF = "g1_29dof_with_hand_rev_1_0.urdf"           # 29 joints + Dex3-1 hands (7 joints each)
MJCF = "g1_29dof_with_hand_rev_1_0.xml"            # same robot for MuJoCo: carries Unitree's colours per mesh
OUT = Path(__file__).resolve().parents[1] / "data" / "unitree_g1"

DISPLAY_TRIS = 200_000
OUTLINE_TRIS = 60_000
CREASE_DEG = 32.0
MIN_PIECE_SHARE = 0.04       # a piece under 4% of its link's area folds into the biggest piece...
MIN_PIECE_TRIS = 150         # ...as does one under 150 triangles
MAX_PIECES = 7               # pieces per link, biggest first; the rest fold into the biggest
DROP = {"logo_link"}         # Unitree's logo plate: not ours to show (its 1 g stays on the torso)

# Link -> the part name people read (labels become GLB node names and the panel's part ids).
NAMES = {
    "pelvis": "pelvis", "pelvis_contour_link": "pelvis_cover",
    "waist_yaw_link": "waist_motor", "waist_roll_link": "waist_bracket", "torso_link": "torso", "head_link": "head",
}
for s in ("left", "right"):
    NAMES.update({
        f"{s}_hip_pitch_link": f"{s}_hip_pitch_motor", f"{s}_hip_roll_link": f"{s}_hip_roll_motor",
        f"{s}_hip_yaw_link": f"{s}_thigh", f"{s}_knee_link": f"{s}_shin", f"{s}_ankle_pitch_link": f"{s}_ankle",
        f"{s}_ankle_roll_link": f"{s}_foot",
        f"{s}_shoulder_pitch_link": f"{s}_shoulder", f"{s}_shoulder_roll_link": f"{s}_shoulder_roll_motor",
        f"{s}_shoulder_yaw_link": f"{s}_upper_arm", f"{s}_elbow_link": f"{s}_forearm",
        f"{s}_wrist_roll_link": f"{s}_wrist_roll_motor", f"{s}_wrist_pitch_link": f"{s}_wrist_pitch_motor",
        f"{s}_wrist_yaw_link": f"{s}_wrist", f"{s}_hand_palm_link": f"{s}_palm",
        f"{s}_hand_thumb_0_link": f"{s}_thumb_base", f"{s}_hand_thumb_1_link": f"{s}_thumb",
        f"{s}_hand_thumb_2_link": f"{s}_thumb_tip", f"{s}_hand_index_0_link": f"{s}_index_finger",
        f"{s}_hand_index_1_link": f"{s}_index_tip", f"{s}_hand_middle_0_link": f"{s}_middle_finger",
        f"{s}_hand_middle_1_link": f"{s}_middle_tip",
    })


def fetch(source: Path | None, cache: Path, name: str) -> Path:
    if source is not None:
        return source / name
    path = cache / name
    if not path.exists():
        path.parent.mkdir(parents=True, exist_ok=True)
        url = f"https://raw.githubusercontent.com/{REPO}/{COMMIT}/{FOLDER}/{name}"
        print("download", url)
        urllib.request.urlretrieve(url, path)
    return path


def rpy_matrix(rpy) -> np.ndarray:
    r, p, y = rpy
    rx = np.array([[1, 0, 0], [0, np.cos(r), -np.sin(r)], [0, np.sin(r), np.cos(r)]])
    ry = np.array([[np.cos(p), 0, np.sin(p)], [0, 1, 0], [-np.sin(p), 0, np.cos(p)]])
    rz = np.array([[np.cos(y), -np.sin(y), 0], [np.sin(y), np.cos(y), 0], [0, 0, 1]])
    return rz @ ry @ rx


def visuals(urdf: Path) -> dict[str, tuple[str, np.ndarray]]:
    """link -> (mesh file, 4x4 link-frame transform of the mesh in mm)."""
    out = {}
    for link in ET.parse(urdf).getroot().findall("link"):
        mesh = link.find("visual/geometry/mesh")
        if mesh is None or link.get("name") in DROP:
            continue
        t = np.eye(4)
        origin = link.find("visual/origin")
        if origin is not None:
            t[:3, :3] = rpy_matrix([float(v) for v in origin.get("rpy", "0 0 0").split()])
            t[:3, 3] = [float(v) * 1000 for v in origin.get("xyz", "0 0 0").split()]
        out[link.get("name")] = (mesh.get("filename"), t)
    return out


def tones(mjcf: Path) -> dict[str, str]:
    """mesh name -> "dark" | "light", from the MuJoCo model's geom colours (0.2 grey / 0.7 grey)."""
    out = {}
    for geom in ET.parse(mjcf).getroot().iter("geom"):
        if geom.get("mesh") and geom.get("rgba"):
            out[geom.get("mesh")] = "dark" if float(geom.get("rgba").split()[0]) < 0.45 else "light"
    return out


def pieces(mesh: trimesh.Trimesh) -> list[trimesh.Trimesh]:
    """The mesh's separate pieces, biggest (by area) first, slivers folded into the biggest."""
    parts = sorted(mesh.split(only_watertight=False), key=lambda p: -p.area)
    total = sum(p.area for p in parts)
    keep = [p for i, p in enumerate(parts)
            if i == 0 or (p.area >= MIN_PIECE_SHARE * total and len(p.faces) >= MIN_PIECE_TRIS)][:MAX_PIECES]
    rest = [p for p in parts if not any(p is k for k in keep)]
    if rest:
        keep[0] = trimesh.util.concatenate([keep[0], *rest])
    return keep


def simplify(mesh: trimesh.Trimesh, target: int) -> trimesh.Trimesh:
    if target >= len(mesh.faces):
        return mesh
    v, f = fast_simplification.simplify(mesh.vertices.astype(np.float32), mesh.faces.astype(np.int64),
                                        1.0 - target / len(mesh.faces))
    out = trimesh.Trimesh(v, f, process=True)
    out.remove_unreferenced_vertices()
    return out


def budget(areas: list[float], tris: list[int], total: int, floor: int) -> list[int]:
    """Triangles per piece: shares of `total` by area^0.75 (small pieces get a little more than their area), never
    more than the piece has, at least `floor`; what a capped piece can't use goes to the others."""
    weights = np.array(areas) ** 0.75
    caps = np.array(tris)
    target = np.zeros(len(caps))
    free = np.ones(len(caps), bool)
    left = total
    for _ in range(10):
        share = left * weights[free] / weights[free].sum()
        target[free] = share
        over = free & (target >= caps)
        if not over.any():
            break
        target[over] = caps[over]
        free &= ~over
        left = total - target[~free].sum()
    return [int(max(min(floor, c), min(c, t))) for t, c in zip(target, caps)]


def shaded(mesh: trimesh.Trimesh) -> tuple[np.ndarray, np.ndarray, np.ndarray]:
    """Vertices split at creases, with smooth normals within each smooth patch."""
    smooth = trimesh.graph.smooth_shade(mesh, angle=np.radians(CREASE_DEG))
    normals = smooth.vertex_normals
    return smooth.vertices.astype(np.float32), smooth.faces, normals


def index_dtype(n: int):
    return np.uint16 if n < 65536 else np.uint32


def main() -> None:
    ap = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    ap.add_argument("--source", type=Path, help="a local robots/g1_description folder (else download at COMMIT)")
    ap.add_argument("--cache", type=Path, default=Path(".g1_cache"), help="where downloads go")
    ap.add_argument("--out", type=Path, default=OUT)
    args = ap.parse_args()

    urdf = fetch(args.source, args.cache, URDF)
    mjcf = fetch(args.source, args.cache, MJCF)
    tone_of = tones(mjcf)
    links = visuals(urdf)

    loaded = []   # (link, piece index, mesh in link-frame mm)
    for link, (filename, t) in links.items():
        mesh = trimesh.load(fetch(args.source, args.cache, filename), force="mesh")
        mesh.apply_scale(1000.0)
        mesh.apply_transform(t)
        mesh.merge_vertices()
        for i, piece in enumerate(pieces(mesh)):
            loaded.append((link, i, piece))
    areas = [p.area for _, _, p in loaded]
    tris = [len(p.faces) for _, _, p in loaded]
    display = budget(areas, tris, DISPLAY_TRIS, 120)
    outline = budget(areas, tris, OUTLINE_TRIS, 60)

    arrays, manifest = {}, {}
    scene = trimesh.Scene()
    totals = [0, 0, 0]
    for (link, i, piece), n_display, n_outline in zip(loaded, display, outline):
        name = NAMES.get(link, link)
        key = name if i == 0 else f"{name}_{i + 1}"
        mesh = simplify(piece, n_display)
        v, f, n = shaded(mesh)
        lite = simplify(piece, n_outline)
        scene.add_geometry(trimesh.Trimesh(v, f, vertex_normals=n, process=False), node_name=key, geom_name=key)
        arrays[f"{key}.v"] = lite.vertices.astype(np.float32)
        arrays[f"{key}.f"] = lite.faces.astype(index_dtype(len(lite.vertices)))
        mesh_name = Path(links[link][0]).stem
        entry = manifest.setdefault(link, {"name": name, "tone": tone_of.get(mesh_name, "light"), "pieces": []})
        entry["pieces"].append({"key": key, "area_mm2": round(float(piece.area), 1), "tris": len(f),
                                "outline_tris": len(lite.faces), "source_tris": len(piece.faces)})
        totals[0] += len(piece.faces)
        totals[1] += len(f)
        totals[2] += len(lite.faces)
    for entry in manifest.values():
        area = sum(p["area_mm2"] for p in entry["pieces"])
        for p in entry["pieces"]:
            p["share"] = round(p["area_mm2"] / area, 5)

    args.out.mkdir(parents=True, exist_ok=True)
    scene.export(args.out / "display.glb")
    np.savez_compressed(args.out / "outline.npz", **arrays)
    (args.out / "parts.json").write_text(json.dumps({
        "source": {"repo": f"https://github.com/{REPO}", "commit": COMMIT, "folder": FOLDER, "urdf": URDF,
                   "colours": MJCF, "license": "BSD-3-Clause"},
        "units": "mm, each link's own frame (URDF visual origin applied)",
        "dropped": sorted(DROP), "crease_deg": CREASE_DEG,
        "triangles": {"source": totals[0], "display": totals[1], "outline": totals[2]},
        "links": manifest}, indent=1), encoding="utf-8")
    shutil.copy(urdf, args.out / URDF)
    if args.source is None:
        license_path = args.cache / "LICENSE"
        if not license_path.exists():
            urllib.request.urlretrieve(f"https://raw.githubusercontent.com/{REPO}/{COMMIT}/LICENSE", license_path)
        shutil.copy(license_path, args.out / "LICENSE")
    pieces_n = sum(len(e["pieces"]) for e in manifest.values())
    sizes = [(args.out / name).stat().st_size / 1e6 for name in ("display.glb", "outline.npz")]
    print(f"{len(manifest)} links, {pieces_n} pieces; triangles {totals[0]} -> display {totals[1]}, "
          f"outline {totals[2]}; display.glb {sizes[0]:.2f} MB, outline.npz {sizes[1]:.2f} MB")


if __name__ == "__main__":
    main()
