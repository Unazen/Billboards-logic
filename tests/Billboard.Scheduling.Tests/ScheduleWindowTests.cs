using Billboard.Scheduling;

namespace Billboard.Scheduling.Tests;

public class ScheduleWindowTests
{
    [Fact]
    public void Duration_ForDayWindow_ReturnsHours()
    {
        var duration = ScheduleWindow.GetDuration(new TimeOnly(9, 0), new TimeOnly(18, 0));

        Assert.Equal(TimeSpan.FromHours(9), duration);
    }

    [Fact]
    public void Duration_ForOvernightWindow_ReturnsHours()
    {
        var duration = ScheduleWindow.GetDuration(new TimeOnly(22, 0), new TimeOnly(2, 0));

        Assert.Equal(TimeSpan.FromHours(4), duration);
    }

    [Fact]
    public void StartDayHours_ExcludesEndBoundary()
    {
        var hours = ScheduleWindow.GetStartDayHours(new TimeOnly(9, 0), new TimeOnly(18, 0));

        Assert.Equal(new[] { 9, 10, 11, 12, 13, 14, 15, 16, 17 }, hours);
    }

    [Fact]
    public void StartDayHours_IncludesPartiallyCoveredLastHour()
    {
        var hours = ScheduleWindow.GetStartDayHours(new TimeOnly(9, 30), new TimeOnly(10, 30));

        Assert.Equal(new[] { 9, 10 }, hours);
    }

    [Fact]
    public void SpillHours_ForOvernightWindow_AreAfterMidnight()
    {
        var hours = ScheduleWindow.GetSpillHours(new TimeOnly(22, 0), new TimeOnly(2, 0));

        Assert.Equal(new[] { 0, 1 }, hours);
    }

    [Fact]
    public void SpillHours_WhenEndsAtMidnight_AreEmpty()
    {
        var hours = ScheduleWindow.GetSpillHours(new TimeOnly(22, 0), new TimeOnly(0, 0));

        Assert.Empty(hours);
    }

    [Fact]
    public void Buckets_WeekdaysOnly_SkipsWeekend()
    {
        var placement = TestData.Placement();

        var buckets = ScheduleWindow.GetBuckets(placement);

        Assert.Equal(5 * 9, buckets.Count);
        Assert.DoesNotContain(buckets, bucket => bucket.Date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday);
        Assert.Equal(TestData.Monday, buckets[0].Date);
    }

    [Fact]
    public void Buckets_Overnight_AddsEarlyHoursOfNextDay()
    {
        var placement = TestData.Placement(
            endDate: TestData.Monday.AddDays(1),
            startTime: new TimeOnly(22, 0),
            endTime: new TimeOnly(2, 0),
            days: new[] { DayOfWeek.Monday });

        var buckets = ScheduleWindow.GetBuckets(placement);

        Assert.Equal(
            new[]
            {
                (TestData.Monday, 22),
                (TestData.Monday, 23),
                (TestData.Monday.AddDays(1), 0),
                (TestData.Monday.AddDays(1), 1),
            },
            buckets);
    }

    [Fact]
    public void Buckets_OvernightOnLastDate_DoNotSpillBeyondEndDate()
    {
        var placement = TestData.Placement(
            endDate: TestData.Monday,
            startTime: new TimeOnly(22, 0),
            endTime: new TimeOnly(2, 0),
            days: new[] { DayOfWeek.Monday });

        var buckets = ScheduleWindow.GetBuckets(placement);

        Assert.Equal(new[] { (TestData.Monday, 22), (TestData.Monday, 23) }, buckets);
    }

    [Fact]
    public void Covers_OvernightSpill_OnNextDay()
    {
        var placement = TestData.Placement(
            endDate: TestData.Monday.AddDays(1),
            startTime: new TimeOnly(22, 0),
            endTime: new TimeOnly(2, 0),
            days: new[] { DayOfWeek.Monday });

        Assert.True(ScheduleWindow.Covers(placement, TestData.Monday, 23));
        Assert.True(ScheduleWindow.Covers(placement, TestData.Monday.AddDays(1), 1));
        Assert.False(ScheduleWindow.Covers(placement, TestData.Monday.AddDays(1), 2));
    }

    [Fact]
    public void Buckets_WhenDaysAreEmpty_AreEmpty()
    {
        var placement = TestData.Placement(days: Array.Empty<DayOfWeek>());

        Assert.Empty(ScheduleWindow.GetBuckets(placement));
    }

    [Fact]
    public void Buckets_WhenWindowIsEmpty_AreEmpty()
    {
        var placement = TestData.Placement(
            startTime: new TimeOnly(9, 0),
            endTime: new TimeOnly(9, 0));

        Assert.Empty(ScheduleWindow.GetBuckets(placement));
    }

    [Fact]
    public void Buckets_ForFullDay_Contain24Hours()
    {
        var placement = TestData.Placement(
            endDate: TestData.Monday,
            startTime: new TimeOnly(0, 0),
            endTime: new TimeOnly(23, 59),
            days: new[] { DayOfWeek.Monday });

        var buckets = ScheduleWindow.GetBuckets(placement);

        Assert.Equal(24, buckets.Count);
        Assert.Equal(0, buckets[0].Hour);
        Assert.Equal(23, buckets[^1].Hour);
    }
}
