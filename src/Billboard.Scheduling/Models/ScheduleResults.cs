namespace Billboard.Scheduling;

/// <summary>Стабильные коды конфликтов для API и тестов.</summary>
public static class ConflictCodes
{
    public const string InvalidDateRange = "INVALID_DATE_RANGE";
    public const string DateRangeTooLong = "DATE_RANGE_TOO_LONG";
    public const string EmptyDaysOfWeek = "EMPTY_DAYS_OF_WEEK";
    public const string InvalidTimeWindow = "INVALID_TIME_WINDOW";
    public const string InvalidDuration = "INVALID_DURATION";
    public const string InvalidPlaysPerHour = "INVALID_PLAYS_PER_HOUR";
    public const string InvalidCost = "INVALID_COST";
    public const string InvalidBillboardCapacity = "INVALID_BILLBOARD_CAPACITY";
    public const string BillboardNotFound = "BILLBOARD_NOT_FOUND";
    public const string BillboardOffline = "BILLBOARD_OFFLINE";
    public const string QuotaExceedsCapacity = "QUOTA_EXCEEDS_CAPACITY";
    public const string InsufficientAdvertisingCapacity = "INSUFFICIENT_ADVERTISING_CAPACITY";
    public const string InsufficientBudget = "INSUFFICIENT_BUDGET";
}

/// <summary>Один конфликт расписания с машиночитаемым кодом и деталями.</summary>
public sealed record ScheduleConflict
{
    public required string Code { get; init; }

    public required string Message { get; init; }

    public Guid? BillboardId { get; init; }

    /// <summary>Час суток (0..23), к которому относится конфликт ёмкости.</summary>
    public int? Hour { get; init; }

    public int? RequiredSeconds { get; init; }

    public int? AvailableSeconds { get; init; }

    /// <summary>Даты, в которые обнаружен конфликт (может быть много, поэтому список ограничен).</summary>
    public IReadOnlyList<DateOnly> Dates { get; init; } = Array.Empty<DateOnly>();

    /// <summary>Какие существующие размещения занимают ёмкость в проблемном часе.</summary>
    public IReadOnlyList<Guid> BlockingPlacementIds { get; init; } = Array.Empty<Guid>();
}

/// <summary>Детализация по одному часовому слоту.</summary>
public sealed record CapacityBucket
{
    public DateOnly Date { get; init; }

    public int Hour { get; init; }

    public int RequiredSeconds { get; init; }

    public int CapacitySeconds { get; init; }

    public int OverflowSeconds => Math.Max(0, RequiredSeconds - CapacitySeconds);
}

/// <summary>Сводка по рекламной ёмкости для проверяемого размещения.</summary>
public sealed record CapacitySummary
{
    public int CapacitySecondsPerHour { get; init; }

    public int CandidateSecondsPerHour { get; init; }

    /// <summary>Максимум занятой ёмкости в час с учётом кандидата.</summary>
    public int PeakRequiredSecondsPerHour { get; init; }

    /// <summary>Максимум занятой ёмкости в час без кандидата.</summary>
    public int PeakExistingSecondsPerHour { get; init; }

    public int ApplicableHourBuckets { get; init; }

    public int OverflowBuckets { get; init; }

    public double PeakUtilizationPercent => CapacitySecondsPerHour <= 0
        ? 0
        : Math.Round(PeakRequiredSecondsPerHour * 100.0 / CapacitySecondsPerHour, 1);

    public int FreeSecondsPerHour => CapacitySecondsPerHour - PeakRequiredSecondsPerHour;

    public IReadOnlyList<CapacityBucket> OverflowDetails { get; init; } = Array.Empty<CapacityBucket>();
}

/// <summary>Вариант свободного времени, который можно предложить вместо конфликтного.</summary>
public sealed record AlternativeSlot
{
    public DateOnly StartDate { get; init; }

    public DateOnly EndDate { get; init; }

    public IReadOnlyList<DayOfWeek> DaysOfWeek { get; init; } = Array.Empty<DayOfWeek>();

    public TimeOnly StartTime { get; init; }

    public TimeOnly EndTime { get; init; }

    /// <summary>Минимальный свободный остаток ёмкости в час после добавления варианта.</summary>
    public int FreeSecondsPerHour { get; init; }

    public double PeakUtilizationPercent { get; init; }
}

/// <summary>Результат проверки одного размещения.</summary>
public sealed record ScheduleValidationResult
{
    public bool IsAvailable => Conflicts.Count == 0;

    public IReadOnlyList<ScheduleConflict> Conflicts { get; init; } = Array.Empty<ScheduleConflict>();

    public IReadOnlyList<AlternativeSlot> Alternatives { get; init; } = Array.Empty<AlternativeSlot>();

    public CapacitySummary Summary { get; init; } = new();

    public bool HasConflict(string code) => Conflicts.Any(conflict => conflict.Code == code);
}

/// <summary>Результат проверки группы: публикация возможна только если прошли все устройства.</summary>
public sealed record GroupScheduleValidationResult
{
    public bool CanPublish => Results.Count > 0 && Results.All(item => item.Validation.IsAvailable);

    public int RequestedBillboardCount => Results.Count;

    public int AvailableBillboardCount => Results.Count(item => item.Validation.IsAvailable);

    public IReadOnlyList<BillboardValidation> Results { get; init; } = Array.Empty<BillboardValidation>();
}

public sealed record BillboardValidation
{
    public Guid BillboardId { get; init; }

    public string BillboardName { get; init; } = string.Empty;

    public ScheduleValidationResult Validation { get; init; } = new();
}

/// <summary>Параметры проверки и поиска альтернатив.</summary>
public sealed record ScheduleValidationOptions
{
    public static ScheduleValidationOptions Default { get; } = new();

    public bool SuggestAlternatives { get; init; } = true;

    public int MaxAlternatives { get; init; } = 5;

    /// <summary>Шаг сетки поиска альтернативного времени в минутах.</summary>
    public int AlternativeStepMinutes { get; init; } = 30;

    /// <summary>Сколько недель вперёд искать свободное окно.</summary>
    public int AlternativeWeeksAhead { get; init; } = 2;

    /// <summary>Максимальная длительность расписания, чтобы проверка не шла по годам.</summary>
    public int MaxHorizonDays { get; init; } = 366;

    public bool CheckBudget { get; init; } = true;
}
