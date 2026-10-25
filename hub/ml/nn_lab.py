"""Neural Net Lab (TensorFlow Playground on the ML desk): Albie's tools and the state the headset reports.

Unity's NeuralLabSim trains the small network on the headset. It applies `ml.nn_command` events and posts its
state to `POST /ml/nn/state`: accuracy and loss, and for each hidden neuron its id (L1N3 = layer 1, neuron 3
from the top), what it detects and the test accuracy if it were switched the other way. A tool sends one
command and waits for the state that includes it (`applied_seq`), so Albie answers with real numbers.

When Play stalls (the headset's `stalled_at_epoch`: the default spiral on a small network stops around 55-60%),
Albie offers the fix himself: he says what's wrong and the Apply card pops up.
"Albie, fix it" (nn_improve → Apply card or a spoken yes → nn_apply) swaps in a measured recipe for the dataset
(RECIPES: inputs, layers, activation, learning rate) and trains it live, slowly enough to watch it learn. From a
stuck network it climbs for 4-15 s and the headset auto-pauses it at 92-94%; from one that already stopped at 92%
it trains on until it settles near 100%. Albie says the new test accuracy either way.
"""
from __future__ import annotations

import asyncio
import logging
import time
from collections.abc import Awaitable, Callable
from typing import Any

from agent import events as ev

log = logging.getLogger(__name__)
Speak = Callable[[str, str], Awaitable[bool]]
PET_ID = "bit"
SETTLE_S, SETTLE_MAX_S = 3.0, 40.0  # announce once test accuracy stops climbing for 3 s (or after 40 s)
AUTO_PAUSE = 0.92  # NeuralLabSim.AutoPauseAccuracy

WAIT_S = 3.0
GOOD = 0.98  # at or above this there's nothing to fix; below it, Play stopping by itself brings Albie's offer
DATASETS = ["circle", "xor", "gauss", "spiral"]
ACTIVATIONS = ["tanh", "relu", "sigmoid", "linear"]
FEATURES = ["x", "y", "x2", "y2", "xy", "sinx", "siny"]
NO_HEADSET = "The Neural Net Lab runs in the headset, and no headset has reported yet. Is Unity connected to the hub?"

# Measured in Unity (ML/Editor/NeuralLabRecipeCheck.cs Pace/PaceRow/Matrix: the Lab's own network and data, 30
# epochs a second, seeds 1-4). The rates are small on purpose: at 0.03 the circle and XOR jump to 92% in one or two
# epochs, which looks fake; at these rates the boundary visibly bends into shape.
#   spiral  x, y [4, 2]: stuck at 52-60%  ->  x, y, x², y², sin x, sin y [8, 8] 0.005: 92% after 9-15 s, ~99% at 20 s
#   circle  sigmoid/linear: stuck ~65%    ->  x, y, x², y² [4] 0.0003: 92% after 3-13 s, ~100% at 30 s
#   xor     sigmoid/linear: stuck ~62%    ->  x, y, x·y [4, 2] 0.0001: 92% after 4-13 s, 98-100% at 30 s
#   gauss   x, y [4, 2] tanh: 100% already;  sigmoid: 93%  ->  tanh 0.0001: 92% after ~4 s, 100% at 10 s
RECIPES: dict[str, dict[str, Any]] = {
    "spiral": {"features": ["x", "y", "x2", "y2", "sinx", "siny"], "hidden": [8, 8], "activation": "tanh",
               "learning_rate": 0.005, "expected": "about 92% test accuracy after 10 to 15 seconds of training"},
    "circle": {"features": ["x", "y", "x2", "y2"], "hidden": [4], "activation": "tanh", "learning_rate": 0.0003,
               "expected": "about 92% test accuracy after 5 to 10 seconds of training"},
    "xor": {"features": ["x", "y", "xy"], "hidden": [4, 2], "activation": "tanh", "learning_rate": 0.0001,
            "expected": "about 92% test accuracy after 5 to 10 seconds of training"},
    "gauss": {"features": ["x", "y"], "hidden": [4, 2], "activation": "tanh", "learning_rate": 0.0001,
              "expected": "about 92% test accuracy after a few seconds of training"},
}
FEATURE_NAMES = {"x": "x", "y": "y", "x2": "x²", "y2": "y²", "xy": "x·y", "sinx": "sin x", "siny": "sin y"}

