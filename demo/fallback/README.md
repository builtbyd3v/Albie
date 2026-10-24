# Demo fallback recordings (ML_PLAN A8)

Recorded training runs that drive ML mode with no training running. Each folder is a copy of a
`.forge/runs/<run_id>/` folder: `events.jsonl` plus the result files.

| Folder | What | Length |
|---|---|---|
| `cifar10-9fc1dd22` | CIFAR-10, 20 epochs: live dot wall every epoch, overfit alert near the end, 56.2% final val accuracy (best 60.6% at epoch 7), flipped labels among the confident mistakes | ~49 s at 1x |

Replay one into the running hub (Unity must be Live on the same hub):

```bash
cd forge-sdk
FORGE_HUB=http://127.0.0.1:8000 python -m forge.replay ../demo/fallback/cifar10-9fc1dd22 --speed 2
```

If the hub serves the CIFAR repo, `POST /ml/replay {"run_id": "9fc1dd22", "speed": 2}` does the same.
Add the best teammate-repo run here once one exists.
