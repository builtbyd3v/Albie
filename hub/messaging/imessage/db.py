"""Read-only access to the local Messages chat database.

The schema varies across macOS releases, so queries are assembled from discovered columns.  SQLite is opened with
mode=ro; this module never writes to the user's Messages database.
"""
from __future__ import annotations

import sqlite3
from pathlib import Path
from typing import Any
from urllib.parse import quote

APPLE_EPOCH = 978_307_200


class MessagesDatabaseError(RuntimeError):
    pass


def connect(path: Path) -> sqlite3.Connection:
    if not path.is_file():
        raise MessagesDatabaseError(f"Messages database not found: {path}")
    try:
        uri_path = quote(path.resolve().as_posix(), safe="/")
        db = sqlite3.connect(f"file:{uri_path}?mode=ro", uri=True, timeout=1.0)
        db.row_factory = sqlite3.Row
        db.execute("SELECT 1 FROM sqlite_master LIMIT 1").fetchone()
        return db
    except (sqlite3.Error, PermissionError) as exc:
        raise MessagesDatabaseError(str(exc)) from exc


def columns(db: sqlite3.Connection, table: str) -> set[str]:
    try:
        return {str(row[1]) for row in db.execute(f'PRAGMA table_info("{table}")')}
    except sqlite3.Error as exc:
        raise MessagesDatabaseError(f"Cannot inspect Messages table {table}: {exc}") from exc


def timestamp(raw: Any) -> str:
    """Convert Cocoa seconds/nanoseconds to Slack's seconds.microseconds string format."""
    try:
        value = int(raw or 0)
    except (TypeError, ValueError, OverflowError):
        return "0.000000"
    if value <= 0:
        return "0.000000"
    seconds = value / 1_000_000_000 if value > 1_000_000_000_000 else value
    unix_seconds = seconds + APPLE_EPOCH
    whole = int(unix_seconds)
    micros = int((unix_seconds - whole) * 1_000_000)
    return f"{whole}.{micros:06d}"


def text_body(text: Any, attributed: Any) -> str:
    """Prefer the plain text column; decode common UTF-8 fragments as a best-effort attributedBody fallback.

    Apple's typedstream format is undocumented. This conservative fallback strips binary/control bytes and only
    returns printable text when a plausible payload is present, avoiding accidental binary garbage in the UI.
    """
    if isinstance(text, str) and text.strip():
        return text
    if not isinstance(attributed, (bytes, bytearray, memoryview)):
        return ""
    blob = bytes(attributed)
    for encoding in ("utf-8", "utf-16le"):
        try:
            decoded = blob.decode(encoding, errors="ignore")
        except (UnicodeError, LookupError):
            continue
        chunks = []
        current = []
        for char in decoded:
            if char.isprintable() or char in "\n\t":
                current.append(char)
            else:
                if len(current) > len(chunks):
                    chunks = current
                current = []
        if len(current) > len(chunks):
            chunks = current
        candidate = "".join(chunks).strip("\x00\ufffc\ufffd \t\r\n")
        if len(candidate) >= 2 and any(c.isalpha() for c in candidate):
            return candidate
    return ""


def rows(db: sqlite3.Connection, *, after_rowid: int = 0, limit: int = 200,
         chat_guid: str | None = None, since_unix: float | None = None) -> list[dict[str, Any]]:
    """Fetch messages with their conversation and sender handles, newest database rows first."""
    message_cols = columns(db, "message")
    chat_cols = columns(db, "chat")
    join_cols = columns(db, "chat_message_join")
    if not {"ROWID", "handle_id", "is_from_me", "date"} <= message_cols:
        raise MessagesDatabaseError("Messages database has an unsupported message schema")
    if not {"chat_id", "message_id"} <= join_cols or "guid" not in chat_cols:
        raise MessagesDatabaseError("Messages database is missing chat joins")
    optional_message = {
        key: (f'm."{key}"' if key in message_cols else f'NULL AS "{key}"')
        for key in ("text", "attributedBody", "date_edited", "thread_originator_guid", "associated_message_type")
    }
    optional_chat = {
        key: (f'c."{key}"' if key in chat_cols else f'NULL AS "{key}"')
        for key in ("display_name", "chat_identifier", "style")
    }
    sql = f'''SELECT m.ROWID AS rowid, m.guid AS guid, m.handle_id AS handle_id, m.is_from_me AS is_from_me,
                     m.date AS date, {", ".join(optional_message.values())}, c.guid AS chat_guid,
                     {", ".join(optional_chat.values())}, h.id AS handle
              FROM message m JOIN chat_message_join cmj ON cmj.message_id=m.ROWID
              JOIN chat c ON c.ROWID=cmj.chat_id LEFT JOIN handle h ON h.ROWID=m.handle_id
              WHERE m.ROWID > ?'''
    args: list[Any] = [max(0, int(after_rowid))]
    if since_unix is not None:
        try:
            max_date = int(db.execute("SELECT COALESCE(MAX(date), 0) FROM message").fetchone()[0] or 0)
        except sqlite3.Error as exc:
            raise MessagesDatabaseError(f"Cannot inspect Messages timestamps: {exc}") from exc
        cutoff = max(0.0, float(since_unix) - APPLE_EPOCH)
        sql += " AND m.date >= ?"
        args.append(int(cutoff * (1_000_000_000 if max_date > 1_000_000_000_000 else 1)))
    if chat_guid:
        sql += " AND c.guid = ?"
        args.append(chat_guid)
    if "associated_message_type" in message_cols:
        sql += " AND COALESCE(m.associated_message_type, 0) NOT BETWEEN 2000 AND 3999"
    sql += " ORDER BY m.ROWID DESC LIMIT ?"
    args.append(max(1, min(int(limit), 1000)))
    try:
        return [dict(row) for row in db.execute(sql, args)]
    except sqlite3.Error as exc:
        raise MessagesDatabaseError(f"Cannot read Messages database: {exc}") from exc


def chat_rowid(db: sqlite3.Connection, chat_guid: str) -> tuple[int, str] | None:
    """Return the local chat row id and chat identifier used by the send adapter."""
    try:
        row = db.execute("SELECT ROWID, chat_identifier FROM chat WHERE guid = ? LIMIT 1", (chat_guid,)).fetchone()
    except sqlite3.Error as exc:
        raise MessagesDatabaseError(f"Cannot find Messages chat: {exc}") from exc
    return (int(row[0]), str(row[1] or chat_guid)) if row else None


def outgoing_match(db: sqlite3.Connection, chat_guid: str, text: str, after_rowid: int) -> dict[str, Any] | None:
    """Find a confirmed outgoing message in the intended conversation after a send attempt."""
    for row in rows(db, after_rowid=after_rowid, limit=1000, chat_guid=chat_guid):
        if int(row.get("is_from_me") or 0) and text_body(row.get("text"), row.get("attributedBody")) == text:
            return row
    return None
