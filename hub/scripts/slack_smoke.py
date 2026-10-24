"""Live check of Bit's Slack connection, no hub needed. Run from hub/ with the tokens in hub/.env:

    .venv/bin/python scripts/slack_smoke.py                       # connect, print every message Bit sees (Ctrl-C to stop)
    .venv/bin/python scripts/slack_smoke.py --send "#forge-team" "hello from Bit"
    .venv/bin/python scripts/slack_smoke.py --digest              # what Bit would say on sit-down right now

Then DM the bot or post in a channel it was invited to and watch the notify.message lines appear.
"""
from __future__ import annotations

import argparse
import asyncio
import logging
import sys
import tempfile
from pathlib import Path
from typing import Any

HUB = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(HUB))

from dotenv import load_dotenv  # noqa: E402

from messaging import MessageStore, SlackService, SlackSettings  # noqa: E402


class PrintBus:
    async def publish(self, type_: str, payload: Any) -> None:
        body = payload.model_dump() if hasattr(payload, "model_dump") else payload
        print(f"{type_:16} {body}", flush=True)


async def speak(pet_id: str, text: str) -> bool:
    print(f"(voice) {pet_id} would say: {text}")
    return False


async def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--send", nargs=2, metavar=("CHANNEL", "TEXT"), help="post TEXT to CHANNEL (#name or id) and exit")
    ap.add_argument("--digest", action="store_true", help="print the sit-down digest and exit")
    ap.add_argument("--data", default=str(HUB / ".data" / "messages"), help="where last_seen_ts lives")
    args = ap.parse_args()

    load_dotenv(HUB / ".env")
    logging.basicConfig(level=logging.INFO, format="%(levelname)s %(name)s %(message)s")
    settings = SlackSettings.from_env()
    if not settings.configured:
        print("SLACK_BOT_TOKEN / SLACK_APP_TOKEN missing in hub/.env (see PROJECT_CONTEXT 8.7)")
        return 2
    store = MessageStore(Path(args.data) if args.digest else Path(tempfile.mkdtemp()) / "messages")
    svc = SlackService(settings, store, PrintBus(), speak)  # type: ignore[arg-type]
    await svc.start()
    try:
        for _ in range(100):  # wait for the connect task (auth_test + socket)
            if svc.connected:
                break
            await asyncio.sleep(0.2)
        if not svc.connected:
            print("not connected after 20 s; check the tokens and the log above")
            return 1
        print(f"connected as bot {svc.bot_user_id}; user id for mentions: {settings.user_id or '(SLACK_USER_ID unset)'}")
        if args.send:
            ts = await svc.send(args.send[0], args.send[1])
            print(f"posted, ts={ts}")
            return 0
        if args.digest:
            d = await svc.digest()
            print(d.model_dump())
            return 0
        print("listening; DM the bot or post in a channel it is in. Ctrl-C to stop.")
        await asyncio.Event().wait()
    except KeyboardInterrupt:
        pass
    finally:
        await svc.stop()
    return 0


if __name__ == "__main__":
    raise SystemExit(asyncio.run(main()))
