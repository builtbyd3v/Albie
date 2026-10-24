"""Hub-side speech-to-text for HubSpeechToText (Unity streams PCM16 mono 16 kHz).

Providers (env STT_PROVIDER):
* ``none``    (default) NoneSTT: accepts audio, returns "" and logs.
* ``whisper`` WhisperSTT: faster-whisper, re-transcribes the growing buffer every
  ~1.5 s for partials. Needs ``pip install -e ".[stt]"``.

Methods are synchronous (whisper is CPU-bound); ws.py calls ``partial``/``finish``
via ``asyncio.to_thread``.
"""

from __future__ import annotations

import ast
import logging
import os
import threading
import time
from functools import lru_cache
from pathlib import Path
from typing import Any, Protocol

log = logging.getLogger(__name__)

SAMPLE_RATE = 16000
PARTIAL_EVERY_S = 1.5
_SKIP_DIRS = {".git", ".venv", "node_modules", "Library", "Temp", "obj", "bin", "__pycache__"}
_CODE_EXT = {".py", ".cs", ".ts", ".js", ".md", ".yaml", ".json", ".shader"}


class SpeechToText(Protocol):
    def feed(self, pcm16: bytes) -> None: ...
    def partial(self) -> str | None: ...  # new partial text, or None if nothing new yet
    def finish(self) -> str: ...  # final transcript; resets the buffer


class NoneSTT:
    """No STT backend: logs and returns empty transcripts."""

    def __init__(self) -> None:
        self._bytes = 0

    def feed(self, pcm16: bytes) -> None:
        self._bytes += len(pcm16)

    def partial(self) -> str | None:
        return None

    def finish(self) -> str:
        log.info("NoneSTT: got %.1f s of audio, STT_PROVIDER=none -> ''", self._bytes / 2 / SAMPLE_RATE)
        self._bytes = 0
        return ""


@lru_cache(maxsize=8)
def build_vocab_prompt(repo: str, limit: int = 120) -> str:
    """Whisper initial_prompt: repo file basenames + top-level Python/C# symbols (cached)."""
    names: list[str] = []
    seen: set[str] = set()

    def add(n: str) -> None:
        if n and n not in seen and len(names) < limit:
            seen.add(n)
            names.append(n)

    root = Path(repo)
    for dirpath, dirnames, filenames in os.walk(root):
        dirnames[:] = [d for d in dirnames if d not in _SKIP_DIRS and not d.startswith(".")]
        for fn in filenames:
            p = Path(dirpath) / fn
            if p.suffix not in _CODE_EXT:
                continue
            add(p.stem)
            if p.suffix == ".py" and p.stat().st_size < 200_000:
                try:
                    tree = ast.parse(p.read_text(errors="ignore"))
                    for node in tree.body:
                        if isinstance(node, (ast.FunctionDef, ast.AsyncFunctionDef, ast.ClassDef)):
                            add(node.name)
                except SyntaxError:
                    pass
        if len(names) >= limit:
            break
    return "Code terms: " + ", ".join(names) + "."


class WhisperSTT:
    """faster-whisper (int8 on CPU) with periodic re-transcription for partials."""

    _models: dict[str, Any] = {}  # model cache shared across sessions (loading is slow)

    def __init__(self, initial_prompt: str | None = None) -> None:
        from faster_whisper import WhisperModel  # optional dependency

        name = os.getenv("WHISPER_MODEL", "small")
        if name not in self._models:
            log.info("loading faster-whisper model %s (int8, cpu)", name)
            self._models[name] = WhisperModel(name, device="cpu", compute_type="int8")
        self._model = self._models[name]
        self._prompt = initial_prompt
        self._buf = bytearray()
        self._lock = threading.Lock()
        self._last_partial_t = 0.0
        self._last_partial = ""

    def feed(self, pcm16: bytes) -> None:
        with self._lock:
            self._buf.extend(pcm16)

    def _transcribe(self, audio: bytes) -> str:
        import numpy as np

        if len(audio) < SAMPLE_RATE // 5:  # < 0.1 s
            return ""
        samples = np.frombuffer(audio, dtype=np.int16).astype(np.float32) / 32768.0
        segments, _ = self._model.transcribe(
            samples, language="en", initial_prompt=self._prompt, beam_size=1, vad_filter=True
        )
        return " ".join(s.text.strip() for s in segments).strip()

    def partial(self) -> str | None:
        now = time.monotonic()
        if now - self._last_partial_t < PARTIAL_EVERY_S:
            return None
        self._last_partial_t = now
        with self._lock:
            audio = bytes(self._buf)
        text = self._transcribe(audio)
        if not text or text == self._last_partial:
            return None
        self._last_partial = text
        return text

    def finish(self) -> str:
        with self._lock:
            audio = bytes(self._buf)
            self._buf.clear()
        self._last_partial = ""
        return self._transcribe(audio)


def make_stt(repo: Path | None = None) -> SpeechToText:
    """Build the provider selected by STT_PROVIDER (falls back to NoneSTT on error)."""
    provider = os.getenv("STT_PROVIDER", "none").lower()
    if provider == "whisper":
        try:
            prompt = build_vocab_prompt(str(repo)) if repo else None
            return WhisperSTT(initial_prompt=prompt)
        except Exception:
            log.exception("WhisperSTT unavailable, falling back to NoneSTT")
    return NoneSTT()
