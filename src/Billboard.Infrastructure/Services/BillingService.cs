using Billboard.Application.Billing;
using Billboard.Application.Common;
using Billboard.Application.Contracts;
using Billboard.Application.Scheduling;
using Billboard.Domain.Entities;
using Billboard.Domain.Enums;
using Billboard.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Billboard.Infrastructure.Services;

public sealed class BillingService(AppDbContext db, WalletLedger ledger, ITariffService tariffs) : IBillingService
{
    private static readonly PlacementStatus[] ReservableStatuses =
    {
        PlacementStatus.Draft, PlacementStatus.PendingPayment, PlacementStatus.Scheduled
    };

    public async Task<QuoteDto> QuoteAsync(Guid userId, bool isAdmin, Guid placementId, int billboardCount, CancellationToken ct = default)
    {
        var placement = await LoadPlacementAsync(placementId, ct);
        EnsureAccess(placement, userId, isAdmin);
        return await BuildQuoteAsync(placement, billboardCount, ct);
    }

    public async Task<QuoteDto> QuoteDraftAsync(Guid userId, AdPlacement draft, int billboardCount, CancellationToken ct = default) =>
        await BuildQuoteAsync(draft, billboardCount, ct);

    public async Task<ReservationDto> ReserveAsync(Guid userId, Guid placementId, int billboardCount, CancellationToken ct = default)
    {
        var placement = await LoadPlacementAsync(placementId, ct);
        EnsureAccess(placement, userId, false);

        // Идемпотентность: повторный вызов для того же размещения ничего не резервирует второй раз.
        var existing = await db.BudgetReservations.FirstOrDefaultAsync(r => r.PlacementId == placementId, ct);
        if (existing is not null) return Map(existing);

        if (!ReservableStatuses.Contains(placement.Status))
            throw AppException.Conflict("INVALID_PLACEMENT_STATE",
                $"Резервирование невозможно в статусе {placement.Status}.");

        var quote = await BuildQuoteAsync(placement, billboardCount, ct);
        if (quote.TotalAmount <= 0)
            throw AppException.Validation("INVALID_QUOTE", "Стоимость размещения равна нулю: проверьте расписание и частоту показов.");

        var wallet = await ledger.GetOrCreateWalletAsync(userId, ct);
        ledger.Reserve(wallet, quote.TotalAmount,
            WalletLedger.UserKey(userId, $"reserve:{placementId}"), placementId, "Резерв под размещение");

        var reservation = new BudgetReservation
        {
            Id = Guid.NewGuid(),
            PlacementId = placementId,
            WalletId = wallet.Id,
            ReservedAmount = quote.TotalAmount,
            PricePerPlay = quote.PricePerPlayAfterDiscount
        };
        db.BudgetReservations.Add(reservation);
        placement.ReservedAmount = quote.TotalAmount;
        SystemLogWriter.Add(db, userId, "BudgetReserved", nameof(AdPlacement), placementId, "OK", quote.TotalAmount.ToString("0.####"));

        try
        {
            await SaveAsync(ct);
        }
        catch (AppException)
        {
            throw;
        }
        catch (DbUpdateException)
        {
            // Параллельный запрос успел создать резерв: возвращаем существующий.
            db.ChangeTracker.Clear();
            var concurrent = await db.BudgetReservations.FirstOrDefaultAsync(r => r.PlacementId == placementId, ct);
            if (concurrent is not null) return Map(concurrent);
            throw;
        }

        return Map(reservation);
    }

    public async Task<ReservationDto> AddToReservationAsync(Guid userId, Guid placementId, decimal amount, string idempotencyKey, CancellationToken ct = default)
    {
        var placement = await LoadPlacementAsync(placementId, ct);
        EnsureAccess(placement, userId, false);

        var reservation = await db.BudgetReservations.FirstOrDefaultAsync(r => r.PlacementId == placementId, ct)
                          ?? throw AppException.NotFound("Резерв размещения");

        var key = WalletLedger.UserKey(userId, $"reserve-add:{placementId}:{idempotencyKey}");
        if (await db.WalletTransactions.AnyAsync(t => t.IdempotencyKey == key, ct))
            return Map(reservation);

        if (placement.Status is PlacementStatus.Cancelled or PlacementStatus.Completed or PlacementStatus.Failed)
            throw AppException.Conflict("INVALID_PLACEMENT_STATE", $"Нельзя пополнять резерв в статусе {placement.Status}.");

        var wallet = await ledger.GetOrCreateWalletAsync(userId, ct);
        ledger.Reserve(wallet, amount, key, placementId, "Дополнительный резерв");
        reservation.ReservedAmount += amount;
        placement.ReservedAmount = reservation.ReservedAmount;

        // Пауза из-за исчерпания бюджета снимается автоматически.
        if (placement.Status == PlacementStatus.Paused && placement.PauseReason == PlaybackService.BudgetExhausted
            && reservation.Remaining >= reservation.PricePerPlay)
        {
            placement.Status = PlacementStatus.Scheduled;
            placement.PauseReason = null;
        }

        SystemLogWriter.Add(db, userId, "BudgetIncreased", nameof(AdPlacement), placementId, "OK", amount.ToString("0.####"));
        await SaveAsync(ct);
        return Map(reservation);
    }

