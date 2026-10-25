"""Bounded `imsg` CLI adapter for sending through Messages.app.

Messages removed or changed parts of its AppleScript dictionary in macOS Tahoe. The `imsg` CLI wraps the published
automation surface and verifies the resulting database row (including Tahoe's ghost-row case). We require its CLI
instead of reporting success from an AppleScript command that may only appear to succeed. The hub never installs it:
without it, iMessage is read-only and the log says how to install it (`brew install steipete/tap/imsg`).
"""
from __future__ import annotations

import asyncio
import json
import logging
import re
import shutil
from dataclasses import dataclass
from pathlib import Path

from .settings import normalize_handle

log = logging.getLogger("forge.messaging.imessage")
INSTALL_HINT = ("imsg CLI not installed, so iMessage is read-only. To send, install it yourself "
                "(`brew install steipete/tap/imsg`, a third-party tap) and restart the hub.")


class IMessageSendError(RuntimeError):
    pass


@dataclass(frozen=True)
class SenderStatus:
    available: bool
    version: str | None
    error: str | None = None


class IMessageSender:
    def __init__(self, executable: str = "imsg", allow: frozenset[str] = frozenset()) -> None:
        self.executable = executable
        self.allow = allow
        self.path = self._find_executable()
        self.status = SenderStatus(False, None, f"{executable} CLI not installed")

    def _find_executable(self) -> str | None:
        path = shutil.which(self.executable)
        if path:
            return path
        if self.executable != "imsg":
            return None
        # GUI-launched apps often omit Homebrew's bin directory from PATH.
        for candidate in (Path("/opt/homebrew/bin/imsg"), Path("/usr/local/bin/imsg")):
            if candidate.is_file():
                return str(candidate)
        return None

    async def start(self) -> SenderStatus:
        if not self.path:
            self.status = SenderStatus(False, None, INSTALL_HINT if self.executable == "imsg" else
                                       f"{self.executable} not found; set IMESSAGE_CLI to the imsg executable")
            return self.status
        try:
            proc = await asyncio.create_subprocess_exec(self.path, "--version", stdout=asyncio.subprocess.PIPE,
                                                        stderr=asyncio.subprocess.PIPE)
            stdout, stderr = await asyncio.wait_for(proc.communicate(), timeout=3)
            output = (stdout or stderr).decode("utf-8", errors="replace").strip()
            match = re.search(r"(\d+)\.(\d+)\.(\d+)", output)
            if proc.returncode != 0 or not match:
                raise IMessageSendError(output or "could not read imsg version")
            version = tuple(map(int, match.groups()))
            if version < (0, 6, 0):
                raise IMessageSendError(f"imsg {match.group(0)} is too old; macOS 26 send verification requires 0.6.0+")
            self.status = SenderStatus(True, match.group(0))
        except (OSError, asyncio.TimeoutError, IMessageSendError) as exc:
            self.status = SenderStatus(False, None, str(exc))
        return self.status

    async def send(self, chat_id: int, text: str, *, handle: str) -> None:
        """Send to one 1:1 chat. `handle` is that chat's identifier; anything not in IMESSAGE_ALLOW is refused here,
        before imsg runs (the error never echoes the handle)."""
        if normalize_handle(handle) not in self.allow:
            raise IMessageSendError("Refused: that conversation is not in IMESSAGE_ALLOW")
        if not self.status.available or not self.path:
            raise IMessageSendError(self.status.error or "iMessage sending is unavailable")
        try:
            proc = await asyncio.create_subprocess_exec(self.path, "send", "--chat-id", str(chat_id), "--text", text,
                                                        "--json", stdout=asyncio.subprocess.PIPE,
                                                        stderr=asyncio.subprocess.PIPE)
            stdout, stderr = await asyncio.wait_for(proc.communicate(), timeout=20)
        except asyncio.TimeoutError as exc:
            proc.kill()
            await proc.wait()
            raise IMessageSendError("imsg timed out; check Messages before trying again because send outcome is unknown") from exc
        except OSError as exc:
            raise IMessageSendError(f"could not run imsg: {exc}") from exc
        if proc.returncode != 0:
            detail = (stderr or stdout).decode("utf-8", errors="replace").strip()
            raise IMessageSendError(detail or f"imsg exited with status {proc.returncode}")

    async def chats(self) -> list[dict[str, object]]:
        """Read contact-resolved chat labels from the same CLI used to send. The caller must drop every chat that is
        not allowlisted right away (imsg has no filter), and never log these."""
        if not self.status.available or not self.path:
            return []
        try:
            proc = await asyncio.create_subprocess_exec(self.path, "chats", "--limit", "1000", "--json",
                                                        stdout=asyncio.subprocess.PIPE,
                                                        stderr=asyncio.subprocess.PIPE)
            stdout, _ = await asyncio.wait_for(proc.communicate(), timeout=10)
            if proc.returncode != 0:
                return []
            parsed = []
            for line in stdout.decode("utf-8", errors="replace").splitlines():
                try:
                    value = json.loads(line)
                except ValueError:
                    continue
                if isinstance(value, dict):
                    parsed.append(value)
            return parsed
        except (OSError, asyncio.TimeoutError):
            return []
