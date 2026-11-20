namespace Billboard.Scheduling;

/// <summary>Запрос на создание или изменение размещения (без идентификаторов и статуса).</summary>
public sealed record PlacementRequest
{
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

    public int MinimumPlaysPerHour { get; init; }

    public int VideoDurationSeconds { get; init; }

    /// <summary>Предварительная стоимость размещения (для проверки бюджета).</summary>
    public decimal EstimatedCost { get; init; }

    /// <summary>
    /// Доступный баланс клиента. Передаёт финансовый модуль (участник 3).
    /// Если не задан, проверка бюджета не выполняется.
    /// </summary>
    public decimal? AvailableBudget { get; init; }

    public Placement ToPlacement(Guid ownerId, PlacementStatus status = PlacementStatus.Draft) => new()
    {
        OwnerId = ownerId,
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
        Status = status,
    };
}

/// <summary>Группа билбордов: расписание назначается всем устройствам атомарно.</summary>
public sealed record PlacementGroup
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public string Name { get; init; } = string.Empty;

    private readonly IReadOnlyList<Guid> _billboardIds = Array.Empty<Guid>();

    public IReadOnlyList<Guid> BillboardIds
    {
        get => _billboardIds;
        init => _billboardIds = value ?? Array.Empty<Guid>();
    }
}

/// <summary>Запрос на назначение одного расписания группе билбордов.</summary>
public sealed record PlacementGroupRequest
{
    private readonly IReadOnlyList<Guid> _billboardIds = Array.Empty<Guid>();

    public IReadOnlyList<Guid> BillboardIds
    {
        get => _billboardIds;
        init => _billboardIds = value ?? Array.Empty<Guid>();
    }

    /// <summary>Шаблон расписания; <see cref="PlacementRequest.BillboardId"/> игнорируется.</summary>
    public PlacementRequest Template { get; init; } = new();
}
