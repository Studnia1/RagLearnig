"""Ocena, czy oferta jest okazją.

Dwa niezależne sygnały:

1. **Próg cenowy** — w konfiguracji podajesz ``max_price`` dla zapytania
   ("Zelda TOTK za mniej niż 150 zł to okazja").
2. **Mediana rynkowa** — porównujemy cenę do mediany aktualnych i niedawno
   widzianych ofert dla tego samego zapytania; cena poniżej
   ``median * discount_threshold`` (np. 0.6 = 40% taniej) to okazja.

Wynik niesie powód, żeby powiadomienie mówiło *dlaczego* to okazja.
"""

from __future__ import annotations

from dataclasses import dataclass
from statistics import median

from .client import Listing

# Oferty za grosze to zwykle "opis w cenie koszulki", scam albo akcesoria.
MIN_SANE_PRICE = 5.0
# Poniżej tylu punktów odniesienia mediana jest zbyt chwiejna, by jej ufać.
MIN_SAMPLE = 5


@dataclass
class DealVerdict:
    is_deal: bool
    reasons: list[str]


def evaluate(
    listing: Listing,
    max_price: float | None,
    market_prices: list[float],
    discount_threshold: float = 0.6,
) -> DealVerdict:
    reasons: list[str] = []
    price = listing.price

    if price < MIN_SANE_PRICE:
        return DealVerdict(False, [f"cena {price:.2f} poniżej progu sensowności"])

    if max_price is not None and price <= max_price:
        reasons.append(f"cena {price:.2f} {listing.currency} ≤ Twój próg {max_price:.2f}")

    sample = [p for p in market_prices if p >= MIN_SANE_PRICE]
    if len(sample) >= MIN_SAMPLE:
        med = median(sample)
        if med > 0 and price <= med * discount_threshold:
            pct = round((1 - price / med) * 100)
            reasons.append(
                f"{pct}% poniżej mediany rynkowej ({med:.2f} {listing.currency}, n={len(sample)})"
            )

    return DealVerdict(bool(reasons), reasons)
