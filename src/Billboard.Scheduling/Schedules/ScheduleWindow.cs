namespace Billboard.Scheduling;

/// <summary>
/// Разбор суточного окна размещения на часовые слоты.
/// Ёмкость считается по календарным часам: слот 09:00–10:00 — это Hour = 9.
/// Если окно начинается или заканчивается внутри часа, этот час считается
/// занятым целиком (консервативная оценка).
/// </summary>
public static class ScheduleWindow
{
    private const long TicksPerHour = TimeSpan.TicksPerHour;

    /// <summary>Длительность суточного окна. 0 означает некорректное окно.</summary>
    public static TimeSpan GetDuration(TimeOnly start, TimeOnly end)
    {
        if (start == end)
        {
            return TimeSpan.Zero;
        }

        return end > start
            ? end.ToTimeSpan() - start.ToTimeSpan()
            : TimeSpan.FromHours(24) - (start.ToTimeSpan() - end.ToTimeSpan());
    }

    /// <summary>Переходит ли окно через полночь.</summary>
    public static bool CrossesMidnight(TimeOnly start, TimeOnly end) => end < start;

    /// <summary>Часы, которые окно занимает в день его начала.</summary>
    public static IReadOnlyList<int> GetStartDayHours(TimeOnly start, TimeOnly end)
    {
        if (start == end)
        {
            return Array.Empty<int>();
        }

        return end > start
            ? HoursBetween(start, end)
            : HoursBetween(start, TimeOnly.MaxValue);
    }

    /// <summary>Часы после полуночи, которые принадлежат окну, начавшемуся вчера.</summary>
    public static IReadOnlyList<int> GetSpillHours(TimeOnly start, TimeOnly end)
    {
        if (start == end || end >= start)
        {
            return Array.Empty<int>();
        }

        return HoursBetween(TimeOnly.MinValue, end);
    }

    /// <summary>Все часовые слоты размещения с учётом дат, дней недели и перехода через полночь.</summary>
    public static IReadOnlyList<(DateOnly Date, int Hour)> GetBuckets(Placement placement)
    {
        var result = new List<(DateOnly Date, int Hour)>();
        var seen = new HashSet<(DateOnly Date, int Hour)>();

        if (placement.StartDate > placement.EndDate || placement.StartTime == placement.EndTime)
        {
            return result;
        }

        var days = placement.NormalizedDays;
        if (days.Count == 0)
        {
            return result;
        }

        var startHours = GetStartDayHours(placement.StartTime, placement.EndTime);
        var spillHours = GetSpillHours(placement.StartTime, placement.EndTime);
        var crossesMidnight = CrossesMidnight(placement.StartTime, placement.EndTime);

        for (var date = placement.StartDate; date <= placement.EndDate; date = date.AddDays(1))
        {
            if (!days.Contains(date.DayOfWeek))
            {
                continue;
            }

            foreach (var hour in startHours)
            {
                if (seen.Add((date, hour)))
                {
                    result.Add((date, hour));
                }
            }

            // После полуночи окно продолжается уже в следующую календарную дату.
            // Если следующая дата выходит за EndDate, «хвост» не учитываем:
            // последний день расписания заканчивается в своих сутках.
            if (crossesMidnight && date < placement.EndDate)
            {
                var next = date.AddDays(1);
                foreach (var hour in spillHours)
                {
                    if (seen.Add((next, hour)))
                    {
                        result.Add((next, hour));
                    }
                }
            }
        }

        return result;
    }

    /// <summary>Покрывает ли размещение конкретный календарный час.</summary>
    public static bool Covers(Placement placement, DateOnly date, int hour)
    {
        if (hour is < 0 or > 23 || date < placement.StartDate || date > placement.EndDate)
        {
            return false;
        }

        var days = placement.NormalizedDays;

        if (days.Contains(date.DayOfWeek) &&
            GetStartDayHours(placement.StartTime, placement.EndTime).Contains(hour))
        {
            return true;
        }

        if (!CrossesMidnight(placement.StartTime, placement.EndTime))
        {
            return false;
        }

        var previous = date.AddDays(-1);
        return previous >= placement.StartDate &&
               days.Contains(previous.DayOfWeek) &&
               GetSpillHours(placement.StartTime, placement.EndTime).Contains(hour);
    }

    private static IReadOnlyList<int> HoursBetween(TimeOnly start, TimeOnly end)
    {
        var hours = new List<int>();
        var first = start.Hour;

        // Конец ровно на границе часа не включает этот час; частичный час — включает.
        var endExclusive = end == TimeOnly.MaxValue
            ? 24
            : end.Hour + (end.Ticks % TicksPerHour == 0 ? 0 : 1);

        for (var hour = first; hour < endExclusive && hour < 24; hour++)
        {
            hours.Add(hour);
        }

        return hours;
    }
}
