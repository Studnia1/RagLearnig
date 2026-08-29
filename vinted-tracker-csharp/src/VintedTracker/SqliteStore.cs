using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace VintedTracker;

public sealed record ItemRecord(
    long Id, string Title, decimal Price, string Currency, string Url, string? PhotoUrl,
    string NormKey, string? Platform, string? GameKey, string? GameTitle, bool Relevant,
    DealTier Tier, double Score, decimal? ReferencePrice, IReadOnlyList<string> Reasons);

public sealed record DealRow(
    long Id, string Game, string Title, decimal Price, string Currency, string Url,
    string? PhotoUrl, string Tier, double Score, decimal? ReferencePrice,
    List<string> Reasons, long FirstSeenUnix);

public sealed record GameStatsRow(decimal? Median, int Sample, int Seen, int Deals);

/// <summary>
/// Trwały magazyn ofert (SQLite). W trybie firehose baza rośnie do dziesiątek
/// tysięcy rekordów — plik JSON przepisywany co cykl przestał wystarczać.
/// </summary>
public sealed class SqliteStore : IDisposable
{
    private const string Schema = """
        CREATE TABLE IF NOT EXISTS items (
            id INTEGER PRIMARY KEY,
            title TEXT NOT NULL,
            price REAL NOT NULL,
            currency TEXT NOT NULL,
            url TEXT NOT NULL,
            photo_url TEXT,
            first_seen INTEGER NOT NULL,
            norm_key TEXT NOT NULL DEFAULT '',
            platform TEXT,
            game_key TEXT,
            game_title TEXT,
            relevant INTEGER NOT NULL DEFAULT 1,
            tier TEXT NOT NULL DEFAULT 'None',
            score REAL NOT NULL DEFAULT 0,
            reference_price REAL,
            reasons TEXT NOT NULL DEFAULT '[]'
        );
        CREATE INDEX IF NOT EXISTS idx_items_game ON items(game_key, first_seen);
        CREATE INDEX IF NOT EXISTS idx_items_norm ON items(norm_key) WHERE game_key IS NULL;
        CREATE INDEX IF NOT EXISTS idx_items_tier ON items(tier, first_seen);
        CREATE TABLE IF NOT EXISTS auto_games (
            key TEXT PRIMARY KEY,
            title TEXT NOT NULL,
            norm_key TEXT NOT NULL,
            platform TEXT,
            created INTEGER NOT NULL
        );
        CREATE TABLE IF NOT EXISTS meta (k TEXT PRIMARY KEY, v TEXT NOT NULL);
        """;

    private readonly SqliteConnection _conn;
    private readonly object _lock = new();

