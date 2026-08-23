using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace VintedTracker;

/// <summary>
/// Normalizacja tytułów ofert i wykrywanie platformy — fundament trybu
/// firehose. Oferta z całej kategorii musi zostać dopasowana do konkretnej
/// gry (i platformy!), zanim policzymy dla niej medianę: ta sama gra na PS4
/// i PS5 ma inne ceny, a "NOWA Zelda TOTK Switch folia!!" i "The Legend of
/// Zelda: Tears of the Kingdom" to ta sama gra.
/// </summary>
public static class TitleNormalizer
{
    /// <summary>Słowa-szum, które nie niosą tożsamości gry.</summary>
    private static readonly HashSet<string> NoiseWords =
    [
        "gra", "gry", "gier", "nowa", "nowe", "nowy", "folia", "folii", "stan",
        "idealny", "idealnym", "bdb", "db", "uzywana", "uzywane", "uzywany",
        "wersja", "polska", "polsku", "pl", "eng", "ang", "na", "do", "od", "w", "z",
        "the", "of", "for", "and", "i", "oraz", "okazja", "tanio", "polecam",
        "oryginalna", "oryginalne", "komplet", "kompletna", "pudelkowa", "plyta",
    ];

    /// <summary>Znaczniki wielosłowne, wykrywane na sklejonym tytule
    /// ("nintendo switch" → "nintendoswitch"). Kolejność: od najbardziej
    /// specyficznych, żeby "nintendo wii" nie wpadło do Switcha.</summary>
    private static readonly (string Marker, string Platform)[] JoinedMarkers =
    [
        ("nintendoswitch", "switch"),
        ("playstation5", "ps5"), ("playstation4", "ps4"),
        ("playstation3", "ps3"), ("playstation2", "ps2"),
        ("xboxseriesx", "xbox-series"), ("xboxseriess", "xbox-series"), ("xboxseries", "xbox-series"),
        ("xbox360", "xbox360"), ("xboxone", "xbox-one"), ("xbox1", "xbox-one"),
        ("psvita", "psvita"), ("gameboyadvance", "gba"), ("gameboy", "gb"),
        ("wiiu", "wiiu"), ("nintendo3ds", "3ds"), ("nintendods", "ds"),
        ("superfamicom", "snes"), ("famicom", "snes"), ("supernintendo", "snes"),
        ("gamecube", "gamecube"), ("playstation1", "ps1"),
        // NTSC-J w tytule = import retro, nie wydanie na współczesną konsolę
        ("ntscj", "ntsc"),
    ];

    /// <summary>Znaczniki jednotokenowe — porównywane z całym tokenem, nie
    /// podciągiem, żeby "pc" nie łapało się w środku słowa.</summary>
    private static readonly Dictionary<string, string> TokenMarkers = new()
    {
        ["switch"] = "switch",
        ["ps5"] = "ps5", ["ps4"] = "ps4", ["ps3"] = "ps3", ["ps2"] = "ps2",
        ["xsx"] = "xbox-series", ["xss"] = "xbox-series",
        ["x360"] = "xbox360", ["xone"] = "xbox-one", ["xbox"] = "xbox-one",
        ["vita"] = "psvita", ["psp"] = "psp",
        ["wiiu"] = "wiiu", ["wii"] = "wii",
        ["3ds"] = "3ds", ["nds"] = "ds", ["ds"] = "ds", ["gba"] = "gba",
        ["snes"] = "snes", ["sfc"] = "snes", ["n64"] = "n64", ["ngc"] = "gamecube",
        ["ps1"] = "ps1", ["psx"] = "ps1", ["psone"] = "ps1", ["ntsc"] = "ntsc",
        ["pc"] = "pc",
    };