    public async Task<ReservationDto?> ReleaseAsync(Guid placementId, CancellationToken ct = default)
    {
        var reservation = await db.BudgetReservations.FirstOrDefaultAsync(r => r.PlacementId == placementId, ct);
        if (reservation is null) return null;

        var remaining = reservation.Remaining;
        if (remaining <= 0) return Map(reservation);

        var wallet = await db.Wallets.FirstAsync(w => w.Id == reservation.WalletId, ct);
        var newReleased = reservation.ReleasedAmount + remaining;
        ledger.Release(wallet, remaining, $"release:{reservation.Id:N}:{newReleased:0.####}", placementId,
            "Возврат неиспользованного резерва");
        reservation.ReleasedAmount = newReleased;
        SystemLogWriter.Add(db, wallet.UserId, "BudgetReleased", nameof(AdPlacement), placementId, "OK", remaining.ToString("0.####"));

        await SaveAsync(ct);
        return Map(reservation);
    }

    public async Task<SubscriptionDto> SubscribeAsync(Guid userId, SubscribeRequest request, CancellationToken ct = default)
    {
        var key = WalletLedger.UserKey(userId, $"subscribe:{request.IdempotencyKey}");

        var prior = await db.WalletTransactions.AsNoTracking().FirstOrDefaultAsync(t => t.IdempotencyKey == key, ct);
        if (prior?.RelatedEntityId is { } priorId)
        {
            var priorSub = await db.Subscriptions.Include(s => s.TariffPlan)
                .FirstOrDefaultAsync(s => s.Id == priorId, ct);
            if (priorSub is not null) return Map(priorSub);
        }

        var plan = await db.TariffPlans.FirstOrDefaultAsync(p => p.Id == request.TariffPlanId && p.IsActive, ct)
                   ?? throw AppException.NotFound("Тариф");

        // Новая подписка заменяет текущую активную.
        var current = await db.Subscriptions.Where(s => s.UserId == userId && s.Status == SubscriptionStatus.Active).ToListAsync(ct);
        foreach (var c in current) c.Status = SubscriptionStatus.Cancelled;

        var now = DateTimeOffset.UtcNow;
        var subscription = new Subscription
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            TariffPlanId = plan.Id,
            TariffPlan = plan,
            StartAt = now,
            EndAt = now.AddDays(30),
            Status = SubscriptionStatus.Active
        };
        db.Subscriptions.Add(subscription);

        var wallet = await ledger.GetOrCreateWalletAsync(userId, ct);
        if (plan.MonthlyPrice > 0)
            ledger.ChargeFromBalance(wallet, plan.MonthlyPrice, key, subscription.Id, $"Подписка «{plan.Name}»");
        else
        {
            // Бесплатный тариф: операции нет, но ключ нужно запомнить для идемпотентности.
            SystemLogWriter.Add(db, userId, "SubscriptionStarted", nameof(Subscription), subscription.Id, "OK", plan.Name);
        }

