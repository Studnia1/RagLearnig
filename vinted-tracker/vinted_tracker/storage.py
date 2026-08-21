"""Trwała pamięć widzianych ofert i historii cen (SQLite)."""

from __future__ import annotations

import sqlite3
import time
from pathlib import Path

SCHEMA = """
CREATE TABLE IF NOT EXISTS items (
    id INTEGER PRIMARY KEY,
    query TEXT NOT NULL,
    title TEXT NOT NULL,
    price REAL NOT NULL,
    currency TEXT NOT NULL,
    url TEXT NOT NULL,
    first_seen INTEGER NOT NULL,
    is_deal INTEGER NOT NULL DEFAULT 0
);
CREATE INDEX IF NOT EXISTS idx_items_query ON items(query);
"""


class Storage:
    def __init__(self, path: str | Path):
        path = Path(path)
        path.parent.mkdir(parents=True, exist_ok=True)
        self.conn = sqlite3.connect(path)
        self.conn.executescript(SCHEMA)

    def is_known(self, item_id: int) -> bool:
        row = self.conn.execute("SELECT 1 FROM items WHERE id = ?", (item_id,)).fetchone()
        return row is not None

    def remember(self, item_id: int, query: str, title: str, price: float,
                 currency: str, url: str, is_deal: bool) -> None:
        self.conn.execute(
            "INSERT OR IGNORE INTO items (id, query, title, price, currency, url, first_seen, is_deal) "
            "VALUES (?, ?, ?, ?, ?, ?, ?, ?)",
            (item_id, query, title, price, currency, url, int(time.time()), int(is_deal)),
        )
        self.conn.commit()

    def recent_prices(self, query: str, max_age_days: int = 30) -> list[float]:
        """Ceny ofert widzianych dla danego zapytania — baza do mediany rynkowej."""
        cutoff = int(time.time()) - max_age_days * 86400
        rows = self.conn.execute(
            "SELECT price FROM items WHERE query = ? AND first_seen >= ?", (query, cutoff)
        ).fetchall()
        return [r[0] for r in rows]

    def close(self) -> None:
        self.conn.close()
