"""Project types: "if it's X, show Y" (ML_PLAN 5.1, 5.2). Adding a type = an entry here plus its runners.

Panel ids are PROJECT_CONTEXT section 7 plus the proposed ml-dots / ml-viz / ml-live (ML_PLAN 7.5).
"""

from __future__ import annotations

ARCHETYPES: dict[str, dict] = {
    "image_classification": {
        "title": "Image classification",
        "panels": ["ml-datastack", "ml-model3d", "ml-training", "ml-results", "ml-dots"],
        "viz_ids": ["per_class_accuracy", "confusion_matrix"],
    },
    "regression": {
        "title": "Regression",
        "panels": ["ml-datastack", "ml-model3d", "ml-training", "ml-results", "ml-viz"],
        "viz_ids": ["pred_vs_actual", "residual_hist", "feature_importance"],
    },
    "text_classification": {
        "title": "Text classification",
        "panels": ["ml-datastack", "ml-model3d", "ml-training", "ml-results", "ml-dots"],
        "viz_ids": ["per_class_accuracy", "confusion_matrix"],
    },
    "generic": {
        "title": "ML project",
        "panels": ["ml-model3d", "ml-training", "ml-viz"],
        "viz_ids": ["final_metrics"],
    },
}


def layout(archetype: str, extra_viz: list[str] | None = None, title: str | None = None) -> dict:
    """The ml.layout event payload {archetype, title, panels, viz_ids} for a profile."""
    spec = ARCHETYPES.get(archetype, ARCHETYPES["generic"])
    viz = list(spec["viz_ids"]) + [v for v in (extra_viz or []) if v not in spec["viz_ids"]]
    return {"archetype": archetype if archetype in ARCHETYPES else "generic",
            "title": title or spec["title"], "panels": list(spec["panels"]), "viz_ids": viz}
