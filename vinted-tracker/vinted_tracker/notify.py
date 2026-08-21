"""Powiadomienia o okazjach: konsola, Discord webhook, Telegram bot."""

from __future__ import annotations

import logging
import os

import requests

from .client import Listing

log = logging.getLogger(__name__)


def format_deal(listing: Listing, query: str, reasons: list[str]) -> str:
    lines = [
        f"🎮 OKAZJA [{query}]: {listing.title}",
        f"   {listing.price:.2f} {listing.currency} — {listing.url}",
    ]
    lines += [f"   • {r}" for r in reasons]
    return "\n".join(lines)


class Notifier:
    """Wysyła wiadomość każdym skonfigurowanym kanałem; brak konfiguracji = tylko konsola.

    Sekrety podaje się przez zmienne środowiskowe:
    ``DISCORD_WEBHOOK_URL``, ``TELEGRAM_BOT_TOKEN`` + ``TELEGRAM_CHAT_ID``.
    """

    def __init__(self) -> None:
        self.discord_webhook = os.environ.get("DISCORD_WEBHOOK_URL")
        self.telegram_token = os.environ.get("TELEGRAM_BOT_TOKEN")
        self.telegram_chat_id = os.environ.get("TELEGRAM_CHAT_ID")

    def send(self, message: str) -> None:
        print(message, flush=True)
        if self.discord_webhook:
            self._send_discord(message)
        if self.telegram_token and self.telegram_chat_id:
            self._send_telegram(message)

    def _send_discord(self, message: str) -> None:
        try:
            resp = requests.post(self.discord_webhook, json={"content": message[:1990]}, timeout=15)
            resp.raise_for_status()
        except requests.RequestException as e:
            log.warning("Discord webhook nie zadziałał: %s", e)

    def _send_telegram(self, message: str) -> None:
        try:
            resp = requests.post(
                f"https://api.telegram.org/bot{self.telegram_token}/sendMessage",
                json={"chat_id": self.telegram_chat_id, "text": message[:4000]},
                timeout=15,
            )
            resp.raise_for_status()
        except requests.RequestException as e:
            log.warning("Telegram nie zadziałał: %s", e)
