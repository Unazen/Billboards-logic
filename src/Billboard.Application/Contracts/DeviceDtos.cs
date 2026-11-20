using System.ComponentModel.DataAnnotations;

namespace Billboard.Application.Contracts;

/// <summary>
/// Файл, который устройство должно скачать. Ссылка временная (presigned URL).
/// Запись привязана к размещению: один и тот же ролик в двух размещениях даёт две записи,
/// иначе симулятор не сможет отправить события показов и соблюсти квоту.
/// </summary>
public record ManifestFileDto(
    Guid VideoAssetId,
    Guid PlacementId,
    int MinimumPlaysPerHour,
    string FileName,
    long SizeBytes,
    decimal? DurationSeconds,
    string? Checksum,
    Uri DownloadUrl,
    DateTimeOffset UrlExpiresAt);

/// <summary>Манифест устройства: что и откуда скачивать.</summary>
public record DeviceManifestDto(
    Guid BillboardId,
    long Version,
    DateTimeOffset GeneratedAt,
    long CacheLimitBytes,
    long CacheUsedBytes,
    IReadOnlyList<ManifestFileDto> Files);

public record CacheReportItemDto(
    Guid VideoAssetId,
    [Range(0, long.MaxValue)] long SizeBytes,
    [Required, StringLength(30)] string DownloadStatus,
    bool IntegrityVerified);

public record CacheReportRequest(
    [Required, MinLength(1), MaxLength(500)] List<CacheReportItemDto> Items);

public record CacheReportResultDto(long CacheUsedBytes, long CacheLimitBytes, int Files);

public record DeviceConfigurationDto(
    Guid BillboardId,
    string Name,
    long CacheLimitBytes,
    int AdvertCapacitySecondsPerHour,
    string TimeZoneId,
    string Status,
    DateTimeOffset? LastSeenAt);
