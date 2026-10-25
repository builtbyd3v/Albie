"""The user's notes: a short list Bit keeps for them ("add that to my notes", "read me my notes").

Saved in .data/desk/notes.json, so they survive restarts. Removing a note keeps it for Undo (the panel's button, or
Bit's notes_remove with undo), so the pet never deletes anything for good.
"""
from __future__ import annotations

import json
import logging
import time
import uuid
from pathlib import Path

from events import Note, NotesUpdated

log = logging.getLogger("forge.desk")

MAX_TEXT = 500
MAX_NOTES = 200


class NotesStore:
    def __init__(self, path: Path) -> None:
        self.path = Path(path)
        self._notes: list[Note] = []  # oldest first on disk; read newest first
        self._last_removed: Note | None = None
        self._load()

    def _load(self) -> None:
        try:
            data = json.loads(self.path.read_text(encoding="utf-8"))
            self._notes = [Note(**n) for n in data.get("notes", [])]
            removed = data.get("last_removed")
            self._last_removed = Note(**removed) if removed else None
        except FileNotFoundError:
            pass
        except (OSError, ValueError, TypeError) as e:
            log.warning("notes unreadable (%s); starting empty", e)

    def _save(self) -> None:
        self.path.parent.mkdir(parents=True, exist_ok=True)
        body = {"notes": [n.model_dump() for n in self._notes],
                "last_removed": self._last_removed.model_dump() if self._last_removed else None}
        tmp = self.path.with_suffix(".tmp")
        tmp.write_text(json.dumps(body, indent=1), encoding="utf-8")
        tmp.replace(self.path)

    def all(self) -> list[Note]:
        return list(reversed(self._notes))

    def updated(self) -> NotesUpdated:
        return NotesUpdated(notes=self.all(), last_removed=self._last_removed)

    def add(self, text: str) -> Note:
        text = _clean(text)
        if len(self._notes) >= MAX_NOTES:
            raise ValueError(f"that's {MAX_NOTES} notes already; remove some first")
        now = time.time()
        note = Note(id="n" + uuid.uuid4().hex[:6], text=text, created_at=now, updated_at=now)
        self._notes.append(note)
        self._save()
        return note

    def find(self, ref: str) -> Note:
        """A note by id, or by a few words of its text ("the one about the demo")."""
        ref = (ref or "").strip()
        for n in self._notes:
            if n.id == ref:
                return n
        words = ref.lower()
        hits = [n for n in self._notes if words and words in n.text.lower()]
        if len(hits) == 1:
            return hits[0]
        if not hits:
            raise LookupError(f"no note matches {ref!r}")
        raise LookupError(f"{len(hits)} notes match {ref!r}: " + "; ".join(n.text[:40] for n in hits[:5]))

    def edit(self, ref: str, text: str | None = None, done: bool | None = None) -> Note:
        note = self.find(ref)
        if text is not None:
            note.text = _clean(text)
        if done is not None:
            note.done = bool(done)
        note.updated_at = time.time()
        self._save()
        return note

    def remove(self, ref: str) -> Note:
        note = self.find(ref)
        self._notes.remove(note)
        self._last_removed = note
        self._save()
        return note

    def undo(self) -> Note:
        """Put the last removed note back where it was (by creation time)."""
        note = self._last_removed
        if note is None:
            raise LookupError("nothing to undo")
        self._notes.append(note)
        self._notes.sort(key=lambda n: n.created_at)
        self._last_removed = None
        self._save()
        return note

    def __len__(self) -> int:
        return len(self._notes)


def _clean(text: str) -> str:
    text = " ".join(str(text or "").split())
    if not text:
        raise ValueError("the note is empty")
    if len(text) > MAX_TEXT:
        raise ValueError(f"a note holds at most {MAX_TEXT} characters")
    return text
