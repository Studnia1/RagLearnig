namespace VintedTracker;

/// <summary>
/// Pętla główna: cyklicznie odpytuje Vinted i zgłasza okazje.
/// Uruchomienie: <c>dotnet run -- --config config.json [--once] [--verbose]</c>
/// </summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var configPath = "config.json";
        var once = false;
        var verbose = false;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--config" when i + 1 < args.Length:
                    configPath = args[++i];
                    break;
                case "--once":
                    once = true;
                    break;
                case "--verbose":
                    verbose = true;
                    break;
                default:
                    Console.Error.WriteLine($"Nieznany argument: {args[i]}");
                    Console.Error.WriteLine("Użycie: VintedTracker [--config config.json] [--once] [--verbose]");
                    return 2;
            }
        }

        var config = Config.Load(configPath);
        var client = new VintedClient(config.Defaults.BaseUrl);
        var store = new StateStore(config.Defaults.StatePath);
        var notifier = new Notifier();

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        var firstRun = true;
        while (!cts.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(config, client, store, notifier, firstRun, verbose, cts.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception e)
            {
                Console.Error.WriteLine($"[error] Przebieg zakończony błędem — próbuję dalej: {e.Message}");
            }
            firstRun = false;
            if (once)
                break;

            var interval = config.Defaults.PollIntervalSeconds;
            var sleep = TimeSpan.FromSeconds(interval + Random.Shared.NextDouble() * interval * 0.2);
            Log(verbose: true, $"Śpię {sleep.TotalSeconds:0} s");
            try { await Task.Delay(sleep, cts.Token); }
            catch (OperationCanceledException) { break; }
        }

        return 0;
    }

    private static async Task RunOnceAsync(
        Config config, VintedClient client, StateStore store, Notifier notifier,
        bool firstRun, bool verbose, CancellationToken ct)
    {
        var dealsFound = 0;
        foreach (var watch in config.Watches)
        {
            ct.ThrowIfCancellationRequested();
            var catalogIds = watch.CatalogIds is { Count: > 0 } ids ? ids : config.Defaults.CatalogIds;
            var discount = watch.DiscountThreshold ?? config.Defaults.DiscountThreshold;

            IReadOnlyList<Listing> listings;
            try
            {
                listings = await client.SearchAsync(watch.Query, catalogIds, ct: ct);
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                Console.Error.WriteLine($"[error] Zapytanie \"{watch.Query}\" nie powiodło się: {e.Message}");
                continue;
            }

            // Mediana liczona z tego, co widać teraz + historia z pliku stanu.
            var market = listings.Select(l => l.Price).Concat(store.RecentPrices(watch.Query)).ToList();

            foreach (var listing in listings)
            {
                if (store.IsKnown(listing.Id))
                    continue;
                var verdict = DealEvaluator.Evaluate(listing, watch.MaxPrice, market, discount);
                store.Remember(listing.Id, new SeenItem
                {
                    Query = watch.Query,
                    Title = listing.Title,
                    Price = listing.Price,
                    Currency = listing.Currency,
                    Url = listing.Url,
                    FirstSeenUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                    IsDeal = verdict.IsDeal,
                });
                // Pierwszy przebieg tylko zapełnia stan — wszystko byłoby "nowe",
                // a mediana i tak dopiero się buduje.
                if (verdict.IsDeal && !firstRun)
                {
                    dealsFound++;
                    await notifier.SendAsync(Notifier.FormatDeal(listing, watch.Query, verdict.Reasons), ct);
                }
            }

            Log(verbose, $"[{watch.Query}] ofert: {listings.Count}, nowych okazji dotąd: {dealsFound}");
            await Task.Delay(TimeSpan.FromSeconds(1 + Random.Shared.NextDouble() * 2), ct);
        }

        store.Save();
    }

    private static void Log(bool verbose, string message)
    {
        if (verbose)
            Console.WriteLine($"{DateTime.Now:HH:mm:ss} {message}");
    }
}
