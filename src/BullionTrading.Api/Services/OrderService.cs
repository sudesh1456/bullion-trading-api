using BullionTrading.Api.Contracts;
using BullionTrading.Api.Data;
using BullionTrading.Api.Domain;
using BullionTrading.Api.Rates;
using Microsoft.EntityFrameworkCore;

namespace BullionTrading.Api.Services;

public class OrderService(AppDbContext db, QuoteService quotes, RateBook rates, TimeProvider clock, ITradeNotifier notifier)
{
    /// <summary>
    /// Executes a locked quote. Retrying with the same idempotency key returns the
    /// original order instead of buying twice (e.g. after a network timeout).
    /// </summary>
    public async Task<(Order Order, bool Replayed)> ExecuteQuoteAsync(int userId, Guid quoteId, string idempotencyKey, CancellationToken ct)
    {
        var existing = await FindByIdempotencyKey(userId, idempotencyKey, ct);
        if (existing is not null) return (existing, true);

        var quote = quotes.Take(userId, quoteId);
        var now = clock.GetUtcNow();
        var order = new Order
        {
            UserId = userId,
            ProductId = quote.ProductId,
            Side = quote.Side,
            Type = OrderType.Market,
            Status = OrderStatus.Filled,
            Quantity = quote.Quantity,
            FilledPrice = quote.UnitPrice,
            IdempotencyKey = idempotencyKey,
            CreatedAt = now,
            FilledAt = now,
        };
        db.Orders.Add(order);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // A concurrent request with the same key won the insert (unique index).
            db.Entry(order).State = EntityState.Detached;
            var raced = await FindByIdempotencyKey(userId, idempotencyKey, ct);
            if (raced is null) throw;
            return (raced, true);
        }

        await db.Entry(order).Reference(o => o.Product).LoadAsync(ct);
        return (order, false);
    }

    public async Task<Order> PlaceLimitAsync(int userId, LimitOrderRequest request, string? idempotencyKey, CancellationToken ct)
    {
        if (idempotencyKey is not null && await FindByIdempotencyKey(userId, idempotencyKey, ct) is { } existing)
            return existing;

        var product = await db.Products.FindAsync([request.ProductId], ct);
        if (product is not { IsActive: true }) throw new DomainException("Product not found or not tradable", StatusCodes.Status404NotFound);

        var now = clock.GetUtcNow();
        if (request.ExpiresAt is { } exp && exp <= now) throw new DomainException("ExpiresAt must be in the future");

        var order = new Order
        {
            UserId = userId,
            ProductId = product.Id,
            Product = product,
            Side = request.Side,
            Type = OrderType.Limit,
            Status = OrderStatus.Pending,
            Quantity = request.Quantity,
            LimitPrice = Pricing.Round(request.LimitPrice),
            IdempotencyKey = idempotencyKey,
            CreatedAt = now,
            ExpiresAt = request.ExpiresAt,
        };
        db.Orders.Add(order);
        await db.SaveChangesAsync(ct);

        // Fill immediately if the limit is already marketable.
        if (rates.Latest(product.Metal) is { } spot) await MatchAsync(spot, ct);
        await db.Entry(order).ReloadAsync(ct);
        return order;
    }

    public async Task<Order> CancelAsync(int userId, int orderId, bool isAdmin, CancellationToken ct)
    {
        var order = await db.Orders.Include(o => o.Product).FirstOrDefaultAsync(o => o.Id == orderId, ct);
        if (order is null || (order.UserId != userId && !isAdmin)) throw new DomainException("Order not found", StatusCodes.Status404NotFound);

        // Conditional update: only a still-pending order can be cancelled, so a
        // cancel racing with the matcher can never undo a fill.
        var changed = await db.Orders
            .Where(o => o.Id == orderId && o.Status == OrderStatus.Pending)
            .ExecuteUpdateAsync(s => s.SetProperty(o => o.Status, OrderStatus.Cancelled), ct);
        if (changed == 0) throw new DomainException($"Order is already {order.Status}", StatusCodes.Status409Conflict);

        await db.Entry(order).ReloadAsync(ct);
        return order;
    }

    /// <summary>Fills marketable limit orders and expires stale ones for the tick's metal.</summary>
    public async Task<int> MatchAsync(SpotTick tick, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var pending = await db.Orders
            .Include(o => o.Product)
            .Where(o => o.Status == OrderStatus.Pending && o.Product!.Metal == tick.Metal)
            .ToListAsync(ct);

        var filled = 0;
        foreach (var order in pending)
        {
            if (order.ExpiresAt is { } exp && exp <= now)
            {
                await Transition(order.Id, OrderStatus.Expired, null, null, ct);
                continue;
            }

            var price = Pricing.UnitPrice(order.Product!, tick.PricePerGram, order.Side);
            if (!Pricing.LimitIsMarketable(order.Side, order.LimitPrice!.Value, price)) continue;

            if (await Transition(order.Id, OrderStatus.Filled, price, now, ct))
            {
                filled++;
                order.Status = OrderStatus.Filled;
                order.FilledPrice = price;
                order.FilledAt = now;
                await notifier.OrderFilledAsync(order.UserId, OrderDto.From(order), ct);
            }
        }
        return filled;
    }

    public async Task<PagedResult<OrderDto>> ListAsync(int? userId, OrderStatus? status, int page, int pageSize, CancellationToken ct)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);

        var query = db.Orders.AsNoTracking().Include(o => o.Product).AsQueryable();
        if (userId is not null) query = query.Where(o => o.UserId == userId);
        if (status is not null) query = query.Where(o => o.Status == status);

        var total = await query.CountAsync(ct);
        var items = await query
            .OrderByDescending(o => o.CreatedAt)
            .ThenByDescending(o => o.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);
        return new PagedResult<OrderDto>(items.Select(OrderDto.From).ToList(), page, pageSize, total);
    }

    private async Task<bool> Transition(int orderId, OrderStatus to, decimal? price, DateTimeOffset? at, CancellationToken ct) =>
        await db.Orders
            .Where(o => o.Id == orderId && o.Status == OrderStatus.Pending)
            .ExecuteUpdateAsync(s => s
                .SetProperty(o => o.Status, to)
                .SetProperty(o => o.FilledPrice, price)
                .SetProperty(o => o.FilledAt, at), ct) == 1;

    private Task<Order?> FindByIdempotencyKey(int userId, string key, CancellationToken ct) =>
        db.Orders.Include(o => o.Product).FirstOrDefaultAsync(o => o.UserId == userId && o.IdempotencyKey == key, ct);
}
