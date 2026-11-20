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

/// <summary>Результат отправки пакета показов в P3 (счётчики, без списка ID).</summary>
public sealed record PlaybackSendResult
{
    public bool Success { get; init; }

    public int Accepted { get; init; }

    public int Duplicates { get; init; }

    public int Rejected { get; init; }

    public IReadOnlyList<string> Errors { get; init; } = Array.Empty<string>();

    public int TotalHandled => Accepted + Duplicates + Rejected;
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

    Task<PlaybackSendResult?> SendPlaybackEventsAsync(
        Guid billboardId,
        IReadOnlyList<PlaybackEvent> events,
        CancellationToken cancellationToken = default);

    Task<bool> SendCacheReportAsync(
        CacheReport report,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// HTTP-клиент API устройств P3. Авторизация — X-Device-Token: "{billboardId}.{secret}".
/// При сетевой ошибке возвращает null/false, не роняя симулятор.
/// </summary>
public sealed class HttpSimulatorApi : ISimulatorApi
{
    private readonly HttpClient _httpClient;
    private readonly Uri _baseUri;
    private readonly string? _deviceToken;
    private readonly TextWriter? _log;

    public HttpSimulatorApi(
        HttpClient httpClient,
        string baseUrl,
        string? deviceToken = null,
        TextWriter? log = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        ArgumentException.ThrowIfNullOrWhiteSpace(baseUrl);

        _baseUri = new Uri(baseUrl.TrimEnd('/') + "/", UriKind.Absolute);
        _deviceToken = string.IsNullOrWhiteSpace(deviceToken) ? null : deviceToken.Trim();
        _log = log;
    }

    public async Task<PlaybackManifest?> GetManifestAsync(
        Guid billboardId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var request = CreateRequest(
                HttpMethod.Get,
                $"api/v1/devices/{billboardId}/manifest");
            using var response = await _httpClient.SendAsync(request, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                Log($"Manifest: HTTP {(int)response.StatusCode}. " +
                    (response.StatusCode == System.Net.HttpStatusCode.Unauthorized
                        ? "Проверьте X-Device-Token."
                        : string.Empty));
                return null;
            }

            var manifest = await response.Content.ReadFromJsonAsync<P3Manifest>(
                SimulatorJson.Options,
                cancellationToken);

            if (manifest is null)
            {
                Log("Manifest: пустой ответ.");
                return null;
            }

            return P3Mapper.ToInternal(manifest);
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
            using var request = CreateRequest(
                HttpMethod.Post,
                $"api/v1/devices/{heartbeat.BillboardId}/heartbeat",
                JsonContent.Create(P3Mapper.ToP3(heartbeat), options: SimulatorJson.Options));
            using var response = await _httpClient.SendAsync(request, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                Log($"Heartbeat: HTTP {(int)response.StatusCode}.");
            }

            // P3 отвечает 204 No Content.
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

    public async Task<PlaybackSendResult?> SendPlaybackEventsAsync(
        Guid billboardId,
        IReadOnlyList<PlaybackEvent> events,
        CancellationToken cancellationToken = default)
    {
        var sendable = events
            .Where(playbackEvent => playbackEvent.PlacementId != Guid.Empty)
            .ToArray();

        if (sendable.Length == 0)
        {
            return new PlaybackSendResult { Success = true };
        }

        try
        {
            var payload = new P3PlaybackBatch(sendable.Select(P3Mapper.ToP3).ToArray());
            using var request = CreateRequest(
                HttpMethod.Post,
                $"api/v1/devices/{billboardId}/playback-events",
                JsonContent.Create(payload, options: SimulatorJson.Options));
            using var response = await _httpClient.SendAsync(request, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                Log($"События показов: HTTP {(int)response.StatusCode}.");
                return null;
            }

            if (response.StatusCode == System.Net.HttpStatusCode.NoContent ||
                response.Content.Headers.ContentLength == 0)
            {
                return new PlaybackSendResult
                {
                    Success = true,
                    Accepted = sendable.Length,
                };
            }

            var result = await response.Content.ReadFromJsonAsync<P3PlaybackResult>(
                SimulatorJson.Options,
                cancellationToken);

            if (result is null)
            {
                return new PlaybackSendResult
                {
                    Success = true,
                    Accepted = sendable.Length,
                };
            }

            return new PlaybackSendResult
            {
                Success = true,
                Accepted = result.Accepted,
                Duplicates = result.Duplicates,
                Rejected = result.Rejected,
                Errors = result.Errors ?? Array.Empty<string>(),
            };
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
            using var request = CreateRequest(
                HttpMethod.Post,
                $"api/v1/devices/{report.BillboardId}/cache-reports",
                JsonContent.Create(P3Mapper.ToP3(report), options: SimulatorJson.Options));
            using var response = await _httpClient.SendAsync(request, cancellationToken);

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

    private HttpRequestMessage CreateRequest(HttpMethod method, string relative, HttpContent? content = null)
    {
        var request = new HttpRequestMessage(method, Url(relative))
        {
            Content = content,
        };

        if (_deviceToken is not null)
        {
            request.Headers.TryAddWithoutValidation("X-Device-Token", _deviceToken);
        }

        return request;
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

    public Task<PlaybackSendResult?> SendPlaybackEventsAsync(
        Guid billboardId,
        IReadOnlyList<PlaybackEvent> events,
        CancellationToken cancellationToken = default)
    {
        if (!Online)
        {
            return Task.FromResult<PlaybackSendResult?>(null);
        }

        lock (_gate)
        {
            _events.AddRange(events);
        }

        return Task.FromResult<PlaybackSendResult?>(new PlaybackSendResult
        {
            Success = true,
            Accepted = events.Count,
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
