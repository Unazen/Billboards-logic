using Billboard.Application.Common;
using Billboard.Application.Contracts;
using Billboard.Application.Scheduling;
using Billboard.Domain.Entities;
using Billboard.Domain.Enums;
using Billboard.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Billboard.Infrastructure.Scheduling;

/// <summary>
/// Проверка расписания по рекламной ёмкости билборда.
///
/// Правило из ТЗ: размещение требует <c>MinimumPlaysPerHour * длительность ролика</c> секунд
/// в каждом часу своего окна. Сумма требований всех действующих размещений не должна
/// превышать <see cref="BillboardDevice.AdvertCapacitySecondsPerHour"/>. Пересечение по времени
/// само по себе конфликтом не является — важна суммарная ёмкость.
///
/// Это базовая реализация для участника 3. Полноценный планировщик (квоты, ротация,
/// приоритеты, переносы) реализует участник 2, заменяя <see cref="IScheduleValidator"/>
/// без изменений в API и биллинге.
/// </summary>
public sealed class CapacityScheduleValidator(AppDbContext db) : IScheduleValidator
{
    /// <summary>Размещения, которые занимают рекламное время.</summary>
    private static readonly PlacementStatus[] CountedStatuses =
    {
        PlacementStatus.Scheduled, PlacementStatus.Downloading, PlacementStatus.Ready,
        PlacementStatus.Active, PlacementStatus.Paused
    };

    public async Task<IReadOnlyList<PlacementConflictDto>> ValidateAsync(AdPlacement candidate, CancellationToken ct = default)
    {
        var billboard = await db.Billboards.AsNoTracking()
                            .FirstOrDefaultAsync(b => b.Id == candidate.BillboardId, ct)
                        ?? throw AppException.NotFound("Билборд");

        var conflicts = new List<PlacementConflictDto>();

        if (billboard.Status == BillboardStatus.Disabled)
            conflicts.Add(new PlacementConflictDto(billboard.Id, "BILLBOARD_DISABLED",
                "Билборд отключён администратором."));

        var video = await db.VideoAssets.AsNoTracking()
                        .FirstOrDefaultAsync(v => v.Id == candidate.VideoAssetId, ct)
                    ?? throw AppException.NotFound("Видео");

        if (video.Status != VideoStatus.Ready)
            conflicts.Add(new PlacementConflictDto(billboard.Id, "VIDEO_NOT_READY",
                "Видео ещё не готово к показу."));

        if (video.DurationSeconds is not > 0)
        {
            // Без длительности проверить ёмкость нельзя — публиковать такое размещение нельзя.
            conflicts.Add(new PlacementConflictDto(billboard.Id, "VIDEO_DURATION_UNKNOWN",
                "Не удалось определить длительность ролика: перезагрузите видео."));
            return conflicts;
        }

        var duration = video.DurationSeconds.Value;

        var others = await db.AdPlacements.AsNoTracking()
            .Include(p => p.VideoAsset)
            .Where(p => p.BillboardId == candidate.BillboardId
                        && p.Id != candidate.Id
                        && CountedStatuses.Contains(p.Status))
            .ToListAsync(ct);

        // Пересечение по времени считаем в памяти: SQLite не умеет сравнивать DateTimeOffset.
        others = others.Where(p => p.StartAt < candidate.EndAt && candidate.StartAt < p.EndAt).ToList();

        // Размещение, приостановленное из-за бюджета, время не занимает.
        others = others.Where(p => !(p.Status == PlacementStatus.Paused
                                     && p.PauseReason == "BudgetExhausted")).ToList();

        var timeZone = TimeZones.Resolve(billboard.TimeZoneId);
        var capacity = billboard.AdvertCapacitySecondsPerHour;

        var hours = EnumerateCandidateHours(candidate, timeZone).ToList();
        if (hours.Count == 0)
        {
            // Окно не содержит ни одного часа выбранных дней: показывать нечего.
            conflicts.Add(new PlacementConflictDto(billboard.Id, "NO_ACTIVE_HOURS",
                "Выбранные дни недели не пересекаются с периодом размещения."));
            return conflicts;
        }

        foreach (var (day, hour) in hours)
        {
            var hourStart = day.ToDateTime(new TimeOnly(hour, 0));
            var hourEnd = hourStart.AddHours(1);

            var demand = (decimal)candidate.MinimumPlaysPerHour * duration;
            demand += others
                .Where(p => Covers(p, hourStart, hourEnd))
                .Sum(p => (decimal)p.MinimumPlaysPerHour * (p.VideoAsset?.DurationSeconds ?? 0m));

            if (demand > capacity)
            {
                conflicts.Add(new PlacementConflictDto(billboard.Id, "INSUFFICIENT_ADVERTISING_CAPACITY",
                    $"Не хватает рекламного времени: {day:dd.MM.yyyy} {hour:00}:00 требуется " +
                    $"{demand:0} с/ч, доступно {capacity} с/ч."));
                break;
            }
        }

        return conflicts;
    }

