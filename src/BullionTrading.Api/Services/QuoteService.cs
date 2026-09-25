using BullionTrading.Api.Contracts;
using BullionTrading.Api.Data;
using BullionTrading.Api.Domain;
using BullionTrading.Api.Rates;
using Microsoft.Extensions.Caching.Memory;

namespace BullionTrading.Api.Services;

public class TradingOptions
{
    public const string Section = "Trading";

    /// <summary>How long a quoted price is honoured.</summary>
    public int QuoteTtlSeconds { get; set; } = 30;
}

/// <summary>
/// Price locking: a quote freezes the current price for a short window so the
/// customer pays exactly what they saw, even if the market moves before they confirm.
/// </summary>
public class QuoteService(AppDbContext db, RateBook rates, IMemoryCache cache, TimeProvider clock, Microsoft.Extensions.Options.IOptions<TradingOptions> options)
{
    private sealed record LockedQuote(int UserId, QuoteDto Quote);

    // Makes read-and-remove atomic so a quote can never be executed twice concurrently.
    private static readonly Lock Gate = new();

    public async Task<QuoteDto> CreateAsync(int userId, QuoteRequest request, CancellationToken ct)
    {
        var product = await db.Products.FindAsync([request.ProductId], ct);
        if (product is not { IsActive: true }) throw new DomainException("Product not found or not tradable", StatusCodes.Status404NotFound);

        var spot = rates.Require(product.Metal);
        var unit = Pricing.UnitPrice(product, spot.PricePerGram, request.Side);
        var expires = clock.GetUtcNow().AddSeconds(options.Value.QuoteTtlSeconds);
        var quote = new QuoteDto(Guid.NewGuid(), product.Id, request.Side, request.Quantity, unit, unit * request.Quantity, expires);

        // Relative cache lifetime (wall clock) plus slack; the authoritative expiry check uses TimeProvider in Take().
        cache.Set(Key(quote.QuoteId), new LockedQuote(userId, quote), TimeSpan.FromSeconds(options.Value.QuoteTtlSeconds + 5));
        return quote;
    }

    /// <summary>Consumes a quote (single use). Throws if missing, expired or owned by another user.</summary>
    public QuoteDto Take(int userId, Guid quoteId)
    {
        LockedQuote? locked;
        lock (Gate)
        {
            if (!cache.TryGetValue(Key(quoteId), out locked) || locked is null || locked.UserId != userId)
                throw new DomainException("Quote not found or already used", StatusCodes.Status404NotFound);
            cache.Remove(Key(quoteId));
        }

        if (locked.Quote.ExpiresAt <= clock.GetUtcNow())
            throw new DomainException("Quote expired, request a new price", StatusCodes.Status409Conflict);

        return locked.Quote;
    }

    private static string Key(Guid id) => $"quote:{id}";
}
