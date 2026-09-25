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
[Route("api/alerts")]
public class AlertsController(AlertService alerts) : ControllerBase
{
    [HttpGet]
    public async Task<IEnumerable<AlertDto>> List(CancellationToken ct) =>
        (await alerts.ListAsync(User.UserId(), ct)).Select(AlertDto.From);

    [HttpPost]
    public async Task<ActionResult<AlertDto>> Create(CreateAlertRequest request, CancellationToken ct) =>
        StatusCode(StatusCodes.Status201Created, AlertDto.From(await alerts.CreateAsync(User.UserId(), request, ct)));

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Cancel(int id, CancellationToken ct)
    {
        await alerts.CancelAsync(User.UserId(), id, ct);
        return NoContent();
    }
}
