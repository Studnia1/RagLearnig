using System.Text.Json;
using System.Text.Json.Serialization;

namespace VintedTracker;

public sealed class Defaults
{
    [JsonPropertyName("baseUrl")] public string BaseUrl { get; init; } = "https://www.vinted.pl";
    [JsonPropertyName("pollIntervalSeconds")] public int PollIntervalSeconds { get; init; } = 300;
    [JsonPropertyName("statePath")] public string StatePath { get; init; } = "data/state.json";
    [JsonPropertyName("watchlistPath")] public string WatchlistPath { get; init; } = "games.json";
    [JsonPropertyName("listenUrl")] public string ListenUrl { get; init; } = "http://localhost:5177";

    /// <summary>ID katalogu gier na danej domenie Vinted. Puste = auto-wykrycie
    /// z drzewa kategorii przy pierwszym cyklu.</summary>
    [JsonPropertyName("catalogIds")] public List<int> CatalogIds { get; init; } = [];

    /// <summary>Ile stron katalogu wolno przejść w pierwszym przebiegu (backfill).
    /// Vinted i tak ucina stronicowanie przy ~1000 ofert (10 stron po 96).</summary>
    [JsonPropertyName("backfillPages")] public int BackfillPages { get; init; } = 10;

    /// <summary>Limit stron na zwykły cykl. 10 = pełny zasięg stronicowania
    /// Vinted, żeby po włączeniu komputera dogonić wszystko, co się da;
    /// watermark i tak kończy wcześniej, gdy nie ma nowych ofert.</summary>
    [JsonPropertyName("maxPagesPerCycle")] public int MaxPagesPerCycle { get; init; } = 10;

    /// <summary>Minimalna marża (mediana − cena) w walucie rynku, żeby wysłać push.</summary>
    [JsonPropertyName("minMargin")] public decimal MinMargin { get; init; } = 50m;

    /// <summary>Od ilu ofert grupa nierozpoznanych tytułów staje się grą "auto".</summary>
    [JsonPropertyName("autoPromoteMinSample")] public int AutoPromoteMinSample { get; init; } = 8;

    /// <summary>Model Claude do weryfikacji ofert po zdjęciach (aktywna, gdy
    /// ustawiono ANTHROPIC_API_KEY). "claude-haiku-4-5" tnie koszt ~5x
    /// przy prostszej ocenie.</summary>
    [JsonPropertyName("visionModel")] public string VisionModel { get; init; } = "claude-opus-5";

    /// <summary>Platformy wycinane całkowicie (jak gruz) — oferty nie wchodzą
    /// do median, okazji ani gier auto.</summary>
    [JsonPropertyName("excludedPlatforms")] public List<string> ExcludedPlatforms { get; init; } =
        ["xbox-one", "xbox-series", "xbox360"];

    /// <summary>Polowanie per platforma: każda gra na tej platformie w cenie
    /// ≤ progu (waluta rynku) to mocna okazja z pushem — bez dopasowywania do
    /// konkretnego tytułu. Klucze = platformy z wykrywania (3ds, psvita, ps3, ps4…).</summary>
    [JsonPropertyName("platformHunts")] public Dictionary<string, decimal> PlatformHunts { get; init; } = new()
    {
        ["3ds"] = 25m,
        ["psvita"] = 30m,
        ["ps3"] = 15m,
        ["ps4"] = 25m,
    };
}

public sealed class Config
{
    [JsonPropertyName("defaults")] public Defaults Defaults { get; init; } = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static Config Load(string path) =>
        JsonSerializer.Deserialize<Config>(File.ReadAllText(path), JsonOptions)
        ?? throw new InvalidDataException($"Pusta lub nieprawidłowa konfiguracja: {path}");
}
