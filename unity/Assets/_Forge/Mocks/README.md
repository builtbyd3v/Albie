# Mocks (build your module with the hub off)

`ForgeNet` (Core/Net) switches to mock mode when no hub answers within 3 s (the desk dock's light turns amber).
Each module owns its own subfolder.

**REST:** in mock mode `ForgeNet.Get("/ml/classes", ...)` reads `Resources/Mocks/ml/classes.json` (query string ignored).
Binary responses (images, GLB) use a `.bytes` extension: `ForgeNet.GetBytes("/cad/model.glb", ...)` reads
`Resources/Mocks/cad/model.glb.bytes`. For anything smarter (versions, query strings), register a handler in your
sandbox: `ForgeNet.MockRest("/cad/metadata.json", path => ...)`.

**Events:** add a `ForgeMockPlayer` to your sandbox scene and give it a JSON file:

```json
[
  { "at": 0.5, "type": "ml.run_state", "payload": { "run_id": "r1", "status": "training", "epoch": 1, "total_epochs": 10 } },
  { "at": 1.0, "type": "ml.metrics",   "payload": { "run_id": "r1", "epoch": 1, "step": 50, "train_loss": 1.9, "train_acc": 0.31, "val_loss": 2.0, "val_acc": 0.29, "lr": 0.001 } }
]
```

`at` is seconds after mock mode starts; `type` and `payload` follow PROJECT_CONTEXT section 7. Your code subscribes the
same way in both modes (`ForgeEvents.OnMlMetrics += ...` or `ForgeNet.On("ml.metrics", ...)`), so nothing changes
when the hub is running.

Layout:

```
Mocks/
  Resources/Mocks/<module>/...   REST responses (ForgeNet loads these at runtime)
  <module>/                      everything else: event sequences, raw exports (e.g. cad/v1..v4 from hub/cad/make_mocks.py)
```
