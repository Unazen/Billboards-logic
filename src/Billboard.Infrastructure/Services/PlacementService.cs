using Billboard.Application.Common;
using Billboard.Application.Contracts;
using Billboard.Application.Scheduling;
using Billboard.Domain.Entities;
using Billboard.Domain.Enums;
using Billboard.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Billboard.Infrastructure.Services;

/// <summary>
/// Размещения: черновик → проверка → резерв бюджета → публикация.
/// Правила планирования вынесены в <see cref="IScheduleValidator"/>, деньги — в <see cref="IBillingService"/>.
/// </summary>
public sealed class PlacementService(AppDbContext db, IScheduleValidator validator, IBillingService billing) : IPlacementService
{
    private static readonly PlacementStatus[] EditableStatuses = { PlacementStatus.Draft, PlacementStatus.PendingPayment };

    private static readonly PlacementStatus[] CancellableStatuses =
    {
        PlacementStatus.Draft, PlacementStatus.PendingPayment, PlacementStatus.Scheduled,
        PlacementStatus.Downloading, PlacementStatus.Ready, PlacementStatus.Active, PlacementStatus.Paused
    };

    private static readonly PlacementStatus[] PausableStatuses =
    {
        PlacementStatus.Scheduled, PlacementStatus.Downloading, PlacementStatus.Ready, PlacementStatus.Active
    };

    public const string ManualPause = "Manual";

    public async Task<IReadOnlyList<PlacementDto>> ListAsync(Guid userId, bool isAdmin, string? status, CancellationToken ct = default)
    {
        var query = db.AdPlacements.AsNoTracking()
            .Include(p => p.VideoAsset)
            .Include(p => p.Billboard)
            .AsQueryable();

        if (!isAdmin) query = query.Where(p => p.OwnerId == userId);

        if (!string.IsNullOrWhiteSpace(status))
        {
            if (!Enum.TryParse<PlacementStatus>(status, true, out var parsed))
                throw AppException.Validation("INVALID_STATUS", $"Неизвестный статус «{status}».");
            query = query.Where(p => p.Status == parsed);
        }

        var items = await query.OrderByDescending(p => p.CreatedAt).ToListAsync(ct);
        return await MapManyAsync(items, ct);
    }

    public async Task<PlacementDto> GetAsync(Guid userId, bool isAdmin, Guid id, CancellationToken ct = default)
    {
        var placement = await LoadAsync(id, ct);
        if (!isAdmin && placement.OwnerId != userId) throw AppException.NotFound("Размещение");
        return (await MapManyAsync(new[] { placement }, ct))[0];
    }

    public async Task<PlacementDto> CreateAsync(Guid userId, PlacementUpsertRequest request, CancellationToken ct = default)
    {
        var mask = ParseMask(request);
        var video = await LoadOwnVideoAsync(userId, request.VideoAssetId, ct);
        var billboard = await LoadBillboardAsync(request.BillboardId, ct);

        EnsureWindow(request);
        EnsureBillboardUsable(billboard);

        var placement = new AdPlacement
        {
            Id = Guid.NewGuid(),
            OwnerId = userId,
            VideoAssetId = video.Id,
            BillboardId = billboard.Id,
            StartAt = request.StartAt,
            EndAt = request.EndAt,
            DaysOfWeekMask = mask,
            StartTimeOfDay = request.StartTimeOfDay,
            EndTimeOfDay = request.EndTimeOfDay,
            MinimumPlaysPerHour = request.MinimumPlaysPerHour,
            Status = PlacementStatus.Draft,
            CreatedAt = DateTimeOffset.UtcNow
        };

        db.AdPlacements.Add(placement);
        SystemLogWriter.Add(db, userId, "PlacementCreated", nameof(AdPlacement), placement.Id);
        await db.SaveChangesAsync(ct);

        placement.VideoAsset = video;
        placement.Billboard = billboard;
        return (await MapManyAsync(new[] { placement }, ct))[0];
    }

