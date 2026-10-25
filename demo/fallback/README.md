# Demo fallback recordings (ML_PLAN A8)

Recorded training runs that drive ML mode with no training running. Each folder is a copy of a
`.forge/runs/<run_id>/` folder: `events.jsonl` plus the result files.

| Folder | What | Length |
|---|---|---|
| `cifar10-9fc1dd22` | CIFAR-10, 20 epochs: live dot wall every epoch, overfit alert near the end, 56.2% final val accuracy (best 60.6% at epoch 7), flipped labels among the confident mistakes | ~49 s at 1x |
| `trading_regression-b5dd50c5` | Teammate repo (`trading_regression`, Ridge backtest), cold-opened and patched by setup: one tick of backtest metrics (out-of-sample Sharpe −0.43, hit rate 47%, max drawdown −2.2%, turnover 9.9) that the Training panel shows as RUN METRICS, then done | ~4 s at 1x |

Replay one into the running hub (Unity must be Live on the same hub):

```bash
cd forge-sdk
FORGE_HUB=http://127.0.0.1:8000 python -m forge.replay ../demo/fallback/cifar10-9fc1dd22 --speed 2
```

If the hub serves the CIFAR repo, `POST /ml/replay {"run_id": "9fc1dd22", "speed": 2}` does the same.
