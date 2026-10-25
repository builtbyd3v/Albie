"""Neural Net Lab: Albie's nn_* tools send ml.nn_command and answer from the state the headset posts back."""

import asyncio
from types import SimpleNamespace

from fastapi.testclient import TestClient

from ml import nn_lab
from ml.tests.test_api import make_app


def headset_state(seq: int, disabled: set[str], before: float | None = None) -> dict:
    """What Unity's NeuralLabSim posts: two layers [4, 2] on the circle; L1N3 is the key neuron."""
    neurons = [{"id": f"L{l}N{i}", "layer": l, "index": i, "enabled": f"L{l}N{i}" not in disabled,
                "detects": "distance from the center (correlation 0.95)" if (l, i) == (2, 1) else "left vs right (x)",
                "test_accuracy_if_toggled": 0.684 if (l, i) == (1, 3) else 0.95}
               for l, n in ((1, 4), (2, 2)) for i in range(1, n + 1)]
    acc = 0.684 if "L1N3" in disabled else 0.996
    history = [] if before is None else [{"action": "neuron_off", "target": "L1N3", "epoch": 840,
                                          "test_accuracy_before": before, "test_accuracy_after": acc}]
    return {"applied_seq": seq, "playing": False, "epoch": 840, "dataset": "circle", "activation": "tanh",
            "learning_rate": 0.03, "features": ["x", "y"], "hidden": [4, 2], "train_accuracy": acc,
            "test_accuracy": acc, "train_loss": 0.01, "test_loss": 0.02, "neurons": neurons, "history": history,
            "panel_open": True}


class Headset:
    """Stands in for Unity: applies each ml.nn_command and reports, like NeuralLabSim.OnCommand."""

    def __init__(self) -> None:
        self.commands: list[dict] = []
        self.lab: nn_lab.NNLab | None = None
        self.disabled: set[str] = set()

    async def publish(self, type_: str, payload: dict) -> None:
        assert type_ == "ml.nn_command"
        self.commands.append(payload)
        before = None
        if payload.get("neuron"):
            before = 0.996
            (self.disabled.discard if payload["enabled"] else self.disabled.add)(payload["neuron"])
        asyncio.get_running_loop().call_later(0.05, self.lab.report, headset_state(payload["seq"], self.disabled, before))


def run(coro):
    return asyncio.run(coro)


def test_switching_off_a_neuron_answers_with_accuracy_before_and_after():
    async def flow():
        headset = Headset()
        state = SimpleNamespace(bus=headset)
        headset.lab = nn_lab.lab(state)
        out = await nn_lab.call_tool("nn_neuron", {"neuron": "l1n3", "enabled": False}, state)
        assert headset.commands[-1] == {"seq": 1, "neuron": "L1N3", "enabled": False}
        assert out["test_accuracy"] == 0.684 and out["switched_off"] == ["L1N3"]
        assert out["last_change"]["test_accuracy_before"] == 0.996 and out["last_change"]["test_accuracy_after"] == 0.684
        status = await nn_lab.call_tool("nn_status", {}, state)
        assert status["most_important"] == "L1N1"  # L1N3 is off; the rest tie at 0.95, the first wins
        assert any("distance from the center" in n["detects"] for n in status["neurons"])
        back = await nn_lab.call_tool("nn_neuron", {"neuron": "L1N3", "enabled": True}, state)
        assert back["test_accuracy"] == 0.996 and back["switched_off"] == [] and back["most_important"] == "L1N3"
    run(flow())


