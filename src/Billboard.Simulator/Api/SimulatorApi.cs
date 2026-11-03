using System.Net.Http.Json;
using Billboard.Scheduling;

namespace Billboard.Simulator.Api;

public sealed record ManifestFetchResult
{
    public PlaybackManifest? Manifest { get; init; }

    public bool FromCache { get; init; }

    public string? Error { get; init; }

    public bool Success => Manifest is not null;
}

public interface IManifestSource
{
    Task<ManifestFetchResult> GetAsync(CancellationToken cancellationToken = default);
}

public interface ISimulatorApi
{
    Task<PlaybackManifest?> GetManifestAsync(
        Guid billboardId,
        CancellationToken cancellationToken = default);

    Task<bool> SendHeartbeatAsync(
        HeartbeatRequest heartbeat,
        CancellationToken cancellationToken = default);

    Task<PlaybackEventBatchResponse?> SendPlaybackEventsAsync(
        Guid billboardId,
        IReadOnlyList<PlaybackEvent> events,
        CancellationToken cancellationToken = default);

    Task<bool> SendCacheReportAsync(
        CacheReport report,
        CancellationToken cancellationToken = default);
}

/// <summary>HTTP-клиент API устройств. При сетевой ошибке возвращает null/false, не роняя симулятор.</summary>
public sealed class HttpSimulatorApi : ISimulatorApi
{
    private readonly HttpClient _httpClient;
    private readonly Uri _baseUri;
    private readonly TextWriter? _log;

    public HttpSimulatorApi(HttpClient httpClient, string baseUrl, TextWriter? log = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        ArgumentException.ThrowIfNullOrWhiteSpace(baseUrl);

        _baseUri = new Uri(baseUrl.TrimEnd('/') + "/", UriKind.Absolute);
        _log = log;
    }

    public async Task<PlaybackManifest?> GetManifestAsync(
        Guid billboardId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await _httpClient.GetAsync(
                Url($"api/v1/devices/{billboardId}/manifest"),
                cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                Log($"Manifest: HTTP {(int)response.StatusCode}.");
                return null;
            }

            return await response.Content.ReadFromJsonAsync<PlaybackManifest>(
                SimulatorJson.Options,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            Log($"Manifest недоступен: {exception.Message}");
            return null;
        }
    }

    public async Task<bool> SendHeartbeatAsync(
        HeartbeatRequest heartbeat,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await _httpClient.PostAsJsonAsync(
                Url($"api/v1/devices/{heartbeat.BillboardId}/heartbeat"),
                heartbeat,
                SimulatorJson.Options,
                cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                Log($"Heartbeat: HTTP {(int)response.StatusCode}.");
            }

            return response.IsSuccessStatusCode;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            Log($"Heartbeat не отправлен: {exception.Message}");
            return false;
        }
    }

    public async Task<PlaybackEventBatchResponse?> SendPlaybackEventsAsync(
        Guid billboardId,
        IReadOnlyList<PlaybackEvent> events,
        CancellationToken cancellationToken = default)
    {
        if (events.Count == 0)
        {
            return new PlaybackEventBatchResponse();
        }

        try
        {
            using var response = await _httpClient.PostAsJsonAsync(
                Url($"api/v1/devices/{billboardId}/playback-events"),
                new PlaybackEventBatch { Events = events },
                SimulatorJson.Options,
                cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                Log($"События показов: HTTP {(int)response.StatusCode}.");
                return null;
            }

            if (response.StatusCode == System.Net.HttpStatusCode.NoContent ||
                response.Content.Headers.ContentLength == 0)
            {
                return new PlaybackEventBatchResponse();
            }

            return await response.Content.ReadFromJsonAsync<PlaybackEventBatchResponse>(
                SimulatorJson.Options,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            Log($"События показов не отправлены: {exception.Message}");
            return null;
        }
    }

    public async Task<bool> SendCacheReportAsync(
        CacheReport report,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await _httpClient.PostAsJsonAsync(
                Url($"api/v1/devices/{report.BillboardId}/cache-reports"),
                report,
                SimulatorJson.Options,
                cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                Log($"Отчёт о кэше: HTTP {(int)response.StatusCode}.");
            }

            return response.IsSuccessStatusCode;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            Log($"Отчёт о кэше не отправлен: {exception.Message}");
            return false;
        }
    }

    private Uri Url(string relative) => new(_baseUri, relative);

    private void Log(string message) => _log?.WriteLine($"[API] {message}");
}

/// <summary>In-memory API для демонстрации и тестов без сервера.</summary>
public sealed class DemoSimulatorApi : ISimulatorApi
{
    private readonly object _gate = new();
    private readonly List<PlaybackEvent> _events = new();
    private PlaybackManifest? _manifest;

    public DemoSimulatorApi(PlaybackManifest? manifest = null)
    {
        _manifest = manifest;
    }

    public bool Online { get; set; } = true;

    public int HeartbeatCount { get; private set; }

    public CacheReport? LastCacheReport { get; private set; }

    public IReadOnlyList<PlaybackEvent> ReceivedEvents
    {
        get
        {
            lock (_gate)
            {
                return _events.ToArray();
            }
        }
    }

    public void SetManifest(PlaybackManifest manifest) => _manifest = manifest;

    public Task<PlaybackManifest?> GetManifestAsync(
        Guid billboardId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(Online ? _manifest : null);

    public Task<bool> SendHeartbeatAsync(
        HeartbeatRequest heartbeat,
        CancellationToken cancellationToken = default)
    {
        if (!Online)
        {
            return Task.FromResult(false);
        }

        HeartbeatCount++;
        return Task.FromResult(true);
    }

    public Task<PlaybackEventBatchResponse?> SendPlaybackEventsAsync(
        Guid billboardId,
        IReadOnlyList<PlaybackEvent> events,
        CancellationToken cancellationToken = default)
    {
        if (!Online)
        {
            return Task.FromResult<PlaybackEventBatchResponse?>(null);
        }

        lock (_gate)
        {
            _events.AddRange(events);
        }

        return Task.FromResult<PlaybackEventBatchResponse?>(new PlaybackEventBatchResponse
        {
            AcceptedEventIds = events.Select(item => item.EventId).ToArray(),
        });
    }

    public Task<bool> SendCacheReportAsync(
        CacheReport report,
        CancellationToken cancellationToken = default)
    {
        if (!Online)
        {
            return Task.FromResult(false);
        }

        LastCacheReport = report;
        return Task.FromResult(true);
    }
}
