"""CAD state for the hub: a library of part scripts, immutable versions on disk, one regeneration at a time,
an event per change.

Every *.py in the parts folder (not starting with _) is a design; its file name is its id. One design is open
(`active`) and the endpoints act on it. Each design has its own version tree: a version records the version it
came from (`parent`), so undo walks back up it. Version numbers are unique across the library, so `?v=N` names
one file forever. Sliders, presets, voice and VS Code saves all go through `_build`.
"""
from __future__ import annotations

import ast
import asyncio
import json
import logging
import shutil
import time
from pathlib import Path
from typing import Any

from pydantic import BaseModel

from cad.part_api import ParamError
from cad.worker import WorkerHandle, default_title, script_sha
from events import CadModelUpdated, CadRegenFailed, CadView, EventBus

log = logging.getLogger("forge.cad")


class VersionRecord(BaseModel):
    version: int
    parent: int | None
    created_at: float
    source: str
    part: str
    params: dict[str, Any]
    param_schema: list[dict[str, Any]]
    analysis: dict[str, Any]
    fingerprints: dict[str, Any]
    script_sha: str
    clamped: list[str] = []
    timings_ms: dict[str, int] = {}
    title: str = ""
    description: str = ""
    presets: dict[str, dict[str, Any]] = {}
    features: list[str] = []


class RegenError(Exception):
    def __init__(self, failure: CadRegenFailed, traceback: str | None = None):
        super().__init__(failure.error)
        self.failure, self.traceback = failure, traceback


def scan_part(script: Path) -> dict[str, Any]:
    """A design's title, description, presets and features, read from its source without running it."""
    info: dict[str, Any] = {"part": script.stem, "title": default_title(script.stem), "description": "",
                            "presets": {}, "features": []}
    try:
        tree = ast.parse(script.read_text(encoding="utf-8"), filename=script.name)
    except (OSError, SyntaxError, ValueError) as e:
        return {**info, "error": f"{type(e).__name__}: {e}"}
    keys = {"TITLE": "title", "DESCRIPTION": "description", "PRESETS": "presets"}
    for node in tree.body:
        if isinstance(node, ast.Assign) and len(node.targets) == 1 and isinstance(node.targets[0], ast.Name) \
                and node.targets[0].id in keys:
            try:
                info[keys[node.targets[0].id]] = ast.literal_eval(node.value)
            except (ValueError, TypeError, SyntaxError):
                pass  # computed: the worker reports the real value once the design is built
        defined = [node.name] if isinstance(node, ast.FunctionDef) else \
            [a.asname or a.name for a in node.names] if isinstance(node, (ast.Import, ast.ImportFrom)) else []
        if "simulate_flight" in defined:
            info["features"] = ["flight"]
    return info