TOOLS: list[dict[str, Any]] = [
    {"name": "nn_open",
     "description": "Show the Neural Net Lab on the ML desk: a small neural network learning a 2D dataset live, "
                    "TensorFlow Playground style (decision boundary, every neuron's picture, accuracy). Use it for "
                    "'show me the neural network', 'open the playground'.",
     "parameters": {"type": "object", "properties": {}}},
    {"name": "nn_close",
     "description": "Switch the ML desk from the Neural Net Lab back to the ML project's panels (training "
                    "dashboard, dataset, model, results). Use it for 'back to my ML project', 'show the training "
                    "dashboard', 'close the neural net'.",
     "parameters": {"type": "object", "properties": {}}},
    {"name": "nn_status",
     "description": "The Neural Net Lab right now: dataset, layers, activation, learning rate, epoch, train and test "
                    "accuracy and loss; each neuron's id (L1N3 = layer 1, neuron 3 from the top), whether it's on, "
                    "what it detects, and test_accuracy_if_toggled (how much it matters); and the last changes with "
                    "accuracy before and after. Use it to answer 'why did the accuracy drop?' or 'what does this "
                    "neuron do?': name the neuron, what it detected, and the before/after numbers.",
     "parameters": {"type": "object", "properties": {}}},
    {"name": "nn_control",
     "description": "Change the Neural Net Lab: play / pause / step one epoch / reset the weights / restore_all "
                    "(switch every neuron back on), the dataset, the activation, the learning rate, the hidden "
                    "layers (e.g. [4, 2] = two layers of 4 and 2 neurons, at most 6 layers of 8) and the input "
                    "features. Changing the data, layers, activation or features starts training over.",
     "parameters": {"type": "object", "properties": {
         "action": {"type": "string", "enum": ["play", "pause", "step", "reset", "restore_all"]},
         "dataset": {"type": "string", "enum": DATASETS},
         "activation": {"type": "string", "enum": ACTIVATIONS},
         "learning_rate": {"type": "number", "description": "0.001 to 1 (default 0.03)"},
         "hidden": {"type": "array", "items": {"type": "integer"}, "description": "neurons per hidden layer"},
         "features": {"type": "array", "items": {"type": "string", "enum": FEATURES},
                      "description": "inputs: x, y, x2 (x²), y2, xy (x·y), sinx, siny"},
         "noise": {"type": "number", "description": "0 to 0.5"}}}},
    {"name": "nn_neuron",
     "description": "Switch one hidden neuron of the Neural Net Lab off (enabled false) or back on. The accuracy "
                    "changes at once; the result has it before and after. 'Turn off neuron 3 in layer 1' is L1N3.",
     "parameters": {"type": "object", "properties": {
         "neuron": {"type": "string", "description": "L<layer>N<index>, e.g. L1N3"},
         "enabled": {"type": "boolean"}},
         "required": ["neuron", "enabled"]}},
    {"name": "nn_improve",
     "description": "The user asks how to make the Neural Net Lab's network better ('why is it bad?', 'fix it', 'what "
                    "can I do better?'). Returns what's wrong (the diagnosis) and the changes that fix it; say them "
                    "briefly and ask whether to apply. An Apply card also pops up. Nothing changes until nn_apply.",
     "parameters": {"type": "object", "properties": {}}},
    {"name": "nn_apply",
     "description": "Only after the user says yes to the nn_improve proposal: apply it and train live. The new test "
                    "accuracy is spoken when it settles (about 10 seconds).",
     "parameters": {"type": "object", "properties": {}}},
]


