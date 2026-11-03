using System.Text.Json.Serialization;

namespace Billboard.Scheduling;

/// <summary>
/// Рекламное размещение. Время хранится в локальном времени билборда:
/// <see cref="StartDate"/> и <see cref="EndDate"/> — включительно,
/// <see cref="StartTime"/> и <see cref="EndTime"/> — окно внутри дня.
/// Окно, у которого конец меньше начала, переходит через полночь.
/// </summary>
public sealed record Placement
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public Guid OwnerId { get; init; }

    public Guid BillboardId { get; init; }

    public Guid VideoAssetId { get; init; }

    public DateOnly StartDate { get; init; }

    public DateOnly EndDate { get; init; }

    private readonly IReadOnlyList<DayOfWeek> _daysOfWeek = Array.Empty<DayOfWeek>();

    public IReadOnlyList<DayOfWeek> DaysOfWeek
    {
        get => _daysOfWeek;
        init => _daysOfWeek = value ?? Array.Empty<DayOfWeek>();
    }

    public TimeOnly StartTime { get; init; }

    public TimeOnly EndTime { get; init; }

    /// <summary>Минимальное число показов в каждый час действия окна.</summary>
    public int MinimumPlaysPerHour { get; init; }

    public int VideoDurationSeconds { get; init; }

    public PlacementStatus Status { get; init; } = PlacementStatus.Draft;

    /// <summary>Статус до приостановки, чтобы корректно возобновить размещение.</summary>
    public PlacementStatus? StatusBeforePause { get; init; }

    public decimal EstimatedCost { get; init; }

    public decimal ReservedAmount { get; init; }

    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? UpdatedAt { get; init; }

    /// <summary>Версия для оптимистической блокировки при обновлении.</summary>
    public long Version { get; init; } = 1;

    /// <summary>Сколько секунд в час требуется размещению по квоте.</summary>
    [JsonIgnore]
    public int RequiredSecondsPerHour => MinimumPlaysPerHour * VideoDurationSeconds;

    /// <summary>Маска дней недели для хранения в БД (бит 0 — воскресенье).</summary>
    [JsonIgnore]
    public int DaysOfWeekMask =>
        DaysOfWeek.Aggregate(0, (mask, day) => mask | (1 << (int)day));

    [JsonIgnore]
    public IReadOnlyList<DayOfWeek> NormalizedDays =>
        DaysOfWeek.Distinct().OrderBy(day => day).ToArray();

    public Placement WithStatus(PlacementStatus status, DateTimeOffset now) => this with
    {
        Status = status,
        StatusBeforePause = status == PlacementStatus.Paused ? Status : null,
        UpdatedAt = now,
        Version = Version + 1,
    };

    public PlacementRequest ToRequest() => new()
    {
        BillboardId = BillboardId,
        VideoAssetId = VideoAssetId,
        StartDate = StartDate,
        EndDate = EndDate,
        DaysOfWeek = DaysOfWeek,
        StartTime = StartTime,
        EndTime = EndTime,
        MinimumPlaysPerHour = MinimumPlaysPerHour,
        VideoDurationSeconds = VideoDurationSeconds,
        EstimatedCost = EstimatedCost,
    };
}
