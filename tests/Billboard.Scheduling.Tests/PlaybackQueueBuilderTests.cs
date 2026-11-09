using Billboard.Scheduling;

namespace Billboard.Scheduling.Tests;

public class PlaybackQueueBuilderTests
{
    private readonly PlaybackQueueBuilder _builder = new();

    private static readonly Guid FirstId = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly Guid SecondId = Guid.Parse("00000000-0000-0000-0000-000000000002");

    [Fact]
    public void Build_DistributesPlaysAccordingToQuota()
    {
        var billboard = TestData.Billboard();
        var first = TestData.Placement(id: FirstId, plays: 6, durationSeconds: 15);
        var second = TestData.Placement(id: SecondId, plays: 2, durationSeconds: 30);

        var plan = _builder.Build(billboard, TestData.Monday, 10, new[] { first, second });

        Assert.Equal(8, plan.RequiredPlays);
        Assert.Equal(8, plan.PlannedPlays);
        Assert.Equal(6, plan.Slots.Count(slot => slot.PlacementId == FirstId));
        Assert.Equal(2, plan.Slots.Count(slot => slot.PlacementId == SecondId));
        Assert.Equal(6 * 15 + 2 * 30, plan.TotalDurationSeconds);
        Assert.Equal(0, plan.Slots[0].StartOffsetSeconds);
        Assert.Equal(15, plan.Slots[1].StartOffsetSeconds);
        Assert.False(plan.IsOverCapacity);
        Assert.Empty(plan.Warnings);
    }

    [Fact]
    public void Build_InterleavesPlacementsInsteadOfGroupingThem()
    {
        var billboard = TestData.Billboard();
        var first = TestData.Placement(id: FirstId, plays: 4, durationSeconds: 10);
        var second = TestData.Placement(id: SecondId, plays: 4, durationSeconds: 10);

        var plan = _builder.Build(billboard, TestData.Monday, 10, new[] { first, second });

        var firstThree = plan.Slots.Take(3).Select(slot => slot.PlacementId).ToArray();
        Assert.Contains(FirstId, firstThree);
        Assert.Contains(SecondId, firstThree);
    }

    [Fact]
    public void Build_WhenPlanExceedsCapacity_TruncatesAndWarns()
    {
        var billboard = TestData.Billboard();
        var first = TestData.Placement(id: FirstId, plays: 100, durationSeconds: 60);
        var second = TestData.Placement(id: SecondId, plays: 1, durationSeconds: 10);

        var plan = _builder.Build(billboard, TestData.Monday, 10, new[] { first, second });

        Assert.True(plan.IsOverCapacity);
        Assert.Contains(PlaybackPlanWarnings.CapacityExceeded, plan.Warnings);
        Assert.Contains(PlaybackPlanWarnings.PlanTruncated, plan.Warnings);
        Assert.True(plan.TotalDurationSeconds <= plan.CapacitySeconds);
        Assert.True(plan.PlannedPlays < plan.RequiredPlays);
    }

    [Fact]
    public void Build_IgnoresPausedAndNonActivePlacements()
    {
        var billboard = TestData.Billboard();
        var paused = TestData.Placement(
            id: FirstId,
            status: PlacementStatus.Paused);
        var outsideHour = TestData.Placement(
            id: SecondId,
            startTime: new TimeOnly(12, 0),
            endTime: new TimeOnly(15, 0));

        var plan = _builder.Build(billboard, TestData.Monday, 10, new[] { paused, outsideHour });

        Assert.Empty(plan.Slots);
        Assert.Equal(0, plan.RequiredPlays);
    }

    [Fact]
    public void Build_IgnoresOtherBillboard()
    {
        var billboard = TestData.Billboard();
        var otherBillboard = TestData.Billboard(id: Guid.NewGuid());
        var placement = TestData.Placement(billboardId: otherBillboard.Id);

        var plan = _builder.Build(billboard, TestData.Monday, 10, new[] { placement });

        Assert.Empty(plan.Slots);
    }

    [Fact]
    public void Build_ThreePlacements_EachGetsMinimumPlays()
    {
        var billboard = TestData.Billboard();
        var first = TestData.Placement(id: FirstId, plays: 2, durationSeconds: 10);
        var second = TestData.Placement(id: SecondId, plays: 3, durationSeconds: 10);
        var third = TestData.Placement(id: Guid.NewGuid(), plays: 4, durationSeconds: 10);

        var plan = _builder.Build(billboard, TestData.Monday, 10, new[] { first, second, third });

        Assert.Equal(9, plan.PlannedPlays);
        Assert.Equal(2, plan.Slots.Count(slot => slot.PlacementId == FirstId));
        Assert.Equal(3, plan.Slots.Count(slot => slot.PlacementId == SecondId));
        Assert.Equal(4, plan.Slots.Count(slot => slot.PlacementId == third.Id));
        Assert.Equal(90, plan.TotalDurationSeconds);
    }

    [Fact]
    public void Build_SequenceStartsAreMonotonic()
    {
        var billboard = TestData.Billboard();
        var first = TestData.Placement(id: FirstId, plays: 5, durationSeconds: 15);
        var second = TestData.Placement(id: SecondId, plays: 5, durationSeconds: 20);

        var plan = _builder.Build(billboard, TestData.Monday, 10, new[] { first, second });

        for (var index = 1; index < plan.Slots.Count; index++)
        {
            Assert.True(
                plan.Slots[index].StartOffsetSeconds >
                plan.Slots[index - 1].StartOffsetSeconds);
        }
    }
}
