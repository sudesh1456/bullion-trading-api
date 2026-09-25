using BullionTrading.Api.Domain;
using Microsoft.Extensions.Options;

namespace BullionTrading.Api.Rates;

/// <summary>
/// Random-walk price generator so the API is fully usable without a paid market
/// data subscription. Swap for a real provider that calls <see cref="RateBook.Publish"/>.
/// </summary>
public class SimulatedRateFeed(RateBook book, IOptions<RateFeedOptions> options, TimeProvider clock, ILogger<SimulatedRateFeed> log)
    : BackgroundService
{
    private readonly Random _random = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var o = options.Value;
        var prices = o.OpeningPrices.ToDictionary(kv => Enum.Parse<Metal>(kv.Key, ignoreCase: true), kv => kv.Value);
        log.LogInformation("Simulated rate feed started: {Prices}", string.Join(", ", prices.Select(p => $"{p.Key}={p.Value}")));

        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(o.IntervalMs), clock);
        do
        {
            foreach (var metal in prices.Keys.ToList())
            {
                var shock = (decimal)(NextGaussian() * o.Volatility);
                prices[metal] = Pricing.Round(prices[metal] * (1 + shock));
                book.Publish(new SpotTick(metal, prices[metal], clock.GetUtcNow()));
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private double NextGaussian()
    {
        // Box–Muller transform
        var u1 = 1.0 - _random.NextDouble();
        var u2 = _random.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }
}
