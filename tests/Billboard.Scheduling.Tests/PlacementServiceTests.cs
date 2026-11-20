using Billboard.Scheduling;

namespace Billboard.Scheduling.Tests;

public class PlacementServiceTests
{
    private static (PlacementService Service, InMemoryPlacementRepository Repository, InMemoryBillboardCatalog Catalog)
        CreateService(params Billboard[] billboards)
    {
        var repository = new InMemoryPlacementRepository();
        var catalog = new InMemoryBillboardCatalog(billboards.Length == 0
            ? new[] { TestData.Billboard() }
            : billboards);
        var service = new PlacementService(
            repository,
            catalog,
            options: new ScheduleValidationOptions { SuggestAlternatives = false });

        return (service, repository, catalog);
    }

    [Fact]
    public async Task CreateDraft_WithValidRequest_SavesDraft()
    {
        var (service, repository, _) = CreateService();

        var result = await service.CreateDraftAsync(TestData.Request(), Guid.NewGuid());

        Assert.True(result.Success);
        Assert.NotNull(result.Placement);
        Assert.Equal(PlacementStatus.Draft, result.Placement!.Status);
        Assert.Single(await repository.ListAsync());
    }

    [Fact]
    public async Task CreateDraft_WithInvalidShape_ReturnsValidationError()
    {
        var (service, repository, _) = CreateService();

        var result = await service.CreateDraftAsync(
            TestData.Request(days: Array.Empty<DayOfWeek>()),
            Guid.NewGuid());

        Assert.False(result.Success);
        Assert.Equal(PlacementErrorCodes.ValidationFailed, result.ErrorCode);
        Assert.Empty(await repository.ListAsync());
    }

    [Fact]
    public async Task CreateDraft_ForUnknownBillboard_ReturnsNotFound()
    {
        var (service, _, _) = CreateService();

        var request = TestData.Request() with { BillboardId = Guid.NewGuid() };
        var result = await service.CreateDraftAsync(request, Guid.NewGuid());

        Assert.False(result.Success);
        Assert.Equal(ConflictCodes.BillboardNotFound, result.ErrorCode);
    }

    [Fact]
    public async Task Publish_WhenFree_TransitionsToScheduled()
    {
        var (service, _, _) = CreateService();
        var draft = await service.CreateDraftAsync(TestData.Request(), Guid.NewGuid());

        var result = await service.PublishAsync(draft.Placement!.Id);

        Assert.True(result.Success);
        Assert.Equal(PlacementStatus.Scheduled, result.Placement!.Status);
        Assert.NotNull(result.Validation);
        Assert.True(result.Validation!.IsAvailable);
    }

    [Fact]
    public async Task Publish_WhenConflict_KeepsDraftAndReturnsValidation()
    {
        var (service, repository, _) = CreateService();
        var existing = TestData.Placement(
            startTime: new TimeOnly(10, 0),
            endTime: new TimeOnly(12, 0),
            plays: 100,
            durationSeconds: 35);
        await repository.AddAsync(existing);

        var draft = await service.CreateDraftAsync(
            TestData.Request(plays: 10, durationSeconds: 30),
            Guid.NewGuid());

        var result = await service.PublishAsync(draft.Placement!.Id);

        Assert.False(result.Success);
        Assert.Equal(PlacementErrorCodes.ValidationFailed, result.ErrorCode);
        Assert.NotNull(result.Validation);
        Assert.False(result.Validation!.IsAvailable);

        var stored = await repository.GetAsync(draft.Placement.Id);
        Assert.Equal(PlacementStatus.Draft, stored!.Status);
    }

    [Fact]
    public async Task Publish_WhenBudgetIsInsufficient_ReturnsBudgetConflict()
    {
        var (service, _, _) = CreateService();
        var draft = await service.CreateDraftAsync(
            TestData.Request(estimatedCost: 100),
            Guid.NewGuid());

        var result = await service.PublishAsync(draft.Placement!.Id, availableBudget: 50);

        Assert.False(result.Success);
        Assert.NotNull(result.Validation);
        Assert.True(result.Validation!.HasConflict(ConflictCodes.InsufficientBudget));
    }

    [Fact]
    public async Task UpdateSchedule_WhenValid_ChangesFieldsAndVersion()
    {
        var (service, repository, _) = CreateService();
        var draft = await service.CreateDraftAsync(TestData.Request(), Guid.NewGuid());
        var originalVersion = draft.Placement!.Version;

        var updatedRequest = TestData.Request(
            startTime: new TimeOnly(8, 0),
            endTime: new TimeOnly(12, 0),
            plays: 4,
            durationSeconds: 20);

        var result = await service.UpdateScheduleAsync(draft.Placement.Id, updatedRequest);

        Assert.True(result.Success);
        Assert.Equal(new TimeOnly(8, 0), result.Placement!.StartTime);
        Assert.Equal(new TimeOnly(12, 0), result.Placement.EndTime);
        Assert.Equal(originalVersion + 1, result.Placement.Version);

        var stored = await repository.GetAsync(draft.Placement.Id);
        Assert.Equal(new TimeOnly(8, 0), stored!.StartTime);
    }

