using System.Text.Json;
using System.Text.Json.Serialization;

namespace VintedTracker;

public sealed class GameWatch
{
    [JsonPropertyName("title")] public required string Title { get; set; }
    [JsonPropertyName("query")] public required string Query { get; set; }
    [JsonPropertyName("maxPrice")] public decimal? MaxPrice { get; set; }
    /// <summary>Dodatkowe frazy dopasowujące tę samą grę (np. skróty: "totk", "acnh").</summary>
    [JsonPropertyName("aliases")] public List<string>? Aliases { get; set; }
}

/// <summary>
/// Lista śledzonych gier w pliku JSON, edytowalna z UI w trakcie działania.
/// Kluczem gry jest <c>query</c> (unikalne).
/// </summary>
public sealed class WatchlistStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly string _path;
    private readonly object _lock = new();
    private List<GameWatch> _games;

    public WatchlistStore(string path)
    {
        _path = path;
        _games = File.Exists(path)
            ? JsonSerializer.Deserialize<List<GameWatch>>(File.ReadAllText(path), JsonOptions) ?? []
            : [];
    }

    public IReadOnlyList<GameWatch> Snapshot()
    {
        lock (_lock)
            return _games.ToList();
    }

    public void Upsert(GameWatch game)
    {
        lock (_lock)
        {
            var idx = _games.FindIndex(g => g.Query.Equals(game.Query, StringComparison.OrdinalIgnoreCase));
            if (idx >= 0)
                _games[idx] = game;
            else
                _games.Add(game);
            Save();
        }
    }

    public bool Remove(string query)
    {
        lock (_lock)
        {
            var removed = _games.RemoveAll(g => g.Query.Equals(query, StringComparison.OrdinalIgnoreCase)) > 0;
            if (removed)
                Save();
            return removed;
        }
    }

    private void Save()
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(_path));
        if (dir is not null)
            Directory.CreateDirectory(dir);
        File.WriteAllText(_path, JsonSerializer.Serialize(_games, JsonOptions));
    }
}
