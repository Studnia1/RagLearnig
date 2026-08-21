using System.Collections.Concurrent;

namespace VintedTracker;

public sealed class GameStats
{
    public required string Query { get; init; }
    public decimal? Median { get; set; }
    public int SampleSize { get; set; }
    public int ListingsLastCycle { get; set; }
    public DateTimeOffset? LastChecked { get; set; }
    public string? LastError { get; set; }
}

/// <summary>
/// Jeden cykl trackera: dla każdej gry z watchlisty pobiera najnowsze oferty,
/// ocenia nowe i zapisuje wynik. Współdzielony przez pętlę w tle i tryb --once.
/// </summary>
public sealed class TrackerEngine(
    Config config,
    VintedClient client,
    StateStore store,
    WatchlistStore watchlist,
    Notifier notifier)
{
    private readonly ConcurrentDictionary<string, GameStats> _stats = new();
    private bool _firstRun = true;

    public DateTimeOffset? LastCycleFinished { get; private set; }
    public bool CycleInProgress { get; private set; }

    public IReadOnlyDictionary<string, GameStats> Stats => _stats;

    public async Task RunCycleAsync(CancellationToken ct)
    {
        CycleInProgress = true;
        try
        {
            foreach (var game in watchlist.Snapshot())
            {
                ct.ThrowIfCancellationRequested();
                await CheckGameAsync(game, ct);
                await Task.Delay(TimeSpan.FromSeconds(1 + Random.Shared.NextDouble() * 2), ct);
            }
            store.Save();
            _firstRun = false;
            LastCycleFinished = DateTimeOffset.UtcNow;
        }
        finally
        {
            CycleInProgress = false;
        }
    }

    private async Task CheckGameAsync(GameWatch game, CancellationToken ct)
    {
        var stats = _stats.GetOrAdd(game.Query, q => new GameStats { Query = q });
        var catalogIds = game.CatalogIds is { Count: > 0 } ids ? ids : config.Defaults.CatalogIds;

        IReadOnlyList<Listing> listings;
        try
        {
            listings = await client.SearchAsync(game.Query, catalogIds, ct: ct);
            stats.LastError = null;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            stats.LastError = e.Message;
            Console.Error.WriteLine($"[error] Zapytanie \"{game.Query}\" nie powiodło się: {e.Message}");
            return;
        }

        // Mediana liczona tylko z ofert wyglądających na grę (bieżące + historia).
        var market = listings
            .Where(l => DealEvaluator.IsRelevant(l.Title))
            .Select(l => l.Price)
            .Concat(store.RecentPrices(game.Query))
            .ToList();

        var sane = market.Where(p => p >= DealEvaluator.MinSanePrice).ToList();
        stats.SampleSize = sane.Count;
        stats.Median = sane.Count >= DealEvaluator.MinSample ? DealEvaluator.TrimmedMedian(sane) : null;
        stats.ListingsLastCycle = listings.Count;
        stats.LastChecked = DateTimeOffset.UtcNow;

        foreach (var listing in listings)
        {
            if (store.IsKnown(listing.Id))
                continue;
            var verdict = DealEvaluator.Evaluate(listing, game.MaxPrice, market);
            store.Remember(listing.Id, new SeenItem
            {
                Query = game.Query,
                Title = listing.Title,
                Price = listing.Price,
                Currency = listing.Currency,
                Url = listing.Url,
                FirstSeenUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                Tier = _firstRun ? DealTier.None : verdict.Tier,
                Score = verdict.Score,
                Reasons = verdict.Reasons.ToList(),
                PhotoUrl = listing.PhotoUrl,
            });
            // Pierwszy przebieg tylko zapełnia stan — wszystko byłoby "nowe",
            // a mediana i tak dopiero się buduje. Push tylko dla mocnych okazji;
            // zwykłe i podejrzane lądują w UI.
            if (!_firstRun && verdict.Tier == DealTier.Strong)
                await notifier.SendAsync(Notifier.FormatDeal(listing, game.Title, verdict), ct);
        }
    }
}