class NNLab:
    def __init__(self, bus: Any, speak: Speak | None = None, note: Speak | None = None) -> None:
        self.bus, self.speak, self.note = bus, speak, note
        self.offered_stall: int | None = None  # the stop Albie last offered a fix for
        self.fixing = False  # Albie's fix is training: its own stop isn't a new reason to offer
        self.state: dict | None = None
        self.received = 0.0
        self.seq = 0
        self.changed = asyncio.Event()
        self.proposal: dict | None = None  # nn_improve's fix, waiting for a yes
        self.offer = 0  # the live Apply card's number (a tap on an older card does nothing)
        self.jobs: set[asyncio.Task[None]] = set()

    def report(self, state: dict) -> None:
        """POST /ml/nn/state: the headset's latest state. Play stopping by itself short of GOOD (stuck, or
        auto-paused at 92%) makes Albie offer his fix, once per stop."""
        self.state, self.received = state, time.time()
        self.changed.set()
        stalled, paused = state.get("stalled_at_epoch"), state.get("auto_paused_at_epoch")
        if state.get("playing") or (stalled is None and paused is None):
            return
        stop = state.get("stop_count", stalled if stalled is not None else paused)  # older headsets: the epoch
        if stop == self.offered_stall:
            return
        self.offered_stall = stop
        if self.fixing:
            return
        if state.get("test_accuracy", 0.0) >= GOOD:
            return
        task = asyncio.get_running_loop().create_task(self.offer_fix(stalled=stalled is not None))
        self.jobs.add(task)
        task.add_done_callback(self.jobs.discard)

    async def offer_fix(self, stalled: bool = True) -> None:
        """Play stopped: say what's wrong and ask, with the Apply card (as if asked "what can I do better?")."""
        out = await self.improve(spoken=True, stalled=stalled)
        if self.note is not None and self.proposal is not None:
            try:
                await self.note(PET_ID, "You just offered to fix the Neural Net Lab's network; a yes means nn_apply.")
            except Exception:  # noqa: BLE001 - only context for the voice model
                log.exception("Neural Net Lab note failed")

    async def command(self, **fields: Any) -> dict:
        self.seq += 1
        seq = self.seq
        await self.bus.publish("ml.nn_command", {"seq": seq, **{k: v for k, v in fields.items() if v is not None}})
        deadline = time.monotonic() + WAIT_S
        while not (self.state and self.state.get("applied_seq", 0) >= seq):
            left = deadline - time.monotonic()
            if left <= 0:
                return {"error": NO_HEADSET} if self.state is None else \
                    {"error": "The headset didn't confirm the change in time.", **summary(self.state)}
            self.changed.clear()
            try:
                await asyncio.wait_for(self.changed.wait(), left)
            except asyncio.TimeoutError:
                pass
        return summary(self.state)

    def status(self) -> dict:
        if self.state is None:
            return {"error": NO_HEADSET}
        return {**summary(self.state), "seconds_ago": round(time.time() - self.received, 1)}


    # ---- "Albie, fix it" ---------------------------------------------------------------------------------
    async def improve(self, spoken: bool = False, stalled: bool = False) -> dict:
        """Diagnose and, when there's a fix, show the Apply card. spoken: Albie says the diagnosis himself (an
        unprompted offer); otherwise the voice model reads the result and the bubble only asks the question."""
        if self.state is None:
            return {"error": NO_HEADSET}
        out = diagnose(self.state, stalled=stalled)
        if not out.get("fix"):
            self.proposal = None
            return {k: v for k, v in out.items() if k != "fix"}
        self.proposal = {**out["fix"], "before": self.state.get("test_accuracy"), "dataset": self.state.get("dataset")}
        self.offer += 1
        card = ev.Card(title="Apply the fix and retrain?", bullets=out["changes"][:5],
                       actions=[ev.CardAction(label="Apply", action_id=f"nn:apply:{self.offer}"),
                                ev.CardAction(label="Not now", action_id=f"nn:dismiss:{self.offer}")])
        text = f"{out['diagnosis']} Want me to fix it?" if spoken else "Apply the fix and retrain?"
        await self.bus.publish("pet.say", {"pet_id": PET_ID, "text": text, "card": card.model_dump(exclude_none=True)})
        if spoken and self.speak is not None:
            try:
                await self.speak(PET_ID, text)
            except Exception:  # noqa: BLE001 - the caption and card already went out
                log.exception("spoken Neural Net Lab offer failed")
        return {k: v for k, v in out.items() if k != "fix"}

    async def apply(self) -> dict:
        if self.proposal is None:
            return {"error": "There's no fix waiting. Ask me what to improve first."}
        fix, self.proposal, self.offer = self.proposal, None, 0
        fields = {k: fix[k] for k in ("features", "hidden", "activation", "learning_rate") if k in fix}
        # A new shape or inputs rebuilds (every neuron is on again). From a stuck network the headset stops it at 92%
        # by itself; from one that already got there, _announce lets it settle higher and stops it.
        before = fix.get("before")
        to_92 = before is None or before < AUTO_PAUSE
        self.fixing = True
        out = await self.command(**fields, action="play", auto_pause=to_92)
        if "error" in out:
            self.fixing = False
            return out
        task = asyncio.create_task(self._announce(before, to_92))
        self.jobs.add(task)
        task.add_done_callback(self.jobs.discard)
        return {"applied": True, "note": "Training live now; the new test accuracy is spoken when it settles.",
                "expected": fix.get("expected") if to_92 else "close to 100% test accuracy after 20 to 30 seconds"}

    async def card_action(self, action_id: str) -> bool:
        """A tap on the Apply / Not now card ("nn:apply:N" / "nn:dismiss:N"). True when it applied the fix."""
        _, kind, n = action_id.split(":", 2) if action_id.count(":") >= 2 else ("", "", "")
        if not n.isdigit() or int(n) != self.offer:
            return False
        if kind != "apply":
            self.proposal, self.offer = None, 0
            await self.say("Okay, I'll leave the network as it is.")
            return False
        out = await self.apply()
        if "error" in out:
            await self.say(out["error"])
            return False
        await self.say("Applied. Watch the boundary: it's training now.")
        return True

    async def _announce(self, before: float | None, to_92: bool = False) -> None:
        """Wait for the fix to finish training (the headset's auto-pause at 92%, or test accuracy no longer
        climbing), make sure it's paused and say the result."""
        try:
            start = last_gain = time.monotonic()
            best = -1.0
            while time.monotonic() - start < SETTLE_MAX_S:
                await asyncio.sleep(0.5)
                state = self.state or {}
                if to_92 and not state.get("playing", True):
                    break  # auto-paused at 92% (or stuck): the headset stopped it
                acc = state.get("test_accuracy", 0.0)
                if acc > best + 0.005:
                    best, last_gain = acc, time.monotonic()
                elif not to_92 and acc >= GOOD and time.monotonic() - last_gain >= SETTLE_S:
                    # From 92%: wait for 98%+ and no longer climbing (a fresh network starts flat at ~50%, and the
                    # slow recipes creep up for 20-30 s); SETTLE_MAX_S caps it.
                    break
            if (self.state or {}).get("playing"):
                await self.command(action="pause")  # freeze it so people can poke neurons on the trained network
        finally:
            self.fixing = False
        after = (self.state or {}).get("test_accuracy")
        more = " Press Play to keep training, or touch a neuron to see what it does." if to_92 else ""
        if before is not None and after is not None:
            await self.say(f"Done! Test accuracy went from {before:.0%} to {after:.0%}.{more}")
        elif after is not None:
            await self.say(f"Done! Test accuracy is {after:.0%}.{more}")

    async def say(self, text: str) -> None:
        await self.bus.publish("pet.say", {"pet_id": PET_ID, "text": text})
        if self.speak is not None:
            try:
                await self.speak(PET_ID, text)
            except Exception:  # noqa: BLE001 - the caption already went out
                log.exception("spoken Neural Net Lab line failed")


