using Billboard.Scheduling;

namespace Billboard.Simulator.Cache;

/// <summary>Запись о файле в локальном кэше.</summary>
public sealed record CacheEntry
{
    public Guid VideoId { get; init; }

    public string RelativePath { get; init; } = string.Empty;

    public long SizeBytes { get; init; }

    public string? ChecksumSha256 { get; init; }

    public DateTimeOffset LastUsedUtc { get; init; }

    public bool Verified { get; init; }

    public Guid? PlacementId { get; init; }

    public string? DisplayName { get; init; }
}

public sealed record CacheResult
{
    public bool Success { get; init; }

    public CacheEntry? Entry { get; init; }

    public string? ErrorCode { get; init; }

    public string? Message { get; init; }

    public static CacheResult Ok(CacheEntry entry) => new() { Success = true, Entry = entry };

    public static CacheResult Fail(string errorCode, string message) => new()
    {
        Success = false,
        ErrorCode = errorCode,
        Message = message,
    };
}

public static class CacheErrorCodes
{
    public const string InvalidItem = "CACHE_INVALID_ITEM";
    public const string CapacityExceeded = "CACHE_CAPACITY_EXCEEDED";
    public const string DownloadFailed = "CACHE_DOWNLOAD_FAILED";
    public const string ChecksumMismatch = "CACHE_CHECKSUM_MISMATCH";
    public const string SizeMismatch = "CACHE_SIZE_MISMATCH";
}

/// <summary>Загрузчик видео. Позволяет подменить HTTP на файл или демо-генератор.</summary>
public interface IVideoDownloader
{
    Task DownloadAsync(ManifestItem item, Stream destination, CancellationToken cancellationToken = default);
}
