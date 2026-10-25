"""notify.* contract (PROJECT_CONTEXT 7 + 8.8): payload key names, digest wording, digest building."""
from __future__ import annotations

from pathlib import Path

import pytest

import events
from events import NotifyDigest, NotifyMessage, NotifySent, SlackMessage, digest_line


def _msg(ts: str, mentions_me: bool = False, text: str = "hi") -> SlackMessage:
    return SlackMessage(
        channel="C01", channel_name="forge-team", user="U01", user_name="hudson",
        text=text, ts=ts, mentions_me=mentions_me,
    )


@pytest.mark.parametrize(
    ("count", "mentions", "line"),
    [
        (0, 0, "You have no new messages today."),
        (1, 0, "You have 1 new message."),
        (3, 0, "You have 3 new messages."),
        (1, 1, "You have 1 new message, and it mentions you."),
        (3, 1, "You have 3 new messages, 1 mentions you."),
        (3, 2, "You have 3 new messages, 2 mention you."),
        (2, 2, "You have 2 new messages, both mention you."),
        (4, 4, "You have 4 new messages, all of them mention you."),
    ],
)
def test_digest_line(count: int, mentions: int, line: str) -> None:
    assert digest_line(count, mentions) == line


def test_slack_message_keys_match_section_7() -> None:
    dumped = _msg("1700000000.000100").model_dump()
    assert list(dumped) == ["channel", "channel_name", "is_dm", "user", "user_name", "text", "ts", "thread_ts", "mentions_me",
                            "reply_count", "edited", "is_me", "user_initials", "source"]
    assert dumped["thread_ts"] is None and dumped["mentions_me"] is False
    assert all(dumped[k] is None for k in ("reply_count", "edited", "is_me", "user_initials"))  # optional: null unless known
    assert NotifyMessage is SlackMessage


def test_notify_sent_keys() -> None:
    dumped = NotifySent(channel="C01", channel_name="forge-team", text="on it", ts="1.2").model_dump()
    assert list(dumped) == ["draft_id", "channel", "channel_name", "text", "ts", "source"]
    assert dumped["draft_id"] is None and dumped["source"] == "slack"


def test_draft_and_cancelled_keys() -> None:
    from events import Draft, NotifyDraftCancelled

    d = Draft(draft_id="d1", channel="C01", channel_name="forge-team", text="yes, by 3").model_dump()
    assert list(d) == ["draft_id", "channel", "channel_name", "text", "thread_ts", "reply_to", "edited", "source"]
    assert d["edited"] is False and d["source"] == "slack"

    from events import ChannelSummary, NotifyChannels

    row = ChannelSummary(id="D01", name="hudson", kind="dm").model_dump()
    assert list(row) == ["id", "name", "kind", "unread", "mentions", "last_ts", "last_text", "topic", "is_default", "source"]
    assert (row["unread"], row["mentions"], row["last_ts"], row["is_default"]) == (0, 0, None, False)
    assert NotifyChannels().model_dump() == {"channels": []}
    assert NotifyDraftCancelled(draft_id="d1").model_dump() == {"draft_id": "d1", "reason": "cancelled"}


def test_digest_build_orders_newest_first_and_caps() -> None:
    msgs = [_msg(f"170000000{i}.000000", mentions_me=(i % 2 == 0)) for i in range(6)]  # 0..5, 3 mentions
    digest = NotifyDigest.build(msgs)
    assert (digest.count, digest.mentions) == (6, 3)
    assert digest.line == "You have 6 new messages, 3 mention you."
    assert [m.ts for m in digest.messages] == [f"170000000{i}.000000" for i in (5, 4, 3, 2)]
    assert list(digest.model_dump()) == ["count", "mentions", "line", "messages"]


def test_digest_build_sorts_by_numeric_ts_not_string() -> None:
    digest = NotifyDigest.build([_msg("999.000000"), _msg("1000.000000")], cap=1)
    assert [m.ts for m in digest.messages] == ["1000.000000"]


def test_digest_build_empty_still_has_a_line() -> None:
    digest = NotifyDigest.build([])
    assert digest.model_dump() == {"count": 0, "mentions": 0, "line": events.NO_NEW_MESSAGES, "messages": []}


def test_imports_worktree_copy() -> None:
    # pytest.ini sets pythonpath=.; guard against the venv's editable install shadowing this checkout.
    assert Path(events.__file__).resolve().parent == Path(__file__).resolve().parents[1]
