using Billboard.Scheduling;

namespace Billboard.Scheduling.Tests;

public class ScheduleSerializerTests
{
    private readonly ScheduleSerializer _serializer = new();

    [Fact]
    public void ExportAndImport_PreserveSchedule()
    {
        var groupId = Guid.NewGuid();
        var placement = TestData.Placement(
            startTime: new TimeOnly(8, 30),
            endTime: new TimeOnly(17, 45),
            plays: 4,
            durationSeconds: 20);
        var data = new ScheduleExport
        {
            Placements = new[] { placement },
            Groups = new[]
            {
                new PlacementGroup
                {
                    Id = groupId,
                    Name = "Центр",
                    BillboardIds = new[] { TestData.BillboardId },
                },
            },
        };

        var json = _serializer.Export(data);
        var imported = _serializer.Import(json);

        var restored = Assert.Single(imported.Placements);
        Assert.Equal(placement.Id, restored.Id);
        Assert.Equal(placement.BillboardId, restored.BillboardId);
        Assert.Equal(placement.StartDate, restored.StartDate);
        Assert.Equal(placement.EndDate, restored.EndDate);
        Assert.Equal(placement.StartTime, restored.StartTime);
        Assert.Equal(placement.EndTime, restored.EndTime);
        Assert.Equal(placement.MinimumPlaysPerHour, restored.MinimumPlaysPerHour);
        Assert.Equal(placement.VideoDurationSeconds, restored.VideoDurationSeconds);
        Assert.Equal(placement.DaysOfWeek, restored.DaysOfWeek);

        var group = Assert.Single(imported.Groups);
        Assert.Equal(groupId, group.Id);
        Assert.Equal("Центр", group.Name);
    }

    [Fact]
    public void TryImport_InvalidJson_ReturnsError()
    {
        var result = _serializer.TryImport("{ not json");

        Assert.False(result.Success);
        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public void TryImport_UnsupportedSchema_ReturnsError()
    {
        var json = _serializer.Export(new ScheduleExport());
        json = json.Replace("\"schemaVersion\": 1", "\"schemaVersion\": 99");

        var result = _serializer.TryImport(json);

        Assert.False(result.Success);
        Assert.Contains(result.Errors, error => error.Contains("99", StringComparison.Ordinal));
    }

    [Fact]
    public void TryImport_InvalidPlacement_ReturnsDetailedErrors()
    {
        var placement = TestData.Placement() with
        {
            BillboardId = Guid.Empty,
            DaysOfWeek = Array.Empty<DayOfWeek>(),
            VideoDurationSeconds = 0,
        };
        var json = _serializer.Export(new ScheduleExport { Placements = new[] { placement } });

        var result = _serializer.TryImport(json);

        Assert.False(result.Success);
        Assert.Contains(result.Errors, error => error.Contains("BillboardId", StringComparison.Ordinal));
        Assert.Contains(result.Errors, error => error.Contains("дни недели", StringComparison.Ordinal));
        Assert.Contains(result.Errors, error => error.Contains("длительность", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void TryImport_DuplicatePlacementIds_ReturnsError()
    {
        var id = Guid.NewGuid();
        var first = TestData.Placement(id: id);
        var second = TestData.Placement(id: id);
        var json = _serializer.Export(new ScheduleExport { Placements = new[] { first, second } });

        var result = _serializer.TryImport(json);

        Assert.False(result.Success);
        Assert.Contains(result.Errors, error => error.Contains("Повторяющийся", StringComparison.Ordinal));
    }

    [Fact]
    public void TryImport_EmptyPlacementId_AssignsNewId()
    {
        var placement = TestData.Placement(id: Guid.Empty);
        var json = _serializer.Export(new ScheduleExport { Placements = new[] { placement } });

        var result = _serializer.TryImport(json);

        Assert.True(result.Success);
        Assert.NotEqual(Guid.Empty, result.Data!.Placements[0].Id);
    }
}
