# Vinted Games Tracker (C#)

Śledzi nowo dodawane oferty gier na Vinted i alarmuje o mocnych okazjach.
Wersja C#/.NET 8 — aplikacja bez zewnętrznych zależności NuGet
(tylko testy używają xunit).

## Jak to działa

1. Co `pollIntervalSeconds` odpytuje nieoficjalne API katalogu Vinted
   (`/api/v2/catalog/items`, to samo, z którego korzysta strona) dla każdego
   zapytania z konfiguracji, sortując od najnowszych. Klient sam pobiera
   anonimowe ciasteczka sesji i odświeża je przy 401/403.
2. Nowe oferty (niewidziane wcześniej ID) zapisuje w pliku stanu JSON.
3. Ofertę uznaje za **okazję**, gdy spełnia którykolwiek warunek:
   - cena ≤ `maxPrice` ustawionego dla zapytania,
   - cena ≤ `discountThreshold` × mediana cen podobnych ofert
     (bieżące wyniki + historia; mediana liczona dopiero od 5 próbek,
     oferty poniżej 5 zł odrzucane jako podejrzane).
4. O okazji powiadamia w konsoli oraz — jeśli skonfigurowano — przez
   Discord webhook i/lub Telegram.

Pierwszy przebieg tylko zapełnia stan (bez alertów), żeby nie zalać Cię
wszystkim, co już wisi w katalogu.

## Wymagania

- [.NET SDK 8.0](https://dotnet.microsoft.com/download/dotnet/8.0)

## Uruchomienie

```bash
cd vinted-tracker-csharp
cp config.example.json config.json   # i dostosuj zapytania oraz progi
dotnet run --project src/VintedTracker -- --config config.json           # pętla ciągła
dotnet run --project src/VintedTracker -- --config config.json --once    # jeden przebieg
```

Powiadomienia (opcjonalne) konfigurują zmienne środowiskowe:

```bash
export DISCORD_WEBHOOK_URL="https://discord.com/api/webhooks/..."
export TELEGRAM_BOT_TOKEN="123456:ABC..."
export TELEGRAM_CHAT_ID="123456789"
```

## Testy

```bash
dotnet test
```

## Konfiguracja

Zobacz `config.example.json` (komentarze `//` w pliku są dozwolone).
Najważniejsze pola:

| Pole | Znaczenie |
|---|---|
| `defaults.baseUrl` | domena Vinted (`vinted.pl`, `vinted.de`, …) — decyduje o rynku i walucie |
| `defaults.pollIntervalSeconds` | częstotliwość sprawdzania (nie schodź dużo poniżej 300 s) |
| `defaults.discountThreshold` | np. `0.6` = alert przy cenie ≤ 60% mediany |
| `defaults.catalogIds` | opcjonalne ID katalogu (np. "Gry i konsole"), do podejrzenia w URL na stronie Vinted |
| `watches[].query` | fraza wyszukiwania (tytuł gry, platforma) |
| `watches[].maxPrice` | twardy próg ceny dla alertu |

## Zastrzeżenia

- To **nieoficjalne** API — Vinted może je zmienić lub ograniczać zbyt częste
  zapytania. Tracker odświeża anonimową sesję przy 401/403 i losowo rozciąga
  odstępy, ale używaj rozsądnych interwałów i tylko do użytku osobistego.
- Mediana rynkowa jest tym lepsza, im dłużej tracker działa i im
  precyzyjniejsze jest zapytanie (np. `"mario kart 8 deluxe switch"` zamiast
  `"mario"`).
