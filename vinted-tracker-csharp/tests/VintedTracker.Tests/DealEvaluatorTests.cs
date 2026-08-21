using System.Text.Json.Nodes;
using VintedTracker;
using Xunit;

namespace VintedTracker.Tests;

public class DealEvaluatorTests
{
    private static Listing MakeListing(decimal price, string title = "Gra testowa") =>
        new(Id: 1, Title: title, Price: price, Currency: "PLN", TotalPrice: null,
            Brand: null, Url: "https://www.vinted.pl/items/1", PhotoUrl: null);

    [Fact]
    public void BelowMaxPriceIsDeal()
    {
        var v = DealEvaluator.Evaluate(MakeListing(100), maxPrice: 120, marketPrices: []);
        Assert.True(v.IsDeal);
        Assert.Contains("próg", v.Reasons[0]);
    }

    [Fact]
    public void AboveMaxPriceWithNoMarketIsNotDeal()
    {
        var v = DealEvaluator.Evaluate(MakeListing(150), maxPrice: 120, marketPrices: []);
        Assert.False(v.IsDeal);
    }

    [Fact]
    public void FarBelowMedianIsDeal()
    {
        decimal[] market = [200, 210, 190, 205, 195, 200];
        var v = DealEvaluator.Evaluate(MakeListing(90), maxPrice: null, marketPrices: market);
        Assert.True(v.IsDeal);
        Assert.Contains("mediany", v.Reasons[0]);
    }

    [Fact]
    public void NearMedianIsNotDeal()
    {
        decimal[] market = [200, 210, 190, 205, 195, 200];
        var v = DealEvaluator.Evaluate(MakeListing(180), maxPrice: null, marketPrices: market);
        Assert.False(v.IsDeal);
    }

    [Fact]
    public void SmallSampleMedianIgnored()
    {
        var v = DealEvaluator.Evaluate(MakeListing(90), maxPrice: null, marketPrices: [200, 210]);
        Assert.False(v.IsDeal);
    }

    [Fact]
    public void SuspiciouslyCheapRejected()
    {
        var market = Enumerable.Repeat(200m, 10).ToList();
        var v = DealEvaluator.Evaluate(MakeListing(1), maxPrice: 120, marketPrices: market);
        Assert.False(v.IsDeal);
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
