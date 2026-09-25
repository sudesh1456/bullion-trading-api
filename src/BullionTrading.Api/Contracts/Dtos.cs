using System.ComponentModel.DataAnnotations;
using BullionTrading.Api.Domain;

namespace BullionTrading.Api.Contracts;

public record LoginRequest([Required, EmailAddress] string Email, [Required] string Password);

public record LoginResponse(string AccessToken, DateTimeOffset ExpiresAt, string DisplayName, Role Role);

public record SpotRateDto(Metal Metal, decimal PricePerGram, DateTimeOffset Timestamp);

public record ProductDto(int Id, string Sku, string Name, Metal Metal, int Purity, decimal WeightGrams, decimal? BuyPrice, decimal? SellPrice, bool IsActive)
{
    public static ProductDto From(Product p, SpotTick? spot)
    {
        var price = spot is { } s ? Pricing.PriceOf(p, s.PricePerGram) : (ProductPrice?)null;
        return new(p.Id, p.Sku, p.Name, p.Metal, p.Purity, p.WeightGrams, price?.Buy, price?.Sell, p.IsActive);
    }
}

/// <summary>Admin view of a product, including the pricing inputs.</summary>
public record AdminProductDto(int Id, string Sku, string Name, Metal Metal, int Purity, decimal WeightGrams, decimal? BuyPrice, decimal? SellPrice, bool IsActive, decimal BuyPremiumPct, decimal SellDiscountPct)
{
    public static AdminProductDto From(Product p, SpotTick? spot)
    {
        var dto = ProductDto.From(p, spot);
        return new(dto.Id, dto.Sku, dto.Name, dto.Metal, dto.Purity, dto.WeightGrams, dto.BuyPrice, dto.SellPrice, dto.IsActive, p.BuyPremiumPct, p.SellDiscountPct);
    }
}

public record QuoteRequest(int ProductId, Side Side, [Range(1, 10_000)] int Quantity);

public record QuoteDto(Guid QuoteId, int ProductId, Side Side, int Quantity, decimal UnitPrice, decimal Total, DateTimeOffset ExpiresAt);

public record ExecuteQuoteRequest(Guid QuoteId);

public record LimitOrderRequest(int ProductId, Side Side, [Range(1, 10_000)] int Quantity, [Range(0.01, 1e12)] decimal LimitPrice, DateTimeOffset? ExpiresAt);

public record OrderDto(
    int Id, int ProductId, string ProductName, Side Side, OrderType Type, OrderStatus Status, int Quantity,
    decimal? LimitPrice, decimal? FilledPrice, decimal? Total, DateTimeOffset CreatedAt, DateTimeOffset? FilledAt, DateTimeOffset? ExpiresAt)
{
    public static OrderDto From(Order o) => new(
        o.Id, o.ProductId, o.Product?.Name ?? "", o.Side, o.Type, o.Status, o.Quantity,
        o.LimitPrice, o.FilledPrice, o.Total, o.CreatedAt, o.FilledAt, o.ExpiresAt);
}

public record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, int TotalCount);

public record CreateAlertRequest(Metal Metal, AlertDirection Direction, [Range(0.01, 1e9)] decimal TargetPrice);

public record AlertDto(int Id, Metal Metal, AlertDirection Direction, decimal TargetPrice, AlertStatus Status, DateTimeOffset CreatedAt, DateTimeOffset? TriggeredAt, decimal? TriggeredPrice)
{
    public static AlertDto From(RateAlert a) => new(a.Id, a.Metal, a.Direction, a.TargetPrice, a.Status, a.CreatedAt, a.TriggeredAt, a.TriggeredPrice);
}

public record UpdatePricingRequest([Range(0, 50)] decimal BuyPremiumPct, [Range(0, 50)] decimal SellDiscountPct, bool IsActive);

public record DailyStatsDto(DateOnly Date, int FilledOrders, int PendingOrders, decimal BuyTurnover, decimal SellTurnover, IReadOnlyList<MetalVolumeDto> ByMetal);

public record MetalVolumeDto(Metal Metal, decimal BuyTurnover, decimal SellTurnover);
