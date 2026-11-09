using Billboard.Scheduling;
using Billboard.Simulator.Api;
using Billboard.Simulator.Cache;

namespace Billboard.Simulator.Runtime;

/// <summary>
/// Основной цикл устройства: обновить manifest, догрузить кэш, показать ролик,
/// отправить событие. При потере сети продолжает показывать кэш и копит события.
/// </summary>
public sealed class SimulatorRunner
{
    private readonly SimulatorOptions _options;
    private readonly ISimulatorApi _api;
    private readonly IManifestSource _manifestSource;
    private readonly LocalCacheManager _cache;
    private readonly IVideoDownloader _downloader;
    private readonly PlaybackEventOutbox _outbox;
    private readonly PlaybackRotator _rotator = new();
    private readonly TextWriter? _log;
    private readonly TimeProvider _time;

    private PlaybackManifest? _manifest;
    private DateTimeOffset _lastManifestAttempt = DateTimeOffset.MinValue;
    private bool _online = true;
    private int _plays;
    private readonly HashSet<Guid> _missingPlacementWarned = new();

    public SimulatorRunner(
        SimulatorOptions options,
        ISimulatorApi api,
        IManifestSource manifestSource,
        LocalCacheManager cache,
        IVideoDownloader downloader,
        TextWriter? log = null,
        TimeProvider? timeProvider = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _api = api ?? throw new ArgumentNullException(nameof(api));
        _manifestSource = manifestSource ?? throw new ArgumentNullException(nameof(manifestSource));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _downloader = downloader ?? throw new ArgumentNullException(nameof(downloader));
        _log = log;
        _time = timeProvider ?? TimeProvider.System;
        _outbox = new PlaybackEventOutbox(
            Path.Combine(options.CacheDirectory, "outbox.json"),
            log);
    }

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        Log($"Симулятор {SimulatorOptions.Version} запущен. Билборд: {_options.BillboardId}.");
        Log($"Кэш: {_options.CacheDirectory}, лимит {FormatBytes(_options.CacheCapacityBytes)}, " +
            $"в outbox {_outbox.Count} событие(й).");

        using var heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var heartbeatTask = HeartbeatLoopAsync(heartbeatCts.Token);

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                if (_manifest is null || IsManifestDue())
                {
                    await RefreshManifestAsync(cancellationToken);
                }

