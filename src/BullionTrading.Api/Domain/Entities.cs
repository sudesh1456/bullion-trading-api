namespace BullionTrading.Api.Domain;

public enum Metal { Gold, Silver }

public enum Side { Buy, Sell }

public enum OrderType { Market, Limit }

public enum OrderStatus { Pending, Filled, Cancelled, Expired }

public enum AlertDirection { Above, Below }

public enum AlertStatus { Active, Triggered, Cancelled }

public enum Role { Trader, Admin }

public class User
{
    public int Id { get; set; }
    public required string Email { get; set; }
    public required string DisplayName { get; set; }
    public required string PasswordHash { get; set; }
    public Role Role { get; set; }
}

public class Product
{
    public int Id { get; set; }
    public required string Sku { get; set; }
    public required string Name { get; set; }
    public Metal Metal { get; set; }

    /// <summary>Fineness in parts per thousand, e.g. 999, 995, 916.</summary>
    public int Purity { get; set; }

    public decimal WeightGrams { get; set; }

    /// <summary>Markup over metal value when the customer buys.</summary>
    public decimal BuyPremiumPct { get; set; }

    /// <summary>Discount under metal value when the customer sells back.</summary>
    public decimal SellDiscountPct { get; set; }

    public bool IsActive { get; set; } = true;
}

public class Order
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public User? User { get; set; }
    public int ProductId { get; set; }
    public Product? Product { get; set; }
    public Side Side { get; set; }
    public OrderType Type { get; set; }
    public OrderStatus Status { get; set; }
    public int Quantity { get; set; }

    /// <summary>Limit orders only: worst acceptable unit price.</summary>
    public decimal? LimitPrice { get; set; }

    /// <summary>Unit price the order executed at.</summary>
    public decimal? FilledPrice { get; set; }

    public decimal? Total => FilledPrice * Quantity;

    /// <summary>Client-supplied key making order placement safe to retry.</summary>
    public string? IdempotencyKey { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? FilledAt { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
}

public class RateAlert
{
    public int Id { get; set; }
    public int UserId { get; set; }
    public Metal Metal { get; set; }
    public AlertDirection Direction { get; set; }

    /// <summary>Spot price per gram (999 fine) that triggers the alert.</summary>
    public decimal TargetPrice { get; set; }

    public AlertStatus Status { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? TriggeredAt { get; set; }
    public decimal? TriggeredPrice { get; set; }
}

/// <summary>Business-rule violation; mapped to a 4xx ProblemDetails response.</summary>
public class DomainException(string message, int statusCode = StatusCodes.Status400BadRequest) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}