    public SqliteStore(string path)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (dir is not null)
            Directory.CreateDirectory(dir);
        _conn = new SqliteConnection($"Data Source={path}");
        _conn.Open();
        Exec(Schema);
        Exec("PRAGMA journal_mode=WAL;");
    }

    public bool IsKnown(long itemId)
    {
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT 1 FROM items WHERE id = $id";
            cmd.Parameters.AddWithValue("$id", itemId);
            return cmd.ExecuteScalar() is not null;
        }
    }

    public void Insert(ItemRecord r)
    {
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                INSERT OR IGNORE INTO items
                    (id, title, price, currency, url, photo_url, first_seen, norm_key,
                     platform, game_key, game_title, relevant, tier, score, reference_price, reasons)
                VALUES ($id, $title, $price, $currency, $url, $photo, $seen, $norm,
                        $platform, $game, $gameTitle, $relevant, $tier, $score, $ref, $reasons)
                """;
            cmd.Parameters.AddWithValue("$id", r.Id);
            cmd.Parameters.AddWithValue("$title", r.Title);
            cmd.Parameters.AddWithValue("$price", (double)r.Price);
            cmd.Parameters.AddWithValue("$currency", r.Currency);
            cmd.Parameters.AddWithValue("$url", r.Url);
            cmd.Parameters.AddWithValue("$photo", (object?)r.PhotoUrl ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$seen", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            cmd.Parameters.AddWithValue("$norm", r.NormKey);
            cmd.Parameters.AddWithValue("$platform", (object?)r.Platform ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$game", (object?)r.GameKey ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$gameTitle", (object?)r.GameTitle ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$relevant", r.Relevant ? 1 : 0);
            cmd.Parameters.AddWithValue("$tier", r.Tier.ToString());
            cmd.Parameters.AddWithValue("$score", r.Score);
            cmd.Parameters.AddWithValue("$ref", (object?)(double?)r.ReferencePrice ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$reasons", JsonSerializer.Serialize(r.Reasons));
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>Ceny sensownych ofert danej gry z ostatnich dni — pula mediany.</summary>
    public IReadOnlyList<decimal> PricesFor(string gameKey, int maxAgeDays = 30)
    {
        var cutoff = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - (long)maxAgeDays * 86400;
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                SELECT price FROM items
                WHERE game_key = $g AND relevant = 1 AND first_seen >= $cutoff
                """;
            cmd.Parameters.AddWithValue("$g", gameKey);
            cmd.Parameters.AddWithValue("$cutoff", cutoff);
            var prices = new List<decimal>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                prices.Add((decimal)reader.GetDouble(0));
            return prices;
        }
    }

    public IReadOnlyList<DealRow> RecentDeals(int limit = 150)
    {
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                SELECT id, COALESCE(game_title, norm_key), title, price, currency,
                       url, photo_url, tier, score, reference_price, reasons, first_seen
                FROM items
                WHERE tier != 'None'
                ORDER BY first_seen DESC
                LIMIT $limit
                """;
            cmd.Parameters.AddWithValue("$limit", limit);
            var rows = new List<DealRow>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                rows.Add(new DealRow(
                    Id: reader.GetInt64(0),
                    Game: reader.GetString(1),
                    Title: reader.GetString(2),
                    Price: (decimal)reader.GetDouble(3),
                    Currency: reader.GetString(4),
                    Url: reader.GetString(5),
                    PhotoUrl: reader.IsDBNull(6) ? null : reader.GetString(6),
                    Tier: reader.GetString(7),
                    Score: reader.GetDouble(8),
                    ReferencePrice: reader.IsDBNull(9) ? null : (decimal)reader.GetDouble(9),
                    Reasons: JsonSerializer.Deserialize<List<string>>(reader.GetString(10)) ?? [],
                    FirstSeenUnix: reader.GetInt64(11)));
            return rows;
        }
    }

    public sealed record CheapestSeenRow(string Title, decimal Price, string Currency, string Url, long FirstSeenUnix);

    /// <summary>Najtańsza wiarygodna oferta gry widziana w ostatnich dniach —
    /// darmowe przybliżenie "najtańszej teraz" z danych firehose'a (oferta
    /// mogła się już sprzedać; skan na żywo to weryfikuje).</summary>
    public CheapestSeenRow? CheapestSeen(string gameKey, decimal minPrice, int maxAgeDays = 7)
    {
        var cutoff = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - (long)maxAgeDays * 86400;
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                SELECT title, price, currency, url, first_seen FROM items
                WHERE game_key = $g AND relevant = 1 AND first_seen >= $cutoff AND price >= $min
                ORDER BY price ASC LIMIT 1
                """;
            cmd.Parameters.AddWithValue("$g", gameKey);
            cmd.Parameters.AddWithValue("$cutoff", cutoff);
            cmd.Parameters.AddWithValue("$min", (double)minPrice);
            using var reader = cmd.ExecuteReader();
            if (!reader.Read())
                return null;
            return new CheapestSeenRow(
                reader.GetString(0), (decimal)reader.GetDouble(1), reader.GetString(2),
                reader.GetString(3), reader.GetInt64(4));
        }
    }

    public sealed record TopOfferRow(
        long Id, string Title, decimal Price, string Currency, string Url,
        string? PhotoUrl, long FirstSeenUnix);

    /// <summary>Najlepsze (najtańsze wiarygodne) oferty gry z ostatnich dni —
    /// do rozwijanego panelu pod grą w tabeli śledzonych.</summary>
    public IReadOnlyList<TopOfferRow> TopOffersFor(
        string gameKey, decimal minPrice, int maxAgeDays = 7, int limit = 5)
    {
        var cutoff = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - (long)maxAgeDays * 86400;
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                SELECT id, title, price, currency, url, photo_url, first_seen FROM items
                WHERE game_key = $g AND relevant = 1 AND first_seen >= $cutoff AND price >= $min
                ORDER BY price ASC LIMIT $limit
                """;
            cmd.Parameters.AddWithValue("$g", gameKey);
            cmd.Parameters.AddWithValue("$cutoff", cutoff);
            cmd.Parameters.AddWithValue("$min", (double)minPrice);
            cmd.Parameters.AddWithValue("$limit", limit);
            var rows = new List<TopOfferRow>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                rows.Add(new TopOfferRow(
                    reader.GetInt64(0), reader.GetString(1), (decimal)reader.GetDouble(2),
                    reader.GetString(3), reader.GetString(4),
                    reader.IsDBNull(5) ? null : reader.GetString(5), reader.GetInt64(6)));
            return rows;
        }
    }

    public GameStatsRow StatsFor(string gameKey, int maxAgeDays = 30)
    {
        var prices = PricesFor(gameKey, maxAgeDays);
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                SELECT COUNT(*), SUM(CASE WHEN tier IN ('Deal','Strong') THEN 1 ELSE 0 END)
                FROM items WHERE game_key = $g
                """;
            cmd.Parameters.AddWithValue("$g", gameKey);
            using var reader = cmd.ExecuteReader();
            reader.Read();
            var seen = reader.GetInt32(0);
            var deals = reader.IsDBNull(1) ? 0 : reader.GetInt32(1);
            var sane = prices.Where(p => p >= DealEvaluator.MinSanePrice).ToList();
            return new GameStatsRow(
                Median: sane.Count >= DealEvaluator.MinSample ? DealEvaluator.TrimmedMedian(sane) : null,
                Sample: sane.Count,
                Seen: seen,
                Deals: deals);
        }
    }

    /// <summary>
    /// Auto-promocja: nierozpoznane oferty grupują się po (norm_key, platforma);
    /// gdy grupa urośnie do <paramref name="minSample"/> ofert, staje się grą
    /// "auto" — od tej pory jej nowe oferty dostają medianę i mogą alertować.
    /// Tytułem zostaje najkrótszy zaobserwowany (zwykle najczystszy).
    /// </summary>
    public int PromoteAutoGames(int minSample)
    {
        lock (_lock)
        {
            using var find = _conn.CreateCommand();
            find.CommandText = """
                SELECT norm_key, COALESCE(platform, ''), COUNT(*)
                FROM items
                WHERE game_key IS NULL AND relevant = 1 AND norm_key != ''
                GROUP BY norm_key, COALESCE(platform, '')
                HAVING COUNT(*) >= $min
                """;
            find.Parameters.AddWithValue("$min", minSample);
            var groups = new List<(string Norm, string Platform)>();
            using (var reader = find.ExecuteReader())
                while (reader.Read())
                    groups.Add((reader.GetString(0), reader.GetString(1)));

            foreach (var (norm, platform) in groups)
            {
                var key = $"auto:{norm}|{platform}";
                using (var title = _conn.CreateCommand())
                {
                    title.CommandText = """
                        SELECT title FROM items
                        WHERE game_key IS NULL AND norm_key = $n AND COALESCE(platform,'') = $p
                        ORDER BY LENGTH(title) LIMIT 1
                        """;
                    title.Parameters.AddWithValue("$n", norm);
                    title.Parameters.AddWithValue("$p", platform);
                    var t = (string?)title.ExecuteScalar() ?? norm;

                    using var ins = _conn.CreateCommand();
                    ins.CommandText = """
                        INSERT OR IGNORE INTO auto_games (key, title, norm_key, platform, created)
                        VALUES ($k, $t, $n, $p, $c)
                        """;
                    ins.Parameters.AddWithValue("$k", key);
                    ins.Parameters.AddWithValue("$t", t);
                    ins.Parameters.AddWithValue("$n", norm);
                    ins.Parameters.AddWithValue("$p", platform);
                    ins.Parameters.AddWithValue("$c", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                    ins.ExecuteNonQuery();
                }

                using var upd = _conn.CreateCommand();
                upd.CommandText = """
                    UPDATE items
                    SET game_key = $k,
                        game_title = (SELECT title FROM auto_games WHERE key = $k)
                    WHERE game_key IS NULL AND norm_key = $n AND COALESCE(platform,'') = $p
                    """;
                upd.Parameters.AddWithValue("$k", key);
                upd.Parameters.AddWithValue("$n", norm);
                upd.Parameters.AddWithValue("$p", platform);
                upd.ExecuteNonQuery();
            }
            return groups.Count;
        }
    }

    /// <summary>Kasuje słownik gier auto (tryb wishlisty ich nie używa).
    /// Oferty odpina od nich reindeksacja — tu znika sam słownik.</summary>
    public int PurgeAutoGames()
    {
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "DELETE FROM auto_games";
            return cmd.ExecuteNonQuery();
        }
    }

    /// <summary>Słownik gier auto: (norm_key, platforma) → (klucz, tytuł).</summary>
    public Dictionary<(string Norm, string Platform), (string Key, string Title)> AutoGameIndex()
    {
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT norm_key, COALESCE(platform,''), key, title FROM auto_games";
            var index = new Dictionary<(string, string), (string, string)>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                index[(reader.GetString(0), reader.GetString(1))] = (reader.GetString(2), reader.GetString(3));
            return index;
        }
    }

    public long ItemCount() => ScalarLong("SELECT COUNT(*) FROM items");
    public long AutoGameCount() => ScalarLong("SELECT COUNT(*) FROM auto_games");

    public sealed record SweepRow(
        long Id, string Title, bool Relevant, string? GameKey,
        string NormKey, string? Platform, string? GameTitle);

    /// <summary>Wiersze do ponownej oceny po zmianie wbudowanych filtrów.</summary>
    public IReadOnlyList<SweepRow> SnapshotForSweep()
    {
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT id, title, relevant, game_key, norm_key, platform, game_title " +
                              "FROM items WHERE relevant = 1 OR game_key IS NOT NULL";
            var rows = new List<SweepRow>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                rows.Add(new SweepRow(
                    reader.GetInt64(0), reader.GetString(1), reader.GetInt32(2) == 1,
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    reader.IsDBNull(4) ? "" : reader.GetString(4),
                    reader.IsDBNull(5) ? null : reader.GetString(5),
                    reader.IsDBNull(6) ? null : reader.GetString(6)));
            return rows;
        }
    }

    /// <summary>Pełny wynik reindeksacji jednego wiersza (porządki po zmianie
    /// filtrów/platform): nowy klucz normalizacji, platforma i przypięcie do
    /// gry. <paramref name="ResetTier"/> zeruje tier, gdy oferta wypada z gry
    /// lub z median — sama zmiana normalizacji nie kasuje historii okazji.</summary>
    public sealed record ReindexRow(
        long Id, string NormKey, string? Platform, string? GameKey, string? GameTitle,
        bool Relevant, bool ResetTier);

    /// <summary>Stosuje reindeksację wsteczną — w odróżnieniu od zwykłego
    /// odpinania umie też PODPIĄĆ ofertę do gry (np. wydania Switch 2, które
    /// stary strażnik cyfr odrzucał).</summary>
    public void ApplyReindex(IReadOnlyList<ReindexRow> rows)
    {
        lock (_lock)
        {
            using var tx = _conn.BeginTransaction();
            using var cmd = _conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText =
                "UPDATE items SET norm_key = @n, platform = @p, game_key = @k, game_title = @t, " +
                "relevant = @r, tier = CASE WHEN @z = 1 THEN 'None' ELSE tier END WHERE id = @i";
            var pn = cmd.CreateParameter(); pn.ParameterName = "@n"; cmd.Parameters.Add(pn);
            var pp = cmd.CreateParameter(); pp.ParameterName = "@p"; cmd.Parameters.Add(pp);
            var pk = cmd.CreateParameter(); pk.ParameterName = "@k"; cmd.Parameters.Add(pk);
            var pt = cmd.CreateParameter(); pt.ParameterName = "@t"; cmd.Parameters.Add(pt);
            var pr = cmd.CreateParameter(); pr.ParameterName = "@r"; cmd.Parameters.Add(pr);
            var pz = cmd.CreateParameter(); pz.ParameterName = "@z"; cmd.Parameters.Add(pz);
            var pi = cmd.CreateParameter(); pi.ParameterName = "@i"; cmd.Parameters.Add(pi);
            foreach (var row in rows)
            {
                pn.Value = row.NormKey;
                pp.Value = (object?)row.Platform ?? DBNull.Value;
                pk.Value = (object?)row.GameKey ?? DBNull.Value;
                pt.Value = (object?)row.GameTitle ?? DBNull.Value;
                pr.Value = row.Relevant ? 1 : 0;
                pz.Value = row.ResetTier ? 1 : 0;
                pi.Value = row.Id;
                cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
    }


    /// <summary>Gruz-lista użytkownika: słowa kluczowe oznaczające akcesoria/śmieci.</summary>
    public IReadOnlyList<string> GetBlocklist()
    {
        var raw = GetMeta("blocklist");
        return raw is null ? [] : JsonSerializer.Deserialize<List<string>>(raw) ?? [];
    }

    /// <summary>Dodaje słowo do gruz-listy i czyści wstecz: oferty z tym słowem
    /// w tytule tracą status okazji i wypadają z puli median. Zwraca liczbę
    /// wyczyszczonych ofert.</summary>
    public int AddBlocklistKeyword(string keyword)
    {
        keyword = keyword.Trim().ToLowerInvariant();
        if (keyword.Length < 2)
            return 0;
        var list = GetBlocklist().ToList();
        if (!list.Contains(keyword))
        {
            list.Add(keyword);
            SetMeta("blocklist", JsonSerializer.Serialize(list));
        }
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = """
                UPDATE items SET tier = 'None', relevant = 0
                WHERE (relevant = 1 OR tier != 'None')
                  AND instr(lower(title), $kw) > 0
                """;
            cmd.Parameters.AddWithValue("$kw", keyword);
            return cmd.ExecuteNonQuery();
        }
    }

    public bool RemoveBlocklistKeyword(string keyword)
    {
        keyword = keyword.Trim().ToLowerInvariant();
        var list = GetBlocklist().ToList();
        var removed = list.Remove(keyword);
        if (removed)
            SetMeta("blocklist", JsonSerializer.Serialize(list));
        return removed;
    }

    public string? GetMeta(string key)
    {
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT v FROM meta WHERE k = $k";
            cmd.Parameters.AddWithValue("$k", key);
            return (string?)cmd.ExecuteScalar();
        }
    }

    public void SetMeta(string key, string value)
    {
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "INSERT INTO meta (k, v) VALUES ($k, $v) ON CONFLICT(k) DO UPDATE SET v = $v";
            cmd.Parameters.AddWithValue("$k", key);
            cmd.Parameters.AddWithValue("$v", value);
            cmd.ExecuteNonQuery();
        }
    }

    private long ScalarLong(string sql)
    {
        lock (_lock)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = sql;
            return (long)(cmd.ExecuteScalar() ?? 0L);
        }
    }

    private void Exec(string sql)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    public void Dispose() => _conn.Dispose();
}
