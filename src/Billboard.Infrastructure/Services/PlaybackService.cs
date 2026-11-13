using Billboard.Application.Common;
using Billboard.Application.Contracts;
using Billboard.Domain.Entities;
using Billboard.Domain.Enums;
using Billboard.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Billboard.Infrastructure.Services;

public sealed class PlaybackService(AppDbContext db, WalletLedger ledger) : IPlaybackService
{
    public const string BudgetExhausted = "BudgetExhausted";
    private const int MaxAttempts = 3;

    private static readonly PlacementStatus[] BillableStatuses =
    {
        PlacementStatus.Scheduled, PlacementStatus.Downloading, PlacementStatus.Ready, PlacementStatus.Active
    };

    public async Task<PlaybackEventsResultDto> IngestAsync(Guid billboardId, IReadOnlyList<PlaybackEventDto> events, CancellationToken ct = default)
    {
        var billboard = await db.Billboards.AsNoTracking().FirstOrDefaultAsync(b => b.Id == billboardId, ct);
        if (billboard is { Status: BillboardStatus.Disabled })
            throw AppException.Conflict("BILLBOARD_DISABLED", "Билборд отключён: показы не принимаются.");

        int accepted = 0, duplicates = 0, rejected = 0;
        var errors = new List<string>();

        // Повторы внутри одного пакета тоже считаются дубликатами.
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var ev in events)
        {
            if (!seen.Add(ev.IdempotencyKey)) { duplicates++; continue; }

            var outcome = EventOutcome.Rejected;
            string? error = null;
            for (var attempt = 1; attempt <= MaxAttempts; attempt++)
            {
                try
                {
                    (outcome, error) = await ProcessOneAsync(billboardId, ev, ct);
                    break;
                }
                catch (DbUpdateConcurrencyException) when (attempt < MaxAttempts)
                {
                    // Кошелёк изменился параллельным списанием: начинаем заново со свежими данными.
                    db.ChangeTracker.Clear();
                }
                catch (DbUpdateConcurrencyException)
                {
                    db.ChangeTracker.Clear();
                    outcome = EventOutcome.Rejected;
                    error = $"{ev.IdempotencyKey}: слишком много конкурирующих списаний, повторите отправку.";
                }
                catch (DbUpdateException)
                {
                    // Параллельная отправка того же события: уникальный ключ не дал создать второй показ.
                    db.ChangeTracker.Clear();
                    outcome = EventOutcome.Duplicate;
                    break;
                }
            }

            switch (outcome)
            {
                case EventOutcome.Accepted: accepted++; break;
                case EventOutcome.Duplicate: duplicates++; break;
                default:
                    rejected++;
                    if (error is not null) errors.Add(error);
                    break;
            }
        }

        return new PlaybackEventsResultDto(accepted, duplicates, rejected, errors);
    }

    private enum EventOutcome { Accepted, Duplicate, Rejected }

    private async Task<(EventOutcome, string?)> ProcessOneAsync(Guid billboardId, PlaybackEventDto ev, CancellationToken ct)
    {
        // Ключ уникален в рамках билборда, но хранится глобально — добавляем префикс устройства.
        var key = $"{billboardId:N}:{ev.IdempotencyKey}";

        if (await db.PlaybackEvents.AnyAsync(e => e.IdempotencyKey == key, ct))
            return (EventOutcome.Duplicate, null);

        if (!Enum.TryParse<PlaybackResult>(ev.Result, true, out var result))
            return (EventOutcome.Rejected, $"{ev.IdempotencyKey}: неизвестный Result «{ev.Result}».");

        var placement = await db.AdPlacements
            .FirstOrDefaultAsync(p => p.Id == ev.PlacementId && p.BillboardId == billboardId && p.VideoAssetId == ev.VideoAssetId, ct);
        if (placement is null)
            return (EventOutcome.Rejected, $"{ev.IdempotencyKey}: размещение не относится к этому билборду.");

        var playback = new PlaybackEvent
        {
            Id = Guid.NewGuid(),
            BillboardId = billboardId,
            PlacementId = placement.Id,
            VideoAssetId = placement.VideoAssetId,
            StartedAt = ev.StartedAt,
            DurationSeconds = ev.DurationSeconds,
            IdempotencyKey = key,
            Result = result,
            ReceivedAt = DateTimeOffset.UtcNow
        };
        db.PlaybackEvents.Add(playback);

        // Деньги списываются только за подтверждённый (завершённый) показ.
        if (result == PlaybackResult.Completed && BillableStatuses.Contains(placement.Status))
            await TryChargeAsync(placement, playback, billboardId, ct);

        await db.SaveChangesAsync(ct);
        return (EventOutcome.Accepted, null);
    }

    private async Task TryChargeAsync(AdPlacement placement, PlaybackEvent playback, Guid billboardId, CancellationToken ct)
    {
        var reservation = await db.BudgetReservations.FirstOrDefaultAsync(r => r.PlacementId == placement.Id, ct);
        if (reservation is null) return; // Бесплатное размещение: показ учитывается без списания.

        var price = reservation.PricePerPlay;
        if (reservation.Remaining < price)
        {
            PauseForBudget(placement);
            return;
        }

        var wallet = await db.Wallets.FirstAsync(w => w.Id == reservation.WalletId, ct);
        ledger.ChargeFromReserve(wallet, price, $"charge:{playback.IdempotencyKey}", playback.Id, "Показ рекламы");
        reservation.ChargedAmount += price;
        playback.ChargedAmount = price;

        // Начисление владельцу билбордов; уникальный PlaybackEventId защищает от дублей.
        db.OwnerEarnings.Add(new OwnerEarning
        {
            Id = Guid.NewGuid(),
            BillboardId = billboardId,
            PlacementId = placement.Id,
            PlaybackEventId = playback.Id,
            Amount = price
        });

        if (placement.Status != PlacementStatus.Active)
            placement.Status = PlacementStatus.Active;

        // Следующий показ уже не поместится в бюджет: приостанавливаем сразу.
        if (reservation.Remaining < price)
            PauseForBudget(placement);
    }

    private void PauseForBudget(AdPlacement placement)
    {
        placement.Status = PlacementStatus.Paused;
        placement.PauseReason = BudgetExhausted;
        // Запись в журнале — основа для уведомления клиента. Видео и размещение не удаляются.
        SystemLogWriter.Add(db, placement.OwnerId, "PlacementPausedBudgetExhausted", nameof(AdPlacement), placement.Id, "WARN",
            "Бюджет исчерпан, платные показы приостановлены.");
    }
}
