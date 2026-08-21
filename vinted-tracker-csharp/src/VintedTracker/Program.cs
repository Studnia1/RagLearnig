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
                default:
                    Console.Error.WriteLine($"Nieznany argument: {args[i]}");
                    Console.Error.WriteLine("Użycie: VintedTracker [--config config.json] [--once]");
                    return 2;
            }
        }

        var config = File.Exists(configPath) ? Config.Load(configPath) : new Config();
        var client = new VintedClient(config.Defaults.BaseUrl);
        var store = new StateStore(config.Defaults.StatePath);
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
                var stats = engine.Stats.GetValueOrDefault(g.Query);
                return new
                {
                    g.Title,
                    g.Query,
                    g.MaxPrice,
                    Median = stats?.Median,
                    SampleSize = stats?.SampleSize ?? 0,
                    LastChecked = stats?.LastChecked,
                    LastError = stats?.LastError,
                    SeenCount = store.CountForQuery(g.Query),
                };
            });
            var deals = store.RecentDeals(150).Select(kv => new
            {
                Id = kv.Key,
                kv.Value.Query,
                kv.Value.Title,
                kv.Value.Price,
                kv.Value.Currency,
                kv.Value.Url,
                kv.Value.PhotoUrl,
                Tier = kv.Value.Tier.ToString(),
                kv.Value.Score,
                kv.Value.ReferencePrice,
                kv.Value.Reasons,
                FirstSeen = DateTimeOffset.FromUnixTimeSeconds(kv.Value.FirstSeenUnix),
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

        Console.WriteLine($"Dashboard: {config.Defaults.ListenUrl}");
        await app.RunAsync();
        store.Save();
        return 0;
    }

    /// <summary>Pętla w tle: cykl, sen z losowym rozrzutem, od nowa.</summary>
    private sealed class PollingService(TrackerEngine engine, int intervalSeconds) : BackgroundService
    {
        protected override async Task ExecuteAsync(CancellationToken ct)
        {
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

                var sleep = TimeSpan.FromSeconds(intervalSeconds + Random.Shared.NextDouble() * intervalSeconds * 0.2);
                try { await Task.Delay(sleep, ct); }
                catch (OperationCanceledException) { break; }
            }
        }
    }
}
