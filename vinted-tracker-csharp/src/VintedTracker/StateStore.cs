using System.Text.Json;
using System.Text.Json.Serialization;

namespace VintedTracker;

public sealed class SeenItem
{
    [JsonPropertyName("query")] public required string Query { get; init; }
    [JsonPropertyName("title")] public required string Title { get; init; }
    [JsonPropertyName("price")] public required decimal Price { get; init; }
    [JsonPropertyName("currency")] public required string Currency { get; init; }
    [JsonPropertyName("url")] public required string Url { get; init; }
    [JsonPropertyName("firstSeen")] public required long FirstSeenUnix { get; init; }
    [JsonPropertyName("isDeal")] public required bool IsDeal { get; init; }
}

/// <summary>
/// Trwała pamięć widzianych ofert i historii cen — plik JSON.
/// Zapis następuje przez <see cref="Save"/> (raz na przebieg), więc
/// pojedynczy cykl nie młóci dysku przy każdej ofercie.
/// </summary>
public sealed class StateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _path;
    private readonly Dictionary<long, SeenItem> _items;

    public StateStore(string path)
    {
        _path = path;
        _items = File.Exists(path)
            ? JsonSerializer.Deserialize<Dictionary<long, SeenItem>>(File.ReadAllText(path)) ?? []
            : [];
    }

    public bool IsKnown(long itemId) => _items.ContainsKey(itemId);

    public void Remember(long itemId, SeenItem item) => _items.TryAdd(itemId, item);

    /// <summary>Ceny ofert widzianych dla danego zapytania — baza do mediany rynkowej.</summary>
    public IReadOnlyList<decimal> RecentPrices(string query, int maxAgeDays = 30)
    {
        var cutoff = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - (long)maxAgeDays * 86400;
        return _items.Values
            .Where(i => i.Query == query && i.FirstSeenUnix >= cutoff)
            .Select(i => i.Price)
            .ToList();
    }

    public void Save()
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(_path));
        if (dir is not null)
            Directory.CreateDirectory(dir);
        File.WriteAllText(_path, JsonSerializer.Serialize(_items, JsonOptions));
    }
}