def test_control_sends_only_what_was_asked_and_rejects_empty_or_unknown():
    async def flow():
        headset = Headset()
        state = SimpleNamespace(bus=headset)
        headset.lab = nn_lab.lab(state)
        await nn_lab.call_tool("nn_control", {"dataset": "spiral", "hidden": [6, 6, 4], "action": "play"}, state)
        assert headset.commands[-1] == {"seq": 1, "action": "play", "dataset": "spiral", "hidden": [6, 6, 4]}
        assert "error" in await nn_lab.call_tool("nn_control", {}, state)
        assert "no neuron L9N1" in (await nn_lab.call_tool("nn_neuron", {"neuron": "L9N1", "enabled": False}, state))["error"]
        assert "L<layer>N<index>" in (await nn_lab.call_tool("nn_neuron", {"neuron": "three", "enabled": False}, state))["error"]
    run(flow())


def test_without_a_headset_albie_says_so(monkeypatch):
    monkeypatch.setattr(nn_lab, "WAIT_S", 0.2)

    async def flow():
        published = []

        class Bus:
            async def publish(self, type_, payload):
                published.append(payload)

        state = SimpleNamespace(bus=Bus())
        assert "no headset has reported" in (await nn_lab.call_tool("nn_open", {}, state))["error"]
        assert "no headset has reported" in (await nn_lab.call_tool("nn_status", {}, state))["error"]
        assert published == [{"seq": 1, "view": "lab"}]
    run(flow())


def test_state_route_feeds_the_tools(tmp_path):
    with TestClient(make_app(tmp_path)) as client:
        assert "error" in client.get("/ml/nn/state").json()
        assert client.post("/ml/nn/state", json=headset_state(4, set())).json() == {"ok": True}
        got = client.get("/ml/nn/state").json()
        assert got["test_accuracy"] == 0.996 and got["most_important"] == "L1N3" and got["seconds_ago"] >= 0


class SpiralHeadset:
    """A spiral that's stuck at 58% with x, y [4, 2] and reaches 99% once the recipe's inputs and layers arrive."""

    def __init__(self) -> None:
        self.commands: list[dict] = []
        self.published: list[tuple[str, dict]] = []
        self.lab: nn_lab.NNLab | None = None
        self.config = {"dataset": "spiral", "features": ["x", "y"], "hidden": [4, 2], "activation": "tanh",
                       "learning_rate": 0.03}

    def state(self, seq: int) -> dict:
        good = self.config["features"] == nn_lab.RECIPES["spiral"]["features"] and self.config["hidden"] == [8, 8]
        neurons = [{"id": f"L{l}N{i}", "enabled": True, "detects": "a mix of patterns", "test_accuracy_if_toggled": 0.5}
                   for l, n in enumerate(self.config["hidden"], 1) for i in range(1, n + 1)]
        return {"applied_seq": seq, **self.config, "playing": False, "epoch": 300, "neurons": neurons, "history": [],
                "test_accuracy": 0.99 if good else 0.58, "train_accuracy": 0.99 if good else 0.6}

    async def publish(self, type_: str, payload: dict) -> None:
        self.published.append((type_, payload))
        if type_ != "ml.nn_command":
            return
        self.commands.append(payload)
        self.config.update({k: payload[k] for k in ("features", "hidden", "activation", "learning_rate") if k in payload})
        asyncio.get_running_loop().call_later(0.02, self.lab.report, self.state(payload["seq"]))


