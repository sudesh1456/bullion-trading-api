using BullionTrading.Api.Auth;
using BullionTrading.Api.Contracts;
using BullionTrading.Api.Rates;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace BullionTrading.Api.Hubs;

/// <summary>Typed client contract, so server code can't typo a method name.</summary>
public interface IRatesClient
{
    Task Rates(IReadOnlyCollection<SpotRateDto> rates);
    Task OrderFilled(OrderDto order);
    Task AlertTriggered(AlertDto alert);
}

/// <summary>
/// Real-time channel. Everyone gets rate ticks; order fills and alerts are sent
/// only to the owning user (see <see cref="SubClaimUserIdProvider"/>).
/// </summary>
[Authorize]
public class RatesHub(RateBook rates) : Hub<IRatesClient>
{
    public const string Path = "/hubs/rates";

    public override async Task OnConnectedAsync()
    {
        // Send a snapshot immediately so clients don't wait for the next tick.
        await Clients.Caller.Rates(rates.All().Select(t => new SpotRateDto(t.Metal, t.PricePerGram, t.Timestamp)).ToList());
        await base.OnConnectedAsync();
    }
}

/// <summary>Routes <c>Clients.User(id)</c> by the JWT <c>sub</c> claim.</summary>
public class SubClaimUserIdProvider : IUserIdProvider
{
    public string? GetUserId(HubConnectionContext connection) => connection.User.FindFirst(Claims.UserId)?.Value;
}
