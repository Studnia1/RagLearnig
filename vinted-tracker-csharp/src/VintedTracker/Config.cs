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

    /// <summary>Limit stron na zwykły cykl — bezpiecznik po dłuższym postoju.</summary>
    [JsonPropertyName("maxPagesPerCycle")] public int MaxPagesPerCycle { get; init; } = 5;

    /// <summary>Minimalna marża (mediana − cena) w walucie rynku, żeby wysłać push.</summary>
    [JsonPropertyName("minMargin")] public decimal MinMargin { get; init; } = 50m;

    /// <summary>Od ilu ofert grupa nierozpoznanych tytułów staje się grą "auto".</summary>
    [JsonPropertyName("autoPromoteMinSample")] public int AutoPromoteMinSample { get; init; } = 8;
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
