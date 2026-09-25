using BullionTrading.Api.Domain;

namespace BullionTrading.Tests;

public class PricingTests
{
    private static Product Product(int purity = 999, decimal grams = 10, decimal premium = 2, decimal discount = 1) => new()
    {
        Sku = "T", Name = "Test", Metal = Metal.Gold, Purity = purity, WeightGrams = grams,
        BuyPremiumPct = premium, SellDiscountPct = discount,
    };

    [Fact]
    public void Applies_premium_and_discount_to_metal_value()
    {
        var price = Pricing.PriceOf(Product(), spotPerGram: 10_000m);
        Assert.Equal(102_000m, price.Buy);   // 100,000 + 2%
        Assert.Equal(99_000m, price.Sell);   // 100,000 − 1%
    }

    [Fact]
    public void Scales_metal_value_by_purity()
    {
        // 22K (916) gold is worth 916/999 of fine gold.
        var value = Pricing.MetalValue(Product(purity: 916, grams: 1), 999m);
        Assert.Equal(916m, value);
    }

    [Fact]
    public void Rounds_to_two_decimals_half_away_from_zero()
    {
        Assert.Equal(1.01m, Pricing.Round(1.005m));
        Assert.Equal(-1.01m, Pricing.Round(-1.005m));
    }

    [Theory]
    [InlineData(Side.Buy, 100, 99.99, true)]    // price fell below buy limit
    [InlineData(Side.Buy, 100, 100, true)]      // exactly at limit
    [InlineData(Side.Buy, 100, 100.01, false)]
    [InlineData(Side.Sell, 100, 100.01, true)]  // price rose above sell limit
    [InlineData(Side.Sell, 100, 99.99, false)]
    public void Limit_orders_fill_only_when_marketable(Side side, decimal limit, decimal current, bool expected) =>
        Assert.Equal(expected, Pricing.LimitIsMarketable(side, limit, current));

    [Theory]
    [InlineData(AlertDirection.Above, 100, 100, true)]
    [InlineData(AlertDirection.Above, 100, 99, false)]
    [InlineData(AlertDirection.Below, 100, 100, true)]
    [InlineData(AlertDirection.Below, 100, 101, false)]
    public void Alerts_trigger_on_crossing(AlertDirection direction, decimal target, decimal spot, bool expected) =>
        Assert.Equal(expected, Pricing.AlertIsTriggered(direction, target, spot));
}
