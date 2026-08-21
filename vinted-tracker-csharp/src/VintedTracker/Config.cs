using System.Text.Json;
using System.Text.Json.Serialization;

namespace VintedTracker;

public sealed class Defaults
{
    [JsonPropertyName("baseUrl")] public string BaseUrl { get; init; } = "https://www.vinted.pl";
    [JsonPropertyName("pollIntervalSeconds")] public int PollIntervalSeconds { get; init; } = 300;
    [JsonPropertyName("statePath")] public string StatePath { get; init; } = "data/state.json";
    [JsonPropertyName("discountThreshold")] public decimal DiscountThreshold { get; init; } = 0.6m;
    [JsonPropertyName("catalogIds")] public List<int> CatalogIds { get; init; } = [];
}

public sealed class Watch
{
    [JsonPropertyName("query")] public required string Query { get; init; }
    [JsonPropertyName("maxPrice")] public decimal? MaxPrice { get; init; }
    [JsonPropertyName("discountThreshold")] public decimal? DiscountThreshold { get; init; }
    [JsonPropertyName("catalogIds")] public List<int>? CatalogIds { get; init; }
}

public sealed class Config
{
    [JsonPropertyName("defaults")] public Defaults Defaults { get; init; } = new();
    [JsonPropertyName("watches")] public List<Watch> Watches { get; init; } = [];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static Config Load(string path) =>
        JsonSerializer.Deserialize<Config>(File.ReadAllText(path), JsonOptions)
        ?? throw new InvalidDataException($"Pusta lub nieprawidłowa konfiguracja: {path}");
}
