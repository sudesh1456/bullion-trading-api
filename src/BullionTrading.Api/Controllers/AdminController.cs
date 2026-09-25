using BullionTrading.Api.Auth;
using BullionTrading.Api.Contracts;
using BullionTrading.Api.Data;
using BullionTrading.Api.Domain;
using BullionTrading.Api.Rates;
using BullionTrading.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace BullionTrading.Api.Controllers;

[ApiController]
[Authorize(Roles = nameof(Role.Admin))]
[Route("api/admin")]
public class AdminController(AppDbContext db, OrderService orders, RateBook rates, TimeProvider clock) : ControllerBase
{
    /// <summary>All customers' orders.</summary>
    [HttpGet("orders")]
    public Task<PagedResult<OrderDto>> Orders([FromQuery] OrderStatus? status, [FromQuery] int page = 1, [FromQuery] int pageSize = 50, CancellationToken ct = default) =>
        orders.ListAsync(null, status, page, pageSize, ct);

    /// <summary>All products, including inactive ones.</summary>
    [HttpGet("products")]
    public async Task<IEnumerable<ProductDto>> Products(CancellationToken ct) =>
        (await db.Products.AsNoTracking().OrderBy(p => p.Id).ToListAsync(ct)).Select(p => ProductDto.From(p, rates.Latest(p.Metal)));

    /// <summary>Adjust premiums/discounts or suspend trading for a product. Takes effect on the next quote.</summary>
    [HttpPut("products/{id:int}/pricing")]
    public async Task<ActionResult<ProductDto>> UpdatePricing(int id, UpdatePricingRequest request, CancellationToken ct)
    {
        var product = await db.Products.FindAsync([id], ct);
        if (product is null) return NotFound();
        product.BuyPremiumPct = request.BuyPremiumPct;
        product.SellDiscountPct = request.SellDiscountPct;
        product.IsActive = request.IsActive;
        await db.SaveChangesAsync(ct);
        return ProductDto.From(product, rates.Latest(product.Metal));
    }

    /// <summary>Today's turnover (UTC) split by side and metal.</summary>
    [HttpGet("stats/today")]
    public async Task<DailyStatsDto> Today(CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var start = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero);

        var filled = await db.Orders.AsNoTracking().Include(o => o.Product)
            .Where(o => o.Status == OrderStatus.Filled && o.FilledAt >= start)
            .ToListAsync(ct);
        var pending = await db.Orders.CountAsync(o => o.Status == OrderStatus.Pending, ct);

        // SQLite can't SUM decimals server-side, so aggregate in memory (fine for a day's orders).
        decimal Sum(IEnumerable<Order> os, Side side) => os.Where(o => o.Side == side).Sum(o => o.Total ?? 0);
        var byMetal = filled.GroupBy(o => o.Product!.Metal)
            .Select(g => new MetalVolumeDto(g.Key, Sum(g, Side.Buy), Sum(g, Side.Sell)))
            .OrderBy(m => m.Metal).ToList();

        return new DailyStatsDto(DateOnly.FromDateTime(start.UtcDateTime), filled.Count, pending, Sum(filled, Side.Buy), Sum(filled, Side.Sell), byMetal);
    }
}
