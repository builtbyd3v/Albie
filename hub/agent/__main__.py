"""Run the agent module alone (no CAD):  python -m agent [--host 0.0.0.0] [--port 8000]"""

from __future__ import annotations

import argparse
import logging
import os

import uvicorn

from .app import create_agent_app, load_env

if __name__ == "__main__":
    load_env()
    ap = argparse.ArgumentParser(prog="python -m agent")
    ap.add_argument("--host", default="0.0.0.0")
    ap.add_argument("--port", type=int, default=8000)
    a = ap.parse_args()
    logging.basicConfig(
        level=os.getenv("LOG_LEVEL", "INFO"),
        format="%(asctime)s %(levelname)s %(name)s: %(message)s",
    )
    uvicorn.run(create_agent_app(), host=a.host, port=a.port)
