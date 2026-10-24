"""Slack CLI `get-manifest` hook: print hub/manifest.json (the CLI passes extra flags; we ignore them)."""
from pathlib import Path
import sys

sys.stdout.write((Path(__file__).resolve().parents[1] / "manifest.json").read_text())
