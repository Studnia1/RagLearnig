"""Pętla główna: cyklicznie odpytuje Vinted i zgłasza okazje.

Uruchomienie:  python -m vinted_tracker.main --config config.yaml [--once]
"""

from __future__ import annotations

import argparse
import logging
import random
import time
from pathlib import Path
from typing import Any

import yaml

from .client import VintedClient
from .deals import evaluate
from .notify import Notifier, format_deal
from .storage import Storage

log = logging.getLogger("vinted_tracker")


def load_config(path: str | Path) -> dict[str, Any]:
    with open(path, encoding="utf-8") as f:
        return yaml.safe_load(f)


def run_once(cfg: dict[str, Any], client: VintedClient, storage: Storage,
             notifier: Notifier, first_run: bool) -> int:
    """Jeden przebieg po wszystkich zapytaniach. Zwraca liczbę znalezionych okazji."""
    defaults = cfg.get("defaults", {})
    discount = float(defaults.get("discount_threshold", 0.6))
    deals_found = 0

    for watch in cfg.get("watches", []):
        query = watch["query"]
        max_price = watch.get("max_price")
        catalog_ids = watch.get("catalog_ids") or defaults.get("catalog_ids") or []
        w_discount = float(watch.get("discount_threshold", discount))

        try:
            listings = client.search(search_text=query, catalog_ids=catalog_ids)
        except Exception as e:
            log.error("Zapytanie %r nie powiodło się: %s", query, e)
            continue

        # Mediana liczona z tego, co widać teraz + historia z bazy.
        market = [l.price for l in listings] + storage.recent_prices(query)

        for listing in listings:
            if storage.is_known(listing.id):
                continue
            verdict = evaluate(listing, max_price, market, w_discount)
            storage.remember(
                listing.id, query, listing.title, listing.price,
                listing.currency, listing.url, verdict.is_deal,
            )
            # Pierwszy przebieg tylko zapełnia bazę — wszystko byłoby "nowe",
            # a mediana i tak dopiero się buduje.
            if verdict.is_deal and not first_run:
                deals_found += 1
                notifier.send(format_deal(listing, query, verdict.reasons))

        log.info("[%s] ofert: %d, nowych okazji dotąd: %d", query, len(listings), deals_found)
        time.sleep(1 + random.random() * 2)  # nie młócimy API między zapytaniami

    return deals_found


def main() -> None:
    parser = argparse.ArgumentParser(description="Śledzenie okazji na gry na Vinted")
    parser.add_argument("--config", default="config.yaml")
    parser.add_argument("--once", action="store_true", help="jeden przebieg zamiast pętli")
    parser.add_argument("--verbose", action="store_true")
    args = parser.parse_args()

    logging.basicConfig(
        level=logging.DEBUG if args.verbose else logging.INFO,
        format="%(asctime)s %(levelname)s %(name)s: %(message)s",
    )

    cfg = load_config(args.config)
    defaults = cfg.get("defaults", {})
    interval = int(defaults.get("poll_interval_seconds", 300))

    client = VintedClient(base_url=defaults.get("base_url", "https://www.vinted.pl"))
    storage = Storage(defaults.get("db_path", "data/tracker.sqlite3"))
    notifier = Notifier()

    first_run = True
    while True:
        try:
            run_once(cfg, client, storage, notifier, first_run)
        except KeyboardInterrupt:
            raise
        except Exception:
            log.exception("Przebieg zakończony błędem — próbuję dalej")
        first_run = False
        if args.once:
            break
        sleep_for = interval + random.uniform(0, interval * 0.2)
        log.info("Śpię %.0f s", sleep_for)
        time.sleep(sleep_for)

    storage.close()


if __name__ == "__main__":
    main()
