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
    /// Próbuje znaleźć ID katalogu gier w drzewie kategorii Vinted
    /// (szuka tytułu zawierającego "gry"/"games"). Null, gdy się nie uda —
    /// wtedy trzeba podać catalogIds w konfiguracji.
    /// </summary>
    public async Task<(int Id, string Title)?> DiscoverGamesCatalogAsync(CancellationToken ct = default)
    {
        if (!_authenticated)
            await RefreshSessionAsync(ct);
        using var resp = await _http.GetAsync($"{_baseUrl}/api/v2/catalogs", ct);
        if (!resp.IsSuccessStatusCode)
            return null;
        var json = JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct));
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

        for (var attempt = 0; attempt < 3; attempt++)
        {
            using var resp = await _http.GetAsync(url, ct);
            if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                await Task.Delay(TimeSpan.FromSeconds(1 + attempt * 2 + Random.Shared.NextDouble()), ct);
                await RefreshSessionAsync(ct);
                continue;
            }
            resp.EnsureSuccessStatusCode();

            var json = JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct));
            var items = json?["items"] as JsonArray ?? [];
            return items.OfType<JsonNode>().Select(i => Listing.FromApi(i, _baseUrl)).ToList();
        }

        throw new HttpRequestException("Vinted API wciąż odrzuca zapytanie (401/403) — spróbuj później");
    }
}
