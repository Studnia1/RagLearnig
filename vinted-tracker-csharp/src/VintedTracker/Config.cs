using System.Text.Json;
using System.Text.Json.Serialization;

namespace VintedTracker;

public sealed class Defaults
{
    [JsonPropertyName("baseUrl")] public string BaseUrl { get; init; } = "https://www.vinted.pl";
    [JsonPropertyName("pollIntervalSeconds")] public int PollIntervalSeconds { get; init; } = 300;
    [JsonPropertyName("statePath")] public string StatePath { get; init; } = "data/state.json";
    [JsonPropertyName("watchlistPath")] public string WatchlistPath { get; init; } = "games.json";
    [JsonPropertyName("listenUrl")] public string ListenUrl { get; init; } = "http://localhost:5177";

    /// <summary>ID katalogu gier na danej domenie Vinted. Puste = auto-wykrycie
    /// z drzewa kategorii przy pierwszym cyklu.</summary>
    [JsonPropertyName("catalogIds")] public List<int> CatalogIds { get; init; } = [];

    /// <summary>Ile stron katalogu wolno przejść w pierwszym przebiegu (backfill).
    /// Vinted i tak ucina stronicowanie przy ~1000 ofert (10 stron po 96).</summary>
    [JsonPropertyName("backfillPages")] public int BackfillPages { get; init; } = 10;

    /// <summary>Limit stron na zwykły cykl. 10 = pełny zasięg stronicowania
    /// Vinted, żeby po włączeniu komputera dogonić wszystko, co się da;
    /// watermark i tak kończy wcześniej, gdy nie ma nowych ofert.</summary>
    [JsonPropertyName("maxPagesPerCycle")] public int MaxPagesPerCycle { get; init; } = 10;

    /// <summary>Tryb wishlisty (domyślny): jedynym źródłem ofert są celowane
    /// wyszukiwania śledzonych gier i polowań na konsole. Bez tego tracker
    /// zasysa cały katalog gier (firehose) — więcej pokrycia, ale i dużo
    /// szumu spoza wishlisty w okazjach i słowniku gier auto.</summary>
    [JsonPropertyName("watchlistOnly")] public bool WatchlistOnly { get; init; } = true;

    /// <summary>Ile gier z kolejki odpytać w jednym cyklu. Pełny obieg trwa
    /// (liczba gier / to) × pollIntervalSeconds — więcej = świeższe dane,
    /// ale i większe ryzyko blokady anty-bot.</summary>
    [JsonPropertyName("watchQueuePerCycle")] public int WatchQueuePerCycle { get; init; } = 6;

    /// <summary>Minimalna marża (mediana − cena) w walucie rynku, żeby wysłać push.</summary>
    [JsonPropertyName("minMargin")] public decimal MinMargin { get; init; } = 50m;

    /// <summary>Od ilu ofert grupa nierozpoznanych tytułów staje się grą "auto".</summary>
    [JsonPropertyName("autoPromoteMinSample")] public int AutoPromoteMinSample { get; init; } = 8;

    /// <summary>Model Claude do weryfikacji ofert po zdjęciach (aktywna, gdy
    /// ustawiono ANTHROPIC_API_KEY). "claude-haiku-4-5" tnie koszt ~5x
    /// przy prostszej ocenie.</summary>
    [JsonPropertyName("visionModel")] public string VisionModel { get; init; } = "claude-opus-5";

    /// <summary>Gdy true, AI odrzuca też oferty gier bez pudełka (sam
    /// kartridż/płyta widoczne na zdjęciu) — nie tylko złą grę/platformę.</summary>
    [JsonPropertyName("visionRequireComplete")] public bool VisionRequireComplete { get; init; } = true;

    /// <summary>Platformy wycinane całkowicie (jak gruz) — oferty nie wchodzą
    /// do median, okazji ani gier auto. Domyślnie wszystko poza Switchem:
    /// śledzimy rynek gier Switch; polowania na konsole (platformHunts)
    /// działają NIEZALEŻNIE od tej listy.</summary>
    [JsonPropertyName("excludedPlatforms")] public List<string> ExcludedPlatforms { get; init; } =
    [
        "xbox-one", "xbox-series", "xbox360",
        "ps1", "ps2", "ps3", "ps4", "ps5", "psp", "psvita",
        "wii", "wiiu", "ds", "3ds", "gb", "gba",
        "n64", "gamecube", "snes", "ntsc", "pc",
    ];

    /// <summary>Polowanie na tanie KONSOLE: oferta konsoli tej platformy w cenie
    /// ≤ progu (waluta rynku) to mocna okazja z pushem. Klucze = platformy
    /// z wykrywania (3ds, psvita, ps3, ps4…).</summary>
    [JsonPropertyName("platformHunts")] public Dictionary<string, decimal> PlatformHunts { get; init; } = new()
    {
        ["3ds"] = 100m,
        ["psvita"] = 150m,
        ["ps3"] = 120m,
        ["ps4"] = 350m,
        ["xbox360"] = 100m,
    };
}

public sealed class Config
{
    [JsonPropertyName("defaults")] public Defaults Defaults { get; init; } = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static Config Load(string path) =>
        JsonSerializer.Deserialize<Config>(File.ReadAllText(path), JsonOptions)
        ?? throw new InvalidDataException($"Pusta lub nieprawidłowa konfiguracja: {path}");
}
