using System.Security.Cryptography;
using Billboard.Application.Common;
using Billboard.Application.Contracts;
using Billboard.Application.Media;
using Billboard.Domain.Entities;
using Billboard.Domain.Enums;
using Billboard.Domain.Interfaces;
using Billboard.Infrastructure.Media;
using Billboard.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Billboard.Infrastructure.Services;

public sealed class VideoService(
    AppDbContext db,
    IVideoStorage storage,
    IVideoProbe probe,
    IVideoTranscoder transcoder,
    ITariffService tariffs,
    IUrlFetcher fetcher,
    IOptions<MediaOptions> mediaOptions,
    ILogger<VideoService> log) : IVideoService
{
    /// <summary>Размещения в этих статусах «используют» видео: удалять его нельзя.</summary>
    private static readonly PlacementStatus[] BlockingStatuses =
    {
        PlacementStatus.PendingPayment, PlacementStatus.Scheduled, PlacementStatus.Downloading,
        PlacementStatus.Ready, PlacementStatus.Active, PlacementStatus.Paused
    };

    private readonly MediaOptions _media = mediaOptions.Value;

    // ---------- загрузка ----------

    public async Task<UploadInitDto> CreateUploadAsync(Guid userId, CreateUploadRequest request, CancellationToken ct = default)
    {
        var plan = await tariffs.GetEffectivePlanAsync(userId, ct);
        var count = await db.VideoAssets.CountAsync(v => v.OwnerId == userId, ct);
        VideoPolicy.ValidateUploadRequest(plan, request.FileName, request.SizeBytes, count);

        var asset = NewAsset(userId, request.FileName);
        db.VideoAssets.Add(asset);
        db.UploadJobs.Add(new UploadJob { Id = Guid.NewGuid(), VideoAssetId = asset.Id, OwnerId = userId });
        await db.SaveChangesAsync(ct);

        return new UploadInitDto(asset.Id, asset.Status.ToString(), plan.MaxVideoSizeBytes);
    }

    public async Task<VideoDto> UploadContentAsync(Guid userId, bool isAdmin, Guid videoId, Stream content, CancellationToken ct = default)
    {
        var asset = await FindAsync(userId, isAdmin, videoId, ct);
        if (asset.Status != VideoStatus.Uploading)
            throw AppException.Conflict("INVALID_VIDEO_STATE", $"Видео в статусе {asset.Status}: загрузка содержимого невозможна.");

        var plan = await tariffs.GetEffectivePlanAsync(asset.OwnerId, ct);
        var temp = NewTempPath(Path.GetExtension(asset.OriginalFileName));
        try
        {
            long size; string sha;
            try
            {
                (size, sha) = await BoundedCopy.CopyToFileAsync(content, temp, plan.MaxVideoSizeBytes, ct);
            }
            catch (AppException ex)
            {
                await MarkFailedAsync(asset, ex.Message);
                throw;
            }
            return await FinalizeAsync(asset, plan, temp, size, sha, ct);
        }
        finally
        {
            DeleteQuietly(temp);
        }
    }

    public async Task<VideoDto> ImportFromUrlAsync(Guid userId, string url, CancellationToken ct = default)
    {
        var uri = UrlGuard.ParseAndValidate(url);

        var plan = await tariffs.GetEffectivePlanAsync(userId, ct);
        var count = await db.VideoAssets.CountAsync(v => v.OwnerId == userId, ct);
        if (count >= plan.MaxVideoCount)
            throw AppException.Conflict("VIDEO_LIMIT_REACHED",
                $"Достигнут лимит медиатеки по тарифу «{plan.Name}»: {plan.MaxVideoCount} видео.");

        var temp = NewTempPath(".tmp");
        try
        {
            FetchResult fetched;
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(_media.ImportTimeoutSeconds));
                fetched = await fetcher.DownloadAsync(uri, temp, plan.MaxVideoSizeBytes, timeout.Token);
            }

            // Имя файла берём из ссылки; если расширения нет — определяем по Content-Type.
            var name = fetched.FileName;
            if (string.IsNullOrEmpty(Path.GetExtension(name)))
            {
                var ext = VideoPolicy.ExtensionForContentType(fetched.ContentType)
                          ?? throw AppException.Validation("UNSUPPORTED_FORMAT", "Не удалось определить формат файла по ссылке.");
                name = (string.IsNullOrWhiteSpace(name) ? "video" : name) + ext;
            }
            VideoPolicy.ValidateExtension(name);

            var asset = NewAsset(userId, name);
            asset.SourceUrl = uri.GetLeftPart(UriPartial.Path); // query может содержать секреты, не сохраняем
            db.VideoAssets.Add(asset);
            db.UploadJobs.Add(new UploadJob { Id = Guid.NewGuid(), VideoAssetId = asset.Id, OwnerId = userId });
            await db.SaveChangesAsync(ct);

            return await FinalizeAsync(asset, plan, temp, fetched.SizeBytes, fetched.Sha256, ct);
        }
        finally
        {
            DeleteQuietly(temp);
        }
    }

    /// <summary>Проверка, сохранение в хранилище, проверка целостности и (опционально) сжатие.</summary>
    private async Task<VideoDto> FinalizeAsync(VideoAsset asset, TariffPlan plan, string tempPath, long size, string sha, CancellationToken ct)
    {
        var job = await db.UploadJobs.FirstOrDefaultAsync(j => j.VideoAssetId == asset.Id, ct);
        asset.Status = VideoStatus.Processing;
        if (job is not null) { job.Status = UploadJobStatus.Running; job.ProgressPercent = 20; }
        await db.SaveChangesAsync(ct);

        try
        {
            var meta = await probe.ProbeAsync(tempPath, ct);
            if (meta is null && _media.RequireProbe)
                throw new AppException("PROBE_UNAVAILABLE",
                    "Не удалось проверить длительность видео: на сервере недоступен ffprobe.", 503);

            VideoPolicy.ValidateProbed(plan, size, meta?.DurationSeconds);
            if (job is not null) job.ProgressPercent = 50;

            await using (var input = File.OpenRead(tempPath))
                await storage.PutAsync(asset.StorageKey, input, size, asset.ContentType, ct);

            // Файл считается готовым только после проверки, что в хранилище лежит ровно столько байт.
            var stored = await storage.GetSizeAsync(asset.StorageKey, ct);
            if (stored != size)
                throw new AppException("INTEGRITY_CHECK_FAILED",
                    "Проверка целостности не пройдена: размер файла в хранилище не совпал.", 500);
            if (job is not null) job.ProgressPercent = 80;

            await TryCompressAsync(asset, plan, tempPath, size, meta?.DurationSeconds, ct);

            asset.SizeBytes = size;
            asset.Checksum = sha;
            asset.DurationSeconds = meta?.DurationSeconds;
            asset.Status = VideoStatus.Ready;
            asset.FailureReason = null;
            if (job is not null)
            {
                job.Status = UploadJobStatus.Succeeded;
                job.ProgressPercent = 100;
                job.CompletedAt = DateTimeOffset.UtcNow;
            }
            await db.SaveChangesAsync(ct);
            return Map(asset);
        }
        catch (Exception ex)
        {
            var message = ex is AppException app ? app.Message : "Внутренняя ошибка обработки видео.";
            if (ex is not AppException) log.LogError(ex, "Ошибка обработки видео {VideoId}", asset.Id);

            await MarkFailedAsync(asset, message);
            // Не оставляем в хранилище недопроверенные файлы.
            try { await storage.DeleteAsync(asset.StorageKey, CancellationToken.None); } catch { /* best effort */ }
            throw;
        }
    }

    private async Task TryCompressAsync(VideoAsset asset, TariffPlan plan, string tempPath, long originalSize, decimal? duration, CancellationToken ct)
    {
        if (plan.CompressionLevel == CompressionLevel.None) return;

        var output = NewTempPath(".mp4");
        try
        {
            if (!await transcoder.TranscodeAsync(tempPath, output, plan.CompressionLevel, ct)) return;

            var info = new FileInfo(output);
            // Сжатая копия нужна только если она заметно меньше оригинала.
            if (!info.Exists || info.Length == 0 || info.Length >= originalSize * 0.9) return;

            var variantId = Guid.NewGuid();
            var key = $"videos/{asset.OwnerId:N}/{asset.Id:N}/variant-{variantId:N}.mp4";

            string checksum;
            await using (var read = File.OpenRead(output))
                checksum = Convert.ToHexString(await SHA256.HashDataAsync(read, ct)).ToLowerInvariant();

            await using (var upload = File.OpenRead(output))
                await storage.PutAsync(key, upload, info.Length, "video/mp4", ct);

            if (await storage.GetSizeAsync(key, ct) != info.Length)
            {
                await storage.DeleteAsync(key, ct);
                return;
            }

            db.VideoVariants.Add(new VideoVariant
            {
                Id = variantId,
                VideoAssetId = asset.Id,
                Format = "mp4",
                SizeBytes = info.Length,
                DurationSeconds = duration,
                StorageKey = key,
                Checksum = checksum,
                Status = VideoStatus.Ready
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Сжатие необязательно: оригинал уже сохранён и проверен.
            log.LogWarning(ex, "Сжатие видео {VideoId} не удалось, используется оригинал", asset.Id);
        }
        finally
        {
            DeleteQuietly(output);
        }
    }

    // ---------- чтение ----------

    public async Task<IReadOnlyList<VideoDto>> ListAsync(Guid userId, bool all, CancellationToken ct = default)
    {
        var query = db.VideoAssets.AsNoTracking().AsQueryable();
        if (!all) query = query.Where(v => v.OwnerId == userId);
        var items = await query.ToListAsync(ct);
        return items.OrderByDescending(v => v.CreatedAt).Select(Map).ToList();
    }

    public async Task<VideoDto> GetAsync(Guid userId, bool isAdmin, Guid videoId, CancellationToken ct = default) =>
        Map(await FindAsync(userId, isAdmin, videoId, ct));

    public async Task<Uri> GetDownloadUrlAsync(Guid userId, bool isAdmin, Guid videoId, CancellationToken ct = default)
    {
        var asset = await FindAsync(userId, isAdmin, videoId, ct);
        if (asset.Status != VideoStatus.Ready)
            throw AppException.Conflict("VIDEO_NOT_READY", "Видео ещё не готово.");
        return await storage.GetDownloadUrlAsync(asset.StorageKey, TimeSpan.FromMinutes(_media.DownloadUrlTtlMinutes), ct);
    }

    public async Task<VideoSourceDto> GetPlaybackSourceAsync(Guid videoId, CancellationToken ct = default)
    {
        var asset = await db.VideoAssets.Include(v => v.Variants).FirstOrDefaultAsync(v => v.Id == videoId, ct)
                    ?? throw AppException.NotFound("Видео");
        if (asset.Status != VideoStatus.Ready)
            throw AppException.Conflict("VIDEO_NOT_READY", "Видео ещё не готово.");

        var ttl = TimeSpan.FromMinutes(_media.DownloadUrlTtlMinutes);
        var variant = asset.Variants.FirstOrDefault(v => v.Status == VideoStatus.Ready);
        return variant is not null
            ? new VideoSourceDto(asset.Id, await storage.GetDownloadUrlAsync(variant.StorageKey, ttl, ct), variant.SizeBytes, variant.Checksum)
            : new VideoSourceDto(asset.Id, await storage.GetDownloadUrlAsync(asset.StorageKey, ttl, ct), asset.SizeBytes, asset.Checksum);
    }

    // ---------- удаление ----------

    public async Task DeleteAsync(Guid userId, bool isAdmin, Guid videoId, CancellationToken ct = default)
    {
        var asset = await db.VideoAssets.Include(v => v.Variants).FirstOrDefaultAsync(v => v.Id == videoId, ct)
                    ?? throw AppException.NotFound("Видео");
        if (!isAdmin && asset.OwnerId != userId) throw AppException.NotFound("Видео");

        var usedBy = await db.AdPlacements
            .Where(p => p.VideoAssetId == videoId && BlockingStatuses.Contains(p.Status))
            .Select(p => p.Id)
            .ToListAsync(ct);
        if (usedBy.Count > 0)
            throw AppException.Conflict("VIDEO_IN_USE",
                "Видео используется в активных размещениях. Сначала отмените или перенесите их.",
                new { placementIds = usedBy });

        var keys = asset.Variants.Select(v => v.StorageKey).Append(asset.StorageKey).ToList();

        // Запись остаётся в БД (мягкое удаление): на неё ссылаются история размещений и показов.
        asset.IsDeleted = true;
        asset.DeletedAt = DateTimeOffset.UtcNow;
        SystemLogWriter.Add(db, userId, "VideoDeleted", nameof(VideoAsset), videoId);
        await db.SaveChangesAsync(ct);

        foreach (var key in keys)
        {
            try { await storage.DeleteAsync(key, ct); }
            catch (Exception ex) { log.LogWarning(ex, "Не удалось удалить объект {Key} из хранилища", key); }
        }
    }

    // ---------- вспомогательные ----------

    private async Task<VideoAsset> FindAsync(Guid userId, bool isAdmin, Guid videoId, CancellationToken ct)
    {
        var asset = await db.VideoAssets.FirstOrDefaultAsync(v => v.Id == videoId, ct)
                    ?? throw AppException.NotFound("Видео");
        // Чужое видео выглядит как несуществующее.
        if (!isAdmin && asset.OwnerId != userId) throw AppException.NotFound("Видео");
        return asset;
    }

    private static VideoAsset NewAsset(Guid ownerId, string fileName)
    {
        var id = Guid.NewGuid();
        var safeName = Path.GetFileName(fileName);
        var ext = Path.GetExtension(safeName).ToLowerInvariant();
        return new VideoAsset
        {
            Id = id,
            OwnerId = ownerId,
            OriginalFileName = safeName,
            // В ключ попадает только расширение: имя файла от пользователя в пути не используется.
            StorageKey = $"videos/{ownerId:N}/{id:N}/original{ext}",
            ContentType = VideoPolicy.ContentTypeForExtension(ext),
            Status = VideoStatus.Uploading
        };
    }

    private async Task MarkFailedAsync(VideoAsset asset, string reason)
    {
        try
        {
            asset.Status = VideoStatus.Failed;
            asset.FailureReason = reason;
            var job = await db.UploadJobs.FirstOrDefaultAsync(j => j.VideoAssetId == asset.Id);
            if (job is not null)
            {
                job.Status = UploadJobStatus.Failed;
                job.Error = reason;
                job.CompletedAt = DateTimeOffset.UtcNow;
            }
            await db.SaveChangesAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Не удалось сохранить статус Failed для видео {VideoId}", asset.Id);
        }
    }

    private string NewTempPath(string extension)
    {
        var dir = string.IsNullOrWhiteSpace(_media.TempPath) ? Path.GetTempPath() : _media.TempPath;
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, $"billboard-{Guid.NewGuid():N}{extension}");
    }

    private static void DeleteQuietly(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* временный файл */ }
    }

    private static VideoDto Map(VideoAsset v) => new(
        v.Id, v.OwnerId, v.OriginalFileName, v.ContentType, v.SizeBytes, v.DurationSeconds,
        v.Status.ToString(), v.FailureReason, v.CreatedAt);
}