    public async Task<PlacementDto> UpdateAsync(Guid userId, Guid id, PlacementUpsertRequest request, CancellationToken ct = default)
    {
        var placement = await LoadAsync(id, ct);
        if (placement.OwnerId != userId) throw AppException.NotFound("Размещение");
        if (!EditableStatuses.Contains(placement.Status))
            throw AppException.Conflict("INVALID_PLACEMENT_STATE",
                $"Изменять можно только черновик или размещение, ожидающее оплаты (текущий статус: {placement.Status}).");

        var mask = ParseMask(request);
        var video = await LoadOwnVideoAsync(userId, request.VideoAssetId, ct);
        var billboard = await LoadBillboardAsync(request.BillboardId, ct);

        EnsureWindow(request);
        EnsureBillboardUsable(billboard);

        placement.VideoAssetId = video.Id;
        placement.BillboardId = billboard.Id;
        placement.StartAt = request.StartAt;
        placement.EndAt = request.EndAt;
        placement.DaysOfWeekMask = mask;
        placement.StartTimeOfDay = request.StartTimeOfDay;
        placement.EndTimeOfDay = request.EndTimeOfDay;
        placement.MinimumPlaysPerHour = request.MinimumPlaysPerHour;

        SystemLogWriter.Add(db, userId, "PlacementUpdated", nameof(AdPlacement), placement.Id);
        await db.SaveChangesAsync(ct);

        placement.VideoAsset = video;
        placement.Billboard = billboard;
        return (await MapManyAsync(new[] { placement }, ct))[0];
    }

    public async Task<PlacementValidationResultDto> ValidateAsync(Guid userId, PlacementUpsertRequest request, CancellationToken ct = default)
    {
        var mask = ParseMask(request);
        var video = await LoadOwnVideoAsync(userId, request.VideoAssetId, ct);
        var billboard = await LoadBillboardAsync(request.BillboardId, ct);
        EnsureWindow(request);

        var candidate = new AdPlacement
        {
            Id = Guid.Empty,
            OwnerId = userId,
            VideoAssetId = video.Id,
            BillboardId = billboard.Id,
            StartAt = request.StartAt,
            EndAt = request.EndAt,
            DaysOfWeekMask = mask,
            StartTimeOfDay = request.StartTimeOfDay,
            EndTimeOfDay = request.EndTimeOfDay,
            MinimumPlaysPerHour = request.MinimumPlaysPerHour,
            Status = PlacementStatus.Draft
        };

        var conflicts = await validator.ValidateAsync(candidate, ct);
        var quote = await billing.QuoteDraftAsync(userId, candidate, 1, ct);
        var alternatives = conflicts.Count == 0
            ? Array.Empty<AlternativeWindowDto>()
            : (await validator.FindAlternativesAsync(candidate, 3, ct)).ToArray();

        return new PlacementValidationResultDto(conflicts.Count == 0, quote, conflicts, alternatives);
    }

    public async Task<IReadOnlyList<AlternativeWindowDto>> GetAlternativesAsync(Guid userId, bool isAdmin, Guid id, int max, CancellationToken ct = default)
    {
        var placement = await LoadAsync(id, ct);
        EnsureVisible(placement, userId, isAdmin);
        return await validator.FindAlternativesAsync(placement, max, ct);
    }

