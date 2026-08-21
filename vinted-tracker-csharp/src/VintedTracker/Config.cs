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
    [JsonPropertyName("catalogIds")] public List<int> CatalogIds { get; init; } = [];
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
