namespace Billboard.Scheduling;

/// <summary>
/// Поиск свободных окон вместо конфликтного. Перебирает начало окна по сетке
/// (по умолчанию каждые 30 минут) в те же дни недели и, если за текущий период
/// места нет, сдвигает расписание на следующую неделю.
/// </summary>
public sealed class AlternativeSlotFinder
{
    public IReadOnlyList<AlternativeSlot> Find(
        PlacementRequest request,
        CapacitySnapshot snapshot,
        ScheduleValidationOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(snapshot);

        options ??= ScheduleValidationOptions.Default;

        var duration = ScheduleWindow.GetDuration(request.StartTime, request.EndTime);
        if (duration <= TimeSpan.Zero || duration >= TimeSpan.FromHours(24))
        {
            return Array.Empty<AlternativeSlot>();
        }

        var days = request.DaysOfWeek.Distinct().OrderBy(day => day).ToArray();
        if (days.Length == 0)
        {
            return Array.Empty<AlternativeSlot>();
        }

        var step = Math.Clamp(options.AlternativeStepMinutes, 5, 120);
        var candidates = new List<AlternativeCandidate>();
        var originalMinutes = (int)request.StartTime.ToTimeSpan().TotalMinutes;

        for (var week = 0; week <= Math.Max(0, options.AlternativeWeeksAhead); week++)
        {
            DateOnly startDate;
            DateOnly endDate;

            try
            {
                startDate = request.StartDate.AddDays(week * 7);
                endDate = request.EndDate.AddDays(week * 7);
            }
            catch (ArgumentOutOfRangeException)
            {
                break;
            }

            for (var minutes = 0; minutes < 24 * 60; minutes += step)
            {
                var start = TimeOnly.MinValue.Add(TimeSpan.FromMinutes(minutes));
                if (week == 0 && start == request.StartTime)
                {
                    continue;
                }

                var end = start.Add(duration);
                if (end == start)
                {
                    continue;
                }

                var candidate = request.ToPlacement(Guid.Empty, PlacementStatus.Scheduled) with
                {
                    StartDate = startDate,
                    EndDate = endDate,
                    StartTime = start,
                    EndTime = end,
                };

                var summary = snapshot.Evaluate(candidate);
                if (summary.OverflowBuckets > 0 || summary.ApplicableHourBuckets == 0)
                {
                    continue;
                }

                candidates.Add(new AlternativeCandidate(
                    week,
                    CircularDistanceMinutes(originalMinutes, minutes),
                    new AlternativeSlot
                    {
                        StartDate = startDate,
                        EndDate = endDate,
                        DaysOfWeek = days,
                        StartTime = start,
                        EndTime = end,
                        FreeSecondsPerHour = summary.FreeSecondsPerHour,
                        PeakUtilizationPercent = summary.PeakUtilizationPercent,
                    }));
            }
        }

        return candidates
            .OrderBy(candidate => candidate.Week)
            .ThenBy(candidate => candidate.DistanceMinutes)
            .ThenByDescending(candidate => candidate.Slot.FreeSecondsPerHour)
            .ThenBy(candidate => candidate.Slot.StartTime)
            .Take(Math.Max(0, options.MaxAlternatives))
            .Select(candidate => candidate.Slot)
            .ToArray();
    }

    private static int CircularDistanceMinutes(int left, int right)
    {
        var diff = Math.Abs(left - right) % (24 * 60);
        return Math.Min(diff, 24 * 60 - diff);
    }

    private sealed record AlternativeCandidate(int Week, int DistanceMinutes, AlternativeSlot Slot);
}
