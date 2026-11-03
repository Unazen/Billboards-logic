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
