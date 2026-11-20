using System.Globalization;
using System.Text.Json;
using Billboard.Scheduling;

namespace Billboard.Simulator.Api;

// DTO-слой P3. Имена и формы повторяют Billboard.Application.Contracts,
// чтобы симулятор говорил с реальным backend без расхождений.
internal sealed record P3Manifest(
    Guid BillboardId,
    long Version,
    DateTimeOffset GeneratedAt,
    long CacheLimitBytes,
    long CacheUsedBytes,
    IReadOnlyList<P3ManifestFile>? Files);

internal sealed record P3ManifestFile(
    Guid VideoAssetId,
    string? FileName,
    long SizeBytes,
    decimal? DurationSeconds,
    string? Checksum,
    string? DownloadUrl,
    DateTimeOffset UrlExpiresAt,
    Guid? PlacementId = null,
    int? MinimumPlaysPerHour = null);

internal sealed record P3Heartbeat(string? SimulatorVersion, string? Status);

internal sealed record P3PlaybackEvent(
    string IdempotencyKey,
    Guid PlacementId,
    Guid VideoAssetId,
    DateTimeOffset StartedAt,
    int DurationSeconds,
    string Result);

internal sealed record P3PlaybackBatch(IReadOnlyList<P3PlaybackEvent> Events);

internal sealed record P3PlaybackResult(
    int Accepted,
    int Duplicates,
    int Rejected,
    IReadOnlyList<string>? Errors);

internal sealed record P3CacheReport(IReadOnlyList<P3CacheItem> Items);

internal sealed record P3CacheItem(
    Guid VideoAssetId,
    long SizeBytes,
    string DownloadStatus,
    bool IntegrityVerified);

/// <summary>Преобразование между внутренними моделями P2 и контрактами P3.</summary>
internal static class P3Mapper
{
    public static PlaybackManifest ToInternal(P3Manifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        var items = (manifest.Files ?? Array.Empty<P3ManifestFile>())
            .Where(file => file.VideoAssetId != Guid.Empty)
            .Select(file => new ManifestItem
            {
                PlacementId = file.PlacementId ?? Guid.Empty,
                VideoId = file.VideoAssetId,
                FileName = string.IsNullOrWhiteSpace(file.FileName) ? "video.mp4" : file.FileName,
                DownloadUrl = file.DownloadUrl ?? string.Empty,
                SizeBytes = file.SizeBytes,
                DurationSeconds = file.DurationSeconds is null
                    ? 0
                    : (int)Math.Round(file.DurationSeconds.Value),
                ChecksumSha256 = file.Checksum,
                PlaysPerHour = file.MinimumPlaysPerHour is > 0 ? file.MinimumPlaysPerHour.Value : 1,
                DownloadUrlExpiresAt = file.UrlExpiresAt,
            })
            .ToArray();

        return new PlaybackManifest
        {
            BillboardId = manifest.BillboardId,
            Version = manifest.Version.ToString(CultureInfo.InvariantCulture),
            GeneratedAt = manifest.GeneratedAt,
            CacheLimitBytes = manifest.CacheLimitBytes,
            Items = items,
        };
    }

    public static P3Heartbeat ToP3(HeartbeatRequest heartbeat) =>
        new(SimulatorOptions.Version, heartbeat.Status.ToString());

    public static P3PlaybackEvent ToP3(PlaybackEvent playbackEvent) => new(
        string.IsNullOrWhiteSpace(playbackEvent.IdempotencyKey)
            ? playbackEvent.EventId.ToString("N")
            : playbackEvent.IdempotencyKey,
        playbackEvent.PlacementId,
        playbackEvent.VideoId,
        playbackEvent.StartedAt,
        playbackEvent.DurationSeconds,
        ToP3(playbackEvent.Result));

    public static P3CacheReport ToP3(CacheReport report) => new(
        report.Items.Select(item => new P3CacheItem(
            item.VideoId,
            item.SizeBytes,
            ToP3(item.State),
            item.State == CacheItemState.Ready)).ToArray());

    public static string ToP3(PlaybackResult result) => result switch
    {
        PlaybackResult.Success => "Completed",
        PlaybackResult.Failed => "Error",
        PlaybackResult.Skipped => "Skipped",
        _ => "Error",
    };

    public static string ToP3(CacheItemState state) => state switch
    {
        CacheItemState.Ready => "Ready",
        CacheItemState.Missing => "Missing",
        CacheItemState.Downloading => "Downloading",
        CacheItemState.Failed => "Failed",
        _ => "Failed",
    };

    public static bool TryParseManifest(string json, out PlaybackManifest? manifest)
    {
        manifest = null;

        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            if (document.RootElement.TryGetProperty("files", out _))
            {
                var p3 = JsonSerializer.Deserialize<P3Manifest>(json, SimulatorJson.Options);
                if (p3 is null)
                {
                    return false;
                }

                manifest = ToInternal(p3);
                return true;
            }

            manifest = JsonSerializer.Deserialize<PlaybackManifest>(json, SimulatorJson.Options);
            return manifest is not null;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
