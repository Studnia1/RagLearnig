using System.Collections.Concurrent;

namespace VintedTracker;

/// <summary>Aktualnie najtańsza wiarygodna oferta śledzonej gry (skan na żądanie).
/// <paramref name="Bargain"/> = cena ≤ 75% mediany, czyli realna okazja.</summary>
/// <param name="Live">true = potwierdzone skanem na żywo; false = przybliżenie
/// z bazy (oferta mogła się już sprzedać).</param>
public sealed record CheapestNow(
    string ListingTitle, decimal Price, string Currency, string Url,
    DateTimeOffset CheckedAt, bool Bargain, decimal? Median, bool Live = true,
    string? AiNote = null);

/// <summary>
/// Silnik w trybie firehose: zamiast zapytania per gra pobiera jeden strumień
/// najnowszych ofert z całej kategorii gier i stronicuje tylko do miejsca,
/// w którym zaczynają się oferty już widziane (watermark). Obciążenie nie
/// zależy więc od liczby śledzonych gier, tylko od tempa nowych ofert —
/// zwykle 1-2 strony na cykl. Duży jest tylko pierwszy przebieg (backfill),
/// który buduje bazę cen i nie alertuje.
/// </summary>
public sealed class TrackerEngine(
    Config config,
    VintedClient client,
    SqliteStore store,
    WatchlistStore watchlist,
    Notifier notifier,
    VisionVerifier vision)
{
    public DateTimeOffset? LastCycleFinished { get; private set; }
    public bool CycleInProgress { get; private set; }
    /// <summary>Ostatni cykl zakończył się blokadą anty-bot — pętla wydłuża odstępy.</summary>
    public bool LastCycleBlocked { get; private set; }

    /// <summary>Wyniki skanu "najtańsze teraz" per zapytanie watchlisty.</summary>
    public ConcurrentDictionary<string, CheapestNow> Cheapest { get; } = new();
    public bool CheapestScanInProgress { get; private set; }

    private IReadOnlyList<string> _blocklist = [];
    private int _cJunk, _cWatch, _cAuto, _cDeal, _cStrong, _cSusp;

    /// <summary>Bump przy każdej zmianie wbudowanych filtrów/platform — wymusza
    /// jednorazowe porządki w bazie przy najbliższym cyklu.</summary>
    private const string FilterVersion = "7";

    /// <summary>Wyniki "najtańsze teraz" przeżywają restart (meta w SQLite).</summary>
    public void LoadPersistedCheapest()
    {
        if (store.GetMeta("cheapest") is not { } raw)
            return;
        var saved = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, CheapestNow>>(raw)
            ?? new Dictionary<string, CheapestNow>();
        foreach (var (query, value) in saved)
            Cheapest[query] = value;
    }

    private void PersistCheapest() =>
        store.SetMeta("cheapest", System.Text.Json.JsonSerializer.Serialize(Cheapest.ToDictionary()));

    private bool CheapestScanIsDue()
    {
        if (store.GetMeta("cheapest_at") is not { } raw)
            return true;
        return !DateTimeOffset.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture,
                   System.Globalization.DateTimeStyles.RoundtripKind, out var last)
               || DateTimeOffset.UtcNow - last > TimeSpan.FromHours(24);
    }

    private void RunSweepIfNeeded(GameMatcher matcher)
    {
        if (store.GetMeta("filter_version") == FilterVersion)
            return;
        var rows = store.SnapshotForSweep();
        var irrelevant = new List<long>();
        var unmatch = new List<long>();
        foreach (var row in rows)
        {
            var platform = TitleNormalizer.DetectPlatform(row.Title);
            if (row.Relevant && (!DealEvaluator.IsRelevant(row.Title, _blocklist)
                || (platform is not null && config.Defaults.ExcludedPlatforms.Contains(platform))))
                irrelevant.Add(row.Id);
            else if (row.GameKey is { } key && key.StartsWith("watch:")
                && matcher.Match(row.Title, platform)?.Key != key)
                unmatch.Add(row.Id);
        }
        store.ApplySweep(irrelevant, unmatch);
        // Zapamiętane "najtańsze teraz" liczyły się starymi filtrami — czyścimy
        // i wymuszamy świeży skan, żeby kolumna nie rozjeżdżała się z panelami.
        Cheapest.Clear();
        store.SetMeta("cheapest", "{}");
        store.SetMeta("cheapest_at", "");
        store.SetMeta("filter_version", FilterVersion);
        Log.Info($"Porządki po zmianie filtrów: {irrelevant.Count} ofert odfiltrowanych, " +
                 $"{unmatch.Count} odpiętych od gier (przejrzano {rows.Count}); " +
                 "wyniki najtańszych wyzerowane do ponownego skanu");
    }

    /// <summary>
    /// Skan na żądanie: dla każdej gry z watchlisty pobiera oferty posortowane
    /// od najtańszej i bierze pierwszą, która wygląda na tę grę (filtr
    /// akcesoriów/kodów + dopasowanie tytułu + próg sensowności ceny).
    /// Celowo nie chodzi w pętli — to ~1 zapytanie na grę.
    /// </summary>
    public async Task ScanCheapestAsync(CancellationToken ct)
    {
        if (CheapestScanInProgress)
            return;
        CheapestScanInProgress = true;
        try
        {
            var matcher = new GameMatcher(watchlist.Snapshot().Select(GamePattern.FromWatch).ToList());
            var blocklist = store.GetBlocklist();
            var misses = new List<string>();
            foreach (var game in watchlist.Snapshot())
            {
                ct.ThrowIfCancellationRequested();

                // Próg wiarygodności idzie już do wyszukiwarki (price_from):
                // dla popularnych tytułów 48 najtańszych wyników to w całości
                // naklejki i karty za grosze — bez tego prawdziwa gra nie
                // mieściła się w pobranej stronie.
                var gameKey = "watch:" + game.Query.ToLowerInvariant();
                var prices = store.PricesFor(gameKey);
                var floor = DealEvaluator.CredibleFloor(prices);
                var sane = prices.Where(p => p >= DealEvaluator.MinSanePrice).ToList();
                decimal? median = sane.Count >= DealEvaluator.MinSample
                    ? DealEvaluator.TrimmedMedian(sane) : null;

                IReadOnlyList<Listing> listings;
                try
                {
                    listings = await client.SearchAsync(
                        game.Query, order: "price_low_to_high", perPage: 48,
                        priceFrom: floor > DealEvaluator.MinSanePrice ? floor : null, ct: ct);
                }
                catch (VintedBlockedException e)
                {
                    LastError = e.Message;
                    Log.Warn($"Skan najtańszych przerwany: {e.Message}");
                    PersistCheapest();
                    return;
                }
                catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
                {
                    Log.Warn($"Najtańsza \"{game.Query}\" nieudana: {e.Message}");
                    continue;
                }

                var candidates = listings.Where(l =>
                    l.Price >= floor
                    && DealEvaluator.IsRelevant(l.Title, blocklist)
                    && matcher.Match(l.Title, TitleNormalizer.DetectPlatform(l.Title))?.Key == gameKey);

                Listing? cheapest = null;
                string? aiNote = null;
                if (vision.Enabled)
                {
                    // AI ogląda zdjęcia maks. 3 najtańszych kandydatów i bierze
                    // pierwszego, który naprawdę jest tą grą.
                    foreach (var candidate in candidates.Take(3))
                    {
                        var ai = await vision.VerifyAsync(game.Title, candidate.Title, candidate.PhotoUrl, ct);
                        if (ai is { IsMatch: false })
                            continue;
                        cheapest = candidate;
                        aiNote = ai is null ? null : $"AI: {(ai.IsMatch ? "✅" : "")} {ai.Note}".Trim();
                        break;
                    }
                }
                else
                {
                    cheapest = candidates.FirstOrDefault();
                }

                if (cheapest is not null)
                    Cheapest[game.Query] = new CheapestNow(
                        cheapest.Title, cheapest.Price, cheapest.Currency, cheapest.Url,
                        DateTimeOffset.UtcNow,
                        Bargain: median is { } m && cheapest.Price <= m * DealEvaluator.DealRatio,
                        Median: median,
                        AiNote: aiNote);
                else
                    misses.Add(game.Title);

                await Task.Delay(TimeSpan.FromSeconds(1 + Random.Shared.NextDouble() * 1.5), ct);
            }
            store.SetMeta("cheapest_at", DateTimeOffset.UtcNow.ToString("O"));
            PersistCheapest();
            var missSample = string.Join(", ", misses.Take(15));
            Log.Info($"Skan najtańszych zakończony: wyniki dla {Cheapest.Count} gier, " +
                     $"bez wiarygodnej oferty: {misses.Count}" +
                     (misses.Count > 0 ? $" ({missSample}{(misses.Count > 15 ? ", …" : "")})" : ""));
        }
        finally
        {
            CheapestScanInProgress = false;
        }
    }
    public int LastCyclePages { get; private set; }
    public int LastCycleNewItems { get; private set; }
    public string? LastError { get; private set; }
    public string? CatalogInfo { get; private set; }

    public async Task RunCycleAsync(CancellationToken ct)
    {
        CycleInProgress = true;
        LastCycleBlocked = false;
        try
        {
            var catalogIds = await EnsureCatalogAsync(ct);
            if (catalogIds.Count == 0)
                return; // LastError ustawione w EnsureCatalogAsync

            // Backfill rozpoznajemy po pustej bazie — przeżywa restart procesu.
            var firstRun = store.ItemCount() == 0;
            var maxPages = firstRun ? config.Defaults.BackfillPages : config.Defaults.MaxPagesPerCycle;
            var matcher = new GameMatcher(watchlist.Snapshot().Select(GamePattern.FromWatch).ToList());
            var autoIndex = store.AutoGameIndex();
            _blocklist = store.GetBlocklist();
            RunSweepIfNeeded(matcher);
            _cJunk = _cWatch = _cAuto = _cDeal = _cStrong = _cSusp = 0;

            var pages = 0;
            var newItems = 0;
            for (var page = 1; page <= maxPages; page++)
            {
                ct.ThrowIfCancellationRequested();
                IReadOnlyList<Listing> listings;
                try
                {
                    listings = await client.CatalogPageAsync(catalogIds, page, ct: ct);
                }
                catch (HttpRequestException e) when (e.Message.Contains("Page offset is invalid"))
                {
                    // Vinted nie pozwala stronicować głębiej niż ~1000 ofert —
                    // to naturalny koniec backfillu, nie błąd.
                    Log.Info($"Limit stronicowania Vinted na stronie {page} — kończę przebieg");
                    break;
                }
                catch (VintedBlockedException e)
                {
                    LastCycleBlocked = true;
                    LastError = e.Message;
                    Log.Warn($"{e.Message}");
                    break;
                }
                catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
                {
                    LastError = e.Message;
                    Log.Error($"Strona {page} katalogu nie powiodła się: {e.Message}");
                    break;
                }

                pages++;
                var pageNew = 0;
                foreach (var listing in listings)
                {
                    if (store.IsKnown(listing.Id))
                        continue;
                    pageNew++;
                    await ProcessListingAsync(listing, matcher, autoIndex, firstRun, ct);
                }
                newItems += pageNew;
                LastError = null;

                // Watermark: strona bez żadnej nowej oferty = dotarliśmy do
                // miejsca, które znamy. Pusta strona = koniec katalogu.
                if (pageNew == 0 || listings.Count == 0)
                    break;
                await Task.Delay(TimeSpan.FromSeconds(1.5 + Random.Shared.NextDouble() * 1.5), ct);
            }

            if (!LastCycleBlocked)
                await SeedWatchlistAsync(matcher, autoIndex, ct);

            var promoted = store.PromoteAutoGames(config.Defaults.AutoPromoteMinSample);
            if (promoted > 0)
                Log.Info($"Auto-promocja: {promoted} nowych grup gier w słowniku");

            LastCyclePages = pages;
            LastCycleNewItems = newItems;
            LastCycleFinished = DateTimeOffset.UtcNow;
            Log.Info($"Cykl: {pages} str., {newItems} nowych (gruz {_cJunk}, watchlista {_cWatch}, " +
                     $"auto {_cAuto}); okazje: {_cStrong} mocnych, {_cDeal} zwykłych, {_cSusp} podejrzanych; " +
                     $"baza {store.ItemCount()}");

            // Raz na dobę odświeżamy "najtańsze teraz" automatycznie — kolumna
            // żyje bez klikania, a przycisk zostaje do odświeżenia na żądanie.
            if (!LastCycleBlocked && !firstRun && CheapestScanIsDue())
            {
                Log.Info("Automatyczny dzienny skan najtańszych…");
                await ScanCheapestAsync(ct);
            }
        }
        finally
        {
            CycleInProgress = false;
        }
    }

    /// <summary>
    /// Jednorazowe zasianie puli cen dla gier z watchlisty: firehose niesie
    /// najnowsze oferty z całej kategorii, więc konkretny tytuł zbiera próbkę
    /// powoli. Celowane wyszukiwanie (raz na grę, wynik zapamiętany w meta)
    /// odblokowuje mediany od pierwszego dnia. Zasiane oferty nie alertują —
    /// to baza cen, nie nowości.
    /// </summary>
    /// <summary>Ile gier maksymalnie zasiać w jednym cyklu — duża watchlista
    /// rozkłada się na kolejne cykle zamiast strzelać setką zapytań naraz.</summary>
    private const int SeedBatchPerCycle = 10;

    private async Task SeedWatchlistAsync(
        GameMatcher matcher,
        Dictionary<(string, string), (string Key, string Title)> autoIndex,
        CancellationToken ct)
    {
        var seededThisCycle = 0;
        foreach (var game in watchlist.Snapshot())
        {
            ct.ThrowIfCancellationRequested();
            if (seededThisCycle >= SeedBatchPerCycle)
            {
                Log.Info("Limit zasiewania na cykl — reszta gier w kolejnych cyklach");
                return;
            }
            var metaKey = "seeded:watch:" + game.Query.ToLowerInvariant();
            if (store.GetMeta(metaKey) == "1")
                continue;
            seededThisCycle++;

            IReadOnlyList<Listing> listings;
            try
            {
                listings = await client.SearchAsync(game.Query, ct: ct);
            }
            catch (VintedBlockedException e)
            {
                LastCycleBlocked = true;
                LastError = e.Message;
                Log.Warn($"Zasiewanie przerwane: {e.Message}");
                return;
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                Log.Warn($"Zasiewanie \"{game.Query}\" nieudane: {e.Message}");
                continue;
            }

            var added = 0;
            foreach (var listing in listings)
            {
                if (store.IsKnown(listing.Id))
                    continue;
                added++;
                // firstRun: true — zasiane oferty budują tylko bazę cen.
                await ProcessListingAsync(listing, matcher, autoIndex, firstRun: true, ct);
            }
            store.SetMeta(metaKey, "1");
            Log.Info($"Zasiano \"{game.Query}\": {added} ofert do bazy cen");
            await Task.Delay(TimeSpan.FromSeconds(1.5 + Random.Shared.NextDouble() * 1.5), ct);
        }
    }

    private async Task ProcessListingAsync(
        Listing listing,
        GameMatcher matcher,
        Dictionary<(string, string), (string Key, string Title)> autoIndex,
        bool firstRun,
        CancellationToken ct)
    {
        var normKey = TitleNormalizer.NormKey(listing.Title);
        var platform = TitleNormalizer.DetectPlatform(listing.Title);
        var relevant = DealEvaluator.IsRelevant(listing.Title, _blocklist)
            && (platform is null || !config.Defaults.ExcludedPlatforms.Contains(platform));
        if (!relevant)
            _cJunk++;

        string? gameKey = null;
        string? gameTitle = null;
        decimal? maxPrice = null;
        if (relevant && normKey.Length > 0)
        {
            if (matcher.Match(listing.Title, platform) is { } watch)
            {
                gameKey = watch.Key;
                gameTitle = watch.Title;
                maxPrice = watch.MaxPrice;
                _cWatch++;
            }
            else if (autoIndex.TryGetValue((normKey, platform ?? ""), out var auto))
            {
                gameKey = auto.Key;
                gameTitle = auto.Title;
                _cAuto++;
            }
        }

        var verdict = gameKey is not null
            ? DealEvaluator.Evaluate(listing, maxPrice, store.PricesFor(gameKey))
            : new DealVerdict(DealTier.None, 0, []);

        // Polowanie per platforma: tania gra na 3DS/Vita/PS3/PS4 to okazja
        // sama w sobie — bez dopasowania do konkretnego tytułu.
        var huntHit = false;
        if (relevant && verdict.Tier != DealTier.Strong
            && DealEvaluator.HuntVerdict(platform, listing.Price, config.Defaults.PlatformHunts) is { } hunt)
        {
            verdict = hunt;
            huntHit = true;
            gameTitle ??= $"Polowanie: {platform}";
        }

        if (firstRun && verdict.Tier != DealTier.Suspicious)
            verdict = verdict with { Tier = DealTier.None }; // backfill nie alertuje

        // Kandydat na push przechodzi weryfikację po zdjęciu (jeśli włączona):
        // AI ogląda fotkę i odsiewa samo pudełko / złą platformę / merch.
        // Trafienie z polowania omija bramkę marży — próg ceny jest już bramką.
        var wouldPush = !firstRun && verdict.Tier == DealTier.Strong
            && (huntHit || (verdict.ReferencePrice is { } rp
                && rp - listing.Price >= config.Defaults.MinMargin));
        if (wouldPush && vision.Enabled)
        {
            var ai = await vision.VerifyAsync(
                huntHit ? "dowolna gra na tę platformę" : gameTitle ?? "?",
                listing.Title, listing.PhotoUrl, ct, platform);
            if (ai is { IsMatch: false })
                verdict = verdict with
                {
                    Tier = DealTier.Suspicious,
                    Reasons = [.. verdict.Reasons, $"AI po zdjęciu: {ai.Note}"],
                };
            else if (ai is { IsMatch: true })
                verdict = verdict with
                {
                    Reasons = [.. verdict.Reasons,
                        $"AI po zdjęciu: ✅ {(ai.Complete ? "kompletna" : "sprawdź kompletność")} — {ai.Note}"],
                };
        }

        store.Insert(new ItemRecord(
            listing.Id, listing.Title, listing.Price, listing.Currency, listing.Url,
            listing.PhotoUrl, normKey, platform, gameKey, gameTitle, relevant,
            verdict.Tier, verdict.Score, verdict.ReferencePrice, verdict.Reasons));

        switch (verdict.Tier)
        {
            case DealTier.Strong: _cStrong++; break;
            case DealTier.Deal: _cDeal++; break;
            case DealTier.Suspicious: _cSusp++; break;
        }

        // Push tylko dla mocnych okazji z realną marżą — reszta ląduje w UI.
        var margin = verdict.ReferencePrice is { } r ? r - listing.Price : 0;
        // wouldPush trzyma warunki (marża lub polowanie); Tier mógł spaść po AI.
        if (wouldPush && verdict.Tier == DealTier.Strong)
        {
            Log.Info($"PUSH: {gameTitle} — {listing.Price:0.00} {listing.Currency} (marża {margin:0.00})");
            await notifier.SendAsync(Notifier.FormatDeal(listing, gameTitle ?? "?", verdict), ct);
        }
    }

    private async Task<IReadOnlyList<int>> EnsureCatalogAsync(CancellationToken ct)
    {
        if (config.Defaults.CatalogIds.Count > 0)
        {
            CatalogInfo = $"z konfiguracji: {string.Join(",", config.Defaults.CatalogIds)}";
            return config.Defaults.CatalogIds;
        }

        if (store.GetMeta("catalog_ids") is { } saved)
        {
            var savedIds = saved.Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(s => int.TryParse(s, out var id) ? id : (int?)null)
                .Where(id => id is not null).Select(id => id!.Value).ToList();
            if (savedIds.Count > 0)
            {
                CatalogInfo = store.GetMeta("catalog_info") ?? saved;
                return savedIds;
            }
        }

        try
        {
            if (await client.DiscoverGamesCatalogsAsync(ct) is { } found)
            {
                store.SetMeta("catalog_ids", string.Join(",", found.Ids));
                store.SetMeta("catalog_info", found.Description);
                CatalogInfo = found.Description;
                Log.Info($"Wykryto katalog gier: {found.Description}");
                return found.Ids;
            }
            LastError = "Nie udało się wykryć katalogu gier (drzewo kategorii ani sondy wyszukiwania) — podaj catalogIds w config.json";
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            LastError = $"Auto-wykrycie katalogu nie powiodło się: {e.Message}";
        }
        Log.Error($"{LastError}");
        return [];
    }
}
