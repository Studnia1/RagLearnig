"""Klient nieoficjalnego API Vinted.

Vinted nie ma publicznego API. Serwis wystawia jednak endpoint
``/api/v2/catalog/items``, z którego korzysta własny frontend. Wystarczy
anonimowa sesja: wchodzimy na stronę główną jak przeglądarka, dostajemy
ciasteczka (m.in. ``access_token_web``) i używamy ich przy zapytaniach.
Przy 401 odświeżamy sesję i ponawiamy.
"""

from __future__ import annotations

import logging
import random
import time
from dataclasses import dataclass
from typing import Any, Iterable

import requests

log = logging.getLogger(__name__)

DEFAULT_UA = (
    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 "
    "(KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36"
)


@dataclass
class Listing:
    """Pojedyncza oferta z katalogu Vinted."""

    id: int
    title: str
    price: float
    currency: str
    total_price: float | None
    brand: str | None
    url: str
    photo_url: str | None
    raw: dict[str, Any]

    @classmethod
    def from_api(cls, item: dict[str, Any], base_url: str) -> "Listing":
        def _amount(node: Any) -> float | None:
            # Cena bywa stringiem, liczbą albo obiektem {"amount": "12.5", ...}
            if node is None:
                return None
            if isinstance(node, dict):
                node = node.get("amount")
            try:
                return float(node)
            except (TypeError, ValueError):
                return None

        price = _amount(item.get("price")) or 0.0
        total = _amount(item.get("total_item_price"))
        currency = "?"
        for node in (item.get("price"), item.get("total_item_price")):
            if isinstance(node, dict) and node.get("currency_code"):
                currency = node["currency_code"]
                break

        photo = item.get("photo") or {}
        return cls(
            id=int(item["id"]),
            title=item.get("title", ""),
            price=price,
            currency=currency,
            total_price=total,
            brand=item.get("brand_title"),
            url=item.get("url") or f"{base_url}/items/{item['id']}",
            photo_url=photo.get("url") if isinstance(photo, dict) else None,
            raw=item,
        )


class VintedClient:
    """Minimalny klient katalogu Vinted z anonimową sesją."""

    def __init__(self, base_url: str = "https://www.vinted.pl", user_agent: str = DEFAULT_UA):
        self.base_url = base_url.rstrip("/")
        self.user_agent = user_agent
        self.session = requests.Session()
        self.session.headers.update(
            {
                "User-Agent": self.user_agent,
                "Accept": "application/json, text/plain, */*",
                "Accept-Language": "pl-PL,pl;q=0.9,en;q=0.8",
            }
        )
        self._authenticated = False

    def _refresh_session(self) -> None:
        log.debug("Odświeżam anonimową sesję Vinted (%s)", self.base_url)
        self.session.cookies.clear()
        resp = self.session.get(self.base_url + "/", timeout=30)
        resp.raise_for_status()
        self._authenticated = True

    def search(
        self,
        search_text: str = "",
        catalog_ids: Iterable[int] = (),
        price_to: float | None = None,
        per_page: int = 96,
        page: int = 1,
        order: str = "newest_first",
    ) -> list[Listing]:
        """Zwraca oferty z katalogu, domyślnie od najnowszych."""
        if not self._authenticated:
            self._refresh_session()

        params: dict[str, Any] = {
            "search_text": search_text,
            "order": order,
            "per_page": per_page,
            "page": page,
        }
        if catalog_ids:
            params["catalog_ids"] = ",".join(str(c) for c in catalog_ids)
        if price_to is not None:
            params["price_to"] = price_to

        url = f"{self.base_url}/api/v2/catalog/items"
        for attempt in range(3):
            resp = self.session.get(url, params=params, timeout=30)
            if resp.status_code in (401, 403):
                log.info("HTTP %s z API — odświeżam sesję (próba %d)", resp.status_code, attempt + 1)
                time.sleep(1 + attempt * 2 + random.random())
                self._refresh_session()
                continue
            resp.raise_for_status()
            items = resp.json().get("items", [])
            return [Listing.from_api(i, self.base_url) for i in items]

        raise RuntimeError(f"Vinted API wciąż odrzuca zapytanie ({resp.status_code}) — spróbuj później")
