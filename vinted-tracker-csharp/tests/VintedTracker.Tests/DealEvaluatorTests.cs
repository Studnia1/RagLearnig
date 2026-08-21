using System.Text.Json.Nodes;
using VintedTracker;
using Xunit;

namespace VintedTracker.Tests;

public class DealEvaluatorTests
{
    private static Listing MakeListing(decimal price, string title = "Pokemon Sword Nintendo Switch") =>
        new(Id: 1, Title: title, Price: price, Currency: "PLN", TotalPrice: null,
            Brand: null, Url: "https://www.vinted.pl/items/1", PhotoUrl: null);

    private static readonly decimal[] Market =
        [180, 190, 195, 200, 200, 205, 210, 215, 220, 400]; // 400 = bundel-outlier

    [Fact]
    public void FarBelowMedianAndInBottomQuartileIsStrong()
    {
        var v = DealEvaluator.Evaluate(MakeListing(110), maxPrice: null, marketPrices: Market);
        Assert.Equal(DealTier.Strong, v.Tier);
        Assert.Contains("mediany", v.Reasons[0]);
    }

    [Fact]
    public void ModeratelyBelowMedianIsOrdinaryDeal()
    {
        var v = DealEvaluator.Evaluate(MakeListing(145), maxPrice: null, marketPrices: Market);
        Assert.Equal(DealTier.Deal, v.Tier);
    }

    [Fact]
    public void NearMedianIsNotDeal()
    {
        var v = DealEvaluator.Evaluate(MakeListing(190), maxPrice: null, marketPrices: Market);
        Assert.Equal(DealTier.None, v.Tier);
    }

    [Fact]
    public void AbsurdlyCheapIsSuspiciousNotStrong()
    {
        var v = DealEvaluator.Evaluate(MakeListing(40), maxPrice: null, marketPrices: Market);
        Assert.Equal(DealTier.Suspicious, v.Tier);
        Assert.False(v.IsDeal);
    }

    [Fact]
    public void WellUnderManualCapIsStrong()
    {
        var v = DealEvaluator.Evaluate(MakeListing(100), maxPrice: 130, marketPrices: []);
        Assert.Equal(DealTier.Strong, v.Tier);
    }

    [Fact]
    public void JustUnderManualCapIsOrdinaryDeal()
    {
        var v = DealEvaluator.Evaluate(MakeListing(125), maxPrice: 130, marketPrices: []);
        Assert.Equal(DealTier.Deal, v.Tier);
    }

    [Fact]
    public void SmallSampleMedianIgnored()
    {
        var v = DealEvaluator.Evaluate(MakeListing(90), maxPrice: null, marketPrices: [200, 210]);
        Assert.Equal(DealTier.None, v.Tier);
    }

    [Fact]
    public void AccessoryIsFilteredOutEvenWhenCheap()
    {
        var v = DealEvaluator.Evaluate(
            MakeListing(15, "Etui na Nintendo Switch pokemon sword"),
            maxPrice: 130, marketPrices: Market);
        Assert.Equal(DealTier.None, v.Tier);
        Assert.Contains("akcesorium", v.Reasons[0]);
    }

    [Fact]
    public void BoxOnlyIsFilteredOut()
    {
        var v = DealEvaluator.Evaluate(
            MakeListing(20, "Pokemon Sword - samo pudełko po grze"),
            maxPrice: 130, marketPrices: Market);
        Assert.Equal(DealTier.None, v.Tier);
    }

    [Fact]
    public void ScoreEqualsDiscountPercent()
    {
        // 110 vs przycięta mediana 202.50 → rabat ~45.7%
        var v = DealEvaluator.Evaluate(MakeListing(110), maxPrice: null, marketPrices: Market);
        Assert.InRange(v.Score, 45, 47);
    }

    [Fact]
    public void TrimmedMedianResistsOutliers()
    {
        // Bez przycięcia bundel za 400 zawyżałby odniesienie.
        var trimmed = DealEvaluator.TrimmedMedian(Market);
        Assert.InRange(trimmed, 195, 210);
    }

    [Theory]
    [InlineData(new double[] { 1, 2, 3 }, 2)]
    [InlineData(new double[] { 4, 1, 3, 2 }, 2.5)]
    public void MedianIsComputedCorrectly(double[] values, double expected)
    {
        var result = DealEvaluator.Median(values.Select(v => (decimal)v).ToList());
        Assert.Equal((decimal)expected, result);
    }

    [Fact]
    public void ListingParsesPriceObjectVariant()
    {
        var item = JsonNode.Parse("""
        {
            "id": 42,
            "title": "Zelda TOTK",
            "price": {"amount": "149.0", "currency_code": "PLN"},
            "total_item_price": {"amount": "156.7", "currency_code": "PLN"},
            "brand_title": "Nintendo",
            "photo": {"url": "https://img"}
        }
        """)!;
        var l = Listing.FromApi(item, "https://www.vinted.pl");
        Assert.Equal(149.0m, l.Price);
        Assert.Equal("PLN", l.Currency);
        Assert.Equal(156.7m, l.TotalPrice);
        Assert.Equal("https://www.vinted.pl/items/42", l.Url);
    }

    [Fact]
    public void ListingParsesScalarPriceVariant()
    {
        var item = JsonNode.Parse("""{"id": 7, "title": "Gra", "price": "59.99"}""")!;
        var l = Listing.FromApi(item, "https://www.vinted.pl");
        Assert.Equal(59.99m, l.Price);
        Assert.Equal("?", l.Currency);
    }
}
