namespace Billboard.Scheduling;

/// <summary>
/// Манифест устройства: только те ролики, которые нужны билборду сейчас.
/// Симулятор не скачивает всю медиатеку.
/// </summary>
public sealed record PlaybackManifest
{
    public Guid BillboardId { get; init; }

    public string Version { get; init; } = string.Empty;

    public DateTimeOffset GeneratedAt { get; init; } = DateTimeOffset.UtcNow;

    public long CacheLimitBytes { get; init; } = Billboard.DefaultCapacityBytes;

    private readonly IReadOnlyList<ManifestItem> _items = Array.Empty<ManifestItem>();

    public IReadOnlyList<ManifestItem> Items
    {
        get => _items;
        init => _items = value ?? Array.Empty<ManifestItem>();
    }

    public ManifestItem? FindItem(Guid videoId) =>
        Items.FirstOrDefault(item => item.VideoId == videoId);
}

/// <summary>Один файл в манифесте.</summary>
public sealed record ManifestItem
{
    public Guid PlacementId { get; init; }

    public Guid VideoId { get; init; }

    /// <summary>Имя для логов и UI; на диске файл хранится по VideoId.</summary>
    public string FileName { get; init; } = string.Empty;

    public string DownloadUrl { get; init; } = string.Empty;

    public long SizeBytes { get; init; }

    public int DurationSeconds { get; init; }

    public string? ChecksumSha256 { get; init; }

    public int PlaysPerHour { get; init; } = 1;

    public DateTimeOffset? ActiveFrom { get; init; }

    public DateTimeOffset? ActiveUntil { get; init; }

    /// <summary>Срок жизни временной ссылки P3; после него нужен новый manifest.</summary>
    public DateTimeOffset? DownloadUrlExpiresAt { get; init; }

    /// <summary>P3 отдаёт placementId только в расширенном manifest; без него событие не отправить.</summary>
    public bool HasPlacement => PlacementId != Guid.Empty;

    public bool IsActiveAt(DateTimeOffset now) =>
        (!ActiveFrom.HasValue || now >= ActiveFrom.Value) &&
        (!ActiveUntil.HasValue || now < ActiveUntil.Value);
}
