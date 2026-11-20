using System.Text.Json;
using Billboard.Simulator;
using Billboard.Simulator.Api;
using Billboard.Simulator.Cache;
using Billboard.Simulator.Runtime;

namespace Billboard.Simulator.Tests;

public class SimulatorRunnerTests
{
    [Fact]
    public async Task Run_WithDemoManifest_CachesAndSendsEvents()
    {
        using var directory = new TempDirectory();
        var log = new StringWriter();
        var options = CreateOptions(directory.Path, maxPlays: 3);
        var cache = new LocalCacheManager(directory.Path, options.CacheCapacityBytes, log: log);
        var api = new DemoSimulatorApi();
        var manifestSource = new DemoManifestSource(options.BillboardId);
        api.SetManifest((await manifestSource.GetAsync()).Manifest!);
        var runner = new SimulatorRunner(
            options,
            api,
            manifestSource,
            cache,
            new DemoVideoDownloader(),
            log);

        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await runner.RunAsync(cancellation.Token);

        Assert.Equal(3, api.ReceivedEvents.Count);
        Assert.Equal(3, cache.Count);
        Assert.All(api.ReceivedEvents, playbackEvent => Assert.False(string.IsNullOrWhiteSpace(playbackEvent.IdempotencyKey)));

        var outbox = new PlaybackEventOutbox(Path.Combine(directory.Path, "outbox.json"));
        Assert.Equal(0, outbox.Count);
    }

    [Fact]
    public async Task Run_WhenApiOffline_KeepsEventsInOutbox()
    {
        using var directory = new TempDirectory();
        var log = new StringWriter();
        var options = CreateOptions(directory.Path);
        var cache = new LocalCacheManager(directory.Path, options.CacheCapacityBytes, log: log);
        var api = new DemoSimulatorApi { Online = false };
        var manifestSource = new DemoManifestSource(options.BillboardId);
        var manifest = (await manifestSource.GetAsync()).Manifest!;
        var manifestPath = Path.Combine(directory.Path, "manifest.json");
        await File.WriteAllTextAsync(manifestPath, Serialize(manifest));

        var runner = new SimulatorRunner(
            options,
            api,
            new FileManifestSource(manifestPath),
            cache,
            new DemoVideoDownloader(),
            log);

        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await runner.RunAsync(cancellation.Token);

        Assert.Empty(api.ReceivedEvents);
        Assert.Equal(3, cache.Count);
        var outbox = new PlaybackEventOutbox(Path.Combine(directory.Path, "outbox.json"));
        Assert.Equal(2, outbox.Count);
    }

    private static SimulatorOptions CreateOptions(string cacheDirectory, int maxPlays = 2) => new()
    {
        BillboardId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
        CacheDirectory = cacheDirectory,
        CacheCapacityBytes = 64L * 1024 * 1024,
        HeartbeatIntervalSeconds = 60,
        ManifestRefreshSeconds = 3600,
        EventBatchSize = 10,
        PlaybackSpeed = 1000,
        MaxPlays = maxPlays,
        DemoMode = true,
    };

    private static string Serialize(object value)
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
        };
        options.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
        return JsonSerializer.Serialize(value, options);
    }
}
