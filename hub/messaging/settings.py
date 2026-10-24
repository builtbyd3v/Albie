"""Slack settings from the environment (hub/.env). See .env.example and PROJECT_CONTEXT 8.7 for the app setup."""
from __future__ import annotations

import os
from dataclasses import dataclass


@dataclass(frozen=True)
class SlackSettings:
    bot_token: str = ""         # xoxb-...  (OAuth & Permissions > Bot User OAuth Token)
    app_token: str = ""         # xapp-...  (Socket Mode app-level token, scope connections:write)
    user_id: str = ""           # the human's member id (U...), for mentions_me
    default_channel: str = ""   # channel id or #name used when messages_send names no channel

    @classmethod
    def from_env(cls) -> "SlackSettings":
        return cls(
            bot_token=os.getenv("SLACK_BOT_TOKEN", "").strip(),
            app_token=os.getenv("SLACK_APP_TOKEN", "").strip(),
            user_id=os.getenv("SLACK_USER_ID", "").strip(),
            default_channel=os.getenv("SLACK_DEFAULT_CHANNEL", "").strip(),
        )

    @property
    def configured(self) -> bool:
        return bool(self.bot_token and self.app_token)
