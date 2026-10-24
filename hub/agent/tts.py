"""Hub-side text-to-speech hook (stub).

Where it plugs in later: after ``agent.answer_done`` the hub could call
``TextToSpeech.speak(text)`` and stream the audio to Unity (e.g. a proposed
``tts.audio`` binary frame). On the Unity side, ``ITextToSpeech.SpeechStarted`` /
``SpeechEnded`` drive the gator's mouth: ``GatorController`` has a
``// TODO(lipsync)`` hook in the Answering state where the speaking animation /
blendshape loop starts and stops. Until then Unity uses ``NullTextToSpeech`` and the
caption alone.
"""

from __future__ import annotations

import logging
from typing import Protocol

log = logging.getLogger(__name__)


class TextToSpeech(Protocol):
    async def speak(self, text: str) -> bytes | None:
        """Synthesize ``text``; return encoded audio bytes, or None if unsupported."""
        ...


class NullTTS:
    async def speak(self, text: str) -> bytes | None:
        log.debug("NullTTS.speak(%r) -> None", text[:60])
        return None
