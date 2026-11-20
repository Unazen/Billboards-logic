using Billboard.Application.Common;
using Billboard.Application.Contracts;
using Billboard.Domain.Entities;
using Billboard.Domain.Enums;
using Billboard.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Billboard.Infrastructure.Services;

public sealed class TariffService(AppDbContext db) : ITariffService
{
    public async Task<TariffPlan> GetEffectivePlanAsync(Guid userId, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var subscriptions = await db.Subscriptions
            .Include(s => s.TariffPlan)
            .Where(s => s.UserId == userId && s.Status == SubscriptionStatus.Active)
            .ToListAsync(ct);

        // Сравнение дат в памяти: так работает и в PostgreSQL, и в SQLite.
        var active = subscriptions.FirstOrDefault(s => s.EndAt > now && s.StartAt <= now && s.TariffPlan is { IsActive: true });
        if (active?.TariffPlan is not null) return active.TariffPlan;

        var plan = await db.TariffPlans.FirstOrDefaultAsync(p => p.IsDefault && p.IsActive, ct)
                   ?? await db.TariffPlans.FirstOrDefaultAsync(p => p.IsActive, ct);
        return plan ?? throw new AppException("NO_TARIFF", "В системе не настроен ни один тариф.", 500);
    }

    public async Task<IReadOnlyList<TariffDto>> ListAsync(bool onlyActive, CancellationToken ct = default)
    {
        var query = db.TariffPlans.AsNoTracking();
        if (onlyActive) query = query.Where(p => p.IsActive);
        var plans = await query.OrderBy(p => p.Name).ToListAsync(ct);
        return plans.Select(Map).ToList();
    }

    public async Task<TariffDto> CreateAsync(TariffUpsertRequest r, CancellationToken ct = default)
    {
        if (await db.TariffPlans.AnyAsync(p => p.Name == r.Name, ct))
            throw AppException.Conflict("TARIFF_EXISTS", $"Тариф «{r.Name}» уже существует.");

        var plan = new TariffPlan { Id = Guid.NewGuid() };
        Apply(plan, r);
        db.TariffPlans.Add(plan);
        await EnsureSingleDefaultAsync(plan, ct);
        SystemLogWriter.Add(db, null, "TariffCreated", nameof(TariffPlan), plan.Id);
        await db.SaveChangesAsync(ct);
        return Map(plan);
    }

    public async Task<TariffDto> UpdateAsync(Guid id, TariffUpsertRequest r, CancellationToken ct = default)
    {
        var plan = await db.TariffPlans.FirstOrDefaultAsync(p => p.Id == id, ct)
                   ?? throw AppException.NotFound("Тариф");
        if (await db.TariffPlans.AnyAsync(p => p.Name == r.Name && p.Id != id, ct))
            throw AppException.Conflict("TARIFF_EXISTS", $"Тариф «{r.Name}» уже существует.");

        Apply(plan, r);
        await EnsureSingleDefaultAsync(plan, ct);
        SystemLogWriter.Add(db, null, "TariffUpdated", nameof(TariffPlan), plan.Id);
        await db.SaveChangesAsync(ct);
        return Map(plan);
    }

    private async Task EnsureSingleDefaultAsync(TariffPlan plan, CancellationToken ct)
    {
        if (!plan.IsDefault) return;
        var others = await db.TariffPlans.Where(p => p.IsDefault && p.Id != plan.Id).ToListAsync(ct);
        foreach (var o in others) o.IsDefault = false;
    }

    private static void Apply(TariffPlan p, TariffUpsertRequest r)
    {
        if (!Enum.TryParse<CompressionLevel>(r.CompressionLevel, true, out var level))
            throw AppException.Validation("INVALID_COMPRESSION", "CompressionLevel: None, Basic или Full.");

        p.Name = r.Name.Trim();
        p.MaxVideoCount = r.MaxVideoCount;
        p.MaxVideoSizeBytes = r.MaxVideoSizeBytes;
        p.MaxVideoDurationSeconds = r.MaxVideoDurationSeconds;
        p.CompressionLevel = level;
        p.AdvancedStatistics = r.AdvancedStatistics;
        p.PriorityProcessing = r.PriorityProcessing;
        p.MultiBillboardDiscountPercent = r.MultiBillboardDiscountPercent;
        p.MonthlyPrice = r.MonthlyPrice;
        p.IsDefault = r.IsDefault;
        p.IsActive = r.IsActive;
    }

    public static TariffDto Map(TariffPlan p) => new(
        p.Id, p.Name, p.MaxVideoCount, p.MaxVideoSizeBytes, p.MaxVideoDurationSeconds,
        p.CompressionLevel.ToString(), p.AdvancedStatistics, p.PriorityProcessing,
        p.MultiBillboardDiscountPercent, p.MonthlyPrice, p.IsDefault, p.IsActive);
}
