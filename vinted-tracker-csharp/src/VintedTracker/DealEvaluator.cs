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

/// <param name="ReferencePrice">Cena odniesienia (mediana rynkowa lub Twój
/// próg — ta, względem której rabat jest największy); null, gdy brak sygnału.</param>
public sealed record DealVerdict(
    DealTier Tier, double Score, IReadOnlyList<string> Reasons, decimal? ReferencePrice = null)
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
    /// <summary>
    /// Rdzenie słów-gruzu, porównywane po złożeniu diakrytyków (á→a, ł→l),
    /// więc jeden rdzeń łapie odmiany i warianty z języków całej UE —
    /// Vinted PL pokazuje też oferty z zagranicy. Wpis ze spacjami wymaga
    /// granicy słowa (" box " nie łapie "xbox").
    /// </summary>
    public static readonly IReadOnlyList<string> AccessoryKeywords =
    [
        // etui / pokrowce / szkła (PL, EN, DE, FR, IT, ES, NL, CZ)
        "etui", "case", "pokrowiec", "tasche", " hoes", " funda", "custodia",
        " obal", "steelbook", "szklo", "hartowan", "protector",
        // sprzęt: kontrolery, konsole, kable, ładowarki, uchwyty
        "kontroler", "controller", "gamepad", "joystick", "manette", " mando ",
        "ovladac", "pad ", "joy-con", "joycon", "konsol", "consol", " dock",
        "ladowark", "charger", "adapter", "kabel", "cable", "grip", "silikon",
        "silicon",
        // figurki / pluszaki / maskotki (EN/DE/SE figur, EE figuur, FR/ES/IT peluche…)
        "figur", "figuur", "amiibo", "maskotka", "mascot", "pluszak", "plush",
        "plusch", "pluss", "peluche", "knuffel", "nendoroid", "figma",
        // zestawy akcesoriów i bundle
        "akcesor", "accessor", "bundle", "huppari",
        // plakaty / naklejki / przypinki / breloczki
        "plakat", "poster", "poszter", "affiche", "juliste",
        "naklejk", "sticker", "aufkleber", "autocollant", "adesiv", "pegatina",
        "samolepk", "nalepk", "matrica", "klisterm",
        "przypink", " pin ", "pins", "badge", "znaczk",
        "brelo", "keychain", "keyring", "anhanger", "porte-cle", "portachiav",
        "llavero", "sleutelh", "kulcstart", "klicenka",
        // kubki / ubrania / torby / drobny merch
        "kubek", " mug ", "tasse", " taza", "tazza", " mok ", "hrnek", "hrncek",
        "bogre", "koszulk", "t-shirt", "tshirt", " shirt", "tricou", "maglietta",
        "camiseta", "tricko", "bluza", "hoodie", "mikina",
        " bag ", "torba", "torebka", "plecak", "rucksack", "backpack", "mochila",
        " borsa", "saszetka", "skin", "magnet", "magnes", "aimant",
        "podkladk", "podstawk", "moneta", " coin", "metal plate", "plakietk",
        "kalendarz", "calendar", "kalender", "pocztowk", "postcard", "postkarte",
        "carte post", "cartolina", "pohlednice", "kepeslap", "vykort", "ansichtk",
        // zabawki
        "zabawk", "mcdonald", "happy meal", "spielzeug", "jouet", "juguete",
        "giocattolo", "speelgoed", "hracka", " toy ", "toys", "miecze",
        // puste pudełka / sama płyta / niekompletne
        "karton", "sam box", "box only", "empty box", " box ", "pudelko", " pudl",
        "krabice", " doboz", "boite", "scatola", "bez gry",
        "kun disk", "disc only", "disk only", "sama plyt", "solo disco",
        "bez pudelk", "sam kartrid", "cartridge only", "cart only", " loose",
        // karty i planszówki
        "karty", " karta ", " card", " carte", "karten", "kaart", "kartya",
        "carti", " kort ", "kortti", "tcg", "planszow", "board game",
        // książki / komiksy / muzyka / film
        "ksiazka", " libro", " livre", " buch ", "kniha", "konyv", "artbook",
        "art book", "manga", "komiks", "comic", "poradnik", "przewodnik", "guide",
        "soundtrack", " ost ", "vinyl", "winyl", "vinil", "schallplatte",
        " cd ", " dvd", "blu-ray", "bluray",
        // wersje cyfrowe
        " kod ", "klucz", "digital", "cyfrow", "steam gift",
        // importy z innych regionów — inna okładka/język = inna wartość rynkowa
        "japo", "japan", "jpn", "giappon", "azjatyck", " asia", "korea",
    ];

    /// <param name="extraKeywords">Gruz-lista użytkownika (dashboard, przycisk 🚫)
    /// dokładana do wbudowanych słów kluczowych.</param>
    public static bool IsRelevant(string title, IReadOnlyCollection<string>? extraKeywords = null)
    {
        var t = " " + TitleNormalizer.StripDiacritics(title.ToLowerInvariant()) + " ";
        if (AccessoryKeywords.Any(t.Contains))
            return false;
        return extraKeywords is null
            || !extraKeywords.Any(k => t.Contains(TitleNormalizer.StripDiacritics(k.ToLowerInvariant())));
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
        decimal? bestReference = null;

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
                bestReference = reference;
                reasons.Add($"{1 - ratio:P0} poniżej mediany rynkowej ({reference:0.00} {listing.Currency}, " +
                            $"n={sample.Count}) i w dolnym kwartyle cen");
            }
            else if (ratio <= DealRatio)
            {
                tier = DealTier.Deal;
                bestDiscount = 1 - ratio;
                bestReference = reference;
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
            if (capDiscount > bestDiscount)
            {
                bestDiscount = capDiscount;
                bestReference = cap;
            }
        }

        if (tier == DealTier.None)
            return new DealVerdict(DealTier.None, 0, reasons);

        var score = Math.Round((double)bestDiscount * 100, 1);
        return new DealVerdict(tier, score, reasons, bestReference);
    }

    /// <summary>
    /// Dolna granica wiarygodnej ceny dla gry: poniżej 30% mediany oferta jest
    /// w strefie "podejrzanie tanio" (scam/sam karton), więc np. skan
    /// "najtańsze teraz" ją pomija. Przy zbyt małej próbce zostaje sam próg
    /// sensowności.
    /// </summary>
    public static decimal CredibleFloor(IReadOnlyList<decimal> marketPrices)
    {
        var sane = marketPrices.Where(p => p >= MinSanePrice).ToList();
        if (sane.Count < MinSample)
            return MinSanePrice;
        return Math.Max(MinSanePrice, TrimmedMedian(sane) * SuspiciousRatio);
    }

    private static readonly string[] ConsoleWords = ["konsol", "consol"];

    /// <summary>Konsola opisana tymi słowami to części/wrak, nie okazja.</summary>
    private static readonly string[] ConsolePartsWords =
    [
        "obudowa", "czesci", "naprawy", "naprawa", "uszkodz", "defekt",
        "broken", "parts", "zamiennik", "sprawdzenia", "spares",
    ];

    /// <summary>
    /// Polowanie na tanie KONSOLE: oferta z "konsola/console" w tytule
    /// i łowioną platformą w cenie ≤ progu to mocna okazja — niezależnie od
    /// filtra gier (konsole są w nim celowo, żeby nie psuły median gier).
    /// Dolny próg (30% progu) odsiewa wraki i scam; słowa części też.
    /// </summary>
    public static DealVerdict? ConsoleHuntVerdict(
        string title, string? platform, decimal price, IReadOnlyDictionary<string, decimal> hunts)
    {
        if (platform is null || !hunts.TryGetValue(platform, out var cap))
            return null;
        var t = " " + TitleNormalizer.StripDiacritics(title.ToLowerInvariant()) + " ";
        if (!ConsoleWords.Any(t.Contains) || ConsolePartsWords.Any(t.Contains))
            return null;
        var floor = Math.Max(MinSanePrice, cap * SuspiciousRatio);
        if (price < floor || price > cap)
            return null;
        return new DealVerdict(
            DealTier.Strong,
            Math.Round((double)((cap - price) / cap * 100), 1),
            [$"polowanie na konsolę {platform}: {price:0.00} ≤ próg {cap:0.00}"],
            ReferencePrice: cap);
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
