"""Screen-bridge supervisor (hub/screens): status events, restart with backoff, shutdown kills the process group.

The child is tests/fake_screen_bridge.py, which prints what start.js prints. Chrome is never launched.
"""
from __future__ import annotations

import asyncio
import os
import sys
import time
from pathlib import Path
from typing import Any

import pytest
from fastapi import FastAPI
from fastapi.testclient import TestClient

from events import SCREEN_STATUS, EventBus, ScreenStatus
from screens import ScreenBridgeService, router
from screens import service as service_mod

FAKE = Path(__file__).resolve().parent / "fake_screen_bridge.py"


class BusRecorder(EventBus):
    def __init__(self) -> None:
        super().__init__()
        self.events: list[tuple[str, dict[str, Any]]] = []

    async def publish(self, type_: str, payload: Any) -> None:
        self.events.append((type_, payload.model_dump() if hasattr(payload, "model_dump") else payload))
        await super().publish(type_, payload)

    def states(self) -> list[str]:
        return [p["state"] for t, p in self.events if t == SCREEN_STATUS]


def _cmd(pidfile: Path, mode: str = "run") -> list[str]:
    return [sys.executable, "-u", str(FAKE), "--pidfile", str(pidfile), "--mode", mode]


def _alive(pid: int) -> bool:
    try:
        os.kill(pid, 0)
    except ProcessLookupError:
        return False
    # a zombie answers kill(0); try to reap it if it's ours, else ask the kernel what it is
    try:
        done, _ = os.waitpid(pid, os.WNOHANG)
        return done == 0
    except ChildProcessError:
        return True


async def _wait_dead(pid: int, timeout: float = 3.0) -> bool:
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        if not _alive(pid):
            return True
        await asyncio.sleep(0.05)
    return not _alive(pid)


async def _wait_for(pred, timeout: float = 5.0) -> None:
    deadline = time.monotonic() + timeout
    while not pred():
        if time.monotonic() > deadline:
            raise AssertionError("timed out")
        await asyncio.sleep(0.02)


def _pids(pidfile: Path) -> tuple[int, int]:
    own, child = pidfile.read_text().split()
    return int(own), int(child)


# ------------------------------------------------------------------ disabled paths
def test_env_off_means_no_child(monkeypatch: pytest.MonkeyPatch, tmp_path: Path) -> None:
    monkeypatch.setenv("SCREEN_BRIDGE", "0")
    bus = BusRecorder()
    svc = ScreenBridgeService(bus, directory=tmp_path)
    asyncio.run(svc.start())
    assert svc.health() == {"enabled": False, "state": "stopped", "source": None}
    assert bus.events == [] and SCREEN_STATUS in bus.sticky


def test_disabled_without_node_or_dir(monkeypatch: pytest.MonkeyPatch, tmp_path: Path, caplog) -> None:
    monkeypatch.delenv("SCREEN_BRIDGE", raising=False)
    monkeypatch.setattr(service_mod.shutil, "which", lambda name: None)
    svc = ScreenBridgeService(BusRecorder(), directory=tmp_path)
    assert svc.enabled  # default on...
    asyncio.run(svc.start())
    assert svc.enabled is False and "node" in caplog.text  # ...until node turns out to be missing

    monkeypatch.setattr(service_mod.shutil, "which", lambda name: "/usr/bin/" + name)
    svc = ScreenBridgeService(BusRecorder(), directory=tmp_path / "nowhere")
    asyncio.run(svc.start())
    assert svc.enabled is False and "start.js" in caplog.text

    (tmp_path / "start.js").write_text("// real one")
    svc = ScreenBridgeService(BusRecorder(), directory=tmp_path)
    assert svc._resolve_command() == ["npm", "start"]
    monkeypatch.setattr(service_mod.shutil, "which", lambda name: None if name == "npm" else "/usr/bin/node")
    assert svc._resolve_command() == ["node", "start.js"]


def test_dir_from_env(monkeypatch: pytest.MonkeyPatch, tmp_path: Path) -> None:
    monkeypatch.setenv("SCREEN_BRIDGE_DIR", str(tmp_path / "sb"))
    assert ScreenBridgeService(BusRecorder()).dir == tmp_path / "sb"
    monkeypatch.delenv("SCREEN_BRIDGE_DIR")
    assert ScreenBridgeService(BusRecorder()).dir == service_mod.DEFAULT_DIR
    assert service_mod.DEFAULT_DIR.name == "screen-bridge"


# ------------------------------------------------------------------ the happy path + shutdown
def test_lines_become_status_events_and_stop_kills_the_group(tmp_path: Path) -> None:
    pidfile = tmp_path / "pids"
    bus = BusRecorder()
    svc = ScreenBridgeService(bus, directory=tmp_path, enabled=True, command=_cmd(pidfile))

    async def run() -> tuple[int, int]:
        await svc.start()
        await _wait_for(lambda: svc.fps is not None)
        own, child = _pids(pidfile)
        assert _alive(own) and _alive(child)
        assert svc.health() == {"enabled": True, "state": "sharing", "source": "| demo"}
        st = svc.status()
        assert st["pid"] == own and st["fps"] == "12 fps, 1280x720" and st["restarts"] == 0
        await svc.stop()
        return own, child

    own, child = asyncio.run(run())
    assert bus.states() == ["starting", "starting", "sharing", "sharing", "stopped"]
    payloads = [p for t, p in bus.events if t == SCREEN_STATUS]
    assert payloads[1]["detail"] == "signaling on ws://0.0.0.0:8766"
    assert payloads[2] == {"state": "sharing", "source": "| demo", "detail": 'Sharing "| demo"'}
    assert payloads[3]["detail"] == "signaling: connected"
    assert payloads[4] == {"state": "stopped", "source": "| demo", "detail": "hub shutting down"}
    assert asyncio.run(_wait_dead(own)) and asyncio.run(_wait_dead(child)), "SIGINT to the group killed both"
    assert svc.status()["pid"] is None


