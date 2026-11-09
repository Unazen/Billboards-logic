using System.Text.Json;
using Billboard.Scheduling;

namespace Billboard.Simulator.Api;

/// <summary>Получает manifest из API и сохраняет последнюю версию для офлайн-режима.</summary>
public sealed class ApiManifestSource : IManifestSource
{
    private readonly ISimulatorApi _api;
    private readonly Guid _billboardId;
    private readonly string _lastManifestPath;
    private readonly TextWriter? _log;

    public ApiManifestSource(
        ISimulatorApi api,
        Guid billboardId,
        string cacheDirectory,
        TextWriter? log = null)
    {
        _api = api ?? throw new ArgumentNullException(nameof(api));
        _billboardId = billboardId;
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheDirectory);
        _lastManifestPath = Path.Combine(cacheDirectory, "last-manifest.json");
        _log = log;
    }

    public async Task<ManifestFetchResult> GetAsync(CancellationToken cancellationToken = default)
    {
        var manifest = await _api.GetManifestAsync(_billboardId, cancellationToken);
        if (manifest is not null)
        {
            try
            {
                await AtomicFile.WriteAllTextAsync(
                    _lastManifestPath,
                    JsonSerializer.Serialize(manifest, SimulatorJson.Options),
                    cancellationToken);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                Log($"Не удалось сохранить manifest: {exception.Message}");
            }

            return new ManifestFetchResult { Manifest = manifest };
        }

        var cached = LoadCachedManifest();
        return new ManifestFetchResult
        {
            Manifest = cached,
            FromCache = cached is not null,
            Error = cached is null
                ? "Manifest недоступен, локальной копии нет."
                : "API недоступен, используется последний сохранённый manifest.",
        };
    }

    private PlaybackManifest? LoadCachedManifest()
    {
        if (!File.Exists(_lastManifestPath))
        {
            return null;
        }

        try
        {
            var json = File.ReadAllText(_lastManifestPath);
            var manifest = JsonSerializer.Deserialize<PlaybackManifest>(json, SimulatorJson.Options);
            if (manifest is not null)
            {
                Log("Используется сохранённый manifest (офлайн).");
            }

            return manifest;
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            Log($"Не удалось прочитать сохранённый manifest: {exception.Message}");
            return null;
        }
    }

    private void Log(string message) => _log?.WriteLine($"[MANIFEST] {message}");
}

/// <summary>Читает manifest из локального файла без API.</summary>
public sealed class FileManifestSource : IManifestSource
{
    private readonly string _path;

    public FileManifestSource(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = Path.GetFullPath(path);
    }

    public Task<ManifestFetchResult> GetAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_path))
        {
            return Task.FromResult(new ManifestFetchResult
            {
                Error = $"Файл manifest не найден: {_path}",
            });
        }

        try
        {
            var json = File.ReadAllText(_path);
            if (!P3Mapper.TryParseManifest(json, out var manifest) || manifest is null)
            {
                return Task.FromResult(new ManifestFetchResult
                {
                    Error = "Manifest пуст или имеет неизвестный формат.",
                });
            }

            return Task.FromResult(new ManifestFetchResult { Manifest = manifest });
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            return Task.FromResult(new ManifestFetchResult
            {
                Error = $"Ошибка чтения manifest: {exception.Message}",
            });
        }
    }
}

/// <summary>Демонстрационный manifest из трёх роликов; не требует сервера.</summary>
public sealed class DemoManifestSource : IManifestSource
{
    private readonly Guid _billboardId;
    private readonly Func<DateTimeOffset> _clock;

    public DemoManifestSource(Guid billboardId, Func<DateTimeOffset>? clock = null)
    {
        _billboardId = billboardId;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public Task<ManifestFetchResult> GetAsync(CancellationToken cancellationToken = default)
    {
        var now = _clock();
        var manifest = new PlaybackManifest
        {
            BillboardId = _billboardId,
            Version = "demo-v1",
            GeneratedAt = now,
            CacheLimitBytes = global::Billboard.Scheduling.Billboard.DefaultCapacityBytes,
            Items = new[]
            {
                new ManifestItem
                {
                    PlacementId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbb001"),
                    VideoId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccc0001"),
                    FileName = "demo-city.mp4",
                    DownloadUrl = "demo://city",
                    SizeBytes = 4L * 1024 * 1024,
                    DurationSeconds = 15,
                    PlaysPerHour = 2,
                    ActiveFrom = now.AddHours(-1),
                    ActiveUntil = now.AddHours(24),
                },
                new ManifestItem
                {
                    PlacementId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbb002"),
                    VideoId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccc0002"),
                    FileName = "demo-mall.mp4",
                    DownloadUrl = "demo://mall",
                    SizeBytes = 6L * 1024 * 1024,
                    DurationSeconds = 20,
                    PlaysPerHour = 1,
                    ActiveFrom = now.AddHours(-1),
                    ActiveUntil = now.AddHours(24),
                },
                new ManifestItem
                {
                    PlacementId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbb003"),
                    VideoId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccc0003"),
                    FileName = "demo-metro.mp4",
                    DownloadUrl = "demo://metro",
                    SizeBytes = 8L * 1024 * 1024,
                    DurationSeconds = 10,
                    PlaysPerHour = 1,
                    ActiveFrom = now.AddHours(-1),
                    ActiveUntil = now.AddHours(24),
                },
            },
        };

        return Task.FromResult(new ManifestFetchResult { Manifest = manifest });
    }
}
