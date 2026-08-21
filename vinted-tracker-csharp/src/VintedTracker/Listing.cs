using System.Globalization;
using System.Text.Json.Nodes;

namespace VintedTracker;

/// <summary>Pojedyncza oferta z katalogu Vinted.</summary>
public sealed record Listing(
    long Id,
    string Title,
    decimal Price,
    string Currency,
    decimal? TotalPrice,
    string? Brand,
    string Url,
    string? PhotoUrl,
    int? CatalogId)
{
    /// <summary>
    /// Parsuje element z odpowiedzi API. Cena bywa stringiem, liczbą albo
    /// obiektem <c>{"amount": "12.5", "currency_code": "PLN"}</c> — zależnie
    /// od wersji API, więc obsługujemy wszystkie warianty.
    /// </summary>
    public static Listing FromApi(JsonNode item, string baseUrl)
    {
        static decimal? Amount(JsonNode? node)
        {
            if (node is JsonObject obj)
                node = obj["amount"];
            return node switch
            {
                null => null,
                JsonValue v when v.TryGetValue(out decimal d) => d,
                JsonValue v when v.TryGetValue(out string? s)
                    && decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out var p) => p,
                _ => null,
            };
        }

        static string? CurrencyOf(JsonNode? node) =>
            node is JsonObject obj ? obj["currency_code"]?.GetValue<string>() : null;

        static int? IntOf(JsonNode? node) => node switch
        {
            JsonValue v when v.TryGetValue(out long l) => (int)l,
            JsonValue v when v.TryGetValue(out string? s) && int.TryParse(s, out var p) => p,
            _ => null,
        };

        long id = item["id"]!.GetValue<long>();
        return new Listing(
            Id: id,
            Title: item["title"]?.GetValue<string>() ?? "",
            Price: Amount(item["price"]) ?? 0m,
            Currency: CurrencyOf(item["price"]) ?? CurrencyOf(item["total_item_price"]) ?? "?",
            TotalPrice: Amount(item["total_item_price"]),
            Brand: item["brand_title"]?.GetValue<string>(),
            Url: item["url"]?.GetValue<string>() ?? $"{baseUrl}/items/{id}",
            PhotoUrl: item["photo"] is JsonObject photo ? photo["url"]?.GetValue<string>() : null,
            CatalogId: IntOf(item["catalog_id"]));
    }
}
