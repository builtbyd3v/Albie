"""CAD state for the hub: immutable versions on disk, one regeneration at a time, an event per change.

Versions form a tree: each records the version it came from (`parent`), so undo walks back up it.
Sliders, voice and VS Code saves all go through `_regenerate`.
"""
from __future__ import annotations

import asyncio
import json
import logging
import shutil
import time
from pathlib import Path
from typing import Any

from pydantic import BaseModel

from cad.part_api import ParamError
from cad.worker import WorkerHandle, script_sha
from events import CadModelUpdated, CadRegenFailed, EventBus

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


class RegenError(Exception):
    def __init__(self, failure: CadRegenFailed, traceback: str | None = None):
        super().__init__(failure.error)
        self.failure, self.traceback = failure, traceback


class CadService:
    def __init__(self, data_dir: Path, script: Path, bus: EventBus, timeout_s: float = 8.0):
        self.dir, self.script, self.bus, self.timeout_s = data_dir, script, bus, timeout_s
        self._worker = WorkerHandle()
        self._lock = asyncio.Lock()
        self._versions: dict[int, VersionRecord] = {}
        self.head: int | None = None

    # lifecycle

    async def start(self) -> None:
        self.dir.mkdir(parents=True, exist_ok=True)
        await asyncio.to_thread(self._worker.spawn)
        self._load_index()
        if self.head is None:
            await self._regenerate({}, source="startup", from_head=False)
        elif self.current.script_sha != script_sha(self.script):
            try:
                await self._regenerate({}, source="script")
            except RegenError as e:
                log.warning("part script changed while the hub was off and fails to build: %s", e)
                await self._announce(self.current, None, "startup")
        else:
            await self._announce(self.current, None, "startup")

    async def stop(self) -> None:
        await asyncio.to_thread(self._worker.close)

    # queries

    @property
    def current(self) -> VersionRecord:
        return self._versions[self.head]

    def get(self, version: int | None) -> VersionRecord:
        return self.current if version is None else self._versions[version]

    def path(self, version: int, name: str) -> Path:
        return self.dir / f"v{version}" / name

    def versions(self) -> list[VersionRecord]:
        return [self._versions[v] for v in sorted(self._versions)]

    def diff(self, old: int | None, new: int) -> tuple[list[str], list[str]]:
        """(changed_ids, removed_ids) of `new` relative to `old`."""
        if old is None:
            return [], []
        before, after = self._versions[old].fingerprints, self._versions[new].fingerprints
        return [i for i, f in after.items() if before.get(i) != f], [i for i in before if i not in after]

    # changes

    async def set_params(self, changes: dict[str, Any], source: str = "api") -> VersionRecord:
        known = {p["name"] for p in self.current.param_schema}
        unknown = sorted(set(changes) - known)
        if unknown:
            raise ParamError(f"Unknown parameter(s) {unknown}; this part has {sorted(known)}")
        return await self._regenerate(changes, source=source)

    async def reset(self) -> VersionRecord:
        return await self._regenerate({}, source="reset", from_head=False)

    async def rebuild_from_script(self) -> VersionRecord:
        return await self._regenerate({}, source="script")

    async def undo(self) -> VersionRecord:
        async with self._lock:
            parent = self.current.parent
            if parent is None or parent not in self._versions:
                raise LookupError("Nothing to undo")
            previous, self.head = self.head, parent
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
        """RocketPy launch-to-apogee sim, cached per version (the first call also imports RocketPy)."""
        record = self.get(version)
        path = self.path(record.version, "flight.json")
        if not path.exists():
            await self._derive(record, "flight", self.timeout_s * 6)
        return json.loads(path.read_text(encoding="utf-8"))

    async def _derive(self, record: VersionRecord, op: str, timeout_s: float) -> dict[str, Any]:
        """Rebuild an existing version in the worker to produce an extra artifact (STL, flight sim)."""
        if record.script_sha != script_sha(self.script):
            raise LookupError(f"v{record.version} was built from an older part script; {op} is only available "
                              "for versions built from the current script")
        async with self._lock:
            result = await asyncio.to_thread(self._worker.call, {
                "op": op, "script": str(self.script), "params": record.params,
                "out_dir": str(self.dir / f"v{record.version}")}, timeout_s)
        if not result["ok"]:
            raise RegenError(CadRegenFailed(source=op, kind=result["kind"], error=result["error"],
                                            kept_version=self.head), result.get("traceback"))
        return result

    async def _regenerate(self, changes: dict[str, Any], *, source: str, from_head: bool = True) -> VersionRecord:
        async with self._lock:
            parent = self.head
            params = {**self._versions[parent].params, **changes} if from_head and parent is not None else dict(changes)
            version = max(self._versions, default=0) + 1
            out = self.dir / f"v{version}"
            shutil.rmtree(out, ignore_errors=True)
            result = await asyncio.to_thread(self._worker.call, {
                "op": "build", "script": str(self.script), "params": params, "out_dir": str(out)}, self.timeout_s)
            if not result["ok"]:
                shutil.rmtree(out, ignore_errors=True)
                failure = CadRegenFailed(source=source, kind=result["kind"], error=result["error"],
                                         error_line=result.get("error_line"), kept_version=parent)
                log.warning("CAD regen failed (%s): %s", source, failure.error)
                await self.bus.publish("cad.regen_failed", failure)
                raise RegenError(failure, result.get("traceback"))
            record = VersionRecord(
                version=version, parent=parent, created_at=time.time(), source=source, part=result["part"],
                params=result["params"], param_schema=result["schema"], analysis=result["analysis"],
                fingerprints=result["fingerprints"], script_sha=result["script_sha"], clamped=result["clamped"],
                timings_ms=result["timings_ms"])
            self._versions[version] = record
            self.head = version
            self._save_index()
            log.info("CAD v%d (%s) built in %s", version, source, result["timings_ms"])
            await self._announce(record, parent, source)
            return record

    async def _announce(self, record: VersionRecord, previous: int | None, source: str) -> None:
        changed, removed = self.diff(previous, record.version)
        await self.bus.publish("cad.model_updated", CadModelUpdated(
            version=record.version, params=record.params, previous_version=previous, changed_ids=changed,
            removed_ids=removed, source=source, analysis=record.analysis))

    # persistence

    def _index_path(self) -> Path:
        return self.dir / "versions.json"

    def _load_index(self) -> None:
        try:
            data = json.loads(self._index_path().read_text(encoding="utf-8"))
            records = [VersionRecord(**r) for r in data["versions"]]
        except FileNotFoundError:
            return
        except (ValueError, KeyError, TypeError) as e:
            log.warning("ignoring unreadable %s: %s", self._index_path(), e)
            return
        self._versions = {r.version: r for r in records if self.path(r.version, "model.glb").exists()}
        head = data.get("head")
        self.head = head if head in self._versions else max(self._versions, default=None)

    def _save_index(self) -> None:
        tmp = self._index_path().with_suffix(".tmp")
        tmp.write_text(json.dumps({"head": self.head, "versions": [r.model_dump() for r in self.versions()]},
                                  indent=1), encoding="utf-8")
        tmp.replace(self._index_path())


async def watch_script(service: CadService) -> None:
    """Regenerate when the part script is saved (the hand-edit path from VS Code)."""
    from watchfiles import awatch

    target = service.script.resolve()
    async for _ in awatch(target.parent, debounce=300, watch_filter=lambda _, p: Path(p).resolve() == target):
        log.info("%s saved; regenerating", target.name)
        try:
            await service.rebuild_from_script()
        except RegenError:
            pass  # cad.regen_failed is already on the bus; the last good model stays up