        await SaveAsync(ct);
        return Map(subscription);
    }

    public async Task CancelSubscriptionAsync(Guid userId, Guid subscriptionId, CancellationToken ct = default)
    {
        var sub = await db.Subscriptions.FirstOrDefaultAsync(s => s.Id == subscriptionId, ct)
                  ?? throw AppException.NotFound("Подписка");
        if (sub.UserId != userId) throw AppException.NotFound("Подписка");

        if (sub.Status != SubscriptionStatus.Active) return;
        sub.Status = SubscriptionStatus.Cancelled;
        SystemLogWriter.Add(db, userId, "SubscriptionCancelled", nameof(Subscription), sub.Id);
        await db.SaveChangesAsync(ct);
    }

    public async Task<EarningsSummaryDto> GetEarningsAsync(Guid? billboardId, int take, CancellationToken ct = default)
    {
        var query = db.OwnerEarnings.AsNoTracking().AsQueryable();
        if (billboardId is { } id) query = query.Where(e => e.BillboardId == id);

        var all = await query.ToListAsync(ct);
        // Суммирование в памяти: SQLite не умеет Sum по decimal.
        var total = all.Sum(e => e.Amount);
        var items = all.OrderByDescending(e => e.CreatedAt)
            .Take(Math.Clamp(take, 1, 500))
            .Select(e => new EarningDto(e.Id, e.BillboardId, e.PlacementId, e.Amount, e.CreatedAt))
            .ToList();
        return new EarningsSummaryDto(total, all.Count, items);
    }

    public async Task<IReadOnlyList<PlacementUsageDto>> GetUsageAsync(Guid? userId, CancellationToken ct = default)
    {
        var placementsQuery = db.AdPlacements.AsNoTracking().AsQueryable();
        if (userId is { } uid) placementsQuery = placementsQuery.Where(p => p.OwnerId == uid);
        var placements = await placementsQuery.Select(p => new { p.Id, p.BillboardId, p.ReservedAmount }).ToListAsync(ct);
        var ids = placements.Select(p => p.Id).ToList();

        var plays = await db.PlaybackEvents.AsNoTracking()
            .Where(e => ids.Contains(e.PlacementId) && e.Result == PlaybackResult.Completed)
            .GroupBy(e => e.PlacementId)
            .Select(g => new { PlacementId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.PlacementId, x => x.Count, ct);

        var reservations = await db.BudgetReservations.AsNoTracking()
            .Where(r => ids.Contains(r.PlacementId))
            .ToDictionaryAsync(r => r.PlacementId, ct);

        return placements.Select(p => new PlacementUsageDto(
            p.Id, p.BillboardId,
            plays.GetValueOrDefault(p.Id),
            reservations.TryGetValue(p.Id, out var r) ? r.ChargedAmount : 0m,
            p.ReservedAmount)).ToList();
    }

    // ---------- внутренние ----------

    private async Task<QuoteDto> BuildQuoteAsync(AdPlacement placement, int billboardCount, CancellationToken ct)
    {
        var billboard = placement.Billboard
                        ?? await db.Billboards.FirstOrDefaultAsync(b => b.Id == placement.BillboardId, ct)
                        ?? throw AppException.NotFound("Билборд");

        var plan = await tariffs.GetEffectivePlanAsync(placement.OwnerId, ct);
        var discount = billboardCount > 1 ? plan.MultiBillboardDiscountPercent : 0m;

        var result = QuoteCalculator.Calculate(
            placement.StartAt, placement.EndAt, placement.DaysOfWeekMask,
            placement.StartTimeOfDay, placement.EndTimeOfDay,
            placement.MinimumPlaysPerHour, billboard.PricePerPlay, discount, TimeZones.Resolve(billboard.TimeZoneId));

        var wallet = await db.Wallets.AsNoTracking().FirstOrDefaultAsync(w => w.UserId == placement.OwnerId, ct);
        return new QuoteDto(placement.Id, result.BillableHours, result.ExpectedPlays, billboard.PricePerPlay, discount,
            result.PricePerPlayAfterDiscount, result.TotalAmount, wallet?.Currency ?? "USD");
    }

    private async Task<AdPlacement> LoadPlacementAsync(Guid placementId, CancellationToken ct) =>
        await db.AdPlacements.Include(p => p.Billboard).FirstOrDefaultAsync(p => p.Id == placementId, ct)
        ?? throw AppException.NotFound("Размещение");

    private static void EnsureAccess(AdPlacement placement, Guid userId, bool isAdmin)
    {
        // Чужое размещение выглядит как несуществующее: не раскрываем факт его наличия.
        if (!isAdmin && placement.OwnerId != userId) throw AppException.NotFound("Размещение");
    }

    private async Task SaveAsync(CancellationToken ct)
    {
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException)
        {
            throw AppException.Conflict("CONCURRENT_UPDATE", "Данные изменились одновременно. Повторите запрос.");
        }
    }

    private static ReservationDto Map(BudgetReservation r) =>
        new(r.PlacementId, r.ReservedAmount, r.ChargedAmount, r.ReleasedAmount, r.Remaining);

    private static SubscriptionDto Map(Subscription s) =>
        new(s.Id, s.TariffPlanId, s.TariffPlan?.Name ?? "", s.StartAt, s.EndAt, s.Status.ToString());
}
