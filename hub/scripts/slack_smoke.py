"""Live check of Bit's Slack connection, no hub needed. Run from hub/ with the tokens in hub/.env:

    .venv/bin/python scripts/slack_smoke.py                        # --listen: print every message in the inbox (Ctrl-C to stop)
    .venv/bin/python scripts/slack_smoke.py --draft "#forge-team" "hello"   # write a draft only (notify.draft), post nothing
    .venv/bin/python scripts/slack_smoke.py --send  "#forge-team" "hello"   # DEV SHORTCUT: draft + send at once, no confirmation
    .venv/bin/python scripts/slack_smoke.py --digest               # what Bit would say on sit-down right now
    .venv/bin/python scripts/slack_smoke.py --channels             # the panel's channel list: names, kinds, unread
    .venv/bin/python scripts/slack_smoke.py --history "#forge-team" 10   # the last 10 top-level messages of one conversation

With SLACK_USER_TOKEN set, messages post under your name and the inbox is yours; without it Bit posts as itself and
only sees channels it was invited to. The real product path is draft -> Send button / "send it"; --send skips that.
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
        print(f"{type_:22} {body}", flush=True)


async def speak(pet_id: str, text: str) -> bool:
    print(f"(voice) {pet_id} would say: {text}")
    return False


async def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    mode = ap.add_mutually_exclusive_group()
    mode.add_argument("--listen", action="store_true", help="print every inbox message as it arrives (default)")
    mode.add_argument("--draft", nargs=2, metavar=("CHANNEL", "TEXT"), help="create a draft for CHANNEL (#name or id); posts nothing")
    mode.add_argument("--send", nargs=2, metavar=("CHANNEL", "TEXT"), help="dev shortcut: draft TEXT and send it immediately")
    mode.add_argument("--digest", action="store_true", help="print the sit-down digest and exit")
    mode.add_argument("--channels", action="store_true", help="print the channel list (GET /messages/channels) and exit")
    mode.add_argument("--history", nargs=2, metavar=("CHANNEL", "N"), help="print the last N messages of CHANNEL (#name, id or a person)")
    ap.add_argument("--data", default=str(HUB / ".data" / "messages"), help="where last_seen_ts lives")
    args = ap.parse_args()

    load_dotenv(HUB / ".env")
    logging.basicConfig(level=logging.INFO, format="%(levelname)s %(name)s %(message)s")
    settings = SlackSettings.from_env()
    if not settings.configured:
        print("SLACK_BOT_TOKEN / SLACK_APP_TOKEN missing in hub/.env (see PROJECT_CONTEXT 8.7)")
        return 2
    store = MessageStore(Path(args.data) if args.digest or args.channels else Path(tempfile.mkdtemp()) / "messages")
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
        who = f"YOU ({svc.user_name}, {svc.user_id})" if svc.as_user else "Bit (no SLACK_USER_TOKEN in hub/.env)"
        print(f"connected; bot {svc.bot_user_id}; posting as {who}; mentions of {svc.user_id or '(unknown user id)'}")
        if args.draft or args.send:
            channel, text = args.draft or args.send
            draft = await svc.create_draft(text, channel)
            if args.draft:
                print(f"draft {draft.draft_id} open for #{draft.channel_name}; nothing posted (expires in {svc.draft_expiry_s:.0f}s)")
                return 0
            _, ts = await svc.send_draft(draft.draft_id)
            print(f"posted as {'you' if svc.as_user else 'Bit'}, ts={ts}")
            return 0
        if args.digest:
            d = await svc.digest()
            print(d.model_dump())
            return 0
        if args.channels:
            await svc.refresh_channels()
            for c in svc.channel_summaries():
                flag = f" unread {c.unread} ({c.mentions} mention you)" if c.unread else ""
                print(f"{c.id:12} {c.kind:9} {c.name:30} {c.last_ts or '-':20}{flag}  {(c.last_text or '')[:60]!r}")
            return 0
        if args.history:
            channel, n = args.history
            rec, messages, has_more = await svc.history(channel, int(n))
            print(f"{rec.label} ({rec.id}, {rec.kind}); has_more={has_more}")
            for m in messages:
                who = "you" if m.is_me else m.user_name
                extra = f"  [{m.reply_count} replies]" if m.reply_count else ""
                print(f"{m.ts}  {who:>12}: {m.text[:100]!r}{extra}")
            return 0
        print("listening; message yourself from another account, or DM Bit. Ctrl-C to stop.")
        await asyncio.Event().wait()
    except KeyboardInterrupt:
        pass
    finally:
        await svc.stop()
    return 0


if __name__ == "__main__":
    raise SystemExit(asyncio.run(main()))