                var item = _rotator.Next();
                if (item is null)
                {
                    if (_manifest is null)
                    {
                        Log("Нет manifest и нет доступного кэша. Повтор через 5 с.");
                    }
                    else
                    {
                        Log("Нет готовых к показу файлов. Повтор через 5 с.");
                    }

                    await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
                }
                else if (!item.IsActiveAt(_time.GetUtcNow()))
                {
                    Log($"[PLAY] Ролик {item.FileName} больше не активен — исключаю из ротации.");
                    _rotator.Remove(item.VideoId);
                    await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
                }
                else if (_cache.TryGetEntry(item.VideoId) is null)
                {
                    Log($"[CACHE] Файл {item.FileName} исчез из кэша — исключаю из ротации.");
                    _rotator.Remove(item.VideoId);
                    await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
                }
                else
                {
                    await PlayAsync(item, cancellationToken);
                    await TryFlushAsync(cancellationToken);

                    if (_options.MaxPlays > 0 && _plays >= _options.MaxPlays)
                    {
                        Log($"Достигнут лимит показов: {_plays}. Остановка.");
                        break;
                    }
                }
            }
        }
        finally
        {
            heartbeatCts.Cancel();
            try
            {
                await heartbeatTask;
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception exception)
            {
                Log($"Heartbeat остановлен с ошибкой: {exception.Message}");
            }

            Log($"Симулятор остановлен. Показов: {_plays}, событий в outbox: {_outbox.Count}.");
        }
    }

    private async Task RefreshManifestAsync(CancellationToken cancellationToken)
    {
        _lastManifestAttempt = _time.GetUtcNow();
        var result = await _manifestSource.GetAsync(cancellationToken);

        if (!result.Success || result.Manifest is null)
        {
            SetOnline(false, "manifest недоступен");
            Log($"Manifest: {result.Error ?? "неизвестная ошибка"}. Работаем на локальном кэше.");
            return;
        }

        var manifest = result.Manifest;
        if (result.FromCache)
        {
            SetOnline(false, "используется сохранённый manifest");
        }

        var changed = _manifest is null ||
                      !string.Equals(_manifest.Version, manifest.Version, StringComparison.Ordinal) ||
                      _manifest.Items.Count != manifest.Items.Count;

        _manifest = manifest;

        if (changed)
        {
            Log($"Manifest {manifest.Version}: {manifest.Items.Count} ролик(ов).");
        }

        await SyncCacheAsync(manifest, cancellationToken);
        await SendCacheReportAsync(manifest, cancellationToken);

        if (!result.FromCache)
        {
            SetOnline(true, null);
        }
    }

    private async Task SyncCacheAsync(PlaybackManifest manifest, CancellationToken cancellationToken)
    {
        var items = manifest.Items
            .Where(item => item.VideoId != Guid.Empty)
            .GroupBy(item => item.VideoId)
            .Select(group => group.First())
            .ToArray();

        var protectedIds = items.Select(item => item.VideoId).ToHashSet();

        foreach (var item in items)
        {
            if (_cache.TryGetEntry(item.VideoId) is not null)
            {
                continue;
            }

            Log($"[CACHE] Скачивание {item.FileName} ({FormatBytes(item.SizeBytes)}).");
            var result = await _cache.EnsureCachedAsync(item, protectedIds, _downloader, cancellationToken);

            if (result.Success)
            {
                Log($"[CACHE] Готово: {item.FileName}.");
            }
            else
            {
                Log($"[CACHE] Ошибка {item.FileName}: {result.ErrorCode} — {result.Message}");
            }
        }

        var now = _time.GetUtcNow();
        var playable = items
            .Where(item => item.IsActiveAt(now))
            .Where(item => _cache.TryGetEntry(item.VideoId) is not null)
            .ToArray();

        var currentIds = _rotator.Items.Select(item => item.VideoId).OrderBy(id => id).ToArray();
        var playableIds = playable.Select(item => item.VideoId).OrderBy(id => id).ToArray();
        if (!currentIds.SequenceEqual(playableIds))
        {
            _rotator.SetItems(playable);
        }

        Log($"[CACHE] К показу готово {playable.Length} из {items.Length}.");
    }

    private async Task PlayAsync(ManifestItem item, CancellationToken cancellationToken)
    {
        var startedAt = _time.GetUtcNow();
        Log($"[PLAY] {item.FileName} ({item.DurationSeconds} с), placement {item.PlacementId}.");

        var speed = Math.Max(0.001, _options.PlaybackSpeed);
        var delay = TimeSpan.FromSeconds(item.DurationSeconds / speed);
        if (delay > TimeSpan.Zero)
        {
            await Task.Delay(delay, cancellationToken);
        }

        _plays++;

        if (item.PlacementId == Guid.Empty)
        {
            if (_missingPlacementWarned.Add(item.VideoId))
            {
                Log("[P3] В manifest нет placementId: ролик показывается, " +
                    "но событие показа не отправляется. Нужно расширить ManifestFileDto в P3.");
            }

            await TryFlushAsync(cancellationToken);
            return;
        }

        var playbackEvent = PlaybackEvent.Create(_options.BillboardId, item, startedAt);
        _outbox.Enqueue(playbackEvent);

        Log($"[PLAY] Событие {playbackEvent.EventId:N} сохранено (outbox: {_outbox.Count}).");
        await TryFlushAsync(cancellationToken);
    }

    private async Task TryFlushAsync(CancellationToken cancellationToken)
    {
        if (!_online || _outbox.Count == 0)
        {
            return;
        }

        var batch = _outbox.Take(_options.EventBatchSize);
        if (batch.Count == 0)
        {
            return;
        }

        var withoutPlacement = batch
            .Where(item => item.PlacementId == Guid.Empty)
            .Select(item => item.EventId)
            .ToArray();

        if (withoutPlacement.Length > 0)
        {
            _outbox.Acknowledge(withoutPlacement);
            Log($"[SYNC] Удалено {withoutPlacement.Length} событие(й) без placementId.");
            batch = batch.Where(item => item.PlacementId != Guid.Empty).ToArray();
            if (batch.Count == 0)
            {
                return;
            }
        }

        var result = await _api.SendPlaybackEventsAsync(
            _options.BillboardId,
            batch,
            cancellationToken);

        if (result is null)
        {
            _outbox.RecordAttempt(batch.Select(item => item.EventId));
            SetOnline(false, "события не отправлены");
            return;
        }

        if (!result.Success || result.TotalHandled != batch.Count)
        {
            _outbox.RecordAttempt(batch.Select(item => item.EventId));
            Log($"[SYNC] P3 подтвердил {result.TotalHandled} из {batch.Count} событие(й) — повтор пакета.");
            SetOnline(false, "P3 не подтвердил весь пакет");
            return;
        }

        // P3 разрешает каждое событие: accepted, duplicate или rejected.
        // Rejected — постоянная ошибка, поэтому повторять его бессмысленно.
        _outbox.Acknowledge(batch.Select(item => item.EventId));

        if (result.Rejected > 0)
        {
            var errors = string.Join("; ", result.Errors.Take(3));
            Log($"[SYNC] P3 отклонил {result.Rejected} событие(й): {errors}");
        }

        SetOnline(true, null);
        Log($"[SYNC] P3: accepted={result.Accepted}, duplicates={result.Duplicates}, " +
            $"rejected={result.Rejected}; в outbox осталось {_outbox.Count}.");
    }

    private async Task HeartbeatLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var heartbeat = new HeartbeatRequest
            {
                BillboardId = _options.BillboardId,
                ReportedAt = _time.GetUtcNow(),
                Status = _online ? BillboardStatus.Online : BillboardStatus.Offline,
                SimulatorVersion = SimulatorOptions.Version,
                CacheUsedBytes = _cache.UsedBytes,
                CacheCapacityBytes = _options.CacheCapacityBytes,
                CachedVideoCount = _cache.Count,
                ActivePlacementCount = _rotator.Count,
            };

            var sent = await _api.SendHeartbeatAsync(heartbeat, cancellationToken);
            if (!sent)
            {
                SetOnline(false, "heartbeat не прошёл");
            }
            else
            {
                SetOnline(true, null);
            }

            await Task.Delay(TimeSpan.FromSeconds(_options.HeartbeatIntervalSeconds), cancellationToken);
        }
    }

    private async Task SendCacheReportAsync(PlaybackManifest manifest, CancellationToken cancellationToken)
    {
        var report = new CacheReport
        {
            BillboardId = _options.BillboardId,
            ReportedAt = _time.GetUtcNow(),
            Items = manifest.Items.Select(item =>
            {
                var entry = _cache.TryGetEntry(item.VideoId);
                return new CacheReportItem
                {
                    VideoId = item.VideoId,
                    SizeBytes = entry?.SizeBytes ?? 0,
                    ChecksumSha256 = entry?.ChecksumSha256,
                    State = entry is null ? CacheItemState.Missing : CacheItemState.Ready,
                };
            }).ToArray(),
        };

        try
        {
            await _api.SendCacheReportAsync(report, cancellationToken);
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException)
        {
            Log($"[CACHE] Отчёт не отправлен: {exception.Message}");
        }
    }

    private bool IsManifestDue() =>
        _time.GetUtcNow() - _lastManifestAttempt >=
        TimeSpan.FromSeconds(_options.ManifestRefreshSeconds);

    private void SetOnline(bool online, string? reason)
    {
        if (_online == online)
        {
            return;
        }

        _online = online;
        if (online)
        {
            Log("Связь с сервером восстановлена.");
        }
        else
        {
            Log($"Связь с сервером потеряна ({reason}). Продолжаем показ из кэша, события копятся в outbox.");
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

    private void Log(string message) =>
        _log?.WriteLine($"[{DateTimeOffset.Now:HH:mm:ss}] {message}");
}
