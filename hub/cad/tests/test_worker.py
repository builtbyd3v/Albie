import shutil
import threading
import time

from cad.worker import WorkerHandle

from conftest import ROCKET


def test_close_during_a_hung_build_is_clean(tmp_path):
    """Hub shutdown while a build runs in another thread: no invalid-handle errors, no respawn."""
    script = tmp_path / "rocket.py"
    shutil.copy(ROCKET, script)
    source = script.read_text(encoding="utf-8")
    script.write_text(source.replace("def build(p) -> BuildResult:", "def build(p) -> BuildResult:\n    while 1: pass"),
                      encoding="utf-8")
    worker = WorkerHandle()
    worker.spawn()
    results = []
    caller = threading.Thread(target=lambda: results.append(worker.call(
        {"op": "build", "script": str(script), "params": {}, "out_dir": str(tmp_path / "v1")}, timeout_s=60)))
    caller.start()
    time.sleep(8)  # worker warm and stuck in the build
    t0 = time.perf_counter()
    worker.close()
    caller.join(timeout=10)
    assert not caller.is_alive() and time.perf_counter() - t0 < 10
    assert results[0]["ok"] is False and results[0]["kind"] == "crash"
    assert worker._proc is None  # closing never respawns
    assert worker.call({"op": "build"}, 1)["error"] == "CAD worker is shutting down"
