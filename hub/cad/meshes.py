"""Mesh parts: shapes that come as triangle meshes (a robot's links), not as build123d solids.

A mesh pack is a folder written offline (cad/tools_offline/prepare_g1.py): display.glb has one node per piece,
named by its key, with crease-split normals; outline.npz has a lighter copy of each piece for the drawing;
parts.json says which pieces make each link. Millimetres, each link's own frame, Z up (no glTF axis swap).

OCCT's glTF reader turns each display piece into a face that holds only a triangulation. OCCT exports it to the
model's GLB as it is (normals included) and its bounding box works, but it has no surface: no volume (give it a
FixedMass), and OCCT's exact hidden-line removal crashes on it (cad/drawing.py has a mesh path instead).

Packs load once per worker process (a tenth of a second for the humanoid). The worker imports a part script afresh
on every build, so a cache there would be lost; this module is imported normally and stays loaded.
"""
from __future__ import annotations

import json
from functools import cached_property
from pathlib import Path
from typing import Any

import numpy as np
from build123d import Color, Face
from OCP.BRep import BRep_Tool
from OCP.BRepMesh import BRepMesh_IncrementalMesh
from OCP.collections import Sequence_TDF_Label
from OCP.gp import gp_Trsf
from OCP.Message import Message, Message_Gravity, Message_ProgressRange
from OCP.RWGltf import RWGltf_CafReader
from OCP.RWMesh import RWMesh_CoordinateSystem
from OCP.TCollection import TCollection_AsciiString, TCollection_ExtendedString
from OCP.TDataStd import TDataStd_Name
from OCP.TDocStd import TDocStd_Document
from OCP.TopAbs import TopAbs_FACE, TopAbs_REVERSED
from OCP.TopExp import TopExp_Explorer
from OCP.TopLoc import TopLoc_Location
from OCP.TopoDS import TopoDS, TopoDS_Face
from OCP.XCAFDoc import XCAFDoc_DocumentTool

OUTLINE = "forge_outline"   # attribute on a placed piece: its outline mesh (vertices, triangles) in its own frame


class Pack:
    """A folder of mesh pieces (display.glb + outline.npz + parts.json), loaded on first use."""

    def __init__(self, folder: Path):
        self.folder = Path(folder)

    @cached_property
    def manifest(self) -> dict[str, Any]:
        return json.loads((self.folder / "parts.json").read_text(encoding="utf-8"))

    @cached_property
    def faces(self) -> dict[str, TopoDS_Face]:
        """Each piece's display mesh as a surfaceless face, by key."""
        return read_glb_faces(self.folder / "display.glb")

    @cached_property
    def outlines(self) -> dict[str, tuple[np.ndarray, np.ndarray]]:
        """Each piece's outline mesh (vertices mm, triangles), by key."""
        with np.load(self.folder / "outline.npz", allow_pickle=False) as data:
            keys = sorted({name.rsplit(".", 1)[0] for name in data.files})
            return {k: (data[f"{k}.v"].astype(float), data[f"{k}.f"].astype(np.int64)) for k in keys}

    def place(self, key: str, transform: np.ndarray, label: str, rgb: tuple[float, float, float]) -> Face:
        """The piece at `transform` (4x4, part mm), labelled and coloured: a new Face sharing the cached mesh."""
        face = Face(TopoDS.Face(self.faces[key].Located(TopLoc_Location(trsf(transform)))))
        face.label, face.color = label, Color(*rgb)
        setattr(face, OUTLINE, self.outlines[key])
        return face


_packs: dict[Path, Pack] = {}


def pack(folder: Path | str) -> Pack:
    """The pack in `folder`, cached for the life of the process."""
    folder = Path(folder).resolve()
    if folder not in _packs:
        _packs[folder] = Pack(folder)
    return _packs[folder]


