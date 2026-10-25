"""CAD worker: runs part scripts in a separate process so an OCCT crash or hang can't take down the hub.

The hub keeps one warm worker (importing build123d takes seconds) and kills/respawns it on timeout.
`run_job` is also usable in-process (tests, make_mocks).
"""
from __future__ import annotations

import hashlib
import importlib.util
import json
import multiprocessing as mp
import re
import threading
import time
import traceback
from pathlib import Path
from typing import Any

from cad.part_api import DEFAULT_GLYPH, BuildResult, Param, ParamError

_loads = 0


def load_part(script: Path):
    """Import the part script fresh (a new module each time, so saved edits always apply)."""
    global _loads
    _loads += 1
    spec = importlib.util.spec_from_file_location(f"forge_part_{_loads}", script)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    for attr in ("PARAMS", "build"):
        if not hasattr(module, attr):
            raise AttributeError(f"{script.name} must define {attr}")
    return module


def default_title(part: str) -> str:
    return part.replace("_", " ").capitalize()


def part_info(module, script: Path) -> dict[str, Any]:
    """What the library shows for a part. Its id is the file name, so a copied script is a new design."""
    return {"part": script.stem, "title": getattr(module, "TITLE", "") or default_title(script.stem),
            "description": getattr(module, "DESCRIPTION", ""),
            "glyph": str(getattr(module, "GLYPH", "") or DEFAULT_GLYPH),
            "presets": {name: dict(values) for name, values in getattr(module, "PRESETS", {}).items()},
            "features": [name for name, fn in (("flight", "simulate_flight"), ("motion", "simulate_motion"))
                         if callable(getattr(module, fn, None))]}


def resolve_params(params: list[Param], supplied: dict[str, Any]) -> tuple[dict[str, Any], list[str]]:
    """Defaults overlaid with supplied values (coerced/clamped). Names the script no longer has are dropped."""
    values, clamped = {}, []
    for p in params:
        if p.name in supplied:
            values[p.name], was_clamped = p.coerce(supplied[p.name])
            if was_clamped:
                clamped.append(p.name)
        else:
            values[p.name] = p.coerce(p.default)[0]
    return values, clamped


def script_sha(script: Path) -> str:
    return hashlib.sha256(script.read_bytes()).hexdigest()[:16]


def _error_line(script: Path, tb: str) -> int | None:
    lines = re.findall(rf'File "{re.escape(str(script))}", line (\d+)', tb)
    return int(lines[-1]) if lines else None


_last_build: tuple[tuple[str, str, str], BuildResult] | None = None   # (script, sha, params) -> result


def build_part(script: Path, supplied: dict[str, Any], reuse: bool = False):
    """Load the script and build it. With `reuse`, the last build comes back if it was the same script text and
    parameters (a drawing requested right after a regen skips the rebuild)."""
    global _last_build
    module = load_part(script)
    values, clamped = resolve_params(module.PARAMS, supplied)
    key = (str(script), script_sha(script), json.dumps(values, sort_keys=True, default=str))
    if reuse and _last_build is not None and _last_build[0] == key:
        return module, values, clamped, _last_build[1]
    _last_build = None
    result = module.build(values)
    if not isinstance(result, BuildResult):
        raise TypeError("build(params) must return a BuildResult")
    result.params = values
    _last_build = (key, result)
    return module, values, clamped, result


