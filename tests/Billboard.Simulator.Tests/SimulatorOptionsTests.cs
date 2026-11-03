using Billboard.Simulator;

namespace Billboard.Simulator.Tests;

public class SimulatorOptionsTests
{
    [Fact]
    public void Parse_DemoMode_UsesDefaultBillboard()
    {
        var options = SimulatorOptions.Parse(new[] { "--demo" });

        Assert.True(options.DemoMode);
        Assert.NotEqual(Guid.Empty, options.BillboardId);
        Assert.Equal(32L * 1024 * 1024 * 1024, options.CacheCapacityBytes);
    }

    [Fact]
    public void Parse_AllArguments()
    {
        var billboardId = Guid.NewGuid();

        var options = SimulatorOptions.Parse(new[]
        {
            "--billboard-id", billboardId.ToString(),
            "--api", "http://localhost:8080/",
            "--cache", "cache-dir",
            "--cache-mb", "100",
            "--heartbeat", "15",
            "--refresh", "20",
            "--batch", "5",
            "--speed", "50",
            "--max-plays", "10",
            "--manifest", "manifest.json",
        });

        Assert.Equal(billboardId, options.BillboardId);
        Assert.Equal("http://localhost:8080", options.ApiBaseUrl);
        Assert.Equal("cache-dir", options.CacheDirectory);
        Assert.Equal(100L * 1024 * 1024, options.CacheCapacityBytes);
        Assert.Equal(15, options.HeartbeatIntervalSeconds);
        Assert.Equal(20, options.ManifestRefreshSeconds);
        Assert.Equal(5, options.EventBatchSize);
        Assert.Equal(50, options.PlaybackSpeed);
        Assert.Equal(10, options.MaxPlays);
        Assert.Equal("manifest.json", options.ManifestFile);
    }

    [Fact]
    public void Parse_WithoutBillboard_Throws()
    {
        Assert.Throws<ArgumentException>(() => SimulatorOptions.Parse(Array.Empty<string>()));
    }

    [Fact]
    public void Parse_Help_DoesNotRequireBillboard()
    {
        var options = SimulatorOptions.Parse(new[] { "--help" });

        Assert.True(options.ShowHelp);
    }

    [Fact]
    public void Parse_UnknownArgument_Throws()
    {
        Assert.Throws<ArgumentException>(() => SimulatorOptions.Parse(new[] { "--unknown" }));
    }

    [Fact]
    public void Parse_EnvironmentValues_AreUsed()
    {
        var billboardId = Guid.NewGuid();
        var env = new Dictionary<string, string?>
        {
            ["BILLBOARD_ID"] = billboardId.ToString(),
            ["BILLBOARD_API_URL"] = "http://example.test",
            ["BILLBOARD_CACHE_MB"] = "64",
            ["BILLBOARD_SPEED"] = "25",
        };

        var options = SimulatorOptions.Parse(Array.Empty<string>(), env);

        Assert.Equal(billboardId, options.BillboardId);
        Assert.Equal("http://example.test", options.ApiBaseUrl);
        Assert.Equal(64L * 1024 * 1024, options.CacheCapacityBytes);
        Assert.Equal(25, options.PlaybackSpeed);
    }

    [Fact]
    public void Parse_CommandLineOverridesEnvironment()
    {
        var envId = Guid.NewGuid();
        var cliId = Guid.NewGuid();
        var env = new Dictionary<string, string?> { ["BILLBOARD_ID"] = envId.ToString() };

        var options = SimulatorOptions.Parse(new[] { "--billboard-id", cliId.ToString() }, env);

        Assert.Equal(cliId, options.BillboardId);
    }
}
