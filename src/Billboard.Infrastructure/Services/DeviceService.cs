using Billboard.Application.Common;
using Billboard.Application.Contracts;
using Billboard.Application.Scheduling;
using Billboard.Domain.Entities;
using Billboard.Domain.Enums;
using Billboard.Infrastructure.Media;
using Billboard.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Billboard.Infrastructure.Services;

/// <summary>
/// Серверная часть API симулятора: манифест (что скачивать), отчёт о локальном кэше,
/// конфигурация устройства. Ссылки на файлы — временные presigned URL из хранилища.
/// </summary>
public sealed class DeviceService(AppDbContext db, IVideoService videos, IOptions<MediaOptions> mediaOptions) : IDeviceService
{
    /// <summary>Размещения, которые устройство обязано показывать.</summary>
    private static readonly PlacementStatus[] PlayingStatuses =
    {
        PlacementStatus.Scheduled, PlacementStatus.Downloading, PlacementStatus.Ready, PlacementStatus.Active
    };

    private readonly MediaOptions _media = mediaOptions.Value;

    public async Task<DeviceManifestDto> GetManifestAsync(Guid billboardId, CancellationToken ct = default)
    {
        var billboard = await db.Billboards.AsNoTracking().FirstOrDefaultAsync(b => b.Id == billboardId, ct)
                        ?? throw AppException.NotFound("Билборд");

        var now = DateTimeOffset.UtcNow;
        var localNow = TimeZoneInfo.ConvertTime(now, TimeZones.Resolve(billboard.TimeZoneId));

        var candidates = await db.AdPlacements.AsNoTracking()
            .Include(p => p.VideoAsset)
            .Where(p => p.BillboardId == billboardId && PlayingStatuses.Contains(p.Status))
            .ToListAsync(ct);

        // Сравнение времени — в памяти: SQLite хранит DateTimeOffset в двоичном виде,
        // и сортировка/сравнение в SQL были бы неверными. В PostgreSQL фильтр всё равно дешёвый.
        var active = candidates
            .Where(p => p.StartAt <= now && now < p.EndAt && IsWithinSchedule(p, localNow))
            .ToList();

        var files = new List<ManifestFileDto>(active.Count);
        var seen = new HashSet<Guid>();
        var expiresAt = now.AddMinutes(Math.Max(1, _media.DownloadUrlTtlMinutes));

        foreach (var placement in active)
        {
            if (placement.VideoAsset is not { Status: VideoStatus.Ready }) continue;
            if (!seen.Add(placement.VideoAssetId)) continue;

            try
            {
                var source = await videos.GetPlaybackSourceAsync(placement.VideoAssetId, ct);
                files.Add(new ManifestFileDto(
                    placement.VideoAssetId,
                    placement.VideoAsset.OriginalFileName,
                    source.SizeBytes,
                    placement.VideoAsset.DurationSeconds,
                    source.Checksum,
                    source.Url,
                    expiresAt));
            }
            catch (AppException)
            {
                // Видео удалили или оно ещё не готово: остальные файлы манифеста это не ломает.
            }
        }

        var cacheUsed = await db.DeviceCachedVideos.AsNoTracking()
            .Where(c => c.BillboardId == billboardId)
            .SumAsync(c => (long?)c.SizeBytes, ct) ?? 0L;

        return new DeviceManifestDto(billboard.Id, ComputeVersion(active), now,
            billboard.CapacityBytes, cacheUsed, files);
    }