def diagnose(state: dict, stalled: bool = False) -> dict:
    """What's holding the network back, and the measured fix for its dataset (RECIPES)."""
    dataset = state.get("dataset", "circle")
    recipe = RECIPES.get(dataset, RECIPES["circle"])
    acc = state.get("test_accuracy", 0.0)
    off = [n["id"] for n in state.get("neurons", []) if not n.get("enabled", True)]
    findings, changes = [], []
    features = state.get("features", [])
    hidden = state.get("hidden", [])
    if off:
        findings.append(f"{len(off)} neuron{'s are' if len(off) > 1 else ' is'} switched off ({', '.join(off)})")
    activation = state.get("activation")
    if activation == "linear":
        findings.append("a linear activation can only draw straight lines, however many neurons it has")
    elif activation == "sigmoid":
        findings.append("sigmoid neurons only output 0 to 1 and pass back tiny gradients, so they learn very slowly")
    elif activation == "relu" and recipe["activation"] != "relu":
        findings.append(f"ReLU neurons are sharp and can go dead; tanh fits the {dataset} more smoothly")
    if state.get("learning_rate", 0.03) >= 0.3:
        findings.append(f"the learning rate ({state.get('learning_rate')}) is so high the weights jump around")
    missing = [f for f in recipe["features"] if f not in features]
    if missing:
        names = ", ".join(FEATURE_NAMES[f] for f in missing)
        findings.append(f"it only sees {', '.join(FEATURE_NAMES.get(f, f) for f in features)}; the {dataset} needs "
                        f"{names} as inputs to bend around")
    if sum(hidden) < sum(recipe["hidden"]):
        findings.append(f"layers {hidden} are too small for the {dataset} pattern")
    if acc >= GOOD and not off:
        return {"test_accuracy": acc, "diagnosis": f"It's already at {acc:.0%} on the {dataset}; nothing to fix.",
                "changes": []}
    if not findings:
        if not changes_needed(state, recipe):
            return {"test_accuracy": acc, "changes": [],
                    "diagnosis": f"It's at {acc:.0%} on the {dataset} with the best setup I know for it. Press Play "
                                 "to keep training: it creeps up from here."}
        findings.append(f"it's stuck at {acc:.0%}: this setup can't fit the {dataset} pattern")
    if sorted(features) != sorted(recipe["features"]):
        changes.append("inputs " + ", ".join(FEATURE_NAMES[f] for f in recipe["features"]))
    if hidden != recipe["hidden"]:
        changes.append(f"hidden layers {recipe['hidden']}")
    if state.get("activation") != recipe["activation"]:
        changes.append(f"{recipe['activation']} activation")
    if abs(state.get("learning_rate", 0.03) - recipe["learning_rate"]) > 1e-6:
        changes.append(f"learning rate {recipe['learning_rate']}")
    if off:
        changes.append("every neuron back on")
    fix = {k: recipe[k] for k in ("features", "hidden", "activation", "learning_rate", "expected")}
    return {"test_accuracy": acc, "diagnosis": f"It's {'stuck ' if stalled else ''}at {acc:.0%} because " + "; ".join(findings) + ".",
            "changes": changes, "expected": recipe["expected"], "fix": fix}


