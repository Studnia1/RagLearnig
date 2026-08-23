using VintedTracker;
using Xunit;

namespace VintedTracker.Tests;

public class MatchingTests
{
    [Theory]
    [InlineData("NOWA Gra Miitopia Nintendo Switch folia!!", "miitopia")]
    [InlineData("Miitopia switch, stan idealny", "miitopia")]
    [InlineData("Mario Kart 8 Deluxe", "8 deluxe kart mario")]
    [InlineData("mario 8 kart deluxe", "8 deluxe kart mario")]
    public void NormKeyIgnoresNoiseOrderAndPlatform(string title, string expected) =>
        Assert.Equal(expected, TitleNormalizer.NormKey(title));

    [Theory]
    [InlineData("Zelda TOTK Nintendo Switch", "switch")]
    [InlineData("Gra Elden Ring PS5", "ps5")]
    [InlineData("Nintendo Wii Sports", "wii")]
    [InlineData("God of War PlayStation 4", "ps4")]
    [InlineData("Wiedźmin 3 xbox one", "xbox-one")]
    [InlineData("Cyberpunk 2077", null)]
    public void DetectsPlatform(string title, string? expected) =>
        Assert.Equal(expected, TitleNormalizer.DetectPlatform(title));

    [Theory]
    [InlineData("Big Brain Academy - Gra na Nintendo DS", "ds")]
    [InlineData("Nintendo 3DS Zelda Ocarina of Time", "3ds")]
    [InlineData("Romancing Saga 2 sfc NTSC-J", "ntsc")]
    [InlineData("Romancing Saga 2 sfc", "snes")]
    [InlineData("Romancing SaGa 2 – Super Famicom (SNES) Japan", "snes")]
    [InlineData("Romancing SaGa Minstrel Song NTSC-J #2", "ntsc")]
    [InlineData("Tales of vesperia japońska ps3 best hits", "ps3")]
    public void HandheldPlatformsAreDetected(string title, string expected) =>
        Assert.Equal(expected, TitleNormalizer.DetectPlatform(title));

    [Fact]
    public void GenericTitleGamesKeepPlatformTokens()
    {
        var matcher = new GameMatcher([
            GamePattern.FromWatch(new GameWatch
            {
                Title = "Nintendo Switch Sports", Query = "nintendo switch sports",
                Aliases = ["switch sports"],
            }),
        ]);
        // Samo "sports" łapało każdy tytuł sportowy — teraz wymagamy platformy.
        Assert.Null(matcher.Match("Nintendo Looney Tunes wacky world of sports", null));
        Assert.Null(matcher.Match("Gra Mario sports mix", null));
        Assert.NotNull(matcher.Match("Nintendo Switch Sports, komplet z opaską", "switch"));
        Assert.NotNull(matcher.Match("Switch Sports stan idealny", "switch"));
    }

    [Fact]
    public void UnknownDigitInTitleRejectsMatch()
    {
        var matcher = new GameMatcher([
            GamePattern.FromWatch(new GameWatch { Title = "Ni no Kuni", Query = "ni no kuni" }),
            GamePattern.FromWatch(new GameWatch { Title = "Mario Kart 8 Deluxe", Query = "mario kart 8 deluxe", Aliases = ["mario kart 8"] }),
        ]);
        // Sequel z cyfrą, której wzorzec nie zna → nie ta gra.
        Assert.Null(matcher.Match("Ni No Kuni 2 Revenant Kingdom switch", "switch"));
        Assert.NotNull(matcher.Match("Ni no Kuni Wrath of the White Witch", null));
        // Cyfra znana wzorcowi przechodzi; bundel z obcą cyfrą odpada.
        Assert.NotNull(matcher.Match("Mario Kart 8 Deluxe Nintendo Switch", "switch"));
        Assert.Null(matcher.Match("FIFA 23 + Mario Kart 8 zestaw 2 gier", "switch"));
    }

    [Fact]
    public void WatchGamesDefaultToSwitchAndRejectOtherPlatforms()
    {
        var matcher = new GameMatcher([
            GamePattern.FromWatch(new GameWatch { Title = "Suikoden I&II HD", Query = "suikoden" }),
        ]);
        // Brak platformy w zapytaniu = domyślnie Switch → wydanie PS2 odpada.
        Assert.Null(matcher.Match("Genso Suikoden 3 ps2 playstation 2 ntsc-j", "ps2"));
        Assert.NotNull(matcher.Match("Suikoden I&II HD Remaster Nintendo Switch", "switch"));
        Assert.NotNull(matcher.Match("Suikoden I&II HD Remaster", null));
    }

