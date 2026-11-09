namespace Billboard.Scheduling;

/// <summary>Сигнал жизнеспособности устройства.</summary>
public sealed record HeartbeatRequest
{
    public Guid BillboardId { get; init; }

    public DateTimeOffset ReportedAt { get; init; } = DateTimeOffset.UtcNow;

    public BillboardStatus Status { get; init; } = BillboardStatus.Online;

    public string SimulatorVersion { get; init; } = "1.0.0";

    public long CacheUsedBytes { get; init; }

    public long CacheCapacityBytes { get; init; } = Billboard.DefaultCapacityBytes;

    public int CachedVideoCount { get; init; }

    public int ActivePlacementCount { get; init; }
}

public sealed record HeartbeatResponse
{
    public DateTimeOffset ServerTime { get; init; } = DateTimeOffset.UtcNow;

    public string? ManifestVersion { get; init; }

    public bool ShouldRefreshManifest { get; init; }
}

/// <summary>Событие подтверждённого показа. IdempotencyKey защищает от двойного списания.</summary>
public sealed record PlaybackEvent
{
    public Guid EventId { get; init; } = Guid.NewGuid();

    /// <summary>Уникальный ключ события; по умолчанию совпадает с EventId.</summary>
    public string IdempotencyKey { get; init; } = string.Empty;

    public Guid BillboardId { get; init; }

    public Guid PlacementId { get; init; }

    public Guid VideoId { get; init; }

    public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.UtcNow;

    public int DurationSeconds { get; init; }

    public PlaybackResult Result { get; init; } = PlaybackResult.Success;

    public string? Error { get; init; }

    public static PlaybackEvent Create(
        Guid billboardId,
        ManifestItem item,
        DateTimeOffset startedAt,
        PlaybackResult result = PlaybackResult.Success,
        string? error = null)
    {
        ArgumentNullException.ThrowIfNull(item);

        var eventId = Guid.NewGuid();
        return new PlaybackEvent
        {
            EventId = eventId,
            IdempotencyKey = eventId.ToString("N"),
            BillboardId = billboardId,
            PlacementId = item.PlacementId,
            VideoId = item.VideoId,
            StartedAt = startedAt,
            DurationSeconds = item.DurationSeconds,
            Result = result,
            Error = error,
        };
    }
}

public sealed record PlaybackEventBatch
{
    private readonly IReadOnlyList<PlaybackEvent> _events = Array.Empty<PlaybackEvent>();

    public IReadOnlyList<PlaybackEvent> Events
    {
        get => _events;
        init => _events = value ?? Array.Empty<PlaybackEvent>();
    }
}

public sealed record PlaybackEventBatchResponse
{
    private readonly IReadOnlyList<Guid> _acceptedEventIds = Array.Empty<Guid>();
    private readonly IReadOnlyList<Guid> _duplicateEventIds = Array.Empty<Guid>();
    private readonly IReadOnlyList<PlaybackEventRejection> _rejected = Array.Empty<PlaybackEventRejection>();

    public IReadOnlyList<Guid> AcceptedEventIds
    {
        get => _acceptedEventIds;
        init => _acceptedEventIds = value ?? Array.Empty<Guid>();
    }

    public IReadOnlyList<Guid> DuplicateEventIds
    {
        get => _duplicateEventIds;
        init => _duplicateEventIds = value ?? Array.Empty<Guid>();
    }

    public IReadOnlyList<PlaybackEventRejection> Rejected
    {
        get => _rejected;
        init => _rejected = value ?? Array.Empty<PlaybackEventRejection>();
    }
}

public sealed record PlaybackEventRejection
{
    public Guid EventId { get; init; }

    public string Reason { get; init; } = string.Empty;
}

public enum CacheItemState
{
    Ready = 0,
    Missing = 1,
    Downloading = 2,
    Failed = 3,
}

/// <summary>Отчёт устройства о локальном кэше.</summary>
public sealed record CacheReport
{
    public Guid BillboardId { get; init; }

    public DateTimeOffset ReportedAt { get; init; } = DateTimeOffset.UtcNow;

    private readonly IReadOnlyList<CacheReportItem> _items = Array.Empty<CacheReportItem>();

    public IReadOnlyList<CacheReportItem> Items
    {
        get => _items;
        init => _items = value ?? Array.Empty<CacheReportItem>();
    }
}

public sealed record CacheReportItem
{
    public Guid VideoId { get; init; }

    public long SizeBytes { get; init; }

    public string? ChecksumSha256 { get; init; }

    public CacheItemState State { get; init; } = CacheItemState.Ready;
}