def test_fix_it_diagnoses_the_spiral_applies_the_recipe_and_says_the_new_accuracy(monkeypatch):
    monkeypatch.setattr(nn_lab, "SETTLE_S", 0.3)
    monkeypatch.setattr(nn_lab, "SETTLE_MAX_S", 6.0)

    async def flow():
        headset, said = SpiralHeadset(), []
        state = SimpleNamespace(bus=headset)

        async def speak(pet_id, text):
            said.append(text)
            return True

        headset.lab = nn_lab.lab(state, speak)
        headset.lab.report(headset.state(0))
        out = await nn_lab.call_tool("nn_improve", {}, state)
        assert out["diagnosis"].startswith("It's at 58% because it only sees x, y; the spiral needs x², y², sin x, sin y")
        assert out["changes"] == ["inputs x, y, x², y², sin x, sin y", "hidden layers [8, 8]"]
        card = [p for t, p in headset.published if t == "pet.say"][-1]["card"]
        assert [a["action_id"] for a in card["actions"]] == ["nn:apply:1", "nn:dismiss:1"]
        assert not headset.commands  # nothing changes before the yes

        assert await headset.lab.card_action("nn:apply:1")  # the tap on Apply
        assert headset.commands[0] == {"seq": 1, "action": "play", "auto_pause": False, "hidden": [8, 8],
                                       "features": nn_lab.RECIPES["spiral"]["features"], "activation": "tanh",
                                       "learning_rate": 0.03}
        assert not await headset.lab.card_action("nn:apply:1")  # spent
        for _ in range(100):
            if any(t.startswith("Done!") for t in said):
                break
            await asyncio.sleep(0.1)
        assert said[-1] == "Done! Test accuracy went from 58% to 99%."
        assert headset.commands[-1]["action"] == "pause"
        again = await nn_lab.call_tool("nn_improve", {}, state)
        assert again["changes"] == [] and "nothing to fix" in again["diagnosis"]
    run(flow())


def test_diagnose_names_switched_off_neurons_linear_and_a_wild_learning_rate():
    state = {"dataset": "circle", "features": ["x", "y"], "hidden": [4], "activation": "linear", "learning_rate": 1,
             "test_accuracy": 0.6, "neurons": [{"id": "L1N2", "enabled": False}]}
    out = nn_lab.diagnose(state)
    assert "1 neuron is switched off (L1N2)" in out["diagnosis"] and "linear activation" in out["diagnosis"]
    assert "learning rate (1)" in out["diagnosis"] and "every neuron back on" in out["changes"]
    assert out["fix"]["hidden"] == [4] and out["fix"]["activation"] == "tanh"


def test_open_and_close_switch_the_ml_desk_and_say_when_the_user_isnt_at_the_desk():
    async def flow():
        class Desk:
            seated, view = True, "closed"

            async def publish(self, type_, payload):
                if self.seated and payload.get("view"):
                    self.view = payload["view"]
                state = {**headset_state(payload["seq"], set()), "view": self.view}
                asyncio.get_running_loop().call_later(0.02, lab.report, state)

        desk = Desk()
        state = SimpleNamespace(bus=desk)
        lab = nn_lab.lab(state)
        assert (await nn_lab.call_tool("nn_open", {}, state))["view"] == "lab"
        assert (await nn_lab.call_tool("nn_close", {}, state))["view"] == "project"
        desk.seated, desk.view = False, "closed"
        assert "sit at the desk" in (await nn_lab.call_tool("nn_open", {}, state))["error"]
    run(flow())


def test_a_stalled_play_makes_albie_offer_the_fix_once():
    async def flow():
        headset, said, notes = SpiralHeadset(), [], []
        state = SimpleNamespace(bus=headset)

        async def speak(pet_id, text):
            said.append(text)
            return True

        async def note(pet_id, text):
            notes.append(text)
            return True

        headset.lab = lab = nn_lab.lab(state, speak, note)
        stalled = {**headset.state(0), "stalled_at_epoch": 210, "playing": False}
        lab.report(stalled)
        for _ in range(50):
            if said:
                break
            await asyncio.sleep(0.02)
        assert said == ["It's stuck at 58% because it only sees x, y; the spiral needs x², y², sin x, sin y as inputs "
                        "to bend around; layers [4, 2] are too small for the spiral pattern. Want me to fix it?"]
        card = [p for t, p in headset.published if t == "pet.say"][-1]
        assert card["text"] == said[0] and card["card"]["actions"][0]["action_id"] == "nn:apply:1"
        assert notes and "nn_apply" in notes[0]
        lab.report(stalled)  # the same stall reported again: no second offer
        await asyncio.sleep(0.1)
        assert len(said) == 1
        assert (await nn_lab.call_tool("nn_apply", {}, state))["applied"]
    run(flow())
