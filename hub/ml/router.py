"""ML router: mount in hub/main.py with `app.include_router(ml.router.router)`."""

from __future__ import annotations

from fastapi import APIRouter, HTTPException

from .models import Atlas, Confusion, DatasetItem, Graph, RelabelRequest, Results, TrainRequest, TrainResponse

router = APIRouter(prefix="/ml", tags=["ml"])


def _todo() -> HTTPException:
    return HTTPException(status_code=501, detail="not implemented")


@router.get("/classes")
def get_classes() -> list[str]:
    raise _todo()


@router.get("/dataset")
def get_dataset(split: str = "val") -> list[DatasetItem]:
    raise _todo()


@router.get("/atlas")
def get_atlas(split: str = "val") -> Atlas:
    raise _todo()


@router.get("/image/{id}")
def get_image(id: str):
    raise _todo()


@router.get("/graph")
def get_graph() -> Graph:
    raise _todo()


@router.post("/train")
def train(req: TrainRequest) -> TrainResponse:
    raise _todo()


@router.get("/results/{run_id}")
def get_results(run_id: str) -> Results:
    raise _todo()


@router.get("/confusion/{run_id}")
def get_confusion(run_id: str) -> Confusion:
    raise _todo()


@router.post("/samples/{id}/relabel")
def relabel(id: str, req: RelabelRequest) -> None:
    raise _todo()


@router.post("/samples/{id}/remove")
def remove(id: str) -> None:
    raise _todo()
