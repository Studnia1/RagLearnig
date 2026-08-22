using VintedTracker;
using Xunit;

namespace VintedTracker.Tests;

public class SqliteStoreTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"vt-test-{Guid.NewGuid():N}.sqlite3");
    private readonly SqliteStore _store;

    public SqliteStoreTests() => _store = new SqliteStore(_path);

    public void Dispose()
    {
        _store.Dispose();
        File.Delete(_path);
    }

    private static ItemRecord Item(long id, decimal price, string? gameKey = "watch:pokemon sword",
        string normKey = "pokemon sword", DealTier tier = DealTier.None) =>
        new(id, $"Pokemon Sword #{id}", price, "PLN", $"https://vinted.pl/items/{id}", null,
            normKey, "switch", gameKey, gameKey is null ? null : "Pokémon Sword", true,
            tier, 0, null, []);

    [Fact]
    public void InsertAndIsKnownRoundtrip()
    {
        Assert.False(_store.IsKnown(1));
        _store.Insert(Item(1, 200));
        Assert.True(_store.IsKnown(1));
        Assert.Equal(1, _store.ItemCount());
    }

    [Fact]
    public void PricesForReturnsOnlyThatGame()
    {
        _store.Insert(Item(1, 200));
        _store.Insert(Item(2, 210));
        _store.Insert(Item(3, 90, gameKey: "watch:inna gra", normKey: "inna gra"));
        var prices = _store.PricesFor("watch:pokemon sword");
        Assert.Equal(2, prices.Count);
        Assert.DoesNotContain(90m, prices);
    }

    [Fact]
    public void RecentDealsReturnsOnlyTieredItemsNewestFirst()
    {
        _store.Insert(Item(1, 200));
        _store.Insert(Item(2, 100, tier: DealTier.Strong));
        _store.Insert(Item(3, 140, tier: DealTier.Deal));
        var deals = _store.RecentDeals();
        Assert.Equal(2, deals.Count);
        Assert.All(deals, d => Assert.NotEqual("None", d.Tier));
        Assert.Equal("Pokémon Sword", deals[0].Game);
    }

    [Fact]
    public void AutoPromotionGroupsUnmatchedItemsAndAssignsKey()
    {
        for (long i = 1; i <= 5; i++)
            _store.Insert(Item(i, 100 + i, gameKey: null, normKey: "hollow knight"));
        Assert.Equal(0, _store.PromoteAutoGames(minSample: 6)); // za mało
        _store.Insert(Item(6, 111, gameKey: null, normKey: "hollow knight"));
        Assert.Equal(1, _store.PromoteAutoGames(minSample: 6));

        var index = _store.AutoGameIndex();
        var (key, _) = index[("hollow knight", "switch")];
        Assert.StartsWith("auto:", key);
        Assert.Equal(6, _store.PricesFor(key).Count); // stare oferty przepisane na grę
        Assert.Equal(1, _store.AutoGameCount());
    }

    [Fact]
    public void BlocklistKeywordCleansRetroactively()
    {
        _store.Insert(Item(1, 100, tier: DealTier.Strong) with { Title = "Pokemon Sword fridge magnet" });
        _store.Insert(Item(2, 200));
        var cleaned = _store.AddBlocklistKeyword("Magnet");
        Assert.Equal(1, cleaned);
        Assert.Contains("magnet", _store.GetBlocklist());
        Assert.Empty(_store.RecentDeals());                       // okazja-gruz znikła
        Assert.Single(_store.PricesFor("watch:pokemon sword"));   // i wypadła z puli median
        Assert.True(_store.RemoveBlocklistKeyword("magnet"));
        Assert.Empty(_store.GetBlocklist());
    }

    [Fact]
    public void MetaRoundtrip()
    {
        Assert.Null(_store.GetMeta("catalog_id"));
        _store.SetMeta("catalog_id", "3025");
        _store.SetMeta("catalog_id", "3026");
        Assert.Equal("3026", _store.GetMeta("catalog_id"));
    }
}
