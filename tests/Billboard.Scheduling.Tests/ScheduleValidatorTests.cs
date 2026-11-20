using Billboard.Scheduling;

namespace Billboard.Scheduling.Tests;

public class ScheduleValidatorTests
{
    private readonly ScheduleValidator _validator = new();

    [Fact]
    public void Validate_WhenScheduleIsFree_ReturnsAvailable()
    {
        var billboard = TestData.Billboard();
        var request = TestData.Request();

        var result = _validator.Validate(request, billboard, Array.Empty<Placement>());

        Assert.True(result.IsAvailable);
        Assert.Empty(result.Conflicts);
        Assert.Empty(result.Alternatives);
        Assert.Equal(90, result.Summary.PeakRequiredSecondsPerHour);
        Assert.Equal(5 * 9, result.Summary.ApplicableHourBuckets);
    }

    [Fact]
    public void Validate_WhenBillboardOffline_ReturnsConflict()
    {
        var billboard = TestData.Billboard(status: BillboardStatus.Offline);
        var request = TestData.Request();

        var result = _validator.Validate(request, billboard, Array.Empty<Placement>());

        Assert.False(result.IsAvailable);
        Assert.True(result.HasConflict(ConflictCodes.BillboardOffline));
    }

    [Fact]
    public void Validate_WhenQuotaExceedsCapacity_ReturnsQuotaConflict()
    {
        var billboard = TestData.Billboard();
        var request = TestData.Request(plays: 100, durationSeconds: 60);
        var result = _validator.Validate(
            request,
            billboard,
            Array.Empty<Placement>(),
            TestData.WithoutAlternatives);

        Assert.False(result.IsAvailable);
        Assert.True(result.HasConflict(ConflictCodes.QuotaExceedsCapacity));
        Assert.Empty(result.Alternatives);
    }

    [Fact]
    public void Validate_WhenCapacityIsExceeded_ReturnsConflictAndAlternatives()
    {
        var billboard = TestData.Billboard();
        var existing = TestData.Placement(
            startTime: new TimeOnly(10, 0),
            endTime: new TimeOnly(12, 0),
            plays: 100,
            durationSeconds: 35);
        var request = TestData.Request(plays: 10, durationSeconds: 30);

        var result = _validator.Validate(request, billboard, new[] { existing });

        Assert.False(result.IsAvailable);
        var conflict = result.Conflicts.First(
            item => item.Code == ConflictCodes.InsufficientAdvertisingCapacity);
        Assert.True(conflict.Hour is 10 or 11);
        Assert.Equal(3800, conflict.RequiredSeconds);
        Assert.NotEmpty(result.Alternatives);
    }

    [Fact]
    public void Validate_AlternativeSlot_IsActuallyAvailable()
    {
        var billboard = TestData.Billboard();
        var existing = TestData.Placement(
            startTime: new TimeOnly(10, 0),
            endTime: new TimeOnly(12, 0),
            plays: 100,
            durationSeconds: 35);
        var request = TestData.Request(plays: 10, durationSeconds: 30);

        var first = _validator.Validate(request, billboard, new[] { existing });
        var alternative = Assert.Single(first.Alternatives.Take(1));

        var alternativeRequest = request with
        {
            StartDate = alternative.StartDate,
            EndDate = alternative.EndDate,
            StartTime = alternative.StartTime,
            EndTime = alternative.EndTime,
        };

        var second = _validator.Validate(
            alternativeRequest,
            billboard,
            new[] { existing },
            TestData.WithoutAlternatives);

        Assert.True(second.IsAvailable, string.Join("; ", second.Conflicts.Select(conflict => conflict.Message)));
        Assert.True(alternative.FreeSecondsPerHour >= 0);
    }

    [Fact]
    public void Validate_WithAlternativesDisabled_ReturnsNoAlternatives()
    {
        var billboard = TestData.Billboard();
        var existing = TestData.Placement(plays: 200, durationSeconds: 30);
        var request = TestData.Request();

        var result = _validator.Validate(request, billboard, new[] { existing }, TestData.WithoutAlternatives);

        Assert.False(result.IsAvailable);
        Assert.Empty(result.Alternatives);
    }

    [Fact]
    public void Validate_WhenDatesAreReversed_ReturnsInvalidDateRange()
    {
        var request = TestData.Request(startDate: TestData.Monday, endDate: TestData.Monday.AddDays(-1));

        var result = _validator.Validate(request, TestData.Billboard(), Array.Empty<Placement>());

        Assert.True(result.HasConflict(ConflictCodes.InvalidDateRange));
    }

    [Fact]
    public void Validate_WhenHorizonIsTooLong_ReturnsDateRangeTooLong()
    {
        var options = new ScheduleValidationOptions { MaxHorizonDays = 3, SuggestAlternatives = false };
        var request = TestData.Request();

        var result = _validator.Validate(request, TestData.Billboard(), Array.Empty<Placement>(), options);

        Assert.True(result.HasConflict(ConflictCodes.DateRangeTooLong));
    }

    [Fact]
    public void Validate_WhenDaysAreEmpty_ReturnsEmptyDaysConflict()
    {
        var request = TestData.Request(days: Array.Empty<DayOfWeek>());

        var result = _validator.Validate(request, TestData.Billboard(), Array.Empty<Placement>());

        Assert.True(result.HasConflict(ConflictCodes.EmptyDaysOfWeek));
    }

