"""Slack drafts with files (Addendum H): attach, open the messenger first, reword in place, upload on send, keep the
draft open when Slack refuses (no network; the Slack web clients are stubs from test_messaging)."""
from __future__ import annotations

import asyncio
from pathlib import Path
from typing import Any

import pytest

from events import Attachment
from messaging import tools as mtools
from messaging.slack_client import FILES_SCOPE_HELP
from tests.test_messaging import SlackApiError, StubClient, make_service, msg


class FilesClient(StubClient):
    """StubClient plus the two calls a send with files makes (files_upload_v2, then files.info for the share's ts)."""

    def __init__(self, identity: str = "UME", shared_after: int = 1) -> None:
        super().__init__(identity)
        self.uploads: list[dict[str, Any]] = []
        self.upload_error: Exception | None = None
        self.shared_after = shared_after  # files.info calls before the share shows up (Slack shares asynchronously)
        self.info_calls = 0

    async def files_upload_v2(self, **kwargs: Any) -> dict[str, Any]:
        if self.upload_error is not None:
            raise self.upload_error
        self.uploads.append(kwargs)
        return {"ok": True, "files": [{"id": f"F{i}", "title": f["title"]} for i, f in enumerate(kwargs["file_uploads"])]}

    async def files_info(self, **kwargs: Any) -> dict[str, Any]:
        self.info_calls += 1
        if self.info_calls <= self.shared_after:
            return {"file": {"id": kwargs["file"], "shares": {}}}
        return {"file": {"id": kwargs["file"], "shares": {"private": {"DU2": [{"ts": "1700000200.000100"}]}}}}


def files_on_disk(tmp_path: Path) -> list[Attachment]:
    out = []
    for kind, name in (("step", "rocket_v7.step"), ("drawing", "rocket_v7_drawing.pdf")):
        path = tmp_path / name
        path.write_bytes(b"x" * 100)
        out.append(Attachment(id=f"rocket-v7-{kind}", name=name, title=kind.upper(), kind=kind, bytes=100,
                              source="cad rocket v7", path=str(path)))
    return out


def service(tmp_path: Path) -> tuple[Any, Any, FilesClient]:
    svc, bus, _ = make_service(tmp_path, as_user=True)
    client = FilesClient()
    client.users = svc._user_client.users
    client.conversations = svc._user_client.conversations
    svc._user_client = client
    svc.share_poll_s = 0.001
    return svc, bus, client


def test_send_with_files_opens_the_messenger_then_uploads_once(tmp_path: Path) -> None:
    svc, bus, client = service(tmp_path)
    shown: list[tuple[str, int]] = []
    asked: list[list[str]] = []

    async def show(view: str) -> dict[str, Any]:
        shown.append((view, len(bus.events)))  # how many events were out when the messenger opened
        return {"ok": True, "view": view}

    async def attach(kinds: list[str]) -> list[Attachment]:
        asked.append(kinds)
        return files_on_disk(tmp_path)

    async def run() -> list[dict[str, Any]]:
        return [
            await mtools.call_tool("messages_send", {"text": "Here are the rocket files for the supplier",
                                                     "channel": "Jane", "attach": ["all"]}, svc, attach=attach, show=show),
            {"uploads_before_confirm": list(client.uploads)},
            await mtools.call_tool("messages_edit_draft", {"text": "Jane, can you send these to the supplier today?"},
                                   svc, attach=attach, show=show),
            await mtools.call_tool("messages_confirm", {"send": True}, svc, attach=attach, show=show),
        ]

    drafted, before, edited, sent = asyncio.run(run())
    assert asked == [["all"]] and shown == [("messages", 0)]  # the messenger opened before the draft went out
    assert drafted["status"] == "awaiting_confirmation" and drafted["channel_name"] == "Jane Doe"
    assert drafted["files"] == ["rocket_v7.step", "rocket_v7_drawing.pdf"]
    assert before == {"uploads_before_confirm": []}  # nothing leaves before the confirm

    card = bus.events[0][1].model_dump()
    assert [a["name"] for a in card["attachments"]] == drafted["files"]
    assert all("path" not in a for a in card["attachments"])  # Unity never sees the hub's paths

    assert edited["draft_id"] == drafted["draft_id"] and edited["files"] == drafted["files"]
    assert bus.events[1][1].edited is True and bus.events[1][1].text.startswith("Jane, can you")

    assert sent["status"] == "sent" and sent["files"] == drafted["files"] and sent["ts"] == "1700000200.000100"
    assert client.posted == []  # one upload carries the text: no separate chat.postMessage
    [upload] = client.uploads
    assert upload["channel"] == "DU2" and upload["initial_comment"].startswith("Jane, can you")
    assert [(f["filename"], Path(f["file"]).name) for f in upload["file_uploads"]] == \
        [("rocket_v7.step", "rocket_v7.step"), ("rocket_v7_drawing.pdf", "rocket_v7_drawing.pdf")]
    assert "thread_ts" not in upload
    assert [t for t, _ in bus.events] == ["notify.draft", "notify.draft", "notify.sent"]


