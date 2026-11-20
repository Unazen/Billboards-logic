using Billboard.Scheduling;

namespace Billboard.Scheduling.Tests;

internal static class TestData
{
    public static readonly DateOnly Monday = new(2026, 11, 2);
    public static readonly DateOnly Sunday = new(2026, 11, 8);
    public static readonly Guid BillboardId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    public static Billboard Billboard(
        int capacitySecondsPerHour = global::Billboard.Scheduling.Billboard.FullHourSeconds,
        BillboardStatus status = BillboardStatus.Online,
        Guid? id = null) => new()
        {
            Id = id ?? BillboardId,
            Name = "Тестовый билборд",
            Status = status,
            AdvertCapacitySecondsPerHour = capacitySecondsPerHour,
        };

    public static Placement Placement(
        Guid? id = null,
        Guid? billboardId = null,
        DateOnly? startDate = null,
        DateOnly? endDate = null,
        IReadOnlyList<DayOfWeek>? days = null,
        TimeOnly? startTime = null,
        TimeOnly? endTime = null,
        int plays = 6,
        int durationSeconds = 15,
        PlacementStatus status = PlacementStatus.Scheduled,
        decimal estimatedCost = 0)
    {
        return new Placement
        {
            Id = id ?? Guid.NewGuid(),
            OwnerId = Guid.Parse("22222222-2222-2222-2222-222222222222"),
            BillboardId = billboardId ?? BillboardId,
            VideoAssetId = Guid.Parse("33333333-3333-3333-3333-333333333333"),
            StartDate = startDate ?? Monday,
            EndDate = endDate ?? Sunday,
            DaysOfWeek = days ?? new[]
            {
                DayOfWeek.Monday,
                DayOfWeek.Tuesday,
                DayOfWeek.Wednesday,
                DayOfWeek.Thursday,
                DayOfWeek.Friday,
            },
            StartTime = startTime ?? new TimeOnly(9, 0),
            EndTime = endTime ?? new TimeOnly(18, 0),
            MinimumPlaysPerHour = plays,
            VideoDurationSeconds = durationSeconds,
            Status = status,
            EstimatedCost = estimatedCost,
        };
    }

    public static PlacementRequest Request(
        DateOnly? startDate = null,
        DateOnly? endDate = null,
        IReadOnlyList<DayOfWeek>? days = null,
        TimeOnly? startTime = null,
        TimeOnly? endTime = null,
        int plays = 6,
        int durationSeconds = 15,
        decimal estimatedCost = 0,
        decimal? availableBudget = null)
    {
        return new PlacementRequest
        {
            BillboardId = BillboardId,
            VideoAssetId = Guid.Parse("33333333-3333-3333-3333-333333333333"),
            StartDate = startDate ?? Monday,
            EndDate = endDate ?? Sunday,
            DaysOfWeek = days ?? new[]
            {
                DayOfWeek.Monday,
                DayOfWeek.Tuesday,
                DayOfWeek.Wednesday,
                DayOfWeek.Thursday,
                DayOfWeek.Friday,
            },
            StartTime = startTime ?? new TimeOnly(9, 0),
            EndTime = endTime ?? new TimeOnly(18, 0),
            MinimumPlaysPerHour = plays,
            VideoDurationSeconds = durationSeconds,
            EstimatedCost = estimatedCost,
            AvailableBudget = availableBudget,
        };
    }

    public static ScheduleValidationOptions WithoutAlternatives => new() { SuggestAlternatives = false };
}
