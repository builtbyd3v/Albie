"""Albie's ui_highlight tool (agent/ui_tools.py). The ML voice tools moved to #51's ml_* tools (ml/tools.py)."""
import asyncio

from agent import ui_tools

class FakeBus:
    def __init__(self):
        self.sent = []

    async def publish(self, type_, payload):
        self.sent.append((type_, payload if isinstance(payload, dict) else payload.model_dump()))


def run(coro):
    return asyncio.run(coro)


def ui(name, args, bus):
    return run(ui_tools.call_tool(name, args, bus))


def test_highlight_broadcasts_the_existing_event():
    bus = FakeBus()
    assert ui("ui_highlight", {"panel": "ml-results", "item_ids": ["val_0003"]}, bus)["ok"]
    assert ui("ui_highlight", {"panel": "ml-results", "class_idx": 3}, bus)["ok"]
    assert bus.sent == [("ui.highlight", {"panel": "ml-results", "item_ids": ["val_0003"], "class_idx": None}),
                        ("ui.highlight", {"panel": "ml-results", "item_ids": None, "class_idx": 3})]


def test_highlight_rejects_bad_input():
    bus = FakeBus()
    assert "error" in ui("ui_highlight", {"panel": "nowhere"}, bus)
    assert "error" in ui("ui_highlight", {"panel": "ml-results", "item_ids": "val_1"}, bus)
    assert "error" in ui("ui_highlight", {"panel": "ml-results", "class_idx": "cat"}, bus)
    assert "error" in ui("ui_nope", {}, bus)
    assert bus.sent == []

