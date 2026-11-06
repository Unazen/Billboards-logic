using Billboard.Domain.Enums;

namespace Billboard.Application.Scheduling;

/// <summary>
/// Битовые маски дней недели для AdPlacement.DaysOfWeekMask.
/// Маска совпадает с enum <see cref="WeekDays"/>: понедельник = 1, вторник = 2, ... воскресенье = 64.
/// Принимает английские названия (Monday, Mon) и русские (Понедельник, Пн).
/// </summary>
public static class WeekMask
{
    private static readonly Dictionary<string, DayOfWeek> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        ["monday"] = DayOfWeek.Monday, ["mon"] = DayOfWeek.Monday, ["понедельник"] = DayOfWeek.Monday, ["пн"] = DayOfWeek.Monday,
        ["tuesday"] = DayOfWeek.Tuesday, ["tue"] = DayOfWeek.Tuesday, ["вторник"] = DayOfWeek.Tuesday, ["вт"] = DayOfWeek.Tuesday,
        ["wednesday"] = DayOfWeek.Wednesday, ["wed"] = DayOfWeek.Wednesday, ["среда"] = DayOfWeek.Wednesday, ["ср"] = DayOfWeek.Wednesday,
        ["thursday"] = DayOfWeek.Thursday, ["thu"] = DayOfWeek.Thursday, ["четверг"] = DayOfWeek.Thursday, ["чт"] = DayOfWeek.Thursday,
        ["friday"] = DayOfWeek.Friday, ["fri"] = DayOfWeek.Friday, ["пятница"] = DayOfWeek.Friday, ["пт"] = DayOfWeek.Friday,
        ["saturday"] = DayOfWeek.Saturday, ["sat"] = DayOfWeek.Saturday, ["суббота"] = DayOfWeek.Saturday, ["сб"] = DayOfWeek.Saturday,
        ["sunday"] = DayOfWeek.Sunday, ["sun"] = DayOfWeek.Sunday, ["воскресенье"] = DayOfWeek.Sunday, ["вс"] = DayOfWeek.Sunday
    };

    /// <summary>Собирает маску из списка дней. Возвращает false, если список пуст или содержит неизвестный день.</summary>
    public static bool TryParse(IEnumerable<string>? days, out int mask)
    {
        mask = 0;
        if (days is null) return false;

        var accumulated = 0;
        foreach (var raw in days)
        {
            if (string.IsNullOrWhiteSpace(raw)) return false;
            if (!Names.TryGetValue(raw.Trim(), out var day)) return false;
            accumulated |= (int)ToFlag(day);
        }

        mask = accumulated;
        return mask != 0;
    }

    /// <summary>Человекочитаемый список дней для ответа API (понедельник → воскресенье).</summary>
    public static IReadOnlyList<string> Format(int mask)
    {
        var result = new List<string>(7);
        for (var i = 0; i < 7; i++)
        {
            var day = (DayOfWeek)((i + 1) % 7);
            if ((mask & (int)ToFlag(day)) != 0)
                result.Add(day.ToString());
        }
        return result;
    }

    public static bool Includes(int mask, DayOfWeek day) => (mask & (int)ToFlag(day)) != 0;

    public static WeekDays ToFlag(DayOfWeek day) => day == DayOfWeek.Sunday
        ? WeekDays.Sunday
        : (WeekDays)(1 << ((int)day - 1));
}
