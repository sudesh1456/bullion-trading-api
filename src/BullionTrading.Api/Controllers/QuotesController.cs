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
[Route("api/quotes")]
public class QuotesController(QuoteService quotes) : ControllerBase
{
    /// <summary>Lock the current price for a short window (default 30 s).</summary>
    [HttpPost]
    public async Task<ActionResult<QuoteDto>> Create(QuoteRequest request, CancellationToken ct) =>
        await quotes.CreateAsync(User.UserId(), request, ct);
}