    public async Task<PublishPlacementResultDto> PublishAsync(Guid userId, bool isAdmin, Guid id, int billboardCount, CancellationToken ct = default)
    {
        var placement = await LoadAsync(id, ct);
        EnsureVisible(placement, userId, isAdmin);

        // Бюджет всегда резервируется у владельца размещения, даже если действие выполняет админ.
        var ownerId = placement.OwnerId;

        billboardCount = Math.Max(1, billboardCount);

        // Повторная публикация уже опубликованного размещения не создаёт второй резерв.
        if (placement.Status is PlacementStatus.Scheduled or PlacementStatus.Active or PlacementStatus.Downloading
            or PlacementStatus.Ready)
        {
            var quoteExisting = await billing.QuoteDraftAsync(ownerId, placement, billboardCount, ct);
            var reservation = await db.BudgetReservations.AsNoTracking()
                .FirstOrDefaultAsync(r => r.PlacementId == placement.Id, ct);
            return new PublishPlacementResultDto(placement.Id, placement.Status.ToString(),
                reservation?.ReservedAmount ?? placement.ReservedAmount, quoteExisting);
        }

        if (placement.Status is not (PlacementStatus.Draft or PlacementStatus.PendingPayment))
            throw AppException.Conflict("INVALID_PLACEMENT_STATE",
                $"Публикация невозможна в статусе {placement.Status}.");

        if (placement.VideoAsset is not { Status: VideoStatus.Ready })
            throw AppException.Conflict("VIDEO_NOT_READY", "Видео ещё не готово к показу.");

        var conflicts = await validator.ValidateAsync(placement, ct);
        if (conflicts.Count > 0)
            throw AppException.Conflict("PLACEMENT_CONFLICT",
                "Невозможно опубликовать размещение: недостаточно рекламного времени.",
                new { conflicts });

        var quote = await billing.QuoteDraftAsync(ownerId, placement, billboardCount, ct);

        ReservationDto reservationResult;
        try
        {
            reservationResult = await billing.ReserveAsync(ownerId, placement.Id, billboardCount, ct);
        }
        catch (AppException ex) when (ex.Code == "INSUFFICIENT_FUNDS")
        {
            // Денег не хватило: размещение остаётся в базе и ждёт пополнения, ничего не списано.
            placement.Status = PlacementStatus.PendingPayment;
            SystemLogWriter.Add(db, userId, "PlacementAwaitingPayment", nameof(AdPlacement), placement.Id, "WARN");
            await db.SaveChangesAsync(ct);
            throw;
        }

        placement.Status = PlacementStatus.Scheduled;
        placement.PauseReason = null;
        placement.ReservedAmount = reservationResult.ReservedAmount;
        SystemLogWriter.Add(db, userId, "PlacementPublished", nameof(AdPlacement), placement.Id, "OK",
            reservationResult.ReservedAmount.ToString("0.####"));
        await db.SaveChangesAsync(ct);

        return new PublishPlacementResultDto(placement.Id, placement.Status.ToString(),
            reservationResult.ReservedAmount, quote);
    }

    public async Task<PlacementDto> PauseAsync(Guid userId, bool isAdmin, Guid id, CancellationToken ct = default)
    {
        var placement = await LoadAsync(id, ct);
        EnsureVisible(placement, userId, isAdmin);
        if (!PausableStatuses.Contains(placement.Status))
            throw AppException.Conflict("INVALID_PLACEMENT_STATE", $"Приостановка невозможна в статусе {placement.Status}.");

        placement.Status = PlacementStatus.Paused;
        placement.PauseReason = ManualPause;
        SystemLogWriter.Add(db, userId, "PlacementPaused", nameof(AdPlacement), placement.Id);
        await db.SaveChangesAsync(ct);
        return (await MapManyAsync(new[] { placement }, ct))[0];
    }

    public async Task<PlacementDto> ResumeAsync(Guid userId, bool isAdmin, Guid id, CancellationToken ct = default)
    {
        var placement = await LoadAsync(id, ct);
        EnsureVisible(placement, userId, isAdmin);
        if (placement.Status != PlacementStatus.Paused)
            throw AppException.Conflict("INVALID_PLACEMENT_STATE", "Размещение не приостановлено.");

        var reservation = await db.BudgetReservations.AsNoTracking()
            .FirstOrDefaultAsync(r => r.PlacementId == placement.Id, ct);

        // Без денег в резерве показывать нельзя: размещение ждёт пополнения.
        placement.Status = reservation is not null && reservation.Remaining >= reservation.PricePerPlay
            ? PlacementStatus.Scheduled
            : PlacementStatus.PendingPayment;
        placement.PauseReason = null;
        SystemLogWriter.Add(db, userId, "PlacementResumed", nameof(AdPlacement), placement.Id);
        await db.SaveChangesAsync(ct);
        return (await MapManyAsync(new[] { placement }, ct))[0];
    }

    public async Task<PlacementDto> CancelAsync(Guid userId, bool isAdmin, Guid id, CancellationToken ct = default)
    {
        var placement = await LoadAsync(id, ct);
        EnsureVisible(placement, userId, isAdmin);
        if (!CancellableStatuses.Contains(placement.Status))
            throw AppException.Conflict("INVALID_PLACEMENT_STATE", $"Отмена невозможна в статусе {placement.Status}.");

        await billing.ReleaseAsync(placement.Id, ct);

        placement.Status = PlacementStatus.Cancelled;
        placement.PauseReason = null;
        placement.ReservedAmount = 0m;
        SystemLogWriter.Add(db, userId, "PlacementCancelled", nameof(AdPlacement), placement.Id);
        await db.SaveChangesAsync(ct);
        return (await MapManyAsync(new[] { placement }, ct))[0];
    }

