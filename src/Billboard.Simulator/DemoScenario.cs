using Billboard.Scheduling;
using Billboard.Simulator.Api;
using Billboard.Simulator.Cache;
using Billboard.Simulator.Runtime;

namespace Billboard.Simulator;

/// <summary>
/// Автономная демонстрация без backend: кэш, ротация, офлайн-очередь,
/// восстановление связи и вытеснение неиспользуемых файлов.
/// </summary>
public static class DemoScenario
{
    public static async Task RunAsync(SimulatorOptions options, TextWriter log)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(log);

        var root = Path.Combine(options.CacheDirectory, "demo");
        RecreateDirectory(root, log);

        // Небольшой лимит нужен, чтобы показать вытеснение за несколько секунд.
        const long demoCapacity = 20L * 1024 * 1024;
        var cache = new LocalCacheManager(root, demoCapacity, log: log);
        var manifestSource = new DemoManifestSource(options.BillboardId);
        var fetch = await manifestSource.GetAsync();
        var manifest = fetch.Manifest ?? throw new InvalidOperationException("Демо-manifest не создан.");
        var api = new DemoSimulatorApi(manifest);
        var outbox = new PlaybackEventOutbox(Path.Combine(root, "outbox.json"), log);
        var rotator = new PlaybackRotator();
        var downloader = new DemoVideoDownloader();

        log.WriteLine("=== ДЕМО: СИМУЛЯТОР БИЛБОРДА ===");
        log.WriteLine($"Билборд: {options.BillboardId}");
        log.WriteLine($"Лимит кэша в демо: {FormatBytes(demoCapacity)} (в продакшене по умолчанию 32 ГБ)");
        log.WriteLine();

        log.WriteLine("--- 1. Загрузка manifest и кэширование ---");
        var protectedIds = manifest.Items.Select(item => item.VideoId).ToHashSet();
        foreach (var item in manifest.Items)
        {
            var result = await cache.EnsureCachedAsync(item, protectedIds, downloader);
            log.WriteLine(result.Success
                ? $"  OK  {item.FileName} — {FormatBytes(item.SizeBytes)}"
                : $"  FAIL {item.FileName} — {result.ErrorCode}: {result.Message}");
        }

        log.WriteLine($"  Занято: {FormatBytes(cache.UsedBytes)} из {FormatBytes(cache.CapacityBytes)}");
        log.WriteLine();

        rotator.SetItems(manifest.Items);

        log.WriteLine("--- 2. Ротация по квотам (онлайн) ---");
        for (var index = 0; index < 4; index++)
        {
            var item = rotator.Next()!;
            var playbackEvent = PlaybackEvent.Create(options.BillboardId, item, DateTimeOffset.UtcNow);
            outbox.Enqueue(playbackEvent);
            var response = await api.SendPlaybackEventsAsync(options.BillboardId, new[] { playbackEvent });
            outbox.Acknowledge(response?.AcceptedEventIds ?? Array.Empty<Guid>());
            log.WriteLine($"  PLAY {item.FileName} ({item.DurationSeconds} с) → событие отправлено");
        }

        log.WriteLine();

        log.WriteLine("--- 3. Обрыв связи: показ из кэша, события в outbox ---");
        api.Online = false;
        for (var index = 0; index < 2; index++)
        {
            var item = rotator.Next()!;
            var playbackEvent = PlaybackEvent.Create(options.BillboardId, item, DateTimeOffset.UtcNow);
            outbox.Enqueue(playbackEvent);
            var response = await api.SendPlaybackEventsAsync(options.BillboardId, new[] { playbackEvent });
            if (response is null)
            {
                log.WriteLine($"  PLAY {item.FileName} → сети нет, событие в outbox ({outbox.Count})");
            }
        }

        log.WriteLine();

        log.WriteLine("--- 4. Восстановление связи и синхронизация outbox ---");
        api.Online = true;
        var pending = outbox.Take(options.EventBatchSize);
        var syncResponse = await api.SendPlaybackEventsAsync(options.BillboardId, pending);
        if (syncResponse is not null)
        {
            outbox.Acknowledge(syncResponse.AcceptedEventIds);
        }

        log.WriteLine($"  Отправлено {pending.Count} событие(й), в outbox осталось {outbox.Count}");
        log.WriteLine();

        log.WriteLine("--- 5. Лимит кэша и защита активного контента ---");
        var extra = new ManifestItem
        {
            PlacementId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbb004"),
            VideoId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccc0004"),
            FileName = "demo-extra.mp4",
            DownloadUrl = "demo://extra",
            SizeBytes = 5L * 1024 * 1024,
            DurationSeconds = 12,
            PlaysPerHour = 1,
        };

        var full = await cache.EnsureCachedAsync(extra, protectedIds, downloader);
        log.WriteLine($"  Все файлы активны → {full.ErrorCode}: {full.Message}");

        var first = manifest.Items[0].VideoId;
        var withoutFirst = protectedIds.Where(id => id != first).ToHashSet();
        var freed = await cache.EvictUnusedAsync(withoutFirst, extra.SizeBytes);
        log.WriteLine($"  Освобождено {FormatBytes(freed)} (удалён только неактивный ролик)");

        var retry = await cache.EnsureCachedAsync(extra, withoutFirst, downloader);
        log.WriteLine(retry.Success
            ? $"  OK  {extra.FileName} — {FormatBytes(extra.SizeBytes)}"
            : $"  FAIL {extra.FileName} — {retry.ErrorCode}: {retry.Message}");
        log.WriteLine($"  Занято после вытеснения: {FormatBytes(cache.UsedBytes)} из {FormatBytes(cache.CapacityBytes)}");
        log.WriteLine();
        log.WriteLine("=== ДЕМО ЗАВЕРШЕНО ===");
    }

    private static void RecreateDirectory(string path, TextWriter log)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }

            Directory.CreateDirectory(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            log.WriteLine($"Не удалось очистить демо-каталог ({exception.Message}); используем его как есть.");
        }
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["Б", "КБ", "МБ", "ГБ", "ТБ"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return $"{value:0.##} {units[unit]}";
    }
}
