using Billboard.Application.Common;
using Billboard.Application.Contracts;
using Billboard.Infrastructure.Scheduling;
using Billboard.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace Billboard.UnitTests;

public class BillboardOwnershipTests
{
    private static BillboardService Service(TestDb t) => new(t.New());

    private static PlacementService Placements(TestDb t)
    {
        var db = t.New();
        return new PlacementService(db, new CapacityScheduleValidator(db),
            new BillingService(db, new WalletLedger(db), new TariffService(db)));
    }

    private static BillboardUpsertRequest Request(string name, Guid? ownerId = null) => new(
        name, "Адрес", null, null, null, null, 0.05m, "UTC", ownerId);

    [Fact]
    public async Task Owner_sees_billboard_in_mine_list_only()
    {
        using var t = new TestDb();
        var owner = await t.AddUserAsync();
        var stranger = await t.AddUserAsync();

        var created = await Service(t).CreateAsync(Request("Мой билборд", owner));

        Assert.Equal(owner, created.Billboard.OwnerId);
        Assert.Single(await Service(t).ListAsync(false, owner));
        Assert.Empty(await Service(t).ListAsync(false, stranger));
    }

    [Fact]
    public async Task Rental_placement_makes_billboard_mine()
    {
        using var t = new TestDb();
        await t.SeedDefaultTariffAsync();
        var client = await t.AddUserAsync();
        var stranger = await t.AddUserAsync();
        var (billboardId, videoId) = await t.AddBillboardWithVideoAsync(client, "Аренда");

        var monday = new DateTimeOffset(2026, 11, 2, 0, 0, 0, TimeSpan.Zero);
        await Placements(t).CreateAsync(client, new PlacementUpsertRequest(
            videoId, billboardId, monday, monday.AddDays(1),
            new[] { "Monday" }, new TimeOnly(9, 0), new TimeOnly(18, 0), 6));

        var mine = await Service(t).ListAsync(false, client);
        Assert.Single(mine);
        Assert.Empty(await Service(t).ListAsync(false, stranger));
    }

    [Fact]
    public async Task Unknown_owner_is_rejected()
    {
        using var t = new TestDb();

        var ex = await Assert.ThrowsAsync<AppException>(() =>
            Service(t).CreateAsync(Request("Билборд", Guid.NewGuid())));

        Assert.Equal(404, ex.StatusCode);
    }
}

public class BillboardGroupOwnershipTests
{
    private static BillboardGroupService Groups(TestDb t) => new(t.New());

    private static PlacementService Placements(TestDb t)
    {
        var db = t.New();
        return new PlacementService(db, new CapacityScheduleValidator(db),
            new BillingService(db, new WalletLedger(db), new TariffService(db)));
    }

    [Fact]
    public async Task Group_owner_sees_it_in_mine_list()
    {
        using var t = new TestDb();
        var owner = await t.AddUserAsync();
        var stranger = await t.AddUserAsync();

        await Groups(t).CreateAsync(new BillboardGroupUpsertRequest("Моя группа", owner));

        Assert.Single(await Groups(t).ListAsync(owner));
        Assert.Empty(await Groups(t).ListAsync(stranger));
    }

    [Fact]
    public async Task Group_with_owned_billboard_is_mine()
    {
        using var t = new TestDb();
        var owner = await t.AddUserAsync();
        var stranger = await t.AddUserAsync();

        var billboard = await t.AddBillboardAsync("Мой билборд", ownerId: owner);
        var group = await Groups(t).CreateAsync(new BillboardGroupUpsertRequest("Общая"));
        await Groups(t).AddBillboardsAsync(group.Id, new BillboardGroupMembersRequest(new[] { billboard }));

        Assert.Single(await Groups(t).ListAsync(owner));
        Assert.Empty(await Groups(t).ListAsync(stranger));
    }

    [Fact]
    public async Task Group_with_rented_billboard_is_mine()
    {
        using var t = new TestDb();
        await t.SeedDefaultTariffAsync();
        var client = await t.AddUserAsync();
        var stranger = await t.AddUserAsync();
        var (billboardId, videoId) = await t.AddBillboardWithVideoAsync(client, "Аренда");

        var monday = new DateTimeOffset(2026, 11, 2, 0, 0, 0, TimeSpan.Zero);
        await Placements(t).CreateAsync(client, new PlacementUpsertRequest(
            videoId, billboardId, monday, monday.AddDays(1),
            new[] { "Monday" }, new TimeOnly(9, 0), new TimeOnly(18, 0), 6));

        var group = await Groups(t).CreateAsync(new BillboardGroupUpsertRequest("Общая"));
        await Groups(t).AddBillboardsAsync(group.Id, new BillboardGroupMembersRequest(new[] { billboardId }));

        Assert.Single(await Groups(t).ListAsync(client));
        Assert.Empty(await Groups(t).ListAsync(stranger));
    }
}
