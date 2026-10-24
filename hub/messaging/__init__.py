"""Messaging module: Bit's Slack presence (PROJECT_CONTEXT 8.7 / 8.8).

Inbound Slack events -> notify.message on /ws; POST /messages/digest on sit-down -> notify.digest + the spoken
greeting; Bit's messages_* tools and /messages/send post replies. Socket Mode, so no public URL or tunnel.
"""

from .router import router
from .settings import SlackSettings
from .slack_client import SlackService
from .store import MessageStore

__all__ = ["MessageStore", "SlackService", "SlackSettings", "router"]
