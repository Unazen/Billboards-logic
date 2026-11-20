namespace Billboard.Scheduling;

/// <summary>Один показ в пределах часового плана.</summary>
public sealed record PlaybackSlot
{
    public int Sequence { get; init; }

    public Guid PlacementId { get; init; }

    public Guid VideoAssetId { get; init; }

    public Guid BillboardId { get; init; }

    public int DurationSeconds { get; init; }

    /// <summary>Идеальная позиция по равномерному распределению (для диагностики).</summary>
    public double IdealOffsetSeconds { get; init; }

    /// <summary>Фактическое начало с учётом длительностей предыдущих роликов.</summary>
    public int StartOffsetSeconds { get; init; }
}

/// <summary>План воспроизведения на один календарный час.</summary>
public sealed record PlaybackPlan
{
    public Guid BillboardId { get; init; }

    public DateOnly Date { get; init; }

    public int Hour { get; init; }

    public IReadOnlyList<PlaybackSlot> Slots { get; init; } = Array.Empty<PlaybackSlot>();

    public int CapacitySeconds { get; init; }

    public int TotalDurationSeconds { get; init; }

    public int RequiredPlays { get; init; }

    public int PlannedPlays => Slots.Count;

    public bool IsOverCapacity { get; init; }

    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
}

public static class PlaybackPlanWarnings
{
    public const string CapacityExceeded = "PLAYBACK_CAPACITY_EXCEEDED";
    public const string PlanTruncated = "PLAYBACK_PLAN_TRUNCATED";
}
