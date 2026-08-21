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
