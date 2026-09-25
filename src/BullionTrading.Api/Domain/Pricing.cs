namespace BullionTrading.Api.Domain;

/// <summary>A spot price for one gram of 999-fine metal.</summary>
public readonly record struct SpotTick(Metal Metal, decimal PricePerGram, DateTimeOffset Timestamp);

public readonly record struct ProductPrice(decimal Buy, decimal Sell);

/// <summary>Pure pricing rules. Everything is decimal and rounded to paise.</summary>
public static class Pricing
{
    /// <summary>Value of the metal content in a product at the given 999 spot price.</summary>
    public static decimal MetalValue(Product product, decimal spotPerGram) =>
        spotPerGram * product.WeightGrams * product.Purity / 999m;

    public static ProductPrice PriceOf(Product product, decimal spotPerGram)
    {
        var value = MetalValue(product, spotPerGram);
        return new ProductPrice(
            Buy: Round(value * (1 + product.BuyPremiumPct / 100m)),
            Sell: Round(value * (1 - product.SellDiscountPct / 100m)));
    }

    /// <summary>Price the customer transacts at for the given side.</summary>
    public static decimal UnitPrice(Product product, decimal spotPerGram, Side side)
    {
        var price = PriceOf(product, spotPerGram);
        return side == Side.Buy ? price.Buy : price.Sell;
    }

    /// <summary>
    /// A buy limit fills once the price drops to or below the limit;
    /// a sell limit fills once the price rises to or above it.
    /// </summary>
    public static bool LimitIsMarketable(Side side, decimal limitPrice, decimal currentPrice) =>
        side == Side.Buy ? currentPrice <= limitPrice : currentPrice >= limitPrice;

    public static bool AlertIsTriggered(AlertDirection direction, decimal target, decimal spot) =>
        direction == AlertDirection.Above ? spot >= target : spot <= target;

    public static decimal Round(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);
}
