using Billboard.Scheduling;

namespace Billboard.Scheduling.Tests;

public class GroupPlacementServiceTests
{
    [Fact]
    public async Task ValidateAsync_AllBillboardsFree_CanPublish()
    {
        var second = TestData.Billboard(id: Guid.NewGuid());
        var (service, _, _) = CreateService(TestData.Billboard(), second);
        var group = Group(TestData.BillboardId, second.Id);

        var result = await service.ValidateAsync(group);

        Assert.True(result.CanPublish);
        Assert.Equal(2, result.Results.Count);
        Assert.Equal(2, result.AvailableBillboardCount);
    }

    [Fact]
    public async Task CreateDraftsAsync_AllFree_CreatesOneDraftPerBillboard()
    {
        var second = TestData.Billboard(id: Guid.NewGuid());
        var (service, repository, _) = CreateService(TestData.Billboard(), second);
        var ownerId = Guid.NewGuid();

        var result = await service.CreateDraftsAsync(Group(TestData.BillboardId, second.Id), ownerId);

        Assert.True(result.Success);
        Assert.Equal(2, result.Placements.Count);
        Assert.All(result.Placements, placement =>
        {
            Assert.Equal(ownerId, placement.OwnerId);
            Assert.Equal(PlacementStatus.Draft, placement.Status);
        });
        Assert.Equal(2, (await repository.ListAsync()).Count);
        Assert.NotNull(result.Validation);
        Assert.True(result.Validation!.CanPublish);
    }

    [Fact]
    public async Task CreateDraftsAsync_OneBillboardConflicts_CreatesNothing()
    {
        var second = TestData.Billboard(id: Guid.NewGuid());
        var (service, repository, _) = CreateService(TestData.Billboard(), second);
        var existing = TestData.Placement(
            billboardId: second.Id,
            startTime: new TimeOnly(10, 0),
            endTime: new TimeOnly(12, 0),
            plays: 100,
            durationSeconds: 35);
        await repository.AddAsync(existing);

        var result = await service.CreateDraftsAsync(
            new PlacementGroupRequest
            {
                BillboardIds = new[] { TestData.BillboardId, second.Id },
                Template = TestData.Request(plays: 10, durationSeconds: 30),
            },
            Guid.NewGuid());

        Assert.False(result.Success);
        Assert.Equal(GroupPlacementErrorCodes.ValidationFailed, result.ErrorCode);
        Assert.NotNull(result.Validation);
        Assert.False(result.Validation!.CanPublish);
        Assert.Single(await repository.ListAsync());
        Assert.DoesNotContain(
            await repository.ListAsync(),
            placement => placement.Status == PlacementStatus.Draft);
    }

    [Fact]
    public async Task CreateDraftsAsync_MissingBillboard_ReturnsValidationFailure()
    {
        var (service, repository, _) = CreateService(TestData.Billboard());

        var result = await service.CreateDraftsAsync(
            Group(TestData.BillboardId, Guid.NewGuid()),
            Guid.NewGuid());

        Assert.False(result.Success);
        Assert.Equal(GroupPlacementErrorCodes.ValidationFailed, result.ErrorCode);
        Assert.Empty(await repository.ListAsync());
    }

    [Fact]
    public async Task CreateDraftsAsync_DuplicateBillboards_CreatesOne()
    {
        var (service, repository, _) = CreateService(TestData.Billboard());

        var result = await service.CreateDraftsAsync(
            Group(TestData.BillboardId, TestData.BillboardId),
            Guid.NewGuid());

        Assert.True(result.Success);
        Assert.Single(result.Placements);
        Assert.Single(await repository.ListAsync());
    }

    [Fact]
    public async Task CreateDraftsAsync_EmptyGroup_ReturnsEmptyGroupError()
    {
        var (service, _, _) = CreateService(TestData.Billboard());

        var result = await service.CreateDraftsAsync(
            new PlacementGroupRequest { Template = TestData.Request() },
            Guid.NewGuid());

        Assert.False(result.Success);
        Assert.Equal(GroupPlacementErrorCodes.EmptyGroup, result.ErrorCode);
    }

    [Fact]
    public async Task CreateDraftsAsync_WhenSaveFails_RollsBackCreatedDrafts()
    {
        var inner = new InMemoryPlacementRepository();
        var repository = new FailOnSecondAddRepository(inner);
        var catalog = new InMemoryBillboardCatalog(
            TestData.Billboard(),
            TestData.Billboard(id: Guid.NewGuid()));
        var service = new GroupPlacementService(
            repository,
            catalog,
            options: new ScheduleValidationOptions { SuggestAlternatives = false });
        var billboardIds = (await catalog.ListAsync()).Select(billboard => billboard.Id).ToArray();

        var result = await service.CreateDraftsAsync(
            new PlacementGroupRequest
            {
                BillboardIds = billboardIds,
                Template = TestData.Request(),
            },
            Guid.NewGuid());

        Assert.False(result.Success);
        Assert.Equal(GroupPlacementErrorCodes.SaveFailed, result.ErrorCode);
        Assert.Empty(await inner.ListAsync());
    }

    private static (GroupPlacementService Service, InMemoryPlacementRepository Repository, InMemoryBillboardCatalog Catalog)
        CreateService(params Billboard[] billboards)
    {
        var repository = new InMemoryPlacementRepository();
        var catalog = new InMemoryBillboardCatalog(billboards);
        var service = new GroupPlacementService(
            repository,
            catalog,
            options: new ScheduleValidationOptions { SuggestAlternatives = false });

        return (service, repository, catalog);
    }

    private static PlacementGroupRequest Group(params Guid[] billboardIds) => new()
    {
        BillboardIds = billboardIds,
        Template = TestData.Request(),
    };

    private sealed class FailOnSecondAddRepository : IPlacementRepository
    {
        private readonly IPlacementRepository _inner;
        private int _adds;

        public FailOnSecondAddRepository(IPlacementRepository inner) => _inner = inner;

        public Task<Placement?> GetAsync(Guid id, CancellationToken cancellationToken = default) =>
            _inner.GetAsync(id, cancellationToken);

        public Task<IReadOnlyList<Placement>> ListAsync(CancellationToken cancellationToken = default) =>
            _inner.ListAsync(cancellationToken);

        public Task<IReadOnlyList<Placement>> GetByBillboardAsync(
            Guid billboardId,
            DateOnly from,
            DateOnly to,
            CancellationToken cancellationToken = default) =>
            _inner.GetByBillboardAsync(billboardId, from, to, cancellationToken);

        public Task AddAsync(Placement placement, CancellationToken cancellationToken = default)
        {
            if (++_adds == 2)
            {
                throw new InvalidOperationException("Тестовая ошибка сохранения.");
            }

            return _inner.AddAsync(placement, cancellationToken);
        }

        public Task<bool> UpdateAsync(
            Placement placement,
            long expectedVersion,
            CancellationToken cancellationToken = default) =>
            _inner.UpdateAsync(placement, expectedVersion, cancellationToken);

        public Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default) =>
            _inner.DeleteAsync(id, cancellationToken);
    }
}
