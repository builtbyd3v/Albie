"""Slack settings from the environment (hub/.env). See .env.example and PROJECT_CONTEXT 8.7 for the app setup."""
from __future__ import annotations

import os
from dataclasses import dataclass


@dataclass(frozen=True)
class SlackSettings:
    bot_token: str = ""         # xoxb-...  the app's own identity (written by `slack run`)
    app_token: str = ""         # xapp-...  Socket Mode app-level token (written by `slack run`)
    user_token: str = ""        # xoxp-...  the USER's token: read their inbox, post under their name
    user_id: str = ""           # optional; derived from user_token at startup, env is the fallback (bot-only mode)
    default_channel: str = ""   # channel id or #name used when messages_send names no channel

    @classmethod
    def from_env(cls) -> "SlackSettings":
        return cls(
            bot_token=os.getenv("SLACK_BOT_TOKEN", "").strip(),
            app_token=os.getenv("SLACK_APP_TOKEN", "").strip(),
            user_token=os.getenv("SLACK_USER_TOKEN", "").strip(),
            user_id=os.getenv("SLACK_USER_ID", "").strip(),
            default_channel=os.getenv("SLACK_DEFAULT_CHANNEL", "").strip(),
        )

    @property
    def configured(self) -> bool:
        return bool(self.bot_token and self.app_token)

    @property
    def as_user(self) -> bool:
        """True when Bit reads the user's inbox and posts as them (PROJECT_CONTEXT 8.7)."""
        return bool(self.user_token)
