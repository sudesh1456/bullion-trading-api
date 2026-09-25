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
[Authorize]
[Route("api/orders")]
public class OrdersController(OrderService orders) : ControllerBase
{
    public const string IdempotencyHeader = "Idempotency-Key";

    /// <summary>Execute a locked quote. Requires an <c>Idempotency-Key</c> header; retries return the original order.</summary>
    [HttpPost]
    public async Task<ActionResult<OrderDto>> Execute(
        ExecuteQuoteRequest request, [FromHeader(Name = IdempotencyHeader)] string? idempotencyKey, CancellationToken ct)
    {
        if (!ValidKey(idempotencyKey))
            return Problem(statusCode: StatusCodes.Status400BadRequest, title: $"{IdempotencyHeader} header (8-64 chars) is required");

        var (order, replayed) = await orders.ExecuteQuoteAsync(User.UserId(), request.QuoteId, idempotencyKey!, ct);
        Response.Headers["Idempotent-Replayed"] = replayed.ToString().ToLowerInvariant();
        var dto = OrderDto.From(order);
        return replayed ? Ok(dto) : CreatedAtAction(nameof(Get), new { id = order.Id }, dto);
    }

    /// <summary>Place a limit order; the matching engine fills it when the price crosses the limit.</summary>
    [HttpPost("limit")]
    public async Task<ActionResult<OrderDto>> PlaceLimit(
        LimitOrderRequest request, [FromHeader(Name = IdempotencyHeader)] string? idempotencyKey, CancellationToken ct)
    {
        if (idempotencyKey is not null && !ValidKey(idempotencyKey))
            return Problem(statusCode: StatusCodes.Status400BadRequest, title: $"{IdempotencyHeader} must be 8-64 chars");

        var order = await orders.PlaceLimitAsync(User.UserId(), request, idempotencyKey, ct);
        return CreatedAtAction(nameof(Get), new { id = order.Id }, OrderDto.From(order));
    }

    [HttpGet]
    public Task<PagedResult<OrderDto>> Mine([FromQuery] OrderStatus? status, [FromQuery] int page = 1, [FromQuery] int pageSize = 20, CancellationToken ct = default) =>
        orders.ListAsync(User.UserId(), status, page, pageSize, ct);

    [HttpGet("{id:int}")]
    public async Task<ActionResult<OrderDto>> Get(int id, [FromServices] AppDbContext db, CancellationToken ct)
    {
        var order = await db.Orders.AsNoTracking().Include(o => o.Product).FirstOrDefaultAsync(o => o.Id == id, ct);
        if (order is null || (order.UserId != User.UserId() && !User.IsInRole(nameof(Role.Admin)))) return NotFound();
        return OrderDto.From(order);
    }

    [HttpDelete("{id:int}")]
    public async Task<OrderDto> Cancel(int id, CancellationToken ct) =>
        OrderDto.From(await orders.CancelAsync(User.UserId(), id, User.IsInRole(nameof(Role.Admin)), ct));

    private static bool ValidKey(string? key) => key is { Length: >= 8 and <= 64 };
}