    public async Task<IReadOnlyList<AlternativeWindowDto>> FindAlternativesAsync(
        AdPlacement candidate, int max, CancellationToken ct = default)
    {
        max = Math.Clamp(max, 1, 20);
        var result = new List<AlternativeWindowDto>(max);
        var length = candidate.EndAt - candidate.StartAt;

        // Ищем те же сутки/часы со сдвигом на час вперёд в пределах двух недель.
        for (var shift = 1; shift <= 24 * 14 && result.Count < max; shift++)
        {
            var shifted = CloneWithWindow(candidate, candidate.StartAt.AddHours(shift), candidate.StartAt.AddHours(shift) + length);
            var conflicts = await ValidateAsync(shifted, ct);
            if (conflicts.Count == 0)
                result.Add(new AlternativeWindowDto(shifted.StartAt, shifted.EndAt));
        }

        return result;
    }

    /// <summary>Каждый час окна размещения в локальном времени билборда.</summary>
    private static IEnumerable<(DateOnly Day, int Hour)> EnumerateCandidateHours(AdPlacement placement, TimeZoneInfo timeZone)
    {
        var startDay = TimeZoneInfo.ConvertTime(placement.StartAt, timeZone).Date;
        var endDay = TimeZoneInfo.ConvertTime(placement.EndAt, timeZone).Date;
        var startHour = placement.StartTimeOfDay.Hour;
        var endHour = placement.EndTimeOfDay.Hour;
        var overnight = placement.EndTimeOfDay <= placement.StartTimeOfDay;

        for (var dayDate = startDay; dayDate <= endDay; dayDate = dayDate.AddDays(1))
        {
            var day = DateOnly.FromDateTime(dayDate);
            if (!WeekMask.Includes(placement.DaysOfWeekMask, dayDate.DayOfWeek)) continue;

            var lastHour = overnight ? 24 : endHour;
            for (var hour = startHour; hour < lastHour; hour++)
                yield return (day, hour);
        }
    }

    /// <summary>Покрывает ли размещение указанный час (с учётом окна, переходящего через полночь).</summary>
    private static bool Covers(AdPlacement placement, DateTime hourStart, DateTime hourEnd)
    {
        for (var offset = -1; offset <= 0; offset++)
        {
            var day = hourStart.Date.AddDays(offset);
            if (!WeekMask.Includes(placement.DaysOfWeekMask, day.DayOfWeek)) continue;

            var windowStart = day + placement.StartTimeOfDay.ToTimeSpan();
            var windowEnd = placement.EndTimeOfDay > placement.StartTimeOfDay
                ? day + placement.EndTimeOfDay.ToTimeSpan()
                : day.AddDays(1) + placement.EndTimeOfDay.ToTimeSpan();

            if (windowStart < hourEnd && hourStart < windowEnd) return true;
        }

        return false;
    }

    private static AdPlacement CloneWithWindow(AdPlacement source, DateTimeOffset start, DateTimeOffset end) => new()
    {
        Id = source.Id,
        OwnerId = source.OwnerId,
        VideoAssetId = source.VideoAssetId,
        BillboardId = source.BillboardId,
        StartAt = start,
        EndAt = end,
        DaysOfWeekMask = source.DaysOfWeekMask,
        StartTimeOfDay = source.StartTimeOfDay,
        EndTimeOfDay = source.EndTimeOfDay,
        MinimumPlaysPerHour = source.MinimumPlaysPerHour,
        Status = source.Status
    };
}
