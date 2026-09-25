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
[Route("api")]
public class MarketController(AppDbContext db, RateBook rates) : ControllerBase
{
    /// <summary>Current spot price per gram (999) for each metal.</summary>
    [HttpGet("rates")]
    [AllowAnonymous]
    public IEnumerable<SpotRateDto> Rates() => rates.All().Select(t => new SpotRateDto(t.Metal, t.PricePerGram, t.Timestamp));

    /// <summary>Tradable products with live buy/sell prices.</summary>
    [HttpGet("products")]
    [AllowAnonymous]
    public async Task<IEnumerable<ProductDto>> Products(CancellationToken ct)
    {
        var products = await db.Products.AsNoTracking().Where(p => p.IsActive).ToListAsync(ct);
        // Sorted in memory: SQLite can't ORDER BY decimal columns.
        return products.OrderBy(p => p.Metal).ThenBy(p => p.WeightGrams).Select(p => ProductDto.From(p, rates.Latest(p.Metal)));
    }
}
