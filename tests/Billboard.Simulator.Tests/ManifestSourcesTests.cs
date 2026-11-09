using System.Text.Json;
using System.Text.Json.Serialization;
using Billboard.Scheduling;
using Billboard.Simulator.Api;

namespace Billboard.Simulator.Tests;

public class ManifestSourcesTests
{
    [Fact]
    public async Task ApiManifestSource_SavesManifestAndFallsBackToCache()
    {
        using var directory = new TempDirectory();
        var billboardId = Guid.NewGuid();
        var manifest = CreateManifest(billboardId);
        var api = new DemoSimulatorApi(manifest);
        var source = new ApiManifestSource(api, billboardId, directory.Path);

        var first = await source.GetAsync();

        Assert.True(first.Success);
        Assert.False(first.FromCache);
        Assert.Equal("v1", first.Manifest!.Version);
        Assert.True(File.Exists(Path.Combine(directory.Path, "last-manifest.json")));

        api.Online = false;
        var second = await source.GetAsync();

        Assert.True(second.Success);
        Assert.True(second.FromCache);
        Assert.Equal("v1", second.Manifest!.Version);
    }

    [Fact]
    public async Task ApiManifestSource_WithoutCacheAndApi_ReturnsError()
    {
        using var directory = new TempDirectory();
        var api = new DemoSimulatorApi();
        var source = new ApiManifestSource(api, Guid.NewGuid(), directory.Path);
        api.Online = false;

        var result = await source.GetAsync();

        Assert.False(result.Success);
        Assert.Null(result.Manifest);
    }

    [Fact]
    public async Task FileManifestSource_ReadsManifest()
    {
        using var directory = new TempDirectory();
        var path = Path.Combine(directory.Path, "manifest.json");
        var manifest = CreateManifest(Guid.NewGuid());
        await File.WriteAllTextAsync(path, Serialize(manifest));

        var result = await new FileManifestSource(path).GetAsync();

        Assert.True(result.Success);
        Assert.Equal(manifest.BillboardId, result.Manifest!.BillboardId);
        Assert.Single(result.Manifest.Items);
    }

    [Fact]
    public async Task FileManifestSource_ReadsP3Manifest()
    {
        using var directory = new TempDirectory();
        var path = Path.Combine(directory.Path, "p3-manifest.json");
        await File.WriteAllTextAsync(path, """
            {
              "billboardId": "d2f3f8d9-6a3e-4e91-9877-2b8e0a5f4321",
              "version": 7,
              "generatedAt": "2026-11-09T12:00:00Z",
              "cacheLimitBytes": 34359738368,
              "cacheUsedBytes": 0,
              "files": [{
                "videoAssetId": "2a8c4c12-1e44-4d3f-9d8a-7a8f58f4a122",
                "fileName": "ad.mp4",
                "sizeBytes": 2048,
                "durationSeconds": 15,
                "checksum": null,
                "downloadUrl": "http://minio/ad.mp4",
                "urlExpiresAt": "2026-11-09T12:15:00Z"
              }]
            }
            """);

        var result = await new FileManifestSource(path).GetAsync();

        Assert.True(result.Success);
        Assert.Equal("7", result.Manifest!.Version);
        var item = Assert.Single(result.Manifest.Items);
        Assert.Equal(Guid.Parse("2a8c4c12-1e44-4d3f-9d8a-7a8f58f4a122"), item.VideoId);
        Assert.False(item.HasPlacement);
    }

    [Fact]
    public async Task FileManifestSource_WhenMissing_ReturnsError()
    {
        using var directory = new TempDirectory();
        var result = await new FileManifestSource(Path.Combine(directory.Path, "missing.json")).GetAsync();

        Assert.False(result.Success);
        Assert.Contains("не найден", result.Error);
    }

    [Fact]
    public async Task DemoManifestSource_ReturnsThreeItems()
    {
        var result = await new DemoManifestSource(Guid.NewGuid()).GetAsync();

        Assert.True(result.Success);
        Assert.Equal(3, result.Manifest!.Items.Count);
        Assert.All(result.Manifest.Items, item => Assert.True(item.SizeBytes > 0));
    }

    private static PlaybackManifest CreateManifest(Guid billboardId) => new()
    {
        BillboardId = billboardId,
        Version = "v1",
        GeneratedAt = DateTimeOffset.UtcNow,
        Items = new[]
        {
            new ManifestItem
            {
                PlacementId = Guid.NewGuid(),
                VideoId = Guid.NewGuid(),
                FileName = "video.mp4",
                DownloadUrl = "test://video",
                SizeBytes = 1024,
                DurationSeconds = 10,
                PlaysPerHour = 1,
            },
        },
    };

    private static string Serialize(PlaybackManifest manifest)
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return JsonSerializer.Serialize(manifest, options);
    }
}
