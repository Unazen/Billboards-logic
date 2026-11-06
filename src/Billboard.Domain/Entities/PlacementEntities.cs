using Billboard.Domain.Enums;

namespace Billboard.Domain.Entities;

/// <summary>
/// Рекламное размещение. Владелец логики расписаний — участник 2;
/// сущность здесь нужна, чтобы БД, финансы и учёт показов ссылались на неё.
/// </summary>
public class AdPlacement
{
    public Guid Id { get; set; }
    public Guid OwnerId { get; set; }
    public Guid VideoAssetId { get; set; }
    public Guid BillboardId { get; set; }
    public DateTimeOffset StartAt { get; set; }
    public DateTimeOffset EndAt { get; set; }

    /// <summary>Битовая маска <see cref="WeekDays"/>.</summary>
    public int DaysOfWeekMask { get; set; }
    public TimeOnly StartTimeOfDay { get; set; }
    public TimeOnly EndTimeOfDay { get; set; }
    public int MinimumPlaysPerHour { get; set; }
    public PlacementStatus Status { get; set; } = PlacementStatus.Draft;
    public decimal ReservedAmount { get; set; }

    /// <summary>Причина паузы, например "BudgetExhausted".</summary>
    public string? PauseReason { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public VideoAsset? VideoAsset { get; set; }
    public BillboardDevice? Billboard { get; set; }
}

public class PlaybackEvent
{
    public Guid Id { get; set; }
    public Guid BillboardId { get; set; }
    public Guid PlacementId { get; set; }
    public Guid VideoAssetId { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public int DurationSeconds { get; set; }

    /// <summary>Уникальный ключ: повторная отправка не создаёт второй показ.</summary>
    public string IdempotencyKey { get; set; } = "";
    public PlaybackResult Result { get; set; }

    /// <summary>Сколько списано за этот показ (0, если не списывалось).</summary>
    public decimal ChargedAmount { get; set; }
    public DateTimeOffset ReceivedAt { get; set; } = DateTimeOffset.UtcNow;
}
