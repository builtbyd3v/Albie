import asyncio

from vscode import live
from vscode.tests.fake_extension import fake_extension  # noqa: F401 - pytest fixture

TEXT = "a = 1\nb = 2\nc = 3\nd = 4\n"


def test_edit_glows_the_new_lines():
    assert live.changed_lines("Edit", {"new_string": "b = 2\nc = 3"}, TEXT) == (2, 3)


def test_trailing_newline_does_not_add_a_line():
    assert live.changed_lines("Edit", {"new_string": "c = 3\n"}, TEXT) == (3, 3)


def test_multiedit_uses_the_first_edit():
    assert live.changed_lines("MultiEdit", {"edits": [{"new_string": "d = 4"}, {"new_string": "a = 1"}]}, TEXT) == (4, 4)


def test_write_glows_the_top_of_the_file():
    assert live.changed_lines("Write", {"content": TEXT}, TEXT) == (1, 4)


def test_deleted_text_has_nothing_to_glow():
    assert live.changed_lines("Edit", {"new_string": ""}, TEXT) is None
    assert live.changed_lines("Edit", {"new_string": "gone"}, TEXT) is None


def test_edited_opens_and_glows_in_vscode(tmp_path, fake_extension):
    f = tmp_path / "rocket.py"
    f.write_text(TEXT)
    asyncio.run(live.opening("Edit", {"file_path": str(f), "old_string": "c = 3"}))
    asyncio.run(live.edited("Edit", {"file_path": str(f), "new_string": "c = 3"}))
    assert fake_extension[0] == ("POST", "/open", {"path": str(f), "line": 3})
    assert fake_extension[1][1] == "/highlight" and fake_extension[1][2]["start"] == 3


def test_vscode_closed_never_raises(tmp_path, monkeypatch):
    from vscode import tools
    monkeypatch.setattr(tools, "BASE", "http://127.0.0.1:9")
    f = tmp_path / "x.py"
    f.write_text(TEXT)
    asyncio.run(live.opening("Edit", {"file_path": str(f), "old_string": "a = 1"}))
    asyncio.run(live.edited("Edit", {"file_path": str(f), "new_string": "a = 1"}))