    [Theory]
    [InlineData("Naklejki Nintendo Fire Emblem Three Hopes Kolekcjonerskie")]
    [InlineData("Mario&Luigi brothership pins")]
    [InlineData("Mario + Rabbids Kingdom Battle – The Official Soundtrack CD– originál")]
    [InlineData("Monster Hunter Stories 2 promó poszter")]
    [InlineData("Zabawki McDonald's Super Mario Bros Mario Kart 8 Deluxe")]
    [InlineData("Final Fantasy IX board game")]
    [InlineData("Final Fantasy VII dvd")]
    [InlineData("The Legend of Zelda Tears of the Kingdom coin")]
    [InlineData("Pocztówki Nintendo Triangle Strategy")]
    [InlineData("Super Mario RPG Princess Peach pin na plecak")]
    [InlineData("Tales of vesperia japońska ps3 best hits")]
    [InlineData("Persona 5 Royal Japan import")]
    [InlineData("Zelda Breath of the Wild JPN")]
    [InlineData("Mario Kart 8 wersja azjatycka")]
    [InlineData("Schlüsselanhänger Zelda Nintendo")]
    [InlineData("Plakát Super Mario Odyssey")]
    [InlineData("Carte postale Animal Crossing")]
    [InlineData("Mario Kart Spielzeug Set")]
    [InlineData("Funda protectora Nintendo Switch Zelda")]
    [InlineData("Kirby plüss figura")]
    [InlineData("Zelda samolepky nálepky set")]
    [InlineData("Super Mario Odyssey bez pudełka")]
    [InlineData("Zelda BOTW sam kartridż")]
    [InlineData("Mario Kart 8 cartridge only")]
    public void MerchAndWrongMediaAreFilteredOut(string title) =>
        Assert.False(DealEvaluator.IsRelevant(title));

    [Theory]
    [InlineData("Pokemon Sword Nintendo Switch stan idealny")]
    [InlineData("Mario Kart 8 Deluxe Switch komplet")]
    [InlineData("Zelda Tears of the Kingdom, folia")]
    [InlineData("Cassette Beasts Nintendo Switch")]
    [InlineData("Disco Elysium Final Cut Switch")]
    public void RealGameListingsPassTheFilter(string title) =>
        Assert.True(DealEvaluator.IsRelevant(title));

    [Fact]
    public void CustomBlocklistKeywordFiltersListing()
    {
        // "kolekcjonera" nie jest na wbudowanej liście — filtruje dopiero gruz-lista.
        Assert.True(DealEvaluator.IsRelevant("Gra dla kolekcjonera vintage"));
        Assert.False(DealEvaluator.IsRelevant("Gra dla kolekcjonera vintage", ["kolekcjonera"]));
    }

    [Fact]
    public void PolishDiacriticsAreStripped()
    {
        Assert.Equal(TitleNormalizer.NormKey("Wiedźmin Dziki Gon"), TitleNormalizer.NormKey("Wiedzmin dziki gon"));
    }

    private static GameMatcher BuildMatcher() => new([
        GamePattern.FromWatch(new GameWatch { Title = "Pokémon Sword", Query = "pokemon sword" }),
        GamePattern.FromWatch(new GameWatch { Title = "Pokémon Shield", Query = "pokemon shield" }),
        GamePattern.FromWatch(new GameWatch
        {
            Title = "Zelda: Tears of the Kingdom",
            Query = "zelda tears of the kingdom switch",
            Aliases = ["zelda totk"],
        }),
    ]);

    [Fact]
    public void MatchesDespiteNoiseWords()
    {
        var m = BuildMatcher().Match("NOWA gra Pokemon Sword Nintendo Switch w folii", "switch");
        Assert.Equal("Pokémon Sword", m?.Title);
    }

    [Fact]
    public void BundleMatchingTwoGamesEquallyIsAmbiguous()
    {
        Assert.Null(BuildMatcher().Match("Pokemon Sword + Shield zestaw", "switch"));
    }

    [Fact]
    public void AliasMatches()
    {
        var m = BuildMatcher().Match("Zelda TOTK stan bdb", null);
        Assert.Equal("Zelda: Tears of the Kingdom", m?.Title);
    }

    [Fact]
    public void PlatformMismatchRejectsMatch()
    {
        // Gra z watchlisty jest na Switcha; oferta wskazuje PS5 → nie ta gra.
        Assert.Null(BuildMatcher().Match("zelda tears of the kingdom", "ps5"));
    }

    [Fact]
    public void UnknownItemPlatformStillMatches()
    {
        var m = BuildMatcher().Match("zelda tears of the kingdom", null);
        Assert.Equal("Zelda: Tears of the Kingdom", m?.Title);
    }
}