    [Fact]
    public void Validate_WhenTimeWindowIsEmpty_ReturnsInvalidTimeWindow()
    {
        var request = TestData.Request(startTime: new TimeOnly(9, 0), endTime: new TimeOnly(9, 0));

        var result = _validator.Validate(request, TestData.Billboard(), Array.Empty<Placement>());

        Assert.True(result.HasConflict(ConflictCodes.InvalidTimeWindow));
    }

    [Fact]
    public void Validate_WhenDurationIsNotPositive_ReturnsInvalidDuration()
    {
        var request = TestData.Request(durationSeconds: 0);

        var result = _validator.Validate(request, TestData.Billboard(), Array.Empty<Placement>());

        Assert.True(result.HasConflict(ConflictCodes.InvalidDuration));
    }

    [Fact]
    public void Validate_WhenPlaysAreNotPositive_ReturnsInvalidPlays()
    {
        var request = TestData.Request(plays: 0);

        var result = _validator.Validate(request, TestData.Billboard(), Array.Empty<Placement>());

        Assert.True(result.HasConflict(ConflictCodes.InvalidPlaysPerHour));
    }

    [Fact]
    public void Validate_WhenCostIsNegative_ReturnsInvalidCost()
    {
        var request = TestData.Request(estimatedCost: -1);

        var result = _validator.Validate(request, TestData.Billboard(), Array.Empty<Placement>());

        Assert.True(result.HasConflict(ConflictCodes.InvalidCost));
    }

    [Fact]
    public void Validate_WhenBudgetIsInsufficient_ReturnsBudgetConflict()
    {
        var request = TestData.Request(estimatedCost: 100, availableBudget: 50);

        var result = _validator.Validate(request, TestData.Billboard(), Array.Empty<Placement>());

        Assert.True(result.HasConflict(ConflictCodes.InsufficientBudget));
        Assert.Empty(result.Alternatives);
    }

    [Fact]
    public void Validate_WhenBudgetIsEnough_ReturnsAvailable()
    {
        var request = TestData.Request(estimatedCost: 100, availableBudget: 150);

        var result = _validator.Validate(request, TestData.Billboard(), Array.Empty<Placement>());

        Assert.True(result.IsAvailable);
    }

    [Fact]
    public void Validate_AdjacentWindows_DoNotConflict()
    {
        var existing = TestData.Placement(
            startTime: new TimeOnly(12, 0),
            endTime: new TimeOnly(15, 0),
            plays: 120,
            durationSeconds: 30);
        var request = TestData.Request(
            startTime: new TimeOnly(9, 0),
            endTime: new TimeOnly(12, 0),
            plays: 120,
            durationSeconds: 30);

        var result = _validator.Validate(
            request,
            TestData.Billboard(),
            new[] { existing },
            TestData.WithoutAlternatives);

        Assert.True(result.IsAvailable);
    }

    [Fact]
    public void Validate_Group_AllBillboardsFree_CanPublish()
    {
        var second = TestData.Billboard(id: Guid.NewGuid());
        var billboards = new Dictionary<Guid, Billboard>
        {
            [TestData.BillboardId] = TestData.Billboard(),
            [second.Id] = second,
        };
        var group = new PlacementGroupRequest
        {
            BillboardIds = new[] { TestData.BillboardId, second.Id },
            Template = TestData.Request(),
        };

        var result = _validator.ValidateGroup(
            group,
            billboards,
            new Dictionary<Guid, IReadOnlyList<Placement>>(),
            TestData.WithoutAlternatives);

        Assert.True(result.CanPublish);
        Assert.Equal(2, result.Results.Count);
        Assert.Equal(2, result.AvailableBillboardCount);
    }

    [Fact]
    public void Validate_Group_OneBillboardConflicts_CannotPublish()
    {
        var second = TestData.Billboard(id: Guid.NewGuid());
        var billboards = new Dictionary<Guid, Billboard>
        {
            [TestData.BillboardId] = TestData.Billboard(),
            [second.Id] = second,
        };
        var existing = TestData.Placement(
            startTime: new TimeOnly(10, 0),
            endTime: new TimeOnly(12, 0),
            plays: 100,
            durationSeconds: 35);
        var group = new PlacementGroupRequest
        {
            BillboardIds = new[] { TestData.BillboardId, second.Id },
            Template = TestData.Request(plays: 10, durationSeconds: 30),
        };

        var result = _validator.ValidateGroup(
            group,
            billboards,
            new Dictionary<Guid, IReadOnlyList<Placement>>
            {
                [TestData.BillboardId] = new[] { existing },
            },
            TestData.WithoutAlternatives);

        Assert.False(result.CanPublish);
        Assert.Equal(1, result.AvailableBillboardCount);
        Assert.Contains(result.Results, item => !item.Validation.IsAvailable);
    }

    [Fact]
    public void Validate_Group_UnknownBillboard_ReturnsNotFound()
    {
        var unknown = Guid.NewGuid();
        var group = new PlacementGroupRequest
        {
            BillboardIds = new[] { unknown },
            Template = TestData.Request(),
        };

        var result = _validator.ValidateGroup(
            group,
            new Dictionary<Guid, Billboard>(),
            new Dictionary<Guid, IReadOnlyList<Placement>>());

        Assert.False(result.CanPublish);
        Assert.True(result.Results[0].Validation.HasConflict(ConflictCodes.BillboardNotFound));
    }
}
