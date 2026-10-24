"""Messaging module: Bit's Slack presence (PROJECT_CONTEXT 8.7 / 8.8).

Inbound Slack events -> notify.message on /ws; POST /messages/digest on sit-down -> notify.digest + the spoken
greeting; Bit's messages_send writes a draft (notify.draft), and only /messages/drafts/{id}/send or the
messages_confirm tool posts it, under the user's own name. Socket Mode, so no public URL or tunnel.
"""

from .router import router
from .settings import SlackSettings
from .slack_client import DraftError, SlackService
from .store import MessageStore

__all__ = ["DraftError", "MessageStore", "SlackService", "SlackSettings", "router"]
