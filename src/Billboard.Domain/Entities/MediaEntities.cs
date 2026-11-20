using Billboard.Domain.Enums;

namespace Billboard.Domain.Entities;

public class VideoAsset
{
    public Guid Id { get; set; }
    public Guid OwnerId { get; set; }
    public string OriginalFileName { get; set; } = "";
    public string StorageKey { get; set; } = "";
    public string ContentType { get; set; } = "";
    public long SizeBytes { get; set; }
    public decimal? DurationSeconds { get; set; }
    public VideoStatus Status { get; set; } = VideoStatus.Uploading;
    public string? Checksum { get; set; }
    public string? FailureReason { get; set; }
    public string? SourceUrl { get; set; }
    public bool IsDeleted { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public List<VideoVariant> Variants { get; set; } = new();
}

/// <summary>Обработанная (сжатая) версия оригинала.</summary>
public class VideoVariant
{
    public Guid Id { get; set; }
    public Guid VideoAssetId { get; set; }
    public string Format { get; set; } = "mp4";
    public long SizeBytes { get; set; }
    public decimal? DurationSeconds { get; set; }
    public string StorageKey { get; set; } = "";
    public string? Checksum { get; set; }
    public VideoStatus Status { get; set; } = VideoStatus.Processing;
    public VideoAsset? VideoAsset { get; set; }
}

public class UploadJob
{
    public Guid Id { get; set; }
    public Guid VideoAssetId { get; set; }
    public Guid OwnerId { get; set; }
    public UploadJobStatus Status { get; set; } = UploadJobStatus.Pending;
    public int ProgressPercent { get; set; }
    public string? Error { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CompletedAt { get; set; }
}
