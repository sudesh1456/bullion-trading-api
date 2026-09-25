using BullionTrading.Api.Contracts;
using BullionTrading.Api.Hubs;
using BullionTrading.Api.Rates;
using Microsoft.AspNetCore.SignalR;

namespace BullionTrading.Api.Services;

public interface ITradeNotifier
{
    Task OrderFilledAsync(int userId, OrderDto order, CancellationToken ct);
    Task AlertTriggeredAsync(int userId, AlertDto alert, CancellationToken ct);
}

public class SignalRTradeNotifier(IHubContext<RatesHub, IRatesClient> hub) : ITradeNotifier
{
    public Task OrderFilledAsync(int userId, OrderDto order, CancellationToken ct) =>
        hub.Clients.User(userId.ToString()).OrderFilled(order);

    public Task AlertTriggeredAsync(int userId, AlertDto alert, CancellationToken ct) =>
        hub.Clients.User(userId.ToString()).AlertTriggered(alert);
}

/// <summary>
/// Single consumer of the tick stream: broadcasts prices, then runs the limit
/// order matcher and alert evaluator. Processing ticks sequentially means order
/// matching never races with itself.
/// </summary>
public class TickProcessor(
    RateBook rates,
    IServiceScopeFactory scopes,
    IHubContext<RatesHub, IRatesClient> hub,
    ILogger<TickProcessor> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var tick in rates.Ticks.ReadAllAsync(stoppingToken))
        {
            try
            {
                await hub.Clients.All.Rates([new SpotRateDto(tick.Metal, tick.PricePerGram, tick.Timestamp)]);

                await using var scope = scopes.CreateAsyncScope();
                var filled = await scope.ServiceProvider.GetRequiredService<OrderService>().MatchAsync(tick, stoppingToken);
                var fired = await scope.ServiceProvider.GetRequiredService<AlertService>().EvaluateAsync(tick, stoppingToken);
                if (filled + fired > 0)
                    log.LogInformation("{Metal} @ {Price}: filled {Filled} limit orders, fired {Fired} alerts", tick.Metal, tick.PricePerGram, filled, fired);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One bad tick must not stop the engine.
                log.LogError(ex, "Failed to process {Metal} tick", tick.Metal);
            }
        }
    }
}
