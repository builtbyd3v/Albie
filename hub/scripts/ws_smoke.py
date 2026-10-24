"""Manual smoke test against a running hub.

    python scripts/ws_smoke.py "where is the websocket handler?" [--mode qa|code|auto] [--url ws://127.0.0.1:8000/ws]

Prints every event until answer_done / task_complete / error. Needs the `websockets`
dev dependency (pip install -r requirements-dev.txt).
"""

from __future__ import annotations

import argparse
import asyncio
import json

import websockets

DONE = {"agent.answer_done", "agent.task_complete", "agent.error"}


async def main() -> None:
    ap = argparse.ArgumentParser()
    ap.add_argument("text")
    ap.add_argument("--mode", default="auto", choices=["qa", "code", "auto"])
    ap.add_argument("--url", default="ws://127.0.0.1:8000/ws")
    ap.add_argument("--pet", default="bit")
    ap.add_argument("--project", default="gatorhack")
    a = ap.parse_args()

    async with websockets.connect(a.url) as ws:
        await ws.send(json.dumps({"type": "hello", "payload": {"client": "smoke", "pet_id": a.pet, "project_id": a.project}}))
        await ws.send(json.dumps({"type": "agent.ask", "payload": {"pet_id": a.pet, "project_id": a.project, "text": a.text, "mode": a.mode}}))
        async for raw in ws:
            msg = json.loads(raw)
            print(f"{msg['type']:<22} {json.dumps(msg['payload'])}")
            if msg["type"] in DONE:
                break


if __name__ == "__main__":
    asyncio.run(main())