def read_glb_faces(path: Path) -> dict[str, TopoDS_Face]:
    """The named mesh nodes of a GLB as surfaceless faces (first face of each), coordinates as stored."""
    for printer in Message.DefaultMessenger_s().Printers():   # quiet, as export_gltf leaves it
        printer.SetTraceLevel(Message_Gravity.Message_Fail)
    doc = TDocStd_Document(TCollection_ExtendedString("forge-mesh-pack"))
    reader = RWGltf_CafReader()
    reader.SetDocument(doc)
    reader.SetSystemLengthUnit(1.0)   # keep the stored numbers (mm) and axes (Z up)
    reader.SetFileLengthUnit(1.0)
    zup = RWMesh_CoordinateSystem.RWMesh_CoordinateSystem_Zup
    reader.SetSystemCoordinateSystem(zup)
    reader.SetFileCoordinateSystem(zup)
    if not reader.Perform(TCollection_AsciiString(str(path)), Message_ProgressRange()):
        raise OSError(f"Could not read {path}")
    tool = XCAFDoc_DocumentTool.ShapeTool_s(doc.Main())
    labels = Sequence_TDF_Label()
    tool.GetShapes(labels)
    out: dict[str, TopoDS_Face] = {}
    for i in range(1, labels.Length() + 1):
        label, name = labels.Value(i), TDataStd_Name()
        if not label.FindAttribute(TDataStd_Name.GetID_s(), name):
            continue
        explorer = TopExp_Explorer(tool.GetShape_s(label), TopAbs_FACE)
        if explorer.More():
            out.setdefault(name.Get().ToExtString(), TopoDS.Face(explorer.Current()))
    return out


def trsf(m: np.ndarray) -> gp_Trsf:
    """A rigid 4x4 transform as gp_Trsf (rotation part orthonormalised first, so OCCT accepts it)."""
    m = np.asarray(m, float)
    u, _, vt = np.linalg.svd(m[:3, :3])
    r = u @ vt
    t = gp_Trsf()
    t.SetValues(*r[0], m[0, 3], *r[1], m[1, 3], *r[2], m[2, 3])
    return t


def is_mesh(shape) -> bool:
    """True for a shape with a surfaceless (triangulation-only) face: OCCT's exact algorithms can't take it."""
    explorer = TopExp_Explorer(getattr(shape, "wrapped", shape), TopAbs_FACE)
    while explorer.More():
        if BRep_Tool.Surface_s(TopoDS.Face(explorer.Current())) is None:
            return True
        explorer.Next()
    return False


def matrix(loc: TopLoc_Location) -> np.ndarray:
    t = loc.Transformation()
    return np.array([[t.Value(r, c) for c in range(1, 5)] for r in range(1, 4)] + [[0.0, 0.0, 0.0, 1.0]])


def triangles(shape, deflection: float) -> tuple[np.ndarray, np.ndarray]:
    """Any leaf as one triangle mesh in part mm: a placed piece's outline mesh, a surfaceless face's own
    triangulation, or a solid meshed to within `deflection` mm. Triangles wind outward (reversed faces flipped)."""
    outline = getattr(shape, OUTLINE, None)
    if outline is not None:
        m = matrix(shape.wrapped.Location())
        return outline[0] @ m[:3, :3].T + m[:3, 3], outline[1]
    if not is_mesh(shape):
        BRepMesh_IncrementalMesh(shape.wrapped, deflection, False, 0.3, True)
    points, tris, offset = [], [], 0
    explorer = TopExp_Explorer(shape.wrapped, TopAbs_FACE)
    while explorer.More():
        face = TopoDS.Face(explorer.Current())
        explorer.Next()
        loc = TopLoc_Location()
        poly = BRep_Tool.Triangulation_s(face, loc)
        if poly is None:
            continue
        fm = matrix(loc)
        p = np.array([(q.X(), q.Y(), q.Z()) for q in (poly.Node(i) for i in range(1, poly.NbNodes() + 1))])
        t = np.array([poly.Triangle(i).Get() for i in range(1, poly.NbTriangles() + 1)]) - 1
        if face.Orientation() == TopAbs_REVERSED:
            t = t[:, ::-1]
        points.append(p @ fm[:3, :3].T + fm[:3, 3])
        tris.append(t + offset)
        offset += len(p)
    if not points:
        return np.zeros((0, 3)), np.zeros((0, 3), int)
    return np.vstack(points), np.vstack(tris)
