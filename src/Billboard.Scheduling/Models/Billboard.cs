namespace Billboard.Scheduling;

/// <summary>
/// Билборд в той части, которая нужна планировщику. Полная сущность с адресом,
/// координатами и владельцем живёт в доменном слое (участник 3).
/// </summary>
public sealed record Billboard
{
    /// <summary>Лимит локального кэша по умолчанию — 32 ГБ (требование ТЗ).</summary>
    public const long DefaultCapacityBytes = 32L * 1024 * 1024 * 1024;

    /// <summary>Полная рекламная ёмкость одного часа в секундах.</summary>
    public const int FullHourSeconds = 3600;

    public Guid Id { get; init; }

    public string Name { get; init; } = string.Empty;

    public BillboardStatus Status { get; init; } = BillboardStatus.Online;

    /// <summary>Сколько секунд рекламы билборд может показать за один час.</summary>
    public int AdvertCapacitySecondsPerHour { get; init; } = FullHourSeconds;

    /// <summary>Ограничение локального кэша устройства в байтах.</summary>
    public long CapacityBytes { get; init; } = DefaultCapacityBytes;

    /// <summary>
    /// Часовой пояс билборда. Расписание хранится в локальном времени билборда;
    /// конвертация в UTC выполняется на границе API.
    /// </summary>
    public string TimeZoneId { get; init; } = "UTC";

    public DateTimeOffset? LastSeenAt { get; init; }

    public bool IsOnline => Status == BillboardStatus.Online;

    public bool HasValidCapacity => AdvertCapacitySecondsPerHour is > 0 and <= FullHourSeconds;
}