def test_hanging_child_gets_sigkill(tmp_path: Path) -> None:
    pidfile = tmp_path / "pids"
    svc = ScreenBridgeService(BusRecorder(), directory=tmp_path, enabled=True, command=_cmd(pidfile, "hang"),
                              kill_grace_s=0.3)

    async def run() -> tuple[float, int, int]:
        await svc.start()
        await _wait_for(lambda: svc.state == "sharing")
        own, child = _pids(pidfile)
        t0 = time.monotonic()
        await svc.stop()
        return time.monotonic() - t0, own, child

    took, own, child = asyncio.run(run())
    assert 0.3 <= took < 3.0  # waited the grace period, then SIGKILL
    assert asyncio.run(_wait_dead(own)) and asyncio.run(_wait_dead(child))


# ------------------------------------------------------------------ restarts
def test_restart_with_backoff_then_give_up(tmp_path: Path) -> None:
    pidfile = tmp_path / "pids"
    bus = BusRecorder()
    svc = ScreenBridgeService(bus, directory=tmp_path, enabled=True, command=_cmd(pidfile, "die-early"),
                              backoff=(0.05, 0.1, 0.15))

    async def run() -> dict[str, Any]:
        await svc.start()
        await _wait_for(lambda: svc.state == "error", timeout=10)
        assert svc._task is not None
        await asyncio.wait_for(svc._task, 2)  # the supervisor returns after giving up
        health = svc.health()
        await svc.stop()
        return health

    health = asyncio.run(run())
    assert health == {"enabled": True, "state": "error", "source": None}
    assert svc.state == "stopped"  # shutdown still reads as stopped
    assert svc.restarts == 3
    details = [p["detail"] for t, p in bus.events if t == SCREEN_STATUS]
    assert details.count("starting") == 4  # four spawns
    assert [d for d in details if d.startswith("exited (2); restarting in")] == \
        ["exited (2); restarting in 0s", "exited (2); restarting in 0s", "exited (2); restarting in 0s"]
    assert bus.states()[-2:] == ["error", "stopped"] and "died 4 times" in details[-2]


def test_sharing_resets_the_restart_budget(tmp_path: Path) -> None:
    pidfile = tmp_path / "pids"
    bus = BusRecorder()
    svc = ScreenBridgeService(bus, directory=tmp_path, enabled=True, command=_cmd(pidfile, "die"),
                              backoff=(0.05, 0.05, 0.05))

    grandchildren: set[int] = set()

    async def run() -> None:
        await svc.start()
        while svc.restarts < 4:  # a plain budget would have stopped at 3
            if pidfile.exists():
                grandchildren.add(_pids(pidfile)[1])
            await asyncio.sleep(0.02)
        assert svc.state != "error"
        await svc.stop()

    asyncio.run(asyncio.wait_for(run(), 10))
    assert "sharing" in bus.states()
    # The stand-in for Chrome outlived each crashed start.js (and held its pipes): the sweep took it down anyway.
    assert len(grandchildren) >= 2 and all(asyncio.run(_wait_dead(pid)) for pid in grandchildren)


# ------------------------------------------------------------------ sticky replay + REST
def test_status_is_sticky_on_attach() -> None:
    class FakeWs:
        def __init__(self) -> None:
            self.sent: list[dict[str, Any]] = []

        async def send_json(self, msg: dict[str, Any]) -> None:
            self.sent.append(msg)

    async def run() -> list[dict[str, Any]]:
        bus = EventBus()
        assert SCREEN_STATUS not in bus.sticky  # modules opt in; the default set is unchanged
        ScreenBridgeService(bus, enabled=False)
        await bus.publish(SCREEN_STATUS, ScreenStatus(state="sharing", source="| demo"))
        late = FakeWs()
        await bus.attach(late)
        return late.sent

    sent = asyncio.run(run())
    assert sent == [{"type": "screen.status", "payload": {"state": "sharing", "source": "| demo", "detail": None}}]
    assert set(EventBus(sticky=["x"]).sticky) == {"x"}


def test_status_route(tmp_path: Path) -> None:
    app = FastAPI()
    app.include_router(router)
    app.state.screens = ScreenBridgeService(EventBus(), directory=tmp_path, enabled=False)
    r = TestClient(app).get("/screens/status")
    assert r.status_code == 200
    body = r.json()
    assert body["enabled"] is False and body["state"] == "stopped"
    assert set(body) == {"enabled", "state", "source", "detail", "fps", "pid", "restarts", "dir", "command"}
