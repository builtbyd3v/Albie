"""Model graph for `GET /ml/graph`: one entry per leaf module, captured with forward hooks."""

from __future__ import annotations

from typing import Any

import torch
import torch.nn as nn

# Model3D colors blocks by these kinds (PROJECT_CONTEXT 8.4). Unknown modules use their lowercased class name.
LAYER_KIND: dict[type, str] = {
    nn.Conv1d: "conv", nn.Conv2d: "conv", nn.Conv3d: "conv",
    nn.MaxPool1d: "pool", nn.MaxPool2d: "pool", nn.AvgPool2d: "pool",
    nn.AdaptiveAvgPool2d: "pool", nn.AdaptiveMaxPool2d: "pool",
    nn.Linear: "linear",
    nn.BatchNorm1d: "norm", nn.BatchNorm2d: "norm", nn.LayerNorm: "norm", nn.GroupNorm: "norm",
    nn.ReLU: "activation", nn.GELU: "activation", nn.SiLU: "activation", nn.LeakyReLU: "activation",
    nn.Sigmoid: "activation", nn.Tanh: "activation", nn.Softmax: "activation",
    nn.Flatten: "reshape",
}


def _shape(x: Any) -> list[int]:
    """Shape without the batch dim; for tuples/lists, the first tensor."""
    if isinstance(x, (tuple, list)):
        x = next((t for t in x if isinstance(t, torch.Tensor)), None)
    return list(x.shape[1:]) if isinstance(x, torch.Tensor) else []


def describe(model: nn.Module, example: torch.Tensor) -> dict:
    """Run one batch through `model` and return `{layers: [{name, type, in_shape, out_shape, params}]}`.

    `example` is one sample without a batch dim (e.g. `val_dataset[0][0]`); it is moved to the model's
    device. Training mode and gradients are left as they were.
    """
    param = next(model.parameters(), None)
    device = param.device if param is not None else torch.device("cpu")
    example = example.unsqueeze(0).to(device)

    layers: list[dict] = []
    hooks = []
    for name, module in model.named_modules():
        if any(True for _ in module.children()):
            continue

        def hook(mod: nn.Module, inp: tuple, out: Any, name: str = name) -> None:
            layers.append({
                "name": name,
                "type": LAYER_KIND.get(type(mod), type(mod).__name__.lower()),
                "in_shape": _shape(inp),
                "out_shape": _shape(out),
                "params": sum(p.numel() for p in mod.parameters()),
            })

        hooks.append(module.register_forward_hook(hook))

    was_training = model.training
    model.eval()
    try:
        with torch.no_grad():
            model(example)
    finally:
        for h in hooks:
            h.remove()
        model.train(was_training)
    return {"layers": layers}
