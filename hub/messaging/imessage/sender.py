"""Bounded `imsg` CLI adapter for sending through Messages.app.

Messages removed or changed parts of its AppleScript dictionary in macOS Tahoe. The `imsg` CLI wraps the published
automation surface and verifies the resulting database row (including Tahoe's ghost-row case). We require its CLI
instead of reporting success from an AppleScript command that may only appear to succeed.
"""
from __future__ import annotations

import asyncio
import json
import logging
import re
import shutil
from dataclasses import dataclass
from pathlib import Path

log = logging.getLogger("forge.messaging.imessage")


class IMessageSendError(RuntimeError):
    pass


@dataclass(frozen=True)
class SenderStatus:
    available: bool
    version: str | None
    error: str | None = None


class IMessageSender:
    def __init__(self, executable: str = "imsg") -> None:
        self.executable = executable
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

    @staticmethod
    def _find_homebrew() -> str | None:
        path = shutil.which("brew")
        if path:
            return path
        for candidate in (Path("/opt/homebrew/bin/brew"), Path("/usr/local/bin/brew")):
            if candidate.is_file():
                return str(candidate)
        return None

    async def _install_with_homebrew(self) -> str | None:
        brew = self._find_homebrew()
        if not brew:
            self.status = SenderStatus(False, None,
                                       "Homebrew is not installed. Install Homebrew, then restart the hub; "
                                       "the hub will install imsg automatically.")
            return None
        try:
            proc = await asyncio.create_subprocess_exec(
                brew, "install", "steipete/tap/imsg",
                stdout=asyncio.subprocess.PIPE, stderr=asyncio.subprocess.PIPE,
            )
            stdout, stderr = await asyncio.wait_for(proc.communicate(), timeout=180)
            if proc.returncode != 0:
                detail = (stderr or stdout).decode("utf-8", errors="replace").strip()
                self.status = SenderStatus(False, None,
                                           f"Homebrew could not install imsg. Run `brew install "
                                           f"steipete/tap/imsg` and restart the hub. {detail}".strip())
                return None
        except asyncio.TimeoutError:
            proc.kill()
            await proc.wait()
            self.status = SenderStatus(False, None,
                                       "Homebrew install of imsg timed out. Run `brew install "
                                       "steipete/tap/imsg` and restart the hub.")
            return None
        except OSError as exc:
            self.status = SenderStatus(False, None, f"Could not run Homebrew: {exc}")
            return None
        self.path = self._find_executable()
        if not self.path:
            self.status = SenderStatus(False, None,
                                       "Homebrew installed imsg, but the executable is not on PATH. "
                                       "Restart the hub or set IMESSAGE_CLI to its full path.")
        return self.path

    async def start(self) -> SenderStatus:
        if not self.path:
            if self.executable == "imsg":
                log.info("imsg is missing; installing it with Homebrew")
                if await self._install_with_homebrew() is None:
                    return self.status
            else:
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

    async def send(self, chat_id: int, text: str) -> None:
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
        """Read contact-resolved chat labels from the same CLI used to send."""
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