def changes_needed(state: dict, recipe: dict) -> bool:
    """Whether the network differs from the recipe (or has neurons switched off)."""
    return (sorted(state.get("features", [])) != sorted(recipe["features"]) or state.get("hidden") != recipe["hidden"]
            or state.get("activation") != recipe["activation"]
            or abs(state.get("learning_rate", 0.03) - recipe["learning_rate"]) > 1e-6
            or any(not n.get("enabled", True) for n in state.get("neurons", [])))


def summary(state: dict) -> dict:
    """What Albie reads: the headline numbers, every neuron, and the last change."""
    keep = ("view", "dataset", "hidden", "activation", "learning_rate", "features", "noise", "epoch", "playing", "auto_paused_at_epoch",
            "train_accuracy", "test_accuracy", "train_loss", "test_loss", "panel_open")
    out = {k: state[k] for k in keep if k in state}
    neurons = state.get("neurons", [])
    out["neurons"] = [{k: n.get(k) for k in ("id", "enabled", "detects", "test_accuracy_if_toggled")} for n in neurons]
    out["switched_off"] = [n["id"] for n in neurons if not n.get("enabled", True)]
    if neurons:
        on = [n for n in neurons if n.get("enabled", True)]
        if on:
            key = min(on, key=lambda n: n.get("test_accuracy_if_toggled", 1))
            out["most_important"] = key["id"]
    history = state.get("history") or []
    if history:
        out["last_change"] = history[-1]
        out["recent_changes"] = history[-5:]
    return out


def lab(state: Any, speak: Speak | None = None, note: Speak | None = None) -> NNLab:
    """The app's NNLab (routes and tools), created on first use. main.py passes Albie's voice and context notes."""
    if getattr(state, "ml_nn", None) is None:
        state.ml_nn = NNLab(state.bus, speak, note)
    else:
        state.ml_nn.speak = speak or state.ml_nn.speak
        state.ml_nn.note = note or state.ml_nn.note
    return state.ml_nn


async def call_tool(name: str, args: dict[str, Any], state: Any) -> dict[str, Any]:
    nn = lab(state)
    if name == "nn_status":
        return nn.status()
    if name == "nn_open":
        out = await nn.command(view="lab")
        if "error" not in out and out.get("view") != "lab":
            return {"error": "The Neural Net Lab didn't open: the user has to sit at the desk first.", **out}
        return out
    if name == "nn_close":
        out = await nn.command(view="project")
        if "error" not in out and out.get("view") != "project":
            return {"error": "The ML desk didn't open: the user has to sit at the desk first.", **out}
        return out
    if name == "nn_control":
        fields = {k: args.get(k) for k in ("action", "dataset", "activation", "learning_rate", "hidden", "features", "noise")}
        if not any(v is not None for v in fields.values()):
            return {"error": "Say what to change: an action, the dataset, activation, learning rate, layers or features."}
        return await nn.command(**fields)
    if name == "nn_neuron":
        neuron = str(args.get("neuron") or "").strip().upper()
        if not neuron.startswith("L") or "N" not in neuron[1:]:
            return {"error": "Name the neuron as L<layer>N<index>, e.g. L1N3 for layer 1, neuron 3."}
        out = await nn.command(neuron=neuron, enabled=bool(args.get("enabled")))
        if "error" not in out and not any(n["id"] == neuron for n in out.get("neurons", [])):
            return {"error": f"There's no neuron {neuron}; the layers are {out.get('hidden')}.", **out}
        return out
    if name == "nn_improve":
        return await nn.improve()
    if name == "nn_apply":
        nn.offer = 0  # the spoken yes: the card's Apply button is spent too
        return await nn.apply()
    return {"error": f"Unknown Neural Net Lab tool {name!r}"}