def test_no_files_no_messenger_unless_asked(tmp_path: Path) -> None:
    svc, bus, client = service(tmp_path)
    shown: list[str] = []

    async def show(view: str) -> dict[str, Any]:
        shown.append(view)
        return {"error": "The user isn't at their desk"}

    async def attach(kinds: list[str]) -> list[Attachment]:
        return files_on_disk(tmp_path)

    async def run() -> list[dict[str, Any]]:
        return [
            await mtools.call_tool("messages_send", {"text": "on it", "channel": "#forge-team"}, svc, attach=attach, show=show),
            await mtools.call_tool("messages_send", {"text": "files", "channel": "#forge-team", "attach": ["step"],
                                                     "open_messenger": False}, svc, attach=attach, show=show),
            await mtools.call_tool("messages_send", {"text": "look", "channel": "#forge-team", "open_messenger": True},
                                   svc, attach=attach, show=show),
        ]

    plain, quiet, asked = asyncio.run(run())
    assert shown == ["messages"]  # only the explicit ask
    assert "view_error" not in plain and "view_error" not in quiet and quiet["files"]
    assert asked["view_error"] == "The user isn't at their desk" and asked["status"] == "awaiting_confirmation"


def test_attach_problems_are_errors_before_any_draft(tmp_path: Path) -> None:
    svc, bus, _ = service(tmp_path)

    async def broken(kinds: list[str]) -> list[Attachment]:
        raise LookupError("v3 was built from an older part script")

    async def run() -> list[dict[str, Any]]:
        return [await mtools.call_tool("messages_send", {"text": "x", "attach": ["all"]}, svc, attach=broken),
                await mtools.call_tool("messages_send", {"text": "x", "attach": ["all"]}, svc)]

    failed, unavailable = asyncio.run(run())
    assert "older part script" in failed["error"] and "isn't available" in unavailable["error"]
    assert bus.events == [] and svc.open_draft is None


def test_refused_upload_keeps_the_draft_for_a_retry(tmp_path: Path) -> None:
    svc, bus, client = service(tmp_path)
    client.upload_error = SlackApiError("missing_scope")

    async def run() -> tuple[dict[str, Any], dict[str, Any], dict[str, Any]]:
        draft = await svc.create_draft("files for you", "Jane", attachments=files_on_disk(tmp_path))
        refused = await mtools.call_tool("messages_confirm", {"send": True}, svc)
        still = svc.open_draft
        client.upload_error = None  # the user fixed the scope
        retried = await mtools.call_tool("messages_confirm", {"send": True}, svc)
        return {"id": draft.draft_id}, refused, {"still": still.draft_id if still else None, **retried}

    draft, refused, retried = asyncio.run(run())
    assert FILES_SCOPE_HELP in refused["error"]
    assert retried["still"] == draft["id"] and retried["status"] == "sent"
    assert [t for t, _ in bus.events] == ["notify.draft", "notify.sent"]  # no cancel in between


