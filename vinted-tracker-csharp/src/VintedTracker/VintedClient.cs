using System.Net;
using System.Text.Json.Nodes;
using System.Web;

namespace VintedTracker;

/// <summary>
/// Minimalny klient nieoficjalnego API katalogu Vinted.
///
/// Vinted nie ma publicznego API, ale frontend korzysta z endpointu
/// <c>/api/v2/catalog/items</c>. Wystarczy anonimowa sesja: wejście na
/// stronę główną jak przeglądarka daje ciasteczka (m.in. access_token_web),
/// których używamy przy zapytaniach. Przy 401/403 sesja jest odświeżana.
/// </summary>
public sealed class VintedClient
{
    private const string DefaultUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
        "(KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36";

    private readonly string _baseUrl;
    private readonly HttpClient _http;
    private readonly CookieContainer _cookies = new();
    private bool _authenticated;

    public VintedClient(string baseUrl = "https://www.vinted.pl")
    {
        _baseUrl = baseUrl.TrimEnd('/');
        _http = new HttpClient(new HttpClientHandler
        {
            CookieContainer = _cookies,
            AutomaticDecompression = DecompressionMethods.All,
        })
        { Timeout = TimeSpan.FromSeconds(30) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(DefaultUserAgent);
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/json, text/plain, */*");
        _http.DefaultRequestHeaders.AcceptLanguage.ParseAdd("pl-PL,pl;q=0.9,en;q=0.8");
    }

    private async Task RefreshSessionAsync(CancellationToken ct)
    {
        foreach (Cookie cookie in _cookies.GetAllCookies())
            cookie.Expired = true;
        using var resp = await _http.GetAsync(_baseUrl + "/", ct);
        resp.EnsureSuccessStatusCode();
        _authenticated = true;
    }

    public Task<IReadOnlyList<Listing>> SearchAsync(
        string searchText,
        IReadOnlyList<int>? catalogIds = null,
        int perPage = 96,
        int page = 1,
        string order = "newest_first",
        CancellationToken ct = default)
    {
        var query = HttpUtility.ParseQueryString(string.Empty);
        query["search_text"] = searchText;
        query["order"] = order;
        query["per_page"] = perPage.ToString();
        query["page"] = page.ToString();
        if (catalogIds is { Count: > 0 })
            query["catalog_ids"] = string.Join(",", catalogIds);
        return FetchItemsAsync(query.ToString()!, ct);
    }

    /// <summary>Strona katalogu bez frazy — tryb firehose (cała kategoria,
    /// od najnowszych).</summary>
    public Task<IReadOnlyList<Listing>> CatalogPageAsync(
        IReadOnlyList<int> catalogIds,
        int page,
        int perPage = 96,
        CancellationToken ct = default)
    {
        var query = HttpUtility.ParseQueryString(string.Empty);
        query["order"] = "newest_first";
        query["per_page"] = perPage.ToString();
        query["page"] = page.ToString();
        query["catalog_ids"] = string.Join(",", catalogIds);
        return FetchItemsAsync(query.ToString()!, ct);
    }

    /// <summary>
    /// Znajduje katalog(i) gier bez udziału użytkownika. Najpierw próbuje
    /// drzewa kategorii; gdy to zawiedzie (endpoint bywa zmieniany), robi
    /// sondy wyszukiwarką ("gra nintendo switch", "gra ps5", …) i zbiera
    /// <c>catalog_id</c> ze zwróconych ofert — najczęstsze ID w takich
    /// wynikach to z definicji katalogi gier. Null dopiero, gdy obie drogi
    /// zawiodą — wtedy zostaje ręczne catalogIds w konfiguracji.
    /// </summary>
    public async Task<(IReadOnlyList<int> Ids, string Description)?> DiscoverGamesCatalogsAsync(
        CancellationToken ct = default)
    {
        if (await TryTreeDiscoveryAsync(ct) is { } fromTree)
            return (new[] { fromTree.Id }, $"{fromTree.Title} (#{fromTree.Id})");

        if (await DiscoverFromHtmlAsync(ct) is { } fromHtml)
            return fromHtml;

        string[] probes =
        [
            "gra nintendo switch", "gra playstation 5", "gra playstation 4",
            "gra xbox", "pokemon nintendo switch",
        ];
        var counts = new Dictionary<int, int>();
        var significant = new HashSet<int>();
        var sampleItemIds = new List<long>();
        foreach (var probe in probes)
        {
            IReadOnlyList<Listing> results;
            try
            {
                results = await SearchAsync(probe, ct: ct);
            }
            catch (HttpRequestException e)
            {
                Console.Error.WriteLine($"[warn] Sonda \"{probe}\" nieudana: {e.Message}");
                continue;
            }
            var local = results
                .Where(r => r.CatalogId is not null)
                .GroupBy(r => r.CatalogId!.Value)
                .ToDictionary(g => g.Key, g => g.Count());
            Console.WriteLine($"[info] Sonda \"{probe}\": ofert {results.Count}, z catalog_id {local.Values.Sum()}");
            // Wyniki wyszukiwania nie niosą już catalog_id — zbieramy ID ofert,
            // żeby w razie czego doczytać katalog z ich stron szczegółów.
            sampleItemIds.AddRange(results.Take(4).Select(r => r.Id));
            var total = local.Values.Sum();
            foreach (var (id, n) in local)
            {
                counts[id] = counts.GetValueOrDefault(id) + n;
                // Liczą się tylko ID z wyraźnym udziałem w sondzie — pojedyncza
                // koszulka z Pokémonem nie wciągnie katalogu odzieży.
                if (n >= Math.Max(5, total / 4))
                    significant.Add(id);
            }
            await Task.Delay(TimeSpan.FromSeconds(1 + Random.Shared.NextDouble()), ct);
        }

        var ids = significant.OrderByDescending(id => counts[id]).Take(6).ToList();
        if (ids.Count > 0)
            return (ids, $"z sond wyszukiwania: {string.Join(",", ids)}");

        // 3) Szczegóły ofert: /api/v2/items/{id} nadal zwraca catalog_id.
        var detailCounts = new Dictionary<int, int>();
        foreach (var itemId in sampleItemIds.Distinct().Take(12))
        {
            try
            {
                if (await GetItemCatalogIdAsync(itemId, ct) is { } cid)
                    detailCounts[cid] = detailCounts.GetValueOrDefault(cid) + 1;
            }
            catch (HttpRequestException)
            {
                // pojedyncza oferta mogła zniknąć — idziemy dalej
            }
            await Task.Delay(TimeSpan.FromSeconds(0.7 + Random.Shared.NextDouble() * 0.8), ct);
        }
        Console.WriteLine($"[info] Szczegóły ofert: catalog_id z {detailCounts.Values.Sum()} ofert, " +
                          $"katalogi: {string.Join(",", detailCounts.Keys)}");
        var confirmed = detailCounts.Where(kv => kv.Value >= 2).Select(kv => kv.Key)
            .OrderByDescending(id => detailCounts[id]).Take(6).ToList();
        if (confirmed.Count == 0 && detailCounts.Count > 0)
            confirmed = [detailCounts.OrderByDescending(kv => kv.Value).First().Key];
        return confirmed.Count > 0
            ? (confirmed, $"ze szczegółów ofert: {string.Join(",", confirmed)}")
            : null;
    }

    /// <summary>
    /// Wyciąga katalogi gier z HTML-u strony katalogu: nawigacja kategorii
    /// (to samo, co widzi przeglądarka) zawiera linki <c>catalog/1234-slug</c>.
    /// Bierzemy ID o slugach gier, preferując gry wideo nad planszówkami.
    /// </summary>
    public async Task<(IReadOnlyList<int> Ids, string Description)?> DiscoverFromHtmlAsync(
        CancellationToken ct = default)
    {
        if (!_authenticated)
            await RefreshSessionAsync(ct);
        foreach (var path in new[] { "/catalog", "/" })
        {
            string html;
            try
            {
                using var resp = await _http.GetAsync(_baseUrl + path, ct);
                if (!resp.IsSuccessStatusCode)
                    continue;
                html = await resp.Content.ReadAsStringAsync(ct);
            }
            catch (HttpRequestException)
            {
                continue;
            }

            var hits = new Dictionary<int, string>();
            foreach (System.Text.RegularExpressions.Match m in
                System.Text.RegularExpressions.Regex.Matches(html, @"catalog/(\d+)-([a-z0-9\-]+)"))
            {
                var slug = m.Groups[2].Value;
                if (slug.Contains("gry") || slug.Contains("konsol")
                    || slug.Contains("video-game") || slug.Contains("games"))
                    hits.TryAdd(int.Parse(m.Groups[1].Value), slug);
            }
            if (hits.Count == 0)
                continue;

            var video = hits
                .Where(kv => kv.Value.Contains("wideo") || kv.Value.Contains("video") || kv.Value.Contains("konsol"))
                .ToDictionary(kv => kv.Key, kv => kv.Value);
            var chosen = (video.Count > 0 ? video : hits).Keys.Take(6).ToList();
            var described = string.Join(", ", chosen.Select(id => $"#{id} ({hits[id]})"));
            Console.WriteLine($"[info] HTML ({path}): znaleziono katalogi gier: {described}");
            return (chosen, $"z HTML: {described}");
        }
        return null;
    }

    /// <summary>Katalog pojedynczej oferty ze strony szczegółów — wyniki
    /// wyszukiwania przestały nieść catalog_id, szczegóły wciąż go mają.</summary>
    public async Task<int?> GetItemCatalogIdAsync(long itemId, CancellationToken ct = default)
    {
        if (!_authenticated)
            await RefreshSessionAsync(ct);
        using var resp = await _http.GetAsync($"{_baseUrl}/api/v2/items/{itemId}", ct);
        if (!resp.IsSuccessStatusCode)
            return null;
        var json = JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct));
        var node = json?["item"]?["catalog_id"] ?? json?["catalog_id"];
        return node switch
        {
            JsonValue v when v.TryGetValue(out long l) => (int)l,
            JsonValue v when v.TryGetValue(out string? s) && int.TryParse(s, out var p) => p,
            _ => null,
        };
    }

    private async Task<(int Id, string Title)?> TryTreeDiscoveryAsync(CancellationToken ct)
    {
        JsonNode? json;
        try
        {
            if (!_authenticated)
                await RefreshSessionAsync(ct);
            using var resp = await _http.GetAsync($"{_baseUrl}/api/v2/catalogs", ct);
            if (!resp.IsSuccessStatusCode)
                return null;
            json = JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct));
        }
        catch (HttpRequestException)
        {
            return null;
        }
        var best = default((int Id, string Title)?);
        void Walk(JsonNode? node)
        {
            if (node is JsonArray arr)
            {
                foreach (var child in arr)
                    Walk(child);
                return;
            }
            if (node is not JsonObject obj)
                return;
            var title = obj["title"]?.GetValue<string>() ?? "";
            var lower = title.ToLowerInvariant();
            if (obj["id"] is not null && (lower.Contains("gry") || lower.Contains("games")))
            {
                var id = (int)obj["id"]!.GetValue<long>();
                // Preferuj węzeł brzmiący jak gry wideo, nie planszówki.
                var score = lower.Contains("wideo") || lower.Contains("video") || lower.Contains("konsol") ? 2 : 1;
                var bestScore = best is null ? 0
                    : best.Value.Title.ToLowerInvariant() is var b
                        && (b.Contains("wideo") || b.Contains("video") || b.Contains("konsol")) ? 2 : 1;
                if (score > bestScore)
                    best = (id, title);
            }
            Walk(obj["catalogs"]);
        }
        Walk(json?["catalogs"] ?? json);
        return best;
    }

    private async Task<IReadOnlyList<Listing>> FetchItemsAsync(string queryString, CancellationToken ct)
    {
        if (!_authenticated)
            await RefreshSessionAsync(ct);

        var url = $"{_baseUrl}/api/v2/catalog/items?{queryString}";

        HttpStatusCode lastStatus = 0;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            using var resp = await _http.GetAsync(url, ct);
            lastStatus = resp.StatusCode;
            if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                await Task.Delay(TimeSpan.FromSeconds(1 + attempt * 2 + Random.Shared.NextDouble()), ct);
                await RefreshSessionAsync(ct);
                continue;
            }
            var body = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
                throw new HttpRequestException($"HTTP {(int)resp.StatusCode} z API: {Snippet(body)}");

            var json = JsonNode.Parse(body);
            var items = json?["items"] as JsonArray ?? [];
            return items.OfType<JsonNode>().Select(i => Listing.FromApi(i, _baseUrl)).ToList();
        }

        throw new HttpRequestException($"Vinted API wciąż odrzuca zapytanie (HTTP {(int)lastStatus}) — spróbuj później");
    }

    private static string Snippet(string body)
    {
        var flat = body.ReplaceLineEndings(" ");
        return flat.Length <= 300 ? flat : flat[..300] + "…";
    }

    /// <summary>
    /// Tryb diagnostyczny: pokazuje krok po kroku, co odpowiada Vinted —
    /// ciasteczka sesji, status i początek odpowiedzi wyszukiwania,
    /// liczbę ofert i obecność catalog_id. Do wklejenia przy zgłaszaniu problemu.
    /// </summary>
    public async Task<string> DebugProbeAsync(string searchText, CancellationToken ct = default)
    {
        var report = new System.Text.StringBuilder();
        report.AppendLine($"== Sonda diagnostyczna: \"{searchText}\" ({_baseUrl}) ==");
        try
        {
            _authenticated = false;
            await RefreshSessionAsync(ct);
            var cookies = _cookies.GetAllCookies().Select(c => c.Name).ToList();
            report.AppendLine($"Sesja OK, ciasteczka: {(cookies.Count > 0 ? string.Join(", ", cookies) : "BRAK")}");
        }
        catch (Exception e)
        {
            report.AppendLine($"Sesja NIEUDANA: {e.GetType().Name}: {e.Message}");
            return report.ToString();
        }

        var query = HttpUtility.ParseQueryString(string.Empty);
        query["search_text"] = searchText;
        query["order"] = "newest_first";
        query["per_page"] = "24";
        query["page"] = "1";
        var url = $"{_baseUrl}/api/v2/catalog/items?{query}";
        try
        {
            using var resp = await _http.GetAsync(url, ct);
            var body = await resp.Content.ReadAsStringAsync(ct);
            report.AppendLine($"GET /api/v2/catalog/items → HTTP {(int)resp.StatusCode}");
            report.AppendLine($"Początek odpowiedzi: {Snippet(body)}");
            if (resp.IsSuccessStatusCode)
            {
                var json = JsonNode.Parse(body);
                var items = json?["items"] as JsonArray;
                report.AppendLine($"items: {(items is null ? "BRAK POLA" : items.Count.ToString())}");
                if (items is { Count: > 0 } && items[0] is JsonObject first)
                {
                    report.AppendLine($"klucze pierwszej oferty: {string.Join(", ", first.Select(kv => kv.Key))}");
                    var withCatalog = items.OfType<JsonObject>().Count(i => i["catalog_id"] is not null);
                    report.AppendLine($"ofert z catalog_id: {withCatalog}/{items.Count}");
                    if (first["item_box"] is { } box)
                        report.AppendLine($"item_box: {Snippet(box.ToJsonString())}");

                    var firstId = first["id"]!.GetValue<long>();
                    using var detail = await _http.GetAsync($"{_baseUrl}/api/v2/items/{firstId}", ct);
                    var detailBody = await detail.Content.ReadAsStringAsync(ct);
                    report.AppendLine($"GET /api/v2/items/{firstId} → HTTP {(int)detail.StatusCode}");
                    if (detail.IsSuccessStatusCode)
                    {
                        var dj = JsonNode.Parse(detailBody);
                        var cid = dj?["item"]?["catalog_id"] ?? dj?["catalog_id"];
                        report.AppendLine($"catalog_id ze szczegółów: {cid?.ToString() ?? "BRAK"}");
                    }
                    else
                    {
                        report.AppendLine($"Odpowiedź szczegółów: {Snippet(detailBody)}");
                    }
                }
            }
        }
        catch (Exception e)
        {
            report.AppendLine($"Zapytanie NIEUDANE: {e.GetType().Name}: {e.Message}");
        }
        return report.ToString();
    }
}