    /// <summary>"switch 2"/"switch-2" → jeden token "switch2". Switch 2 to
    /// osobny rynek cen, a bez sklejenia goła cyfra "2" wpadała na strażnika
    /// cyfr w dopasowaniu i oferty Switch 2 były odrzucane jako sequele.
    /// Lookahead pilnuje, żeby "switch 2023" czy "switch 20 gier" zostały
    /// przy Switchu 1. "switch2" NIE jest znacznikiem platformy do zrzucenia —
    /// niesie tożsamość ("Zelda BOTW Switch 2 Edition" ≠ wydanie na Switcha).</summary>
    private static readonly Regex Switch2Rx = new(@"switch[\s\-–]*2(?!\d)", RegexOptions.Compiled);

    private static string FoldSwitch2(string normalized) => Switch2Rx.Replace(normalized, "switch2");

    /// <summary>Tokeny tytułu po normalizacji: małe litery, bez diakrytyków,
    /// bez interpunkcji, bez słów-szumu i (domyślnie) bez znaczników platformy.
    /// <paramref name="keepPlatform"/> zachowuje znaczniki — potrzebne, gdy
    /// platforma jest częścią tożsamości gry ("Nintendo Switch Sports").</summary>
    public static List<string> Tokenize(string title, bool keepPlatform = false)
    {
        var normalized = FoldSwitch2(StripDiacritics(title.ToLowerInvariant()));
        var sb = new StringBuilder(normalized.Length);
        foreach (var ch in normalized)
            sb.Append(char.IsLetterOrDigit(ch) ? ch : ' ');
        return sb.ToString()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(t => !NoiseWords.Contains(t) && (keepPlatform || !IsPlatformToken(t)))
            .ToList();
    }

    /// <summary>Klucz grupowania: posortowane unikalne tokeny — kolejność słów
    /// w tytule nie ma znaczenia ("mario kart 8" == "8 kart mario").</summary>
    public static string NormKey(string title) =>
        string.Join(' ', Tokenize(title).Distinct().Order());

    /// <summary>Wykrywa platformę z tytułu; null gdy nie wskazano.</summary>
    public static string? DetectPlatform(string title)
    {
        var normalized = FoldSwitch2(StripDiacritics(title.ToLowerInvariant()));
        var flat = new StringBuilder(normalized.Length);
        var tokenBuf = new StringBuilder(normalized.Length + 1);
        foreach (var ch in normalized)
        {
            if (char.IsLetterOrDigit(ch))
            {
                flat.Append(ch);
                tokenBuf.Append(ch);
            }
            else
            {
                tokenBuf.Append(' ');
            }
        }
        var tokens = tokenBuf.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries);

        // Switch 2 przed znacznikami sklejonymi: sklejony tytuł zawiera też
        // "nintendoswitch", które ukradłoby trafienie dla Switcha 1. Tylko po
        // całym tokenie — sklejenie "switch 2023" dałoby fałszywe "switch2".
        if (tokens.Any(t => t is "switch2" or "nintendoswitch2"))
            return "switch2";

        var s = flat.ToString();
        foreach (var (marker, platform) in JoinedMarkers)
            if (s.Contains(marker))
                return platform;

        foreach (var token in tokens)
            if (TokenMarkers.TryGetValue(token, out var platform))
                return platform;
        return null;
    }

    private static bool IsPlatformToken(string token) =>
        TokenMarkers.ContainsKey(token) || token is "nintendo" or "playstation";

    /// <summary>Składa diakrytyki (á→a, ü→u, ł→l) — używane też przez filtr
    /// gruzu, żeby jeden rdzeń łapał warianty ze wszystkich języków Vinted.</summary>
    public static string StripDiacritics(string s)
    {
        var formD = s.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(formD.Length);
        foreach (var ch in formD)
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
                sb.Append(ch switch { 'ł' => 'l', 'Ł' => 'L', _ => ch });
        return sb.ToString().Normalize(NormalizationForm.FormC);
    }
}

