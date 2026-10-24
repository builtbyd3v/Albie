import asyncio
import copy
import json
from types import SimpleNamespace as NS

from fastapi import FastAPI
from fastapi.testclient import TestClient

from agent.brain import Brain
from agent.router import router

TOOLS = [{"name": "cad_set_params", "description": "Change the rocket.",
          "parameters": {"type": "object", "properties": {"changes": {"type": "object"}}}}]


def tool_use(id_, name, input_):
    return NS(stop_reason="tool_use", content=[NS(type="tool_use", id=id_, name=name, input=input_)])


def text(t):
    return NS(stop_reason="end_turn", content=[NS(type="text", text=t)])


class FakeClient:
    """Stands in for AsyncAnthropic: replays canned responses, records each request."""

    def __init__(self, *responses):
        self.responses, self.requests = list(responses), []
        self.beta = NS(messages=NS(create=self.create))

    async def create(self, **kwargs):
        self.requests.append(copy.deepcopy(kwargs))
        return self.responses.pop(0)


class FakeBus:
    def __init__(self, log):
        self.log = log

    async def publish(self, type_, payload):
        self.log.append((type_, payload.model_dump() if hasattr(payload, "model_dump") else payload))


def make(*responses, tool=None):
    log = []

    async def call_tool(name, args):
        log.append(("tool", name, args))
        return tool(name, args) if tool else {"version": 2}

    client = FakeClient(*responses)
    return Brain(FakeBus(log), TOOLS, call_tool, client=client), client, log


def test_tool_round_trip():
    brain, client, log = make(tool_use("t1", "cad_set_params", {"changes": {"fin_count": 4}}), text("Done!"))

    reply = asyncio.run(brain.reply("bit", "add a fourth fin"))

    assert reply == "Done!"
    assert client.requests[0]["tools"][0]["input_schema"] == TOOLS[0]["parameters"]
    result = client.requests[1]["messages"][-1]["content"][0]
    assert result["type"] == "tool_result" and result["tool_use_id"] == "t1"
    assert json.loads(result["content"]) == {"version": 2}
    # Bit starts moving before the work happens, and speaks at the end.
    working = log.index(("pet.state", {"pet_id": "bit", "mode": "working", "target_panel": "cad-viewer"}))
    assert working < log.index(("tool", "cad_set_params", {"changes": {"fin_count": 4}}))
    assert ("pet.say", {"pet_id": "bit", "text": "Done!", "card": None}) in log


def test_tool_crash_is_reported_to_the_model():
    def boom(name, args):
        raise RuntimeError("worker died")

    brain, client, _ = make(tool_use("t1", "cad_set_params", {}), text("Sorry, that broke."), tool=boom)

    assert asyncio.run(brain.reply("bit", "go")) == "Sorry, that broke."
    result = client.requests[1]["messages"][-1]["content"][0]
    assert result["is_error"] is True and "worker died" in result["content"]


def test_history_carries_over_between_turns():
    brain, client, _ = make(text("Hi!"), text("Still here."))

    asyncio.run(brain.reply("bit", "hello"))
    asyncio.run(brain.reply("bit", "you there?"))

    roles = [m["role"] for m in client.requests[1]["messages"]]
    assert roles == ["user", "assistant", "user"]


def test_refusal_after_a_tool_step_rolls_back_the_whole_turn():
    brain, _, _ = make(tool_use("t1", "cad_set_params", {}), NS(stop_reason="refusal", content=[]))

    assert asyncio.run(brain.reply("bit", "...")) != ""
    assert brain.messages == []  # no orphan tool_use left to 400 the next request


def test_api_error_rolls_back_and_unfreezes_bit():
    brain, client, log = make()

    async def fail(**kwargs):
        raise ConnectionError("network down")

    client.beta.messages.create = fail
    try:
        asyncio.run(brain.reply("bit", "hello"))
    except ConnectionError:
        pass
    assert brain.messages == []
    assert log[-1] == ("pet.state", {"pet_id": "bit", "mode": "idle", "target_panel": None})


def test_router_rejects_empty_text():
    app = FastAPI()
    app.include_router(router)
    app.state.brain, _, _ = make(text("ok"))
    with TestClient(app) as c:
        assert c.post("/agent/text", json={"pet_id": "bit", "text": ""}).status_code == 422
        assert c.post("/agent/text", json={"pet_id": "bit", "text": "hi"}).json() == {"reply": "ok"}
