namespace VintedTracker;

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
    Notifier notifier)
{
    public DateTimeOffset? LastCycleFinished { get; private set; }
    public bool CycleInProgress { get; private set; }
    public int LastCyclePages { get; private set; }
    public int LastCycleNewItems { get; private set; }
    public string? LastError { get; private set; }
    public string? CatalogInfo { get; private set; }

    public async Task RunCycleAsync(CancellationToken ct)
    {
        CycleInProgress = true;
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
                    Console.WriteLine($"[info] Limit stronicowania Vinted na stronie {page} — kończę przebieg");
                    break;
                }
                catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
                {
                    LastError = e.Message;
                    Console.Error.WriteLine($"[error] Strona {page} katalogu nie powiodła się: {e.Message}");
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

            var promoted = store.PromoteAutoGames(config.Defaults.AutoPromoteMinSample);
            if (promoted > 0)
                Console.WriteLine($"[info] Auto-promocja: {promoted} nowych grup gier w słowniku");

            LastCyclePages = pages;
            LastCycleNewItems = newItems;
            LastCycleFinished = DateTimeOffset.UtcNow;
        }
        finally
        {
            CycleInProgress = false;
        }
    }

    private async Task ProcessListingAsync(
        Listing listing,
        GameMatcher matcher,
        Dictionary<(string, string), (string Key, string Title)> autoIndex,
        bool firstRun,
        CancellationToken ct)
    {
        var relevant = DealEvaluator.IsRelevant(listing.Title);
        var normKey = TitleNormalizer.NormKey(listing.Title);
        var platform = TitleNormalizer.DetectPlatform(listing.Title);

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
            }
            else if (autoIndex.TryGetValue((normKey, platform ?? ""), out var auto))
            {
                gameKey = auto.Key;
                gameTitle = auto.Title;
            }
        }

        var verdict = gameKey is not null
            ? DealEvaluator.Evaluate(listing, maxPrice, store.PricesFor(gameKey))
            : new DealVerdict(DealTier.None, 0, []);
        if (firstRun && verdict.Tier != DealTier.Suspicious)
            verdict = verdict with { Tier = DealTier.None }; // backfill nie alertuje

        store.Insert(new ItemRecord(
            listing.Id, listing.Title, listing.Price, listing.Currency, listing.Url,
            listing.PhotoUrl, normKey, platform, gameKey, gameTitle, relevant,
            verdict.Tier, verdict.Score, verdict.ReferencePrice, verdict.Reasons));

        // Push tylko dla mocnych okazji z realną marżą — reszta ląduje w UI.
        var margin = verdict.ReferencePrice is { } r ? r - listing.Price : 0;
        if (!firstRun && verdict.Tier == DealTier.Strong && margin >= config.Defaults.MinMargin)
            await notifier.SendAsync(Notifier.FormatDeal(listing, gameTitle ?? "?", verdict), ct);
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
                Console.WriteLine($"[info] Wykryto katalog gier: {found.Description}");
                return found.Ids;
            }
            LastError = "Nie udało się wykryć katalogu gier (drzewo kategorii ani sondy wyszukiwania) — podaj catalogIds w config.json";
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            LastError = $"Auto-wykrycie katalogu nie powiodło się: {e.Message}";
        }
        Console.Error.WriteLine($"[error] {LastError}");
        return [];
    }
}
