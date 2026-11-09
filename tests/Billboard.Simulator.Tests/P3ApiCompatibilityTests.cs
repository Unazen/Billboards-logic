using System.Net;
using System.Text;
using System.Text.Json;
using Billboard.Scheduling;
using Billboard.Simulator.Api;

namespace Billboard.Simulator.Tests;

public class P3ApiCompatibilityTests
{
    private static readonly Guid BillboardId = Guid.Parse("d2f3f8d9-6a3e-4e91-9877-2b8e0a5f4321");
    private static readonly Guid VideoId = Guid.Parse("2a8c4c12-1e44-4d3f-9d8a-7a8f58f4a122");
    private static readonly Guid PlacementId = Guid.Parse("0f7d1c2e-1111-4a3b-9a10-aaaaaaaaaaaa");

    [Fact]
    public async Task GetManifest_MapsP3FileContract()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK, """
            {
              "billboardId": "d2f3f8d9-6a3e-4e91-9877-2b8e0a5f4321",
              "version": 42,
              "generatedAt": "2026-11-09T12:00:00Z",
              "cacheLimitBytes": 34359738368,
              "cacheUsedBytes": 0,
              "files": [{
                "videoAssetId": "2a8c4c12-1e44-4d3f-9d8a-7a8f58f4a122",
                "fileName": "ad.mp4",
                "sizeBytes": 2048,
                "durationSeconds": 15,
                "checksum": "abc",
                "downloadUrl": "http://minio:9000/bucket/ad.mp4",
                "urlExpiresAt": "2026-11-09T12:15:00Z"
              }]
            }
            """);
        var api = CreateApi(handler, "billboard.secret");

        var manifest = await api.GetManifestAsync(BillboardId);

        Assert.NotNull(manifest);
        Assert.Equal("42", manifest!.Version);
        Assert.Equal(34359738368, manifest.CacheLimitBytes);
        var item = Assert.Single(manifest.Items);
        Assert.Equal(VideoId, item.VideoId);
        Assert.Equal(Guid.Empty, item.PlacementId);
        Assert.False(item.HasPlacement);
        Assert.Equal("ad.mp4", item.FileName);
        Assert.Equal(2048, item.SizeBytes);
        Assert.Equal(15, item.DurationSeconds);
        Assert.Equal("abc", item.ChecksumSha256);
        Assert.Equal(1, item.PlaysPerHour);
        Assert.Equal("http://minio:9000/bucket/ad.mp4", item.DownloadUrl);
        Assert.Equal(HttpMethod.Get, handler.LastRequest!.Method);
        Assert.Equal("billboard.secret", handler.LastRequest.DeviceToken);
        Assert.Equal(
            new Uri($"http://localhost:8080/api/v1/devices/{BillboardId}/manifest"),
            handler.LastRequest.Uri);
    }

    [Fact]
    public async Task GetManifest_UsesOptionalPlacementFields()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK, $$"""
            {
              "billboardId": "{{BillboardId}}",
              "version": 43,
              "generatedAt": "2026-11-09T12:00:00Z",
              "cacheLimitBytes": 34359738368,
              "cacheUsedBytes": 0,
              "files": [{
                "videoAssetId": "{{VideoId}}",
                "placementId": "{{PlacementId}}",
                "minimumPlaysPerHour": 6,
                "fileName": "ad.mp4",
                "sizeBytes": 2048,
                "durationSeconds": 15,
                "checksum": null,
                "downloadUrl": "http://minio/ad.mp4",
                "urlExpiresAt": "2026-11-09T12:15:00Z"
              }]
            }
            """);
        var api = CreateApi(handler);

        var manifest = await api.GetManifestAsync(BillboardId);

        var item = Assert.Single(manifest!.Items);
        Assert.Equal(PlacementId, item.PlacementId);
        Assert.True(item.HasPlacement);
        Assert.Equal(6, item.PlaysPerHour);
    }

    [Fact]
    public async Task GetManifest_KeepsPerPlacementEntriesForSameVideo()
    {
        var secondPlacement = Guid.NewGuid();
        var handler = new RecordingHandler(HttpStatusCode.OK, $$"""
            {
              "billboardId": "{{BillboardId}}",
              "version": 44,
              "generatedAt": "2026-11-09T12:00:00Z",
              "cacheLimitBytes": 34359738368,
              "cacheUsedBytes": 0,
              "files": [
                {
                  "videoAssetId": "{{VideoId}}",
                  "placementId": "{{PlacementId}}",
                  "minimumPlaysPerHour": 6,
                  "fileName": "ad.mp4",
                  "sizeBytes": 2048,
                  "durationSeconds": 15,
                  "checksum": null,
                  "downloadUrl": "http://minio/ad.mp4",
                  "urlExpiresAt": "2026-11-09T12:15:00Z"
                },
                {
                  "videoAssetId": "{{VideoId}}",
                  "placementId": "{{secondPlacement}}",
                  "minimumPlaysPerHour": 2,
                  "fileName": "ad.mp4",
                  "sizeBytes": 2048,
                  "durationSeconds": 15,
                  "checksum": null,
                  "downloadUrl": "http://minio/ad.mp4",
                  "urlExpiresAt": "2026-11-09T12:15:00Z"
                }
              ]
            }
            """);
        var api = CreateApi(handler);

        var manifest = await api.GetManifestAsync(BillboardId);

        Assert.NotNull(manifest);
        Assert.Equal(2, manifest!.Items.Count);
        Assert.Contains(manifest.Items, item => item.PlacementId == PlacementId && item.PlaysPerHour == 6);
        Assert.Contains(manifest.Items, item => item.PlacementId == secondPlacement && item.PlaysPerHour == 2);
    }

    [Fact]
    public async Task SendPlaybackEvents_MapsP3ContractAndCounters()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK, """
            { "accepted": 1, "duplicates": 0, "rejected": 0, "errors": [] }
            """);
        var api = CreateApi(handler, "billboard.secret");
        var playbackEvent = new PlaybackEvent
        {
            EventId = Guid.NewGuid(),
            IdempotencyKey = "event-key-1",
            BillboardId = BillboardId,
            PlacementId = PlacementId,
            VideoId = VideoId,
            StartedAt = DateTimeOffset.Parse("2026-11-09T12:00:10Z"),
            DurationSeconds = 15,
            Result = PlaybackResult.Success,
        };

        var result = await api.SendPlaybackEventsAsync(BillboardId, new[] { playbackEvent });

        Assert.NotNull(result);
        Assert.True(result!.Success);
        Assert.Equal(1, result.Accepted);
        Assert.Equal(1, result.TotalHandled);
        Assert.Equal(
            new Uri($"http://localhost:8080/api/v1/devices/{BillboardId}/playback-events"),
            handler.LastRequest!.Uri);
        Assert.Equal("billboard.secret", handler.LastRequest.DeviceToken);

        using var body = JsonDocument.Parse(handler.LastRequest.Body!);
        var p3Event = body.RootElement.GetProperty("events")[0];
        Assert.Equal("event-key-1", p3Event.GetProperty("idempotencyKey").GetString());
        Assert.Equal(PlacementId, p3Event.GetProperty("placementId").GetGuid());
        Assert.Equal(VideoId, p3Event.GetProperty("videoAssetId").GetGuid());
        Assert.Equal("Completed", p3Event.GetProperty("result").GetString());
        Assert.False(p3Event.TryGetProperty("videoId", out _));
    }

    [Fact]
    public async Task SendPlaybackEvents_WithoutPlacement_DoesNotCallApi()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK, "{}");
        var api = CreateApi(handler);
        var playbackEvent = new PlaybackEvent
        {
            EventId = Guid.NewGuid(),
            IdempotencyKey = "event-key-2",
            BillboardId = BillboardId,
            PlacementId = Guid.Empty,
            VideoId = VideoId,
            DurationSeconds = 15,
        };

        var result = await api.SendPlaybackEventsAsync(BillboardId, new[] { playbackEvent });

        Assert.NotNull(result);
        Assert.True(result!.Success);
        Assert.Null(handler.LastRequest);
    }

    [Fact]
    public async Task SendCacheReport_MapsP3Contract()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK, """
            { "cacheUsedBytes": 2048, "cacheLimitBytes": 34359738368, "files": 1 }
            """);
        var api = CreateApi(handler);
        var report = new CacheReport
        {
            BillboardId = BillboardId,
            Items = new[]
            {
                new CacheReportItem
                {
                    VideoId = VideoId,
                    SizeBytes = 2048,
                    ChecksumSha256 = "abc",
                    State = CacheItemState.Ready,
                },
            },
        };

        var sent = await api.SendCacheReportAsync(report);

        Assert.True(sent);
        using var body = JsonDocument.Parse(handler.LastRequest!.Body!);
        var item = body.RootElement.GetProperty("items")[0];
        Assert.Equal(VideoId, item.GetProperty("videoAssetId").GetGuid());
        Assert.Equal("Ready", item.GetProperty("downloadStatus").GetString());
        Assert.True(item.GetProperty("integrityVerified").GetBoolean());
        Assert.False(item.TryGetProperty("videoId", out _));
    }

    [Fact]
    public async Task SendHeartbeat_MapsP3Contract()
    {
        var handler = new RecordingHandler(HttpStatusCode.NoContent, null);
        var api = CreateApi(handler);

        var sent = await api.SendHeartbeatAsync(new HeartbeatRequest
        {
            BillboardId = BillboardId,
            Status = BillboardStatus.Online,
        });

        Assert.True(sent);
        using var body = JsonDocument.Parse(handler.LastRequest!.Body!);
        Assert.Equal("1.0.0", body.RootElement.GetProperty("simulatorVersion").GetString());
        Assert.Equal("Online", body.RootElement.GetProperty("status").GetString());
        Assert.Equal(
            new Uri($"http://localhost:8080/api/v1/devices/{BillboardId}/heartbeat"),
            handler.LastRequest.Uri);
    }

    [Fact]
    public async Task GetManifest_Unauthorized_ReturnsNull()
    {
        var handler = new RecordingHandler(HttpStatusCode.Unauthorized, "{}");
        var api = CreateApi(handler, "wrong.token");

        var manifest = await api.GetManifestAsync(BillboardId);

        Assert.Null(manifest);
    }

    private static HttpSimulatorApi CreateApi(RecordingHandler handler, string? token = null)
    {
        var client = new HttpClient(handler);
        return new HttpSimulatorApi(client, "http://localhost:8080", token);
    }

    private sealed record CapturedRequest(
        HttpMethod Method,
        Uri? Uri,
        string? Body,
        string? DeviceToken);

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string? _responseBody;

        public RecordingHandler(HttpStatusCode status, string? responseBody)
        {
            _status = status;
            _responseBody = responseBody;
        }

        public CapturedRequest? LastRequest { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);

            request.Headers.TryGetValues("X-Device-Token", out var tokens);

            LastRequest = new CapturedRequest(
                request.Method,
                request.RequestUri,
                body,
                tokens?.FirstOrDefault());

            var response = new HttpResponseMessage(_status);
            if (_responseBody is not null)
            {
                response.Content = new StringContent(
                    _responseBody,
                    Encoding.UTF8,
                    "application/json");
            }

            return response;
        }
    }
}
