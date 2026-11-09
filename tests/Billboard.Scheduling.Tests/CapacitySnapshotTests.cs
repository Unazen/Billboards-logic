using Billboard.Scheduling;

namespace Billboard.Scheduling.Tests;

public class CapacitySnapshotTests
{
    [Fact]
    public void Evaluate_EmptySchedule_ReturnsCandidateDemand()
    {
        var billboard = TestData.Billboard();
        var candidate = TestData.Placement(plays: 6, durationSeconds: 15);
        var snapshot = CapacitySnapshot.Create(billboard, Array.Empty<Placement>());

        var summary = snapshot.Evaluate(candidate);

        Assert.Equal(90, summary.CandidateSecondsPerHour);
        Assert.Equal(90, summary.PeakRequiredSecondsPerHour);
        Assert.Equal(0, summary.PeakExistingSecondsPerHour);
        Assert.Equal(5 * 9, summary.ApplicableHourBuckets);
        Assert.Equal(0, summary.OverflowBuckets);
        Assert.Equal(2.5, summary.PeakUtilizationPercent);
    }

    [Fact]
    public void Evaluate_OverlappingPlacementSumsDemand()
    {
        var billboard = TestData.Billboard();
        var existing = TestData.Placement(
            startTime: new TimeOnly(10, 0),
            endTime: new TimeOnly(12, 0),
            plays: 10,
            durationSeconds: 30);
        var candidate = TestData.Placement(plays: 6, durationSeconds: 15);
        var snapshot = CapacitySnapshot.Create(billboard, new[] { existing });

        var summary = snapshot.Evaluate(candidate);

        Assert.Equal(300, summary.PeakExistingSecondsPerHour);
        Assert.Equal(390, summary.PeakRequiredSecondsPerHour);
        Assert.Equal(0, summary.OverflowBuckets);
        Assert.Empty(summary.OverflowDetails);
    }

    [Fact]
    public void Evaluate_WhenDemandExceedsCapacity_ReportsOverflowBuckets()
    {
        var billboard = TestData.Billboard();
        var existing = TestData.Placement(
            startTime: new TimeOnly(10, 0),
            endTime: new TimeOnly(12, 0),
            plays: 100,
            durationSeconds: 35);
        var candidate = TestData.Placement(plays: 10, durationSeconds: 30);
        var snapshot = CapacitySnapshot.Create(billboard, new[] { existing });

        var summary = snapshot.Evaluate(candidate);

        Assert.Equal(3800, summary.PeakRequiredSecondsPerHour);
        Assert.Equal(10, summary.OverflowBuckets);
        Assert.All(summary.OverflowDetails, bucket => Assert.InRange(bucket.Hour, 10, 11));
        Assert.Equal(2000, summary.OverflowDetails.Sum(bucket => bucket.OverflowSeconds));
    }

    [Fact]
    public void Evaluate_NonOverlappingWindows_DoNotSumDemand()
    {
        var billboard = TestData.Billboard();
        var existing = TestData.Placement(
            startTime: new TimeOnly(12, 0),
            endTime: new TimeOnly(15, 0),
            plays: 100,
            durationSeconds: 30);
        var candidate = TestData.Placement(
            startTime: new TimeOnly(9, 0),
            endTime: new TimeOnly(12, 0),
            plays: 10,
            durationSeconds: 30);
        var snapshot = CapacitySnapshot.Create(billboard, new[] { existing });

        var summary = snapshot.Evaluate(candidate);

        Assert.Equal(300, summary.PeakRequiredSecondsPerHour);
        Assert.Equal(0, summary.OverflowBuckets);
    }

    [Theory]
    [InlineData(PlacementStatus.Draft)]
    [InlineData(PlacementStatus.PendingPayment)]
    [InlineData(PlacementStatus.Paused)]
    [InlineData(PlacementStatus.Completed)]
    [InlineData(PlacementStatus.Cancelled)]
    [InlineData(PlacementStatus.Failed)]
    public void Evaluate_NonOccupyingStatuses_AreIgnored(PlacementStatus status)
    {
        var billboard = TestData.Billboard();
        var existing = TestData.Placement(
            plays: 200,
            durationSeconds: 30,
            status: status);
        var candidate = TestData.Placement(plays: 6, durationSeconds: 15);
        var snapshot = CapacitySnapshot.Create(billboard, new[] { existing });

        var summary = snapshot.Evaluate(candidate);

        Assert.Equal(0, summary.PeakExistingSecondsPerHour);
        Assert.Equal(0, summary.OverflowBuckets);
    }

    [Fact]
    public void Evaluate_OtherBillboardPlacements_AreIgnored()
    {
        var billboard = TestData.Billboard();
        var otherBillboard = TestData.Billboard(id: Guid.NewGuid());
        var existing = TestData.Placement(
            billboardId: otherBillboard.Id,
            plays: 200,
            durationSeconds: 30);
        var candidate = TestData.Placement();
        var snapshot = CapacitySnapshot.Create(billboard, new[] { existing });

        var summary = snapshot.Evaluate(candidate);

        Assert.Equal(0, summary.PeakExistingSecondsPerHour);
    }

    [Fact]
    public void GetBlockingPlacementIds_ReturnsOverlappingPlacements()
    {
        var billboard = TestData.Billboard();
        var first = TestData.Placement(
            startTime: new TimeOnly(9, 0),
            endTime: new TimeOnly(12, 0));
        var second = TestData.Placement(
            startTime: new TimeOnly(15, 0),
            endTime: new TimeOnly(18, 0));
        var snapshot = CapacitySnapshot.Create(billboard, new[] { first, second });

        var blockers = snapshot.GetBlockingPlacementIds(TestData.Monday, 10);

        Assert.Single(blockers);
        Assert.Equal(first.Id, blockers[0]);
    }
}
