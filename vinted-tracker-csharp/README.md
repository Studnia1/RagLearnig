# Vinted Games Tracker (C#)

Śledzi **wszystkie** nowo dodawane oferty w kategorii gier na Vinted,
rozpoznaje tytuły i platformy, buduje cennik rynkowy i alarmuje o okazjach
z realną marżą — pod kupowanie gier tanio (dla siebie albo do odsprzedaży).
C#/.NET 8; jedyna zależność aplikacji to Microsoft.Data.Sqlite.

![Dashboard](docs/dashboard.png)

## Architektura: firehose + watermark

Zamiast zapytania per gra tracker pobiera **jeden strumień najnowszych ofert
z całej kategorii gier** i stronicuje tylko do miejsca, w którym zaczynają
się oferty już widziane. Obciążenie nie zależy więc od liczby śledzonych
gier — to zwykle **1–2 zapytania co cykl**, niezależnie czy śledzisz 16 gier
czy cały rynek. Duży jest tylko pierwszy przebieg (backfill, domyślnie
20 stron ≈ 2000 ofert), który buduje bazę cen i nie alertuje.

Każda nowa oferta przechodzi przez:

1. **Filtr trafności** — etui, steelbooki, amiibo, "same kartony" odpadają
   po słowach kluczowych i nie zaśmiecają cenników.
2. **Normalizację tytułu** — małe litery, bez diakrytyków i interpunkcji,
   bez słów-szumu ("NOWA", "folia", "stan idealny"); kolejność słów bez
   znaczenia. **Platforma wykrywana osobno** (Switch/PS4/PS5/Xbox…), bo ta
   sama gra na różnych platformach ma różne ceny.
3. **Dopasowanie do gry** — najpierw watchlista (`games.json`; pewne
   dopasowanie wszystkich tokenów zapytania lub aliasu; niejednoznaczność,
   np. bundle "Sword + Shield", nie alertuje). Nierozpoznane oferty grupują
   się po znormalizowanym tytule i platformie — gdy grupa urośnie do
   `autoPromoteMinSample` ofert, staje się grą **auto** i od tej pory też ma
   medianę i może alertować. Słownik rośnie więc sam z rynkiem.
4. **Ocenę okazji** — jak wcześniej: przycięta mediana (min 5 próbek),
   mocna okazja = dwa niezależne sygnały (≤60% mediany **i** dolny kwartyl,
   albo ≤85% ręcznego progu), zwykła ≤75% mediany, ≤30% mediany =
   „podejrzanie tanio" (scam-bezpiecznik).
5. **Bramkę marży** — push (Telegram/Discord) idzie tylko dla mocnych okazji
   z marżą `mediana − cena ≥ minMargin` (domyślnie **50 zł**), żeby alerty
   były warte odbioru z telefonu. Wszystko i tak ląduje w dashboardzie.

Stan mieszka w SQLite (`data/tracker.sqlite3`) — baza rośnie z rynkiem
i przeżywa restarty (backfill nie powtarza się).

## Wymagania

- [.NET SDK 8.0](https://dotnet.microsoft.com/download/dotnet/8.0)

## Uruchomienie

```bash
cd vinted-tracker-csharp
dotnet run --project src/VintedTracker
# Dashboard: http://localhost:5177
```

Konfiguracja jest opcjonalna — bez `config.json` działają wartości domyślne
(vinted.pl, cykl co 5 min, marża 50 zł, port 5177). Żeby coś zmienić:
`cp config.example.json config.json` i edytuj.

ID katalogu gier tracker próbuje wykryć sam z drzewa kategorii Vinted;
jeśli mu się nie uda (zobaczysz błąd w pasku statusu), podejrzyj ID w URL
katalogu w przeglądarce (np. `.../catalog/3025-gry`) i wpisz w `catalogIds`.

Tryb jednorazowy bez UI (np. pod crona): `dotnet run --project src/VintedTracker -- --once`.

## Alerty na Telegram (zalecane przy flippingu)

1. Napisz do [@BotFather](https://t.me/BotFather) → `/newbot` → dostajesz
   **token** (`123456:ABC...`).
2. Napisz cokolwiek do swojego nowego bota (musi móc Ci odpisywać).
3. Wejdź na `https://api.telegram.org/bot<TOKEN>/getUpdates` — w odpowiedzi
   znajdziesz `"chat":{"id":123456789}` — to Twój **chat id**.
4. Przed startem trackera:

```bash
export TELEGRAM_BOT_TOKEN="123456:ABC..."
export TELEGRAM_CHAT_ID="123456789"
```

Analogicznie działa `DISCORD_WEBHOOK_URL` (webhook kanału).

## Dashboard

- **Pasek statusu** — ostatni cykl, ile nowych ofert i stron, rozmiar bazy,
  liczba gier auto, wykryty katalog, ewentualne błędy pobierania.
- **Okazje** — karty z całego rynku (watchlista + gry auto): odznaka
  🔥 mocna / okazja / ⚠️ podejrzanie tanio, różnica cenowa w zł, powody,
  link do oferty, filtry.
- **Śledzone gry** — mediana rynkowa, próbka, liczba widzianych ofert
  i okazji, edycja progu `maxPrice` w tabeli, dodawanie/usuwanie gier.
- **Sprawdź teraz** — ręczne wywołanie cyklu.

Watchlista (`games.json`) wspiera aliasy dopasowania, np.:

```json
{ "title": "Zelda: Tears of the Kingdom", "query": "zelda tears of the kingdom switch",
  "aliases": ["zelda totk"], "maxPrice": 150 }
```

## Testy

```bash
dotnet test
```

## Zastrzeżenia

- To **nieoficjalne** API — Vinted może je zmienić lub ograniczać zbyt
  częste zapytania. Tracker odświeża anonimową sesję przy 401/403, rozciąga
  odstępy losowo i stronicuje oszczędnie; używaj rozsądnych interwałów
  i tylko do użytku osobistego.
- Mediany są tym lepsze, im dłużej tracker działa; gry auto potrzebują
  `autoPromoteMinSample` ofert, zanim zaczną alertować.
- Dashboard nie ma logowania — słuchaj na `localhost` (domyślnie) albo
  zabezpiecz go samodzielnie, zanim wystawisz na zewnątrz.
