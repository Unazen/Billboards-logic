namespace Billboard.Scheduling;

/// <summary>Техническое состояние билборда.</summary>
public enum BillboardStatus
{
    Online = 0,
    Offline = 1,
    Error = 2,
    Disabled = 3,
}

/// <summary>Состояние рекламного размещения (см. ТЗ, раздел 2.3).</summary>
public enum PlacementStatus
{
    Draft = 0,
    PendingPayment = 1,
    Scheduled = 2,
    Downloading = 3,
    Ready = 4,
    Active = 5,
    Paused = 6,
    Completed = 7,
    Cancelled = 8,
    Failed = 9,
}

/// <summary>Результат одного показа на устройстве.</summary>
public enum PlaybackResult
{
    Success = 0,
    Failed = 1,
    Skipped = 2,
}

/// <summary>Правила, по которым статус размещения влияет на расписание.</summary>
public static class PlacementStatusRules
{
    /// <summary>
    /// Занимает ли размещение рекламную ёмкость. Черновик, неоплаченное,
    /// приостановленное и завершённое размещение ёмкость не занимают.
    /// </summary>
    public static bool OccupiesCapacity(this PlacementStatus status) => status is
        PlacementStatus.Scheduled or
        PlacementStatus.Downloading or
        PlacementStatus.Ready or
        PlacementStatus.Active;

    /// <summary>Можно ли ещё менять расписание.</summary>
    public static bool CanEditSchedule(this PlacementStatus status) => status is
        PlacementStatus.Draft or
        PlacementStatus.PendingPayment or
        PlacementStatus.Scheduled or
        PlacementStatus.Paused;

    public static bool IsTerminal(this PlacementStatus status) => status is
        PlacementStatus.Completed or
        PlacementStatus.Cancelled;

    public static bool IsPublished(this PlacementStatus status) => status is
        PlacementStatus.Scheduled or
        PlacementStatus.Downloading or
        PlacementStatus.Ready or
        PlacementStatus.Active or
        PlacementStatus.Paused;
}
