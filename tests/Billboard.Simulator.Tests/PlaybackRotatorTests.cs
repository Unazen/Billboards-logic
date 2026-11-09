using Billboard.Simulator.Runtime;

namespace Billboard.Simulator.Tests;

public class PlaybackRotatorTests
{
    [Fact]
    public void SetItems_IgnoresInvalidItemsAndDuplicates()
    {
        var rotator = new PlaybackRotator();
        var videoId = Guid.NewGuid();
        var placementId = Guid.NewGuid();

        rotator.SetItems(new[]
        {
            TestItems.Create(videoId: videoId, placementId: placementId, durationSeconds: 10, playsPerHour: 1),
            TestItems.Create(videoId: videoId, placementId: placementId, durationSeconds: 20, playsPerHour: 2),
            TestItems.Create(videoId: videoId, durationSeconds: 15, playsPerHour: 1),
            TestItems.Create(videoId: Guid.Empty),
            TestItems.Create(durationSeconds: 0),
            TestItems.Create(playsPerHour: 0),
        });

        Assert.Equal(2, rotator.Count);
        Assert.Contains(rotator.Items, item => item.DurationSeconds == 20);
        Assert.Contains(rotator.Items, item => item.DurationSeconds == 15);
    }

    [Fact]
    public void Next_SameVideoInTwoPlacements_UsesEachQuota()
    {
        var rotator = new PlaybackRotator();
        var videoId = Guid.NewGuid();
        var firstPlacement = Guid.NewGuid();
        var secondPlacement = Guid.NewGuid();
        rotator.SetItems(new[]
        {
            TestItems.Create(videoId: videoId, placementId: firstPlacement, playsPerHour: 2),
            TestItems.Create(videoId: videoId, placementId: secondPlacement, playsPerHour: 1),
        });

        var picks = Enumerable.Range(0, 6).Select(_ => rotator.Next()!).ToArray();

        Assert.Equal(4, picks.Count(item => item.PlacementId == firstPlacement));
        Assert.Equal(2, picks.Count(item => item.PlacementId == secondPlacement));
        Assert.All(picks, item => Assert.Equal(videoId, item.VideoId));
    }

    [Fact]
    public void Next_DistributesPlaysByQuota()
    {
        var rotator = new PlaybackRotator();
        var first = TestItems.Create(playsPerHour: 2);
        var second = TestItems.Create(playsPerHour: 1);
        var third = TestItems.Create(playsPerHour: 1);
        rotator.SetItems(new[] { first, second, third });

        var picks = Enumerable.Range(0, 8).Select(_ => rotator.Next()!).ToArray();

        Assert.Equal(4, picks.Count(item => item.VideoId == first.VideoId));
        Assert.Equal(2, picks.Count(item => item.VideoId == second.VideoId));
        Assert.Equal(2, picks.Count(item => item.VideoId == third.VideoId));
    }

    [Fact]
    public void Next_WhenEmpty_ReturnsNull()
    {
        var rotator = new PlaybackRotator();

        Assert.Null(rotator.Next());
    }

    [Fact]
    public void Remove_ExcludesItemFromRotation()
    {
        var rotator = new PlaybackRotator();
        var first = TestItems.Create(playsPerHour: 1);
        var second = TestItems.Create(playsPerHour: 1);
        rotator.SetItems(new[] { first, second });

        var removed = rotator.Remove(first.VideoId);

        Assert.True(removed);
        Assert.Equal(1, rotator.Count);
        Assert.All(
            Enumerable.Range(0, 4).Select(_ => rotator.Next()!),
            item => Assert.Equal(second.VideoId, item.VideoId));
    }

    [Fact]
    public void Remove_ExcludesSpecificPlacement()
    {
        var rotator = new PlaybackRotator();
        var videoId = Guid.NewGuid();
        var firstPlacement = Guid.NewGuid();
        var secondPlacement = Guid.NewGuid();
        rotator.SetItems(new[]
        {
            TestItems.Create(videoId: videoId, placementId: firstPlacement),
            TestItems.Create(videoId: videoId, placementId: secondPlacement),
        });

        var removed = rotator.Remove(videoId, firstPlacement);

        Assert.True(removed);
        Assert.Equal(1, rotator.Count);
        Assert.All(
            Enumerable.Range(0, 4).Select(_ => rotator.Next()!),
            item => Assert.Equal(secondPlacement, item.PlacementId));
    }

    [Fact]
    public void SetItems_ReplacesRotation()
    {
        var rotator = new PlaybackRotator();
        rotator.SetItems(new[] { TestItems.Create(playsPerHour: 1) });
        var replacement = TestItems.Create(playsPerHour: 3);

        rotator.SetItems(new[] { replacement });

        Assert.Equal(1, rotator.Count);
        Assert.Equal(replacement.VideoId, rotator.Next()!.VideoId);
    }
}
