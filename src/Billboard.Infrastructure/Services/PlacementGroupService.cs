using Billboard.Application.Common;
using Billboard.Application.Contracts;
using Billboard.Application.Scheduling;
using Billboard.Domain.Entities;
using Billboard.Domain.Enums;
using Billboard.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Billboard.Infrastructure.Services;

/// <summary>
/// Назначение одного расписания группе билбордов (контракт P2).
/// Проверяются все устройства; черновики создаются в одной транзакции и сразу
/// резервируют бюджет. Если хотя бы одно устройство не прошло проверку или
/// не хватило денег — не создаётся ничего.
/// </summary>
public sealed class PlacementGroupService(
    AppDbContext db,
    IScheduleValidator validator,
    IBillingService billing,
    IPlacementService placements) : IPlacementGroupService
{
    public const string EmptyGroup = "GROUP_IS_EMPTY";
    public const string ValidationFailed = "GROUP_PLACEMENT_VALIDATION_FAILED";
    public const string SaveFailed = "GROUP_PLACEMENT_SAVE_FAILED";

    public async Task<GroupScheduleValidationDto> ValidateAsync(
        Guid userId, PlacementGroupRequestDto request, CancellationToken ct = default)
    {
        var prepared = await PrepareAsync(userId, request, ct);
        return await BuildValidationAsync(prepared, ct);
    }

    public async Task<GroupPlacementOperationDto> CreateDraftsAsync(
        Guid userId, PlacementGroupRequestDto request, CancellationToken ct = default)
    {
        var prepared = await PrepareAsync(userId, request, ct);
        var validation = await BuildValidationAsync(prepared, ct);

        if (!validation.CanPublish)
        {
            return new GroupPlacementOperationDto(
                false, Array.Empty<PlacementDto>(), validation, ValidationFailed,
                "Группа не может быть назначена: хотя бы один билборд не прошёл проверку.");
        }

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        try
        {
            foreach (var candidate in prepared.Candidates)
                db.AdPlacements.Add(candidate);

            await db.SaveChangesAsync(ct);

            // Резерв бюджета в той же транзакции: при нехватке денег откатится всё.
            foreach (var candidate in prepared.Candidates)
                await billing.ReserveAsync(userId, candidate.Id, 1, ct);

            await transaction.CommitAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await transaction.RollbackAsync(CancellationToken.None);
            db.ChangeTracker.Clear();

            if (ex is AppException)
                throw;

            return new GroupPlacementOperationDto(
                false, Array.Empty<PlacementDto>(), validation, SaveFailed,
                "Не удалось сохранить групповое назначение: изменения отменены.");
        }

        var created = new List<PlacementDto>(prepared.Candidates.Count);
        foreach (var candidate in prepared.Candidates)
            created.Add(await placements.GetAsync(userId, false, candidate.Id, ct));

        return new GroupPlacementOperationDto(true, created, validation, null, null);
    }

    // ---------- подготовка ----------

    private sealed record Prepared(List<BillboardDevice> Billboards, List<AdPlacement> Candidates);

    private async Task<Prepared> PrepareAsync(Guid userId, PlacementGroupRequestDto request, CancellationToken ct)
    {
        var billboardIds = request.BillboardIds.Distinct().ToList();
        if (billboardIds.Count == 0)
            throw AppException.Validation(EmptyGroup, "В группе не выбран ни один билборд.");

        var template = request.Template;
        if (!WeekMask.TryParse(template.DaysOfWeek, out var mask))
            throw AppException.Validation("INVALID_DAYS",
                "DaysOfWeek: укажите дни недели, например [\"Monday\",\"Tuesday\"].");

        if (template.EndDate < template.StartDate)
            throw AppException.Validation("INVALID_WINDOW", "EndDate должен быть не раньше StartDate.");
        if (template.StartTime == template.EndTime)
            throw AppException.Validation("INVALID_WINDOW", "StartTime и EndTime не должны совпадать.");

        var startAt = new DateTimeOffset(template.StartDate.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var endAt = new DateTimeOffset(template.EndDate.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        if (endAt - startAt > TimeSpan.FromDays(366))
            throw AppException.Validation("INVALID_WINDOW", "Слишком длинное размещение: максимум 366 дней.");

        var video = await db.VideoAssets.FirstOrDefaultAsync(v => v.Id == template.VideoAssetId, ct)
                    ?? throw AppException.NotFound("Видео");
        if (video.OwnerId != userId)
            throw AppException.NotFound("Видео");
        if (video.Status != VideoStatus.Ready)
            throw AppException.Conflict("VIDEO_NOT_READY", "Видео ещё не готово к показу.");
        if (video.DurationSeconds is not > 0)
            throw AppException.Conflict("VIDEO_DURATION_UNKNOWN",
                "Не удалось определить длительность ролика: перезагрузите видео.");

        var found = await db.Billboards.AsNoTracking()
            .Where(b => billboardIds.Contains(b.Id))
            .ToListAsync(ct);

        var missing = billboardIds.Except(found.Select(b => b.Id)).ToList();
        if (missing.Count > 0)
            throw AppException.Validation("UNKNOWN_BILLBOARD", "Некоторые билборды не найдены.",
                new { billboardIds = missing });

        var byId = found.ToDictionary(b => b.Id);
        var billboards = billboardIds.Select(id => byId[id]).ToList();

        var candidates = billboards.Select(billboard => new AdPlacement
        {
            Id = Guid.NewGuid(),
            OwnerId = userId,
            VideoAssetId = video.Id,
            BillboardId = billboard.Id,
            StartAt = startAt,
            EndAt = endAt,
            DaysOfWeekMask = mask,
            StartTimeOfDay = template.StartTime,
            EndTimeOfDay = template.EndTime,
            MinimumPlaysPerHour = template.MinimumPlaysPerHour,
            Status = PlacementStatus.Draft,
            CreatedAt = DateTimeOffset.UtcNow
        }).ToList();

        return new Prepared(billboards, candidates);
    }

    private async Task<GroupScheduleValidationDto> BuildValidationAsync(Prepared prepared, CancellationToken ct)
    {
        var results = new List<BillboardScheduleValidationDto>(prepared.Candidates.Count);

        for (var i = 0; i < prepared.Candidates.Count; i++)
        {
            var candidate = prepared.Candidates[i];
            var billboard = prepared.Billboards[i];

            var conflicts = await validator.ValidateAsync(candidate, ct);
            var alternatives = conflicts.Count == 0
                ? Array.Empty<AlternativeWindowDto>()
                : (await validator.FindAlternativesAsync(candidate, 3, ct)).ToArray();

            results.Add(new BillboardScheduleValidationDto(
                billboard.Id,
                billboard.Name,
                conflicts.Count == 0,
                conflicts.Select(c => new GroupScheduleConflictDto(c.Reason, c.Message ?? "")).ToList(),
                alternatives));
        }

        var available = results.Count(r => r.IsAvailable);
        return new GroupScheduleValidationDto(
            results.Count > 0 && available == results.Count,
            results.Count,
            available,
            results);
    }
}
