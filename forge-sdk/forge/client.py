"""Transport from the SDK to the hub."""

from __future__ import annotations

import json
import os
import sys
from typing import Any

HUB_URL = os.environ.get("FORGE_HUB_URL", "http://localhost:8000")
OFFLINE = os.environ.get("FORGE_OFFLINE") == "1"


def emit(event_type: str, payload: dict[str, Any]) -> None:
    """Send one `{type, payload}` event. Prints it as a JSON line when offline."""
    event = {"type": event_type, "payload": payload}
    if OFFLINE:
        print(json.dumps(event), file=sys.stdout, flush=True)
        return
    # TODO: POST to a hub ingest endpoint (not in section 7 yet; propose one, e.g. POST /ml/ingest)
    print(json.dumps(event), file=sys.stdout, flush=True)
