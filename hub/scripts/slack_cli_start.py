"""`slack run` start hook: copy the tokens the Slack CLI hands us into hub/.env, then exit.

The Slack CLI (`slack install -E local`, `slack run`) owns the app and passes SLACK_BOT_TOKEN / SLACK_APP_TOKEN
to this hook as environment variables. We only need them once, in hub/.env, so the hub can be started the
normal way (`uvicorn main:app ...`). Run `slack run` from hub/ whenever the tokens change (e.g. after a reinstall).
"""
from __future__ import annotations

import os
import sys
from pathlib import Path

HUB = Path(__file__).resolve().parents[1]
ENV = HUB / ".env"
KEYS = {
    "SLACK_BOT_TOKEN": ("SLACK_BOT_TOKEN", "SLACK_CLI_XOXB"),
    "SLACK_APP_TOKEN": ("SLACK_APP_TOKEN", "SLACK_CLI_XAPP"),
}


def main() -> int:
    found = {k: next((os.environ[v] for v in names if os.environ.get(v)), "") for k, names in KEYS.items()}
    missing = [k for k, v in found.items() if not v]
    if missing:
        print(f"slack_cli_start: CLI did not provide {missing}; nothing written", file=sys.stderr)
        return 1
    if not ENV.exists():
        ENV.write_text((HUB / ".env.example").read_text())
    lines = ENV.read_text().splitlines()
    seen = set()
    for i, line in enumerate(lines):
        key = line.split("=", 1)[0].strip()
        if key in found:
            lines[i] = f"{key}={found[key]}"
            seen.add(key)
    lines += [f"{k}={v}" for k, v in found.items() if k not in seen]
    ENV.write_text("\n".join(lines) + "\n")
    print(f"slack_cli_start: wrote {', '.join(found)} to {ENV}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