    // ---------- проверки ----------

    private static void EnsureVisible(AdPlacement placement, Guid userId, bool isAdmin)
    {
        if (!isAdmin && placement.OwnerId != userId)
            throw AppException.NotFound("Размещение");
    }

    private static int ParseMask(PlacementUpsertRequest request)
    {
        if (!WeekMask.TryParse(request.DaysOfWeek, out var mask))
            throw AppException.Validation("INVALID_DAYS",
                "DaysOfWeek: укажите дни недели, например [\"Monday\",\"Tuesday\"] или [\"Пн\",\"Вт\"].");
        return mask;
    }

    private static void EnsureWindow(PlacementUpsertRequest request)
    {
        if (request.EndAt <= request.StartAt)
            throw AppException.Validation("INVALID_WINDOW", "EndAt должен быть позже StartAt.");
        if (request.EndAt - request.StartAt > TimeSpan.FromDays(366))
            throw AppException.Validation("INVALID_WINDOW", "Слишком длинное размещение: максимум 366 дней.");
        if (request.StartTimeOfDay == request.EndTimeOfDay)
            throw AppException.Validation("INVALID_WINDOW", "StartTimeOfDay и EndTimeOfDay не должны совпадать.");
    }

    private static void EnsureBillboardUsable(BillboardDevice billboard)
    {
        if (billboard.Status == BillboardStatus.Disabled)
            throw AppException.Conflict("BILLBOARD_DISABLED", "Билборд отключён администратором.");
    }

    private async Task<VideoAsset> LoadOwnVideoAsync(Guid userId, Guid videoId, CancellationToken ct)
    {
        var video = await db.VideoAssets.FirstOrDefaultAsync(v => v.Id == videoId, ct)
                    ?? throw AppException.NotFound("Видео");
        if (video.OwnerId != userId) throw AppException.NotFound("Видео");
        return video;
    }

    private async Task<BillboardDevice> LoadBillboardAsync(Guid billboardId, CancellationToken ct) =>
        await db.Billboards.FirstOrDefaultAsync(b => b.Id == billboardId, ct) ?? throw AppException.NotFound("Билборд");

    private async Task<AdPlacement> LoadAsync(Guid id, CancellationToken ct) =>
        await db.AdPlacements.Include(p => p.VideoAsset).Include(p => p.Billboard)
            .FirstOrDefaultAsync(p => p.Id == id, ct)
        ?? throw AppException.NotFound("Размещение");

    // ---------- отображение ----------

    private async Task<IReadOnlyList<PlacementDto>> MapManyAsync(IReadOnlyList<AdPlacement> placements, CancellationToken ct)
    {
        if (placements.Count == 0) return Array.Empty<PlacementDto>();

        var ids = placements.Select(p => p.Id).ToList();

        var reservations = await db.BudgetReservations.AsNoTracking()
            .Where(r => ids.Contains(r.PlacementId))
            .ToDictionaryAsync(r => r.PlacementId, ct);

        var plays = await db.PlaybackEvents.AsNoTracking()
            .Where(e => ids.Contains(e.PlacementId) && e.Result == PlaybackResult.Completed)
            .GroupBy(e => e.PlacementId)
            .Select(g => new { PlacementId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.PlacementId, x => x.Count, ct);

        return placements.Select(p => new PlacementDto(
            p.Id, p.OwnerId, p.VideoAssetId, p.VideoAsset?.OriginalFileName ?? "",
            p.BillboardId, p.Billboard?.Name ?? "",
            p.StartAt, p.EndAt, p.DaysOfWeekMask, WeekMask.Format(p.DaysOfWeekMask),
            p.StartTimeOfDay, p.EndTimeOfDay, p.MinimumPlaysPerHour, p.Status.ToString(), p.PauseReason,
            p.ReservedAmount,
            reservations.TryGetValue(p.Id, out var r) ? r.ChargedAmount : 0m,
            plays.GetValueOrDefault(p.Id),
            p.CreatedAt)).ToList();
    }
}