def test_only_a_clear_refusal_reopens_the_draft(tmp_path: Path) -> None:
    """Slack's error answer: nothing was posted, so Send may try again. A timeout may have posted it: the draft goes,
    so a second "send it" can't post it twice."""
    svc, bus, client = service(tmp_path)

    async def failing(exc: Exception) -> Any:
        raise exc

    async def run() -> tuple[Any, Any]:
        await svc.create_draft("plain text", "#forge-team")
        client.chat_postMessage = lambda **kw: failing(SlackApiError("channel_not_found"))  # type: ignore[method-assign]
        with pytest.raises(SlackApiError):
            await svc.send_draft()
        refused_kept = svc.open_draft
        client.chat_postMessage = lambda **kw: failing(asyncio.TimeoutError())  # type: ignore[method-assign]
        with pytest.raises(asyncio.TimeoutError):
            await svc.send_draft()
        return refused_kept, svc.open_draft

    kept, after_timeout = asyncio.run(run())
    assert kept is not None and after_timeout is None


def test_a_cancelled_caller_still_finishes_a_sent_message(tmp_path: Path) -> None:
    """Once the upload is through, notify.sent goes out even if the voice turn waiting on it is cancelled."""
    svc, bus, client = service(tmp_path)
    client.shared_after = 3
    svc.share_poll_s = 0.05

    async def run() -> None:
        await svc.create_draft("files", "Jane", attachments=files_on_disk(tmp_path))
        send = asyncio.ensure_future(svc.send_draft())
        while not client.uploads:
            await asyncio.sleep(0.005)
        send.cancel()  # mid files.info polling
        await asyncio.sleep(0.4)

    asyncio.run(run())
    assert svc.open_draft is None  # not reopened: it was posted
    assert bus.last("notify.sent").ts == "1700000200.000100"


def test_missing_file_and_a_ts_that_never_shows(tmp_path: Path) -> None:
    svc, bus, client = service(tmp_path)
    client.shared_after = 99  # Slack never reports the share in time

    async def run() -> tuple[Any, Any]:
        files = files_on_disk(tmp_path)
        await svc.create_draft("files", "Jane", attachments=files)
        Path(files[0].path).unlink()
        with pytest.raises(FileNotFoundError, match="rocket_v7.step"):
            await svc.send_draft()
        assert svc.open_draft is not None  # still open
        await svc.create_draft("files again", "Jane", attachments=files_on_disk(tmp_path))
        return await svc.send_draft()

    draft, ts = asyncio.run(run())
    assert ts == "" and client.uploads and bus.last("notify.sent").ts == ""


def test_reply_in_thread_with_files(tmp_path: Path) -> None:
    svc, bus, client = service(tmp_path)
    svc.store.add(msg("1700000010.000100", text="can you send the drawings?"))

    async def run() -> None:
        await svc.create_draft("here you go", reply_to_ts="1700000010.000100", attachments=files_on_disk(tmp_path))
        await svc.send_draft()

    asyncio.run(run())
    assert client.uploads[0]["thread_ts"] == "1700000010.000100" and client.uploads[0]["channel"] == "C1"


def test_edit_without_a_draft_and_tool_shape(tmp_path: Path) -> None:
    svc, _, _ = service(tmp_path)
    out = asyncio.run(mtools.call_tool("messages_edit_draft", {"text": "hello"}, svc))
    assert "no open draft" in out["error"]
    send = next(t for t in mtools.TOOLS if t["name"] == "messages_send")
    assert send["parameters"]["properties"]["attach"]["items"]["enum"] == ["all", "step", "stl", "drawing", "parts"]
    assert "messages_edit_draft" in send["description"]
