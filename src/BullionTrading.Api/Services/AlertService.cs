using BullionTrading.Api.Contracts;
using BullionTrading.Api.Data;
using BullionTrading.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace BullionTrading.Api.Services;

public class AlertService(AppDbContext db, TimeProvider clock, ITradeNotifier notifier)
{
    public const int MaxActivePerUser = 20;

    public async Task<RateAlert> CreateAsync(int userId, CreateAlertRequest request, CancellationToken ct)
    {
        var active = await db.RateAlerts.CountAsync(a => a.UserId == userId && a.Status == AlertStatus.Active, ct);
        if (active >= MaxActivePerUser) throw new DomainException($"You can have at most {MaxActivePerUser} active alerts");

        var alert = new RateAlert
        {
            UserId = userId,
            Metal = request.Metal,
            Direction = request.Direction,
            TargetPrice = Pricing.Round(request.TargetPrice),
            Status = AlertStatus.Active,
            CreatedAt = clock.GetUtcNow(),
        };
        db.RateAlerts.Add(alert);
        await db.SaveChangesAsync(ct);
        return alert;
    }

    public Task<List<RateAlert>> ListAsync(int userId, CancellationToken ct) =>
        db.RateAlerts.AsNoTracking().Where(a => a.UserId == userId).OrderByDescending(a => a.Id).ToListAsync(ct);

    public async Task CancelAsync(int userId, int alertId, CancellationToken ct)
    {
        var changed = await db.RateAlerts
            .Where(a => a.Id == alertId && a.UserId == userId && a.Status == AlertStatus.Active)
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.Status, AlertStatus.Cancelled), ct);
        if (changed == 0) throw new DomainException("Active alert not found", StatusCodes.Status404NotFound);
    }

    /// <summary>Fires every active alert whose threshold the tick has crossed. Each alert fires once.</summary>
    public async Task<int> EvaluateAsync(SpotTick tick, CancellationToken ct)
    {
        var candidates = await db.RateAlerts
            .Where(a => a.Status == AlertStatus.Active && a.Metal == tick.Metal)
            .ToListAsync(ct);

        var fired = 0;
        foreach (var alert in candidates.Where(a => Pricing.AlertIsTriggered(a.Direction, a.TargetPrice, tick.PricePerGram)))
        {
            var changed = await db.RateAlerts
                .Where(a => a.Id == alert.Id && a.Status == AlertStatus.Active)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(a => a.Status, AlertStatus.Triggered)
                    .SetProperty(a => a.TriggeredAt, tick.Timestamp)
                    .SetProperty(a => a.TriggeredPrice, tick.PricePerGram), ct);
            if (changed == 0) continue;

            fired++;
            alert.Status = AlertStatus.Triggered;
            alert.TriggeredAt = tick.Timestamp;
            alert.TriggeredPrice = tick.PricePerGram;
            await notifier.AlertTriggeredAsync(alert.UserId, AlertDto.From(alert), ct);
        }
        return fired;
    }
}
