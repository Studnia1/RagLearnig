# Vinted Games Tracker

Śledzi nowo dodawane oferty gier na Vinted i alarmuje o mocnych okazjach.

## Jak to działa

1. Co `poll_interval_seconds` odpytuje nieoficjalne API katalogu Vinted
   (`/api/v2/catalog/items`, to samo, z którego korzysta strona) dla każdego
   zapytania z konfiguracji, sortując od najnowszych.
2. Nowe oferty (niewidziane wcześniej ID) zapisuje w SQLite.
3. Ofertę uznaje za **okazję**, gdy spełnia którykolwiek warunek:
   - cena ≤ `max_price` ustawionego dla zapytania,
   - cena ≤ `discount_threshold` × mediana cen podobnych ofert
     (bieżące wyniki + historia z bazy; mediana liczona dopiero od 5 próbek,
     oferty poniżej 5 zł odrzucane jako podejrzane).
4. O okazji powiadamia w konsoli oraz — jeśli skonfigurowano — przez
   Discord webhook i/lub Telegram.

Pierwszy przebieg tylko zapełnia bazę (bez alertów), żeby nie zalać Cię
wszystkim, co już wisi w katalogu.

## Instalacja

```bash
cd vinted-tracker
python -m venv .venv && source .venv/bin/activate
pip install -r requirements.txt
cp config.example.yaml config.yaml   # i dostosuj zapytania oraz progi
```

## Uruchomienie

```bash
python -m vinted_tracker.main --config config.yaml          # pętla ciągła
python -m vinted_tracker.main --config config.yaml --once   # jeden przebieg
```

Powiadomienia (opcjonalne) konfigurują zmienne środowiskowe:

```bash
export DISCORD_WEBHOOK_URL="https://discord.com/api/webhooks/..."
export TELEGRAM_BOT_TOKEN="123456:ABC..."
export TELEGRAM_CHAT_ID="123456789"
```

## Testy

```bash
python -m unittest discover -s tests
```

## Konfiguracja

Zobacz `config.example.yaml`. Najważniejsze pola:

| Pole | Znaczenie |
|---|---|
| `defaults.base_url` | domena Vinted (`vinted.pl`, `vinted.de`, …) — decyduje o rynku i walucie |
| `defaults.poll_interval_seconds` | częstotliwość sprawdzania (nie schodź dużo poniżej 300 s) |
| `defaults.discount_threshold` | np. `0.6` = alert przy cenie ≤ 60% mediany |
| `defaults.catalog_ids` | opcjonalne ID katalogu (np. "Gry i konsole"), do podejrzenia w URL na stronie Vinted |
| `watches[].query` | fraza wyszukiwania (tytuł gry, platforma) |
| `watches[].max_price` | twardy próg ceny dla alertu |

## Zastrzeżenia

- To **nieoficjalne** API — Vinted może je zmienić lub ograniczać zbyt częste
  zapytania. Tracker odświeża anonimową sesję przy 401/403 i losowo rozciąga
  odstępy, ale używaj rozsądnych interwałów i tylko do użytku osobistego.
- Mediana rynkowa jest tym lepsza, im dłużej tracker działa i im
  precyzyjniejsze jest zapytanie (np. `"mario kart 8 deluxe switch"` zamiast
  `"mario"`).
