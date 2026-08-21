namespace VintedTracker;

public sealed record DealVerdict(bool IsDeal, IReadOnlyList<string> Reasons);

/// <summary>
/// Ocena, czy oferta jest okazją. Dwa niezależne sygnały:
/// twardy próg ceny (<c>maxPrice</c> z konfiguracji) oraz cena wyraźnie
/// poniżej mediany rynkowej podobnych ofert (bieżące wyniki + historia).
/// </summary>
public static class DealEvaluator
{
    /// <summary>Oferty za grosze to zwykle scam, akcesoria albo "opis w cenie koszulki".</summary>
    public const decimal MinSanePrice = 5m;

    /// <summary>Poniżej tylu punktów odniesienia mediana jest zbyt chwiejna, by jej ufać.</summary>
    public const int MinSample = 5;

    public static DealVerdict Evaluate(
        Listing listing,
        decimal? maxPrice,
        IReadOnlyList<decimal> marketPrices,
        decimal discountThreshold = 0.6m)
    {
        var reasons = new List<string>();
        var price = listing.Price;

        if (price < MinSanePrice)
            return new DealVerdict(false, [$"cena {price:0.00} poniżej progu sensowności"]);

        if (maxPrice is { } cap && price <= cap)
            reasons.Add($"cena {price:0.00} {listing.Currency} ≤ Twój próg {cap:0.00}");

        var sample = marketPrices.Where(p => p >= MinSanePrice).ToList();
        if (sample.Count >= MinSample)
        {
            var median = Median(sample);
            if (median > 0 && price <= median * discountThreshold)
            {
                var pct = Math.Round((1 - price / median) * 100);
                reasons.Add($"{pct}% poniżej mediany rynkowej ({median:0.00} {listing.Currency}, n={sample.Count})");
            }
        }

        return new DealVerdict(reasons.Count > 0, reasons);
    }

    public static decimal Median(IReadOnlyList<decimal> values)
    {
        var sorted = values.Order().ToList();
        var mid = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2;
    }
}