    public async Task<CacheReportResultDto> ReportCacheAsync(
        Guid billboardId, IReadOnlyList<CacheReportItemDto> items, CancellationToken ct = default)
    {
        var billboard = await db.Billboards.FirstOrDefaultAsync(b => b.Id == billboardId, ct)
                        ?? throw AppException.NotFound("Билборд");

        var videoIds = items.Select(i => i.VideoAssetId).Distinct().ToList();
        var existingVideos = await db.VideoAssets.AsNoTracking()
            .Where(v => videoIds.Contains(v.Id))
            .Select(v => v.Id)
            .ToListAsync(ct);

        var unknown = videoIds.Except(existingVideos).ToList();
        if (unknown.Count > 0)
            throw AppException.Validation("UNKNOWN_VIDEO",
                "Отчёт содержит видео, которых нет в медиатеке.", new { videoIds = unknown });

        var cached = await db.DeviceCachedVideos
            .Where(c => c.BillboardId == billboardId && videoIds.Contains(c.VideoAssetId))
            .ToDictionaryAsync(c => c.VideoAssetId, ct);

        var now = DateTimeOffset.UtcNow;
        foreach (var item in items)
        {
            if (cached.TryGetValue(item.VideoAssetId, out var row))
            {
                row.SizeBytes = item.SizeBytes;
                row.DownloadStatus = item.DownloadStatus;
                row.IntegrityVerified = item.IntegrityVerified;
                row.LastUsedAt = now;
            }
            else
            {
                db.DeviceCachedVideos.Add(new DeviceCachedVideo
                {
                    Id = Guid.NewGuid(),
                    BillboardId = billboardId,
                    VideoAssetId = item.VideoAssetId,
                    SizeBytes = item.SizeBytes,
                    DownloadStatus = item.DownloadStatus,
                    IntegrityVerified = item.IntegrityVerified,
                    LastUsedAt = now
                });
            }
        }

        await db.SaveChangesAsync(ct);

        var used = await db.DeviceCachedVideos.AsNoTracking()
            .Where(c => c.BillboardId == billboardId)
            .SumAsync(c => (long?)c.SizeBytes, ct) ?? 0L;

        return new CacheReportResultDto(used, billboard.CapacityBytes, items.Count);
    }

    public async Task<DeviceConfigurationDto> GetConfigurationAsync(Guid billboardId, CancellationToken ct = default)
    {
        var billboard = await db.Billboards.AsNoTracking().FirstOrDefaultAsync(b => b.Id == billboardId, ct)
                        ?? throw AppException.NotFound("Билборд");

        return new DeviceConfigurationDto(billboard.Id, billboard.Name, billboard.CapacityBytes,
            billboard.AdvertCapacitySecondsPerHour, billboard.TimeZoneId, billboard.Status.ToString(),
            billboard.LastSeenAt);
    }

    // ---------- вспомогательные ----------

    /// <summary>Попадает ли текущий локальный момент в окно размещения (дни недели + часы).</summary>
    public static bool IsWithinSchedule(AdPlacement placement, DateTimeOffset localNow)
    {
        var time = localNow.TimeOfDay;
        var start = placement.StartTimeOfDay.ToTimeSpan();
        var end = placement.EndTimeOfDay.ToTimeSpan();

        var overnight = end <= start;
        var matchesDay = overnight && time < end
            ? WeekMask.Includes(placement.DaysOfWeekMask, localNow.AddDays(-1).DayOfWeek)
            : WeekMask.Includes(placement.DaysOfWeekMask, localNow.DayOfWeek);

        if (!matchesDay) return false;
        return overnight ? (time >= start || time < end) : (time >= start && time < end);
    }

    /// <summary>Версия манифеста: меняется при любом изменении набора размещений.</summary>
    public static long ComputeVersion(IEnumerable<AdPlacement> placements)
    {
        ulong hash = 14695981039346656037UL;
        foreach (var p in placements.OrderBy(p => p.Id))
        {
            foreach (var b in p.Id.ToByteArray())
            {
                hash ^= b;
                hash *= 1099511628211UL;
            }
            foreach (var b in BitConverter.GetBytes(p.CreatedAt.UtcTicks))
            {
                hash ^= b;
                hash *= 1099511628211UL;
            }
        }
        return (long)(hash & 0x7FFF_FFFF_FFFF_FFFFUL);
    }
}
