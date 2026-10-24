"""Live voice sessions by pet id, so other modules can make Bit speak unprompted (the sit-down digest).

ws.py registers a session when hello opens one and unregisters it when the socket or the session goes away.
`speak` duck-types on `say(text)` (GrokVoiceSession has it); sessions without it are skipped. `note` does the
same with `note(text)`: one line of context the model reads but doesn't answer (the view in front of the user,
the Slack message in focus).
"""
from __future__ import annotations

import logging
from typing import Any

log = logging.getLogger(__name__)

_sessions: dict[str, list[Any]] = {}


def register(pet_id: str, session: Any) -> None:
    live = _sessions.setdefault(pet_id, [])
    if session not in live:
        live.append(session)


def unregister(pet_id: str, session: Any) -> None:
    live = _sessions.get(pet_id)
    if live and session in live:
        live.remove(session)
    if live == []:
        _sessions.pop(pet_id, None)


def live(pet_id: str) -> list[Any]:
    return list(_sessions.get(pet_id, ()))


async def note(pet_id: str, text: str) -> bool:
    """Give every live voice session for the pet one line of context (no reply). True if one took it."""
    noted = False
    for session in live(pet_id):
        fn = getattr(session, "note", None)
        if fn is None:
            continue
        try:
            await fn(text)
            noted = True
        except Exception:  # noqa: BLE001
            log.exception("note failed for %s", pet_id)
    return noted


async def speak(pet_id: str, text: str) -> bool:
    """Say `text` on every live voice session for the pet. True if at least one spoke."""
    spoke = False
    for session in live(pet_id):
        say = getattr(session, "say", None)
        if say is None:
            continue
        try:
            await say(text)
            spoke = True
        except Exception:  # noqa: BLE001 - one broken session must not block the others
            log.exception("speak failed for %s", pet_id)
    return spoke
