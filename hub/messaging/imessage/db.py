"""Read-only access to the local Messages chat database.

The schema varies across macOS releases, so queries are assembled from discovered columns.  SQLite is opened with
mode=ro; this module never writes to the user's Messages database.

Privacy: every query that touches chats or messages takes the allowlist and filters in SQL, so rows from other
conversations never reach Python. Only 1:1 chats (style 45) whose chat_identifier is an allowed handle match; group
chats never do (their identifier is "chatNNN", and style 43 is excluded too). An empty allowlist matches nothing.
"""
from __future__ import annotations

import sqlite3
from pathlib import Path
from typing import Any
from urllib.parse import quote

from .settings import handle_variants

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


def _norm_sql(col: str) -> str:
    """SQL twin of settings.normalize_handle minus the +1 rule (handle_variants covers that side)."""
    stripped = col
    for ch in " -().":
        stripped = f"replace({stripped}, '{ch}', '')"
    return f"(CASE WHEN instr({col}, '@') > 0 THEN lower({col}) ELSE {stripped} END)"


def allow_filter(db: sqlite3.Connection, allow: frozenset[str], alias: str = "c") -> tuple[str, list[str]]:
    """A WHERE fragment (and its args) that keeps only allowlisted 1:1 chats. Fails closed: "0" matches nothing."""
    chat_cols = columns(db, "chat")
    variants = sorted({v for h in allow for v in handle_variants(h)})
    if not variants or "chat_identifier" not in chat_cols:
        return "0", []
    clause = f"{_norm_sql(f'{alias}.chat_identifier')} IN ({', '.join('?' * len(variants))})"
    if "style" in chat_cols:
        clause += f" AND {alias}.style = 45"
    return f"({clause})", variants


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
    """The plain `text` column, else the string inside `attributedBody` (newer macOS often leaves `text` NULL).

    attributedBody is an NSAttributedString in Apple's typedstream format. Its text is the first NSString payload:
    after the class name comes a `+` marker, a length (one byte; 0x81 = a 2-byte little-endian length follows, 0x82 = a
    4-byte one) and that many UTF-8 bytes. Anything that doesn't parse gives "" rather than binary garbage.
    """
    if isinstance(text, str) and text.strip():
        return text
    if not isinstance(attributed, (bytes, bytearray, memoryview)):
        return ""
    blob = bytes(attributed)
    start = blob.find(b"NSString")
    if start < 0:
        return ""
    plus = blob.find(b"+", start + len(b"NSString"))
    if plus < 0 or plus - start > 32:
        return ""
    i = plus + 1
    if i >= len(blob):
        return ""
    length = blob[i]
    i += 1
    if length == 0x81:
        length, i = int.from_bytes(blob[i:i + 2], "little"), i + 2
    elif length == 0x82:
        length, i = int.from_bytes(blob[i:i + 4], "little"), i + 4
    elif length >= 0x80:
        return ""
    if length <= 0 or i + length > len(blob):
        return ""
    return blob[i:i + length].decode("utf-8", errors="replace").strip("\ufffc")


def rows(db: sqlite3.Connection, *, allow: frozenset[str], after_rowid: int = 0, limit: int = 200,
         chat_guid: str | None = None, since_unix: float | None = None) -> list[dict[str, Any]]:
    """Fetch allowlisted messages with their conversation and sender handles, newest database rows first."""
    if not allow:
        return []
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
    chat_clause, chat_args = allow_filter(db, allow)
    variants = sorted({v for h in allow for v in handle_variants(h)})
    # The sender must be me or the allowed contact too, in case a 1:1 chat ever carries a stray handle.
    sql += f" AND {chat_clause} AND (m.is_from_me = 1 OR {_norm_sql('h.id')} IN ({', '.join('?' * len(variants))}))"
    args += chat_args + variants
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


def chat_rowid(db: sqlite3.Connection, chat_guid: str, *, allow: frozenset[str]) -> tuple[int, str] | None:
    """Return the local chat row id and chat identifier used by the send adapter (allowlisted chats only)."""
    clause, args = allow_filter(db, allow)
    try:
        row = db.execute(f"SELECT c.ROWID, c.chat_identifier FROM chat c WHERE c.guid = ? AND {clause} LIMIT 1",
                         (chat_guid, *args)).fetchone()
    except sqlite3.Error as exc:
        raise MessagesDatabaseError(f"Cannot find Messages chat: {exc}") from exc
    return (int(row[0]), str(row[1] or chat_guid)) if row else None


def chats(db: sqlite3.Connection, *, allow: frozenset[str], chat_guid: str | None = None) -> list[sqlite3.Row]:
    """Allowlisted chats as (guid, chat_identifier, display_name, style) rows, newest first; one chat with chat_guid."""
    clause, args = allow_filter(db, allow)
    chat_cols = columns(db, "chat")
    pick = ", ".join(f"c.{k}" if k in chat_cols else f"NULL AS {k}" for k in ("display_name", "style"))
    sql = f"SELECT c.guid, c.chat_identifier, {pick} FROM chat c WHERE {clause}"
    if chat_guid is not None:
        sql += " AND c.guid = ?"
        args = [*args, chat_guid]
    try:
        return db.execute(sql + " ORDER BY c.ROWID DESC LIMIT 1000", args).fetchall()
    except sqlite3.Error as exc:
        raise MessagesDatabaseError(f"Cannot read Messages chats: {exc}") from exc


def outgoing_match(db: sqlite3.Connection, chat_guid: str, text: str, after_rowid: int, *,
                   allow: frozenset[str]) -> dict[str, Any] | None:
    """Find a confirmed outgoing message in the intended conversation after a send attempt."""
    for row in rows(db, allow=allow, after_rowid=after_rowid, limit=1000, chat_guid=chat_guid):
        if int(row.get("is_from_me") or 0) and text_body(row.get("text"), row.get("attributedBody")) == text:
            return row
    return None