    [Fact]
    public async Task UpdateSchedule_WhenConflict_LeavesPlacementUnchanged()
    {
        var (service, repository, _) = CreateService();
        var existing = TestData.Placement(
            startTime: new TimeOnly(10, 0),
            endTime: new TimeOnly(12, 0),
            plays: 100,
            durationSeconds: 35);
        await repository.AddAsync(existing);

        var draft = await service.CreateDraftAsync(TestData.Request(), Guid.NewGuid());
        var original = await repository.GetAsync(draft.Placement!.Id);

        var result = await service.UpdateScheduleAsync(
            draft.Placement.Id,
            TestData.Request(plays: 10, durationSeconds: 30));

        Assert.False(result.Success);
        Assert.Equal(PlacementErrorCodes.ValidationFailed, result.ErrorCode);

        var stored = await repository.GetAsync(draft.Placement.Id);
        Assert.Equal(original!.StartTime, stored!.StartTime);
        Assert.Equal(original.Version, stored.Version);
    }

    [Fact]
    public async Task PauseAndResume_RestorePreviousStatus()
    {
        var (service, repository, _) = CreateService();
        var draft = await service.CreateDraftAsync(TestData.Request(), Guid.NewGuid());
        var published = await service.PublishAsync(draft.Placement!.Id);

        var paused = await service.PauseAsync(published.Placement!.Id);

        Assert.True(paused.Success);
        Assert.Equal(PlacementStatus.Paused, paused.Placement!.Status);
        Assert.Equal(PlacementStatus.Scheduled, paused.Placement.StatusBeforePause);

        var resumed = await service.ResumeAsync(paused.Placement.Id);

        Assert.True(resumed.Success);
        Assert.Equal(PlacementStatus.Scheduled, resumed.Placement!.Status);
        Assert.Null(resumed.Placement.StatusBeforePause);

        var stored = await repository.GetAsync(paused.Placement.Id);
        Assert.Equal(PlacementStatus.Scheduled, stored!.Status);
    }

    [Fact]
    public async Task Cancel_MakesPlacementTerminal()
    {
        var (service, repository, _) = CreateService();
        var draft = await service.CreateDraftAsync(TestData.Request(), Guid.NewGuid());
        var published = await service.PublishAsync(draft.Placement!.Id);

        var cancelled = await service.CancelAsync(published.Placement!.Id);

        Assert.True(cancelled.Success);
        Assert.Equal(PlacementStatus.Cancelled, cancelled.Placement!.Status);

        var repeat = await service.CancelAsync(cancelled.Placement.Id);
        Assert.True(repeat.Success);

        var publishAgain = await service.PublishAsync(cancelled.Placement.Id);
        Assert.False(publishAgain.Success);
        Assert.Equal(PlacementErrorCodes.InvalidStatus, publishAgain.ErrorCode);

        var stored = await repository.GetAsync(cancelled.Placement.Id);
        Assert.Equal(PlacementStatus.Cancelled, stored!.Status);
    }

    [Fact]
    public async Task UpdateSchedule_ForCancelledPlacement_IsRejected()
    {
        var (service, _, _) = CreateService();
        var draft = await service.CreateDraftAsync(TestData.Request(), Guid.NewGuid());
        var cancelled = await service.CancelAsync(draft.Placement!.Id);

        var result = await service.UpdateScheduleAsync(
            cancelled.Placement!.Id,
            TestData.Request(startTime: new TimeOnly(8, 0), endTime: new TimeOnly(09, 0)));

        Assert.False(result.Success);
        Assert.Equal(PlacementErrorCodes.InvalidStatus, result.ErrorCode);
    }

    [Fact]
    public async Task ListAsync_FiltersByOwnerAndBillboard()
    {
        var secondBillboard = TestData.Billboard(id: Guid.NewGuid());
        var (service, _, _) = CreateService(TestData.Billboard(), secondBillboard);
        var owner = Guid.NewGuid();
        var otherOwner = Guid.NewGuid();

        await service.CreateDraftAsync(TestData.Request(), owner);
        await service.CreateDraftAsync(
            TestData.Request() with { BillboardId = secondBillboard.Id },
            owner);
        await service.CreateDraftAsync(TestData.Request(), otherOwner);

        var byOwner = await service.ListAsync(ownerId: owner);
        var byBillboard = await service.ListAsync(billboardId: secondBillboard.Id);

        Assert.Equal(2, byOwner.Count);
        Assert.Single(byBillboard);
        Assert.Equal(secondBillboard.Id, byBillboard[0].BillboardId);
    }
}
