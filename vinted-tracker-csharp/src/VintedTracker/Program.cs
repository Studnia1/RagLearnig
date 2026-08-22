using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace VintedTracker;

/// <summary>
/// Tracker okazji + webowy dashboard.
/// Uruchomienie: <c>dotnet run -- [--config config.json] [--once]</c>.
/// Domyślnie startuje serwer (adres w konfiguracji, <c>listenUrl</c>)
/// z pętlą sprawdzającą w tle; <c>--once</c> robi jeden przebieg bez UI.
/// </summary>
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var configPath = "config.json";
        var once = false;
        var probe = false;
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
                case "--probe":
                    probe = true;
                    break;
                default:
                    Console.Error.WriteLine($"Nieznany argument: {args[i]}");
                    Console.Error.WriteLine("Użycie: VintedTracker [--config config.json] [--once] [--probe]");
                    return 2;
            }
        }

        var config = File.Exists(configPath) ? Config.Load(configPath) : new Config();
        var client = new VintedClient(config.Defaults.BaseUrl);

        if (probe)
        {
            Console.WriteLine(await client.DebugProbeAsync("gra nintendo switch"));
            Console.WriteLine(await client.DebugProbeAsync("pokemon"));
            Console.WriteLine(await client.DiscoverFromHtmlAsync() is { } h
                ? $"HTML discovery OK: {h.Description}"
                : "HTML discovery: nie znaleziono linków katalogów gier w HTML");
            return 0;
        }

        using var store = new SqliteStore(config.Defaults.StatePath);
        var watchlist = new WatchlistStore(config.Defaults.WatchlistPath);
        var notifier = new Notifier();
        var engine = new TrackerEngine(config, client, store, watchlist, notifier);

        if (once)
        {
            using var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
            await engine.RunCycleAsync(cts.Token);
            return 0;
        }

        // Pliki danych (config/games/state) są względem katalogu roboczego,
        // ale dashboard serwujemy z wwwroot obok binarki — niezależnie od cwd.
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            WebRootPath = Path.Combine(AppContext.BaseDirectory, "wwwroot"),
        });
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.WebHost.UseUrls(config.Defaults.ListenUrl);
        builder.Services.AddSingleton(engine);
        builder.Services.AddHostedService(_ => new PollingService(engine, config.Defaults.PollIntervalSeconds));

        var app = builder.Build();
        app.UseDefaultFiles();
        app.UseStaticFiles();

        app.MapGet("/api/overview", () =>
        {
            var games = watchlist.Snapshot().Select(g =>
            {
                var stats = store.StatsFor("watch:" + g.Query.ToLowerInvariant());
                var cheapest = engine.Cheapest.GetValueOrDefault(g.Query);
                return new
                {
                    g.Title,
                    g.Query,
                    g.MaxPrice,
                    g.Aliases,
                    stats.Median,
                    SampleSize = stats.Sample,
                    SeenCount = stats.Seen,
                    DealCount = stats.Deals,
                    Cheapest = cheapest,
                };
            });
            var deals = store.RecentDeals(150).Select(d => new
            {
                d.Id,
                Query = d.Game,
                d.Title,
                d.Price,
                d.Currency,
                d.Url,
                d.PhotoUrl,
                d.Tier,
                d.Score,
                d.ReferencePrice,
                d.Reasons,
                FirstSeen = DateTimeOffset.FromUnixTimeSeconds(d.FirstSeenUnix),
            });
            return Results.Ok(new
            {
                Games = games,
                Deals = deals,
                Status = new
                {
                    engine.CycleInProgress,
                    engine.LastCycleFinished,
                    PollIntervalSeconds = config.Defaults.PollIntervalSeconds,
                    config.Defaults.BaseUrl,
                    engine.LastCyclePages,
                    engine.LastCycleNewItems,
                    engine.LastError,
                    engine.CatalogInfo,
                    ItemsTotal = store.ItemCount(),
                    AutoGames = store.AutoGameCount(),
                    config.Defaults.MinMargin,
                    engine.CheapestScanInProgress,
                },
            });
        });

        app.MapPost("/api/games", (GameWatch game) =>
        {
            if (string.IsNullOrWhiteSpace(game.Query) || string.IsNullOrWhiteSpace(game.Title))
                return Results.BadRequest(new { error = "Wymagane pola: title, query" });
            watchlist.Upsert(game);
            return Results.Ok(game);
        });

        app.MapDelete("/api/games/{query}", (string query) =>
            watchlist.Remove(query) ? Results.NoContent() : Results.NotFound());

        app.MapPost("/api/check", (TrackerEngine eng) =>
        {
            if (eng.CycleInProgress)
                return Results.Conflict(new { error = "Cykl już trwa" });
            _ = Task.Run(() => eng.RunCycleAsync(CancellationToken.None));
            return Results.Accepted();
        });

        app.MapPost("/api/cheapest", (TrackerEngine eng) =>
        {
            if (eng.CheapestScanInProgress)
                return Results.Conflict(new { error = "Skan już trwa" });
            _ = Task.Run(() => eng.ScanCheapestAsync(CancellationToken.None));
            return Results.Accepted();
        });

        Console.WriteLine($"Dashboard: {config.Defaults.ListenUrl}");
        await app.RunAsync();
        return 0;
    }

    /// <summary>Pętla w tle: cykl, sen z losowym rozrzutem, od nowa.
    /// Przy blokadzie anty-bot odstępy rosną (x2 za każdy zablokowany cykl,
    /// do 30 min), żeby nie podtrzymywać blokady kolejnymi żądaniami.</summary>
    private sealed class PollingService(TrackerEngine engine, int intervalSeconds) : BackgroundService
    {
        protected override async Task ExecuteAsync(CancellationToken ct)
        {
            var blockedStreak = 0;
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    if (!engine.CycleInProgress) // /api/check mógł już odpalić cykl
                        await engine.RunCycleAsync(ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception e)
                {
                    Console.Error.WriteLine($"[error] Przebieg zakończony błędem — próbuję dalej: {e.Message}");
                }

                blockedStreak = engine.LastCycleBlocked ? blockedStreak + 1 : 0;
                var seconds = Math.Min(intervalSeconds * Math.Pow(2, blockedStreak), 1800);
                if (blockedStreak > 0)
                    Console.WriteLine($"[info] Blokada anty-bot — następna próba za {seconds / 60:0.#} min");
                var sleep = TimeSpan.FromSeconds(seconds + Random.Shared.NextDouble() * seconds * 0.2);
                try { await Task.Delay(sleep, ct); }
                catch (OperationCanceledException) { break; }
            }
        }
    }
}
