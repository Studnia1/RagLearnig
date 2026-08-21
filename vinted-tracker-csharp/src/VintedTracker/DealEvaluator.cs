namespace VintedTracker;

public enum DealTier
{
    /// <summary>Zwykła oferta — nic ciekawego.</summary>
    None,
    /// <summary>Cena wyraźnie poniżej rynku albo w Twoim progu.</summary>
    Deal,
    /// <summary>Mocna okazja — duży rabat potwierdzony dwoma sygnałami.</summary>
    Strong,
    /// <summary>Podejrzanie tanio — częściej scam / sam karton niż okazja.</summary>
    Suspicious,
}

public sealed record DealVerdict(DealTier Tier, double Score, IReadOnlyList<string> Reasons)
{
    public bool IsDeal => Tier is DealTier.Deal or DealTier.Strong;
}

/// <summary>
/// Ocena, czy oferta jest okazją — i jak mocną.
///
/// Definicja jest wielowarstwowa, bo pojedynczy sygnał kłamie:
/// <list type="number">
/// <item><b>Filtr trafności</b> — wyniki wyszukiwania pełne są akcesoriów
/// (etui, steelbooki, amiibo, same kartony), które są tanie, bo nie są grą.
/// Odrzucamy je po słowach kluczowych, zanim policzymy cokolwiek.</item>
/// <item><b>Odporna cena odniesienia</b> — mediana z próbki przyciętej o 10%
/// z każdej strony (trimmed median), liczona dopiero od 5 ofert. Zwykła
/// średnia lub mediana z 3 ofert skacze od jednego bundla za 400 zł.</item>
/// <item><b>Dwa niezależne sygnały na „mocną”</b> — cena ≤ 60% mediany
/// <i>i jednocześnie</i> w dolnym kwartylu próbki; albo cena z wyraźnym
/// zapasem (≤ 85%) poniżej Twojego ręcznego progu. Jeden sygnał daje
/// najwyżej zwykłą okazję.</item>
/// <item><b>Bezpiecznik too-good-to-be-true</b> — cena ≤ 30% mediany to
/// zwykle scam, uszkodzona płytka albo „sam box”; oznaczamy jako
/// podejrzaną zamiast świętować.</item>
/// </list>
/// Wynik (Score) to procent rabatu względem najlepszego odniesienia —
/// służy do sortowania alertów.
/// </summary>
public static class DealEvaluator
{
    /// <summary>Oferty za grosze to zwykle scam, akcesoria albo "opis w cenie koszulki".</summary>
    public const decimal MinSanePrice = 5m;

    /// <summary>Poniżej tylu punktów odniesienia mediana jest zbyt chwiejna, by jej ufać.</summary>
    public const int MinSample = 5;

    /// <summary>Cena ≤ 30% mediany: częściej scam / sam karton niż okazja.</summary>
    public const decimal SuspiciousRatio = 0.30m;

    /// <summary>Mocna okazja wymaga ceny ≤ 60% mediany (plus dolnego kwartyla).</summary>
    public const decimal StrongRatio = 0.60m;

    /// <summary>Zwykła okazja: cena ≤ 75% mediany.</summary>
    public const decimal DealRatio = 0.75m;

    /// <summary>Mocna okazja względem ręcznego progu wymaga zapasu (≤ 85% progu).</summary>
    public const decimal StrongMaxPriceRatio = 0.85m;

    /// <summary>
    /// Frazy, po których odrzucamy ofertę jako akcesorium/dodatek, nie grę.
    /// Porównywane bez wielkości liter.
    /// </summary>
    public static readonly IReadOnlyList<string> AccessoryKeywords =
    [
        "etui", "case", "pokrowiec", "steelbook", "poradnik", "przewodnik",
        "figurka", "amiibo", "plakat", "brelok", "przypinka", "naklejka",
        "skin", "kubek", "koszulka", "bluza", "maskotka", "pluszak",
        "karton", "sam box", "box only", "pudełko po", "bez gry",
        "kontroler", "pad ", "joy-con", "joycon", "konsola",
    ];

