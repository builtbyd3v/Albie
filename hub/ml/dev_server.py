"""Standalone dev server for the ML router until the shared hub/main.py lands.

Run from hub/: uvicorn ml.dev_server:app --reload --port 8000
"""

from fastapi import FastAPI

from ml.router import router

app = FastAPI(title="forge-hub (ml dev)")
app.include_router(router)