/// <summary>Wzorzec dopasowania jednej gry z watchlisty.</summary>
public sealed record GamePattern(
    string Key, string Title, string? Platform, decimal? MaxPrice,
    IReadOnlyList<IReadOnlyList<string>> TokenSets)
{
    /// <summary>Słowa zbyt generyczne, by same niosły tożsamość gry — wzorzec
    /// złożony tylko z nich zachowuje znaczniki platformy ("switch sports"
    /// zamiast gołego "sports", które łapało każdy tytuł sportowy).</summary>
    private static readonly HashSet<string> GenericTokens =
        ["sports", "sport", "party", "world", "game", "games", "land", "story", "deluxe"];

    private static IReadOnlyList<string> PatternTokens(string phrase)
    {
        var tokens = TitleNormalizer.Tokenize(phrase);
        if (tokens.Count == 0 || tokens.All(GenericTokens.Contains))
            tokens = TitleNormalizer.Tokenize(phrase, keepPlatform: true);
        return tokens;
    }

    public static GamePattern FromWatch(GameWatch watch)
    {
        var sets = new List<IReadOnlyList<string>> { PatternTokens(watch.Query) };
        foreach (var alias in watch.Aliases ?? [])
            sets.Add(PatternTokens(alias));
        return new GamePattern(
            Key: "watch:" + watch.Query.ToLowerInvariant(),
            Title: watch.Title,
            // Domyślnie Switch: bez tego gra bez platformy w zapytaniu łapała
            // wydania na PS2/SNES/DVD itp. Nadpisywalne per gra w games.json.
            Platform: watch.Platform ?? TitleNormalizer.DetectPlatform(watch.Query) ?? "switch",
            MaxPrice: watch.MaxPrice,
            TokenSets: sets);
    }
}

/// <summary>
/// Dopasowuje oferty do gier. Watchlista ma priorytet (pewne dopasowanie
/// wszystkich tokenów zapytania/aliasu); reszta trafia do puli norm-key,
/// z której z czasem wyrastają gry "auto" (patrz auto-promocja w silniku).
/// </summary>
public sealed class GameMatcher(IReadOnlyList<GamePattern> patterns)
{
    /// <summary>
    /// Zwraca dopasowaną grę albo null (brak lub niejednoznaczność).
    /// Niejednoznaczność (np. "pokemon sword shield bundle" pasuje do dwóch
    /// gier równie dobrze) traktujemy jak brak — lepiej nie alertować, niż
    /// alertować z ceną bundla.
    /// </summary>
    public GamePattern? Match(string title, string? itemPlatform)
    {
        // keepPlatform: nadzbiór zwykłych tokenów — wzorce bez znaczników
        // platformy działają jak dotąd, a wzorce z nimi (np. "switch sports")
        // mogą ich wymagać.
        var tokens = TitleNormalizer.Tokenize(title, keepPlatform: true).ToHashSet();
        // Cyfra w tytule, której wzorzec nie zna, to zwykle sequel albo bundel
        // ("Ni No Kuni 2", "FIFA 23 + Mario Kart 8") — nie ta gra.
        var digits = tokens.Where(t => t.All(char.IsDigit)).ToHashSet();
        GamePattern? best = null;
        var bestLen = 0;
        var tie = false;

        foreach (var p in patterns)
        {
            // Platforma z tytułu oferty sprzeczna z platformą gry → nie ta gra.
            if (p.Platform is not null && itemPlatform is not null && p.Platform != itemPlatform)
                continue;
            foreach (var set in p.TokenSets)
            {
                if (set.Count == 0 || !set.All(tokens.Contains))
                    continue;
                if (digits.Except(set).Any())
                    continue;
                if (set.Count > bestLen)
                {
                    best = p;
                    bestLen = set.Count;
                    tie = false;
                }
                else if (set.Count == bestLen && !ReferenceEquals(p, best))
                {
                    tie = true;
                }
            }
        }

        return tie ? null : best;
    }
}