def run_job(job: dict[str, Any]) -> dict[str, Any]:
    from cad.analysis import analyze, leaves, mass_properties
    from cad.export import export_part

    script, out_dir = Path(job["script"]), Path(job["out_dir"])
    try:
        t0 = time.perf_counter()
        module, values, clamped, result = build_part(script, job.get("params", {}),
                                                     reuse=job["op"] in ("drawing", "package"))
        t_build = time.perf_counter()
        if job["op"] == "stl":
            from build123d import export_stl
            out_dir.mkdir(parents=True, exist_ok=True)
            export_stl(result.assembly, str(out_dir / "model.stl"))
            return {"ok": True}
        masses = mass_properties(result)
        if job["op"] == "flight":
            simulate = getattr(module, "simulate_flight", None)
            if simulate is None:
                raise LookupError(f"{script.stem} has no flight simulation")
            flight = simulate(result, masses)
            (out_dir / "flight.json").write_text(json.dumps(flight, indent=2), encoding="utf-8")
            return {"ok": True, "flight": flight}
        if job["op"] == "motion":
            from cad.export import walk
            from cad.scene import motion
            simulate = getattr(module, "simulate_motion", None)
            if simulate is None:
                raise LookupError(f"{script.stem} has no motion")
            data = motion(simulate(result, masses), [shape.label for shape, _ in walk(result.assembly)])
            (out_dir / "motion.json").write_text(json.dumps(data), encoding="utf-8")
            return {"ok": True, "motion": data}
        if job["op"] == "drawing":
            from cad.drawing import draw
            timings = draw(result, masses, out_dir, title=part_info(module, script)["title"], part=script.stem,
                           version=job.get("version"), created_at=job.get("created_at"),
                           highlight=job.get("highlight", ()))
            return {"ok": True, "timings_ms": {"build": round((t_build - t0) * 1000), **timings}}
        if job["op"] == "package":
            from cad.package import write_package
            files = write_package(result, masses, out_dir / "package", part=script.stem,
                                  title=part_info(module, script)["title"], version=job["version"],
                                  created_at=job.get("created_at"))
            return {"ok": True, "files": files, "timings_ms": {"build": round((t_build - t0) * 1000),
                                                               "package": round((time.perf_counter() - t_build) * 1000)}}
        info = part_info(module, script)
        analysis = analyze(result, masses, getattr(module, "analyze", None), features=info["features"])
        exported = export_part(result, out_dir, part=info["part"], params=values, masses=masses)
        t_done = time.perf_counter()
        labels = [shape.label for shape in leaves(result.assembly)]
        dimensions = [d["id"] for d in analysis["dimensions"]]
        schema = [p.schema(values[p.name], labels, dimensions) for p in module.PARAMS]
        (out_dir / "analysis.json").write_text(json.dumps(analysis, indent=2), encoding="utf-8")
        return {"ok": True, **info, "params": values, "clamped": clamped, "schema": schema,
                "analysis": analysis, "fingerprints": exported["fingerprints"],
                "script_sha": script_sha(script), "glb_bytes": exported["glb_bytes"],
                "timings_ms": {"build": round((t_build - t0) * 1000), "export": round((t_done - t_build) * 1000)}}
    except ParamError as e:
        return {"ok": False, "kind": "params", "error": str(e)}
    except Exception as e:  # noqa: BLE001 - the user's script can raise anything; report it, keep running
        tb = traceback.format_exc(limit=12)
        return {"ok": False, "kind": "build", "error": f"{type(e).__name__}: {e}",
                "error_line": _error_line(script, tb), "traceback": tb}


def worker_main(conn) -> None:
    import build123d  # noqa: F401 - warm the OCCT import before reporting ready
    try:
        import cad.drawing  # noqa: F401 - and matplotlib (a second or two), so the first drawing doesn't wait
    except ImportError:
        pass  # the drawing op reports it; builds still work
    conn.send({"ready": True})
    while True:
        try:
            job = conn.recv()
        except EOFError:
            return
        conn.send(run_job(job))


class WorkerHandle:
    """One warm worker process. Calls run in threads (asyncio.to_thread), so the pipe is guarded by a lock;
    a cancelled caller can leave its thread mid-call, which is why close() kills the process first."""

    def __init__(self, ready_timeout_s: float = 120.0):
        self._ctx = mp.get_context("spawn")
        self._ready_timeout_s = ready_timeout_s
        self._lock = threading.Lock()
        self._closing = False
        self._proc = None
        self._conn = None
        self._ready = False

    def spawn(self) -> None:
        with self._lock:
            self._closing = False
            self._spawn()

    def close(self) -> None:
        self._closing = True
        proc = self._proc
        if proc is not None and proc.is_alive():
            proc.kill()  # unblocks a call() still waiting on the pipe
        with self._lock:
            self._shutdown()

    def call(self, job: dict[str, Any], timeout_s: float) -> dict[str, Any]:
        with self._lock:
            if self._closing:
                return {"ok": False, "kind": "crash", "error": "CAD worker is shutting down"}
            if self._proc is None or not self._proc.is_alive():
                self._restart()
            try:
                if not self._ready:
                    if not self._conn.poll(self._ready_timeout_s):
                        self._restart()
                        return {"ok": False, "kind": "timeout", "error": "CAD worker did not start"}
                    self._conn.recv()
                    self._ready = True
                self._conn.send(job)
                if self._conn.poll(timeout_s):
                    return self._conn.recv()
            except (EOFError, BrokenPipeError, ConnectionResetError, OSError):
                self._restart()
                return {"ok": False, "kind": "crash", "error": "CAD worker crashed; restarted it"}
            self._restart()  # hung: kill it, start warming a fresh one
            return {"ok": False, "kind": "timeout", "error": f"CAD build took longer than {timeout_s:g} s; stopped it"}

    def _spawn(self) -> None:
        parent, child = self._ctx.Pipe()
        self._proc = self._ctx.Process(target=worker_main, args=(child,), daemon=True, name="forge-cad-worker")
        self._proc.start()
        child.close()
        self._conn, self._ready = parent, False

    def _restart(self) -> None:
        self._shutdown()
        if not self._closing:
            self._spawn()

    def _shutdown(self) -> None:
        if self._proc is not None:
            if self._proc.is_alive():
                self._proc.kill()
            self._proc.join(timeout=5)
        if self._conn is not None:
            self._conn.close()
        self._proc, self._conn, self._ready = None, None, False
