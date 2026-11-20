namespace Billboard.Application.Scheduling;

/// <summary>Единое место разбора часовых поясов билбордов (IANA или Windows id).</summary>
public static class TimeZones
{
    public static TimeZoneInfo Resolve(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return TimeZoneInfo.Utc;
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            // Неизвестный пояс не должен ронять расчёты: считаем по UTC и сообщаем об этом вызывающему коду.
            return TimeZoneInfo.Utc;
        }
    }

    /// <summary>Локальные дата и время билборда для момента времени.</summary>
    public static DateTime Local(DateTimeOffset moment, string? timeZoneId) =>
        TimeZoneInfo.ConvertTime(moment, Resolve(timeZoneId)).DateTime;
}
