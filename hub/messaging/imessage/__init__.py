"""Optional local Messages database integration (enabled only on macOS)."""

from .service import IMessageService
from .settings import IMessageSettings

__all__ = ["IMessageService", "IMessageSettings"]