class CadService:
    def __init__(self, data_dir: Path, script: Path, bus: EventBus, timeout_s: float = 8.0):
        """`script` is the design that opens first; every part script next to it is in the library."""
        self.dir, self.bus, self.timeout_s = data_dir, bus, timeout_s
        self.parts_dir, self.default_part = script.parent, script.stem
        self.active = script.stem
        self._worker = WorkerHandle()
        self._lock = asyncio.Lock()
        self._versions: dict[int, VersionRecord] = {}
        self.heads: dict[str, int] = {}

    # lifecycle

    async def start(self) -> None:
        self.dir.mkdir(parents=True, exist_ok=True)
        await asyncio.to_thread(self._worker.spawn)
        self._load_index()
        if self.active not in self.parts():
            self.active = self.default_part
        async with self._lock:
            try:
                await self._bring_up(self.active, "startup")
            except RegenError:
                if self.active == self.default_part:
                    raise
                log.warning("%s fails to build; opening %s instead", self.active, self.default_part)
                self.active = self.default_part
                await self._bring_up(self.active, "startup")
            self._save_index()

    async def stop(self) -> None:
        await asyncio.to_thread(self._worker.close)

    # queries

    @property
    def script(self) -> Path:
        """The open design's part script."""
        return self.script_for(self.active)

    def script_for(self, part: str) -> Path:
        return self.parts_dir / f"{part}.py"

    @property
    def head(self) -> int | None:
        return self.heads.get(self.active)

    @property
    def current(self) -> VersionRecord:
        return self._versions[self.head]

    def get(self, version: int | None) -> VersionRecord:
        return self.current if version is None else self._versions[version]

    def path(self, version: int, name: str) -> Path:
        return self._dir_for(self._versions[version].part, version) / name

    def versions(self, part: str | None = None) -> list[VersionRecord]:
        """One design's versions (the open one by default), oldest first."""
        part = part or self.active
        return [r for _, r in sorted(self._versions.items()) if r.part == part]

    def parts(self) -> list[str]:
        """The library. Files starting with _ (the template, helpers) aren't designs."""
        return sorted(p.stem for p in self.parts_dir.glob("*.py") if not p.name.startswith("_"))

    def part_infos(self) -> list[dict[str, Any]]:
        """Every design with its title, description, presets and features, and its latest version if built."""
        out = []
        for part in self.parts():
            head = self._versions.get(self.heads.get(part))
            try:
                fresh = head is not None and head.script_sha == script_sha(self.script_for(part))
            except OSError:
                continue  # deleted while listing
            info = ({"part": part, "title": head.title, "description": head.description, "presets": head.presets,
                     "features": head.features} if fresh else scan_part(self.script_for(part)))
            out.append({**info, "version": head.version if head else None, "active": part == self.active})
        return out

    def diff(self, old: int | None, new: int) -> tuple[list[str], list[str]]:
        """(changed_ids, removed_ids) of `new` relative to `old`."""
        if old is None:
            return [], []
        before, after = self._versions[old].fingerprints, self._versions[new].fingerprints
        return [i for i, f in after.items() if before.get(i) != f], [i for i in before if i not in after]

    # changes (all on the open design)

    async def set_params(self, changes: dict[str, Any], source: str = "api") -> VersionRecord:
        async with self._lock:
            known = {p["name"] for p in self.current.param_schema}
            unknown = sorted(set(changes) - known)
            if unknown:
                raise ParamError(f"Unknown parameter(s) {unknown}; {self.current.title} has {sorted(known)}")
            return await self._build(self.active, changes, source=source)

    async def apply_preset(self, name: str, source: str = "preset") -> VersionRecord:
        """A named set of values from the design's PRESETS, on top of the current ones. Case doesn't matter."""
        presets = self.current.presets
        match = next((n for n in presets if n.casefold() == name.strip().casefold()), None)
        if match is None:
            raise LookupError(f"No preset {name!r}; {self.current.title} has {list(presets)}")
        return await self.set_params(presets[match], source=source)

    async def reset(self) -> VersionRecord:
        async with self._lock:
            return await self._build(self.active, {}, source="reset", from_head=False)

    async def rebuild_from_script(self) -> VersionRecord:
        async with self._lock:
            return await self._build(self.active, {}, source="script")

    async def open_part(self, part: str) -> VersionRecord:
        """Switch the open design. Its latest version comes back (built first if it's new or its script changed)."""
        if part not in self.parts():
            raise LookupError(f"No design {part!r}; the library has {self.parts()}")
        async with self._lock:
            if part == self.active:
                return self.current
            await self.bus.publish("cad.view", CadView(explode=False, focus_id=None))  # part IDs differ per design
            previous, self.active = self.active, part
            try:
                record = await self._bring_up(part, "open")
            except RegenError:
                self.active = previous  # a new design that doesn't build yet: stay on the old one
                raise
            self._save_index()
            return record

    async def undo(self) -> VersionRecord:
        async with self._lock:
            parent = self.current.parent
            if parent is None or parent not in self._versions:
                raise LookupError("Nothing to undo")
            previous, self.heads[self.active] = self.head, parent
            self._save_index()
            await self._announce(self.current, previous, "undo")
            return self.current

    async def stl(self, version: int | None) -> Path:
        record = self.get(version)
        path = self.path(record.version, "model.stl")
        if not path.exists():
            await self._derive(record, "stl", self.timeout_s * 2)
        return path

    async def flight(self, version: int | None) -> dict[str, Any]:
        """Launch-to-apogee sim for designs that have one (the rocket), cached per version."""
        record = self.get(version)
        if "flight" not in record.features:
            raise LookupError(f"{record.title or record.part} has no flight simulation")
        path = self.path(record.version, "flight.json")
        if not path.exists():
            await self._derive(record, "flight", self.timeout_s * 6)
        return json.loads(path.read_text(encoding="utf-8"))

    async def drawing(self, version: int | None, fmt: str = "png") -> Path:
        """Engineering drawing (cad/drawing.py), cached per version: one job writes drawing.png and drawing.svg.
        Orange marks the parts that changed from the version this one came from (its parent)."""
        if fmt not in ("png", "svg"):
            raise ValueError(f"No drawing format {fmt!r}")
        record = self.get(version)
        path = self.path(record.version, f"drawing.{fmt}")
        if not path.exists():
            changed = self.diff(record.parent, record.version)[0] if record.parent in self._versions else []
            await self._derive(record, "drawing", self.timeout_s * 2, done=path, version=record.version,
                               created_at=record.created_at, highlight=changed)
        return path

    async def _derive(self, record: VersionRecord, op: str, timeout_s: float, done: Path | None = None,
                      **job: Any) -> dict[str, Any]:
        """Rebuild an existing version in the worker to produce an extra artifact (STL, flight sim, drawing).
        `done`: skip the job if that file appeared while this call waited for the lock. `job`: extra op fields."""
        script = self.script_for(record.part)
        if record.script_sha != script_sha(script):
            raise LookupError(f"v{record.version} was built from an older part script; {op} is only available "
                              "for versions built from the current script")
        async with self._lock:
            if done is not None and done.exists():
                return {"ok": True}
            result = await asyncio.to_thread(self._worker.call, {
                **job, "op": op, "script": str(script), "params": record.params,
                "out_dir": str(self._dir_for(record.part, record.version))}, timeout_s)
        if not result["ok"]:
            raise RegenError(CadRegenFailed(source=op, kind=result["kind"], error=result["error"],
                                            kept_version=self.head), result.get("traceback"))
        return result

    async def _bring_up(self, part: str, source: str) -> VersionRecord:
        """Show a design's latest version, building it first if it has none or its script changed. Lock held."""
        head = self.heads.get(part)
        if head is None:
            return await self._build(part, {}, source=source, from_head=False)
        record = self._versions[head]
        if record.script_sha != script_sha(self.script_for(part)):
            try:
                return await self._build(part, {}, source="script")
            except RegenError as e:
                log.warning("%s changed and fails to build; showing v%d: %s", part, head, e)
        await self._announce(record, None, source)
        return record

    async def _build(self, part: str, changes: dict[str, Any], *, source: str, from_head: bool = True) -> VersionRecord:
        """Build a new version of `part` and make it that design's head. Lock held."""
        parent = self.heads.get(part)
        params = {**self._versions[parent].params, **changes} if from_head and parent is not None else dict(changes)
        version = max(self._versions, default=0) + 1
        out = self._dir_for(part, version)
        shutil.rmtree(out, ignore_errors=True)
        result = await asyncio.to_thread(self._worker.call, {
            "op": "build", "script": str(self.script_for(part)), "params": params, "out_dir": str(out)}, self.timeout_s)
        if not result["ok"]:
            shutil.rmtree(out, ignore_errors=True)
            failure = CadRegenFailed(source=source, kind=result["kind"], error=result["error"],
                                     error_line=result.get("error_line"), kept_version=parent)
            log.warning("CAD regen of %s failed (%s): %s", part, source, failure.error)
            await self.bus.publish("cad.regen_failed", failure)
            raise RegenError(failure, result.get("traceback"))
        record = VersionRecord(
            version=version, parent=parent, created_at=time.time(), source=source, part=part,
            params=result["params"], param_schema=result["schema"], analysis=result["analysis"],
            fingerprints=result["fingerprints"], script_sha=result["script_sha"], clamped=result["clamped"],
            timings_ms=result["timings_ms"], title=result["title"], description=result["description"],
            presets=result["presets"], features=result["features"])
        self._versions[version] = record
        self.heads[part] = version
        self._save_index()
        log.info("CAD %s v%d (%s) built in %s", part, version, source, result["timings_ms"])
        await self._announce(record, parent, source)
        return record

    async def _announce(self, record: VersionRecord, previous: int | None, source: str) -> None:
        changed, removed = self.diff(previous, record.version)
        await self.bus.publish("cad.model_updated", CadModelUpdated(
            version=record.version, part=record.part, title=record.title, params=record.params,
            previous_version=previous, changed_ids=changed, removed_ids=removed, source=source,
            analysis=record.analysis))

    # persistence

    def _dir_for(self, part: str, version: int) -> Path:
        return self.dir / part / f"v{version}"

    def _index_path(self) -> Path:
        return self.dir / "library.json"

    def _load_index(self) -> None:
        try:
            data = json.loads(self._index_path().read_text(encoding="utf-8"))
            records = [VersionRecord(**r) for r in data["versions"]]
        except FileNotFoundError:
            return
        except (ValueError, KeyError, TypeError) as e:
            log.warning("ignoring unreadable %s: %s", self._index_path(), e)
            return
        self._versions = {r.version: r for r in records if (self._dir_for(r.part, r.version) / "model.glb").exists()}
        saved, latest = data.get("heads") or {}, {}
        for v, r in sorted(self._versions.items()):
            latest[r.part] = v
        # A saved head whose files are gone falls back to that design's newest version.
        self.heads = {part: saved[part] if saved.get(part) in self._versions and self._versions[saved[part]].part == part
                      else v for part, v in latest.items()}
        if isinstance(data.get("active"), str):
            self.active = data["active"]

    def _save_index(self) -> None:
        tmp = self._index_path().with_suffix(".tmp")
        tmp.write_text(json.dumps({"active": self.active, "heads": self.heads,
                                   "versions": [r.model_dump() for _, r in sorted(self._versions.items())]},
                                  indent=1), encoding="utf-8")
        tmp.replace(self._index_path())


async def watch_script(service: CadService) -> None:
    """Watch the parts folder. Saving the open design regenerates it (the hand-edit path from VS Code). Other
    designs need nothing: GET /cad/parts reads the folder, and a design whose script changed rebuilds when opened."""
    from watchfiles import Change, awatch

    async for changes in awatch(service.parts_dir.resolve(), debounce=300,
                                watch_filter=lambda _, p: p.endswith(".py")):
        saved = {Path(p).resolve() for change, p in changes if change != Change.deleted}
        if service.script.resolve() not in saved:
            log.info("CAD library changed: %s", sorted(p.name for p in saved) or "a design was deleted")
            continue
        log.info("%s saved; regenerating", service.script.name)
        try:
            await service.rebuild_from_script()
        except RegenError:
            pass  # cad.regen_failed is already on the bus; the last good model stays up
