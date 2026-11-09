using Billboard.Domain.Enums;

namespace Billboard.Application.Billing;

public record QuoteResult(
    decimal BillableHours,
    int ExpectedPlays,
    decimal PricePerPlayAfterDiscount,
    decimal TotalAmount);

/// <summary>
/// Предварительный расчёт стоимости: C = N_показов * P_показ.
/// N = ceil(часы действия * минимальная частота в час).
/// Расчёт дискретен по часам, как в ТЗ для первой версии.
/// </summary>
public static class QuoteCalculator
{
    public static decimal CountBillableHours(
        DateTimeOffset startAt, DateTimeOffset endAt, int daysMask,
        TimeOnly startTimeOfDay, TimeOnly endTimeOfDay, TimeZoneInfo timeZone)
    {
        var start = TimeZoneInfo.ConvertTime(startAt, timeZone).DateTime;
        var end = TimeZoneInfo.ConvertTime(endAt, timeZone).DateTime;
        if (end <= start) return 0m;

        decimal hours = 0m;
        for (var day = start.Date; day <= end.Date; day = day.AddDays(1))
        {
            if (((int)ToWeekDay(day.DayOfWeek) & daysMask) == 0) continue;

            var windowStart = day + startTimeOfDay.ToTimeSpan();
            // Окно, у которого конец <= начала, считаем переходящим через полночь.
            var windowEnd = endTimeOfDay > startTimeOfDay
                ? day + endTimeOfDay.ToTimeSpan()
                : day.AddDays(1) + endTimeOfDay.ToTimeSpan();

            if (windowStart < start) windowStart = start;
            if (windowEnd > end) windowEnd = end;
            if (windowEnd > windowStart)
                hours += (decimal)(windowEnd - windowStart).TotalHours;
        }

        return Math.Round(hours, 4);
    }

    public static QuoteResult Calculate(
        DateTimeOffset startAt, DateTimeOffset endAt, int daysMask,
        TimeOnly startTimeOfDay, TimeOnly endTimeOfDay,
        int minimumPlaysPerHour, decimal pricePerPlay, decimal discountPercent,
        TimeZoneInfo timeZone)
    {
        var hours = CountBillableHours(startAt, endAt, daysMask, startTimeOfDay, endTimeOfDay, timeZone);
        var plays = (int)Math.Ceiling(hours * Math.Max(0, minimumPlaysPerHour));

        var discount = Math.Clamp(discountPercent, 0m, 100m);
        var unit = Math.Round(pricePerPlay * (1m - discount / 100m), 4, MidpointRounding.AwayFromZero);

        // Округляем вверх до копеек: резерв не должен быть меньше суммы списаний.
        var total = Math.Ceiling(plays * unit * 100m) / 100m;
        return new QuoteResult(hours, plays, unit, total);
    }

    public static WeekDays ToWeekDay(DayOfWeek day) => day == DayOfWeek.Sunday
        ? WeekDays.Sunday
        : (WeekDays)(1 << ((int)day - 1));
}
