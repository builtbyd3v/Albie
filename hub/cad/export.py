"""Export a built part as model.glb + metadata.json in cad-vision's schema 1.0.

cad-vision (github.com/BAAA-Hacks/cad-vision, MIT) maps every metadata object to exactly one GLB node.
We map by node name, so part labels must be unique, then run the same checks as its CadMetadata.cs.
"""
from __future__ import annotations

import hashlib
import json
import struct
from pathlib import Path
from typing import Any, Iterator

from build123d import Unit, export_gltf

from cad.part_api import BuildResult

GLB_MAGIC, GLB_JSON_CHUNK = 0x46546C67, 0x4E4F534A


def walk(shape, parent: str | None = None) -> Iterator[tuple[Any, str | None]]:
    yield shape, parent
    for child in shape.children:
        yield from walk(child, shape.label)


def object_id(label: str) -> str:
    return label.upper()


def check_labels(assembly) -> None:
    seen: set[str] = set()
    for shape, _ in walk(assembly):
        if not shape.label:
            raise ValueError(f"Every part needs a label (found an unlabelled {type(shape).__name__})")
        if object_id(shape.label) in seen:
            raise ValueError(f"Duplicate label {shape.label!r}: labels become GLB node names and object IDs")
        seen.add(object_id(shape.label))


def read_glb_json(path: Path) -> dict[str, Any]:
    raw = path.read_bytes()
    magic, version, total = struct.unpack_from("<3I", raw)
    length, kind = struct.unpack_from("<2I", raw, 12)
    if magic != GLB_MAGIC or version != 2 or total != len(raw) or kind != GLB_JSON_CHUNK:
        raise ValueError(f"{path} is not a complete GLB 2 file")
    return json.loads(raw[20:20 + length])


def fingerprint(shape) -> list[Any]:
    """Cheap identity used to tell which parts changed between versions."""
    if shape.children:
        return sorted(object_id(c.label) for c in shape.children)
    bb = shape.bounding_box()
    return [round(shape.volume, 1)] + [round(v, 2) for v in (bb.min.X, bb.min.Y, bb.min.Z,
                                                              bb.max.X, bb.max.Y, bb.max.Z)]


def validate_metadata(metadata: dict[str, Any], gltf: dict[str, Any]) -> None:
    """The rules cad-vision's CadMetadata.cs enforces on load (schema 1.0 inline mapping)."""
    if str(metadata.get("schemaVersion")) not in ("1", "1.0", "2.1"):
        raise ValueError("schemaVersion must be 1, 1.0 or 2.1")
    root = metadata["project"]["rootObjectId"]
    objects = {o["id"]: o for o in metadata["objects"]}
    if not objects or len(objects) != len(metadata["objects"]):
        raise ValueError("objects must be nonempty with unique IDs")
    used: set[int] = set()
    for o in metadata["objects"]:
        index = o.get("glbNodeIndex")
        if not isinstance(index, int) or index < 0 or index in used or index >= len(gltf["nodes"]):
            raise ValueError(f"{o['id']}: invalid or duplicate glbNodeIndex {index!r}")
        if gltf["nodes"][index].get("name") != o["name"]:
            raise ValueError(f"{o['id']}: GLB node {index} is named {gltf['nodes'][index].get('name')!r}")
        used.add(index)
    if root not in objects or objects[root]["parentId"] is not None:
        raise ValueError("rootObjectId must exist and have no parent")
    for oid in objects:
        seen, cursor = set(), oid
        while cursor != root:
            if cursor in seen:
                raise ValueError(f"hierarchy cycle at {cursor}")
            seen.add(cursor)
            cursor = objects[cursor]["parentId"]
            if cursor not in objects:
                raise ValueError(f"{oid} does not connect to root {root}")
    for oid, o in objects.items():
        if "childIds" in o and set(o["childIds"]) != {k for k, v in objects.items() if v["parentId"] == oid}:
            raise ValueError(f"{oid} has inconsistent childIds")


def export_part(result: BuildResult, out_dir: Path, *, part: str, params: dict[str, Any],
                masses: dict[str, tuple[float, tuple[float, float, float]]]) -> dict[str, Any]:
    check_labels(result.assembly)
    out_dir.mkdir(parents=True, exist_ok=True)
    glb = out_dir / "model.glb"
    # unit is the SOURCE unit: MM makes OCCT write metres, as glTF requires. Unit.M gives a 1000x model.
    export_gltf(result.assembly, str(glb), unit=Unit.MM, binary=True)
    gltf = read_glb_json(glb)
    nodes_by_name: dict[str, list[int]] = {}
    for i, node in enumerate(gltf["nodes"]):
        nodes_by_name.setdefault(node.get("name"), []).append(i)

    objects, fingerprints = [], {}
    for shape, parent in walk(result.assembly):
        oid = object_id(shape.label)
        nodes = nodes_by_name.get(shape.label, [])
        if len(nodes) != 1:
            raise ValueError(f"GLB node mapping for {shape.label!r}: expected 1 node, found {len(nodes)}")
        model = result.masses.get(shape.label)
        mass = masses.get(shape.label)
        entry: dict[str, Any] = {
            "id": oid, "name": shape.label, "type": "assembly" if shape.children else "part",
            "glbNodeIndex": nodes[0], "parentId": object_id(parent) if parent else None,
            "material": {"assigned": bool(model) if not shape.children else None,
                         "name": getattr(model, "material", None) or None,
                         "density": model.density_g_cm3 * 1000 if hasattr(model, "density_g_cm3") else None},
            "mass": round(mass[0] / 1000, 6) if mass else None,
            "volume": round(shape.volume * 1e-9, 12) if not shape.children else None,
            "definitionStatus": "fully_defined", "customProperties": {},
        }
        if mass:
            entry["centerOfMass"] = [round(v / 1000, 6) for v in mass[1]]
        if shape.children:
            entry["childIds"] = [object_id(c.label) for c in shape.children]
        if parent is None:
            entry["customProperties"] = {k: str(v) for k, v in params.items()}
        objects.append(entry)
        fingerprints[oid] = fingerprint(shape)

    metadata = {
        "schemaVersion": "1.0",
        "project": {"id": f"FORGE_{part.upper()}", "name": part, "rootObjectId": object_id(result.assembly.label),
                    "units": {"length": "m", "mass": "kg", "angle": "deg"}},
        "coordinateSystem": "part axes (Z-up, tail at z=0); the GLB root node rotates them to glTF Y-up",
        "provenance": {"synthetic": False, "source": "forge hub/cad (build123d)",
                       "sourceGlbSha256": hashlib.sha256(glb.read_bytes()).hexdigest()},
        "objects": objects, "mates": [], "interferences": [],
    }
    validate_metadata(metadata, gltf)
    (out_dir / "metadata.json").write_text(json.dumps(metadata, indent=2), encoding="utf-8")
    return {"metadata": metadata, "fingerprints": fingerprints, "glb_bytes": glb.stat().st_size}
