using System.Collections.Concurrent;
using System.Threading.Channels;
using BullionTrading.Api.Domain;

namespace BullionTrading.Api.Rates;

public class RateFeedOptions
{
    public const string Section = "RateFeed";

    /// <summary>Run the built-in simulated feed. Disable when a real provider publishes ticks.</summary>
    public bool Simulated { get; set; } = true;

    public int IntervalMs { get; set; } = 1000;

    /// <summary>Opening spot prices per gram (999), keyed by metal name.</summary>
    public Dictionary<string, decimal> OpeningPrices { get; set; } = new() { ["Gold"] = 11_500m, ["Silver"] = 140m };

    /// <summary>Per-tick volatility as a fraction, e.g. 0.0005 = 0.05%.</summary>
    public double Volatility { get; set; } = 0.0005;
}

/// <summary>
/// The single source of truth for current spot prices. Any feed (simulated,
/// exchange websocket, polling) calls <see cref="Publish"/>; consumers read the
/// latest price or drain <see cref="Ticks"/> for event-driven processing.
/// </summary>
public class RateBook
{
    private readonly ConcurrentDictionary<Metal, SpotTick> _latest = new();
    private readonly Channel<SpotTick> _ticks = Channel.CreateBounded<SpotTick>(
        new BoundedChannelOptions(1024) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });

    public ChannelReader<SpotTick> Ticks => _ticks.Reader;

    public void Publish(SpotTick tick)
    {
        if (tick.PricePerGram <= 0) throw new ArgumentOutOfRangeException(nameof(tick), "Price must be positive");
        _latest[tick.Metal] = tick;
        _ticks.Writer.TryWrite(tick);
    }

    public SpotTick? Latest(Metal metal) => _latest.TryGetValue(metal, out var t) ? t : null;

    public SpotTick Require(Metal metal) =>
        Latest(metal) ?? throw new DomainException($"No live {metal} rate yet", StatusCodes.Status503ServiceUnavailable);

    public IReadOnlyCollection<SpotTick> All() => _latest.Values.OrderBy(t => t.Metal).ToList();
}