    public static bool IsRelevant(string title)
    {
        var t = " " + title.ToLowerInvariant() + " ";
        return !AccessoryKeywords.Any(t.Contains);
    }

    public static DealVerdict Evaluate(
        Listing listing,
        decimal? maxPrice,
        IReadOnlyList<decimal> marketPrices)
    {
        var price = listing.Price;

        if (!IsRelevant(listing.Title))
            return new DealVerdict(DealTier.None, 0, ["wygląda na akcesorium/dodatek, nie grę"]);

        if (price < MinSanePrice)
            return new DealVerdict(DealTier.None, 0, [$"cena {price:0.00} poniżej progu sensowności"]);

        var reasons = new List<string>();
        var tier = DealTier.None;
        decimal bestDiscount = 0; // ułamek 0..1 względem najlepszego odniesienia

        // Sygnał rynkowy: przycięta mediana + dolny kwartyl.
        var sample = marketPrices.Where(p => p >= MinSanePrice).ToList();
        if (sample.Count >= MinSample)
        {
            var reference = TrimmedMedian(sample);
            var p25 = Percentile(sample, 0.25);
            var ratio = price / reference;

            if (ratio <= SuspiciousRatio)
                return new DealVerdict(DealTier.Suspicious, 0,
                    [$"cena {price:0.00} {listing.Currency} to ledwie {ratio:P0} mediany ({reference:0.00}) — " +
                     "sprawdź, czy to nie sam karton, uszkodzona płytka albo scam"]);

            if (ratio <= StrongRatio && price <= p25)
            {
                tier = DealTier.Strong;
                bestDiscount = 1 - ratio;
                reasons.Add($"{1 - ratio:P0} poniżej mediany rynkowej ({reference:0.00} {listing.Currency}, " +
                            $"n={sample.Count}) i w dolnym kwartyle cen");
            }
            else if (ratio <= DealRatio)
            {
                tier = DealTier.Deal;
                bestDiscount = 1 - ratio;
                reasons.Add($"{1 - ratio:P0} poniżej mediany rynkowej ({reference:0.00} {listing.Currency}, n={sample.Count})");
            }
        }

        // Sygnał ręczny: Twój próg ceny dla tej gry.
        if (maxPrice is { } cap && price <= cap)
        {
            var capDiscount = 1 - price / cap;
            if (price <= cap * StrongMaxPriceRatio)
            {
                tier = DealTier.Strong;
                reasons.Add($"cena {price:0.00} {listing.Currency} z zapasem poniżej Twojego progu {cap:0.00}");
            }
            else
            {
                if (tier == DealTier.None)
                    tier = DealTier.Deal;
                reasons.Add($"cena {price:0.00} {listing.Currency} ≤ Twój próg {cap:0.00}");
            }
            bestDiscount = Math.Max(bestDiscount, capDiscount);
        }

        if (tier == DealTier.None)
            return new DealVerdict(DealTier.None, 0, reasons);

        var score = Math.Round((double)bestDiscount * 100, 1);
        return new DealVerdict(tier, score, reasons);
    }

    /// <summary>Mediana z próbki przyciętej o 10% najtańszych i 10% najdroższych.</summary>
    public static decimal TrimmedMedian(IReadOnlyList<decimal> values)
    {
        var sorted = values.Order().ToList();
        var trim = sorted.Count / 10;
        var trimmed = sorted.Skip(trim).Take(sorted.Count - 2 * trim).ToList();
        return Median(trimmed);
    }

    public static decimal Median(IReadOnlyList<decimal> values)
    {
        var sorted = values.Order().ToList();
        var mid = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2;
    }

    /// <summary>Kwantyl metodą najbliższej rangi.</summary>
    public static decimal Percentile(IReadOnlyList<decimal> values, double p)
    {
        var sorted = values.Order().ToList();
        var rank = (int)Math.Ceiling(p * sorted.Count) - 1;
        return sorted[Math.Clamp(rank, 0, sorted.Count - 1)];
    }
}
