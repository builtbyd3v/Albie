import json

import forge


def test_log_prints_metrics_event(capsys, monkeypatch):
    forge.log(epoch=1, train_loss=1.0, train_acc=0.5, val_loss=1.1, val_acc=0.45, lr=1e-3)
    event = json.loads(capsys.readouterr().out.strip().splitlines()[-1])
    assert event["type"] == "ml.metrics"
    assert event["payload"]["epoch"] == 1
