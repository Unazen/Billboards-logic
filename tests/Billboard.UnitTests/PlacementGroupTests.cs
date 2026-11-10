using Billboard.Application.Common;
using Billboard.Application.Contracts;
using Billboard.Infrastructure.Payments;
using Billboard.Infrastructure.Scheduling;
using Billboard.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace Billboard.UnitTests;

public class PlacementGroupTests
{
    private static PlacementGroupService Groups(TestDb t)
    {
        var db = t.New();
        var billing = new BillingService(db, new WalletLedger(db), new TariffService(db));
        var placements = new PlacementService(db, new CapacityScheduleValidator(db), billing);
        return new PlacementGroupService(db, new CapacityScheduleValidator(db), billing, placements);
    }

    private static WalletService Wallets(TestDb t)
    {
        var db = t.New();
        return new WalletService(db, new WalletLedger(db), new MockPaymentService());
    }

    private static PlacementGroupRequestDto Request(Guid videoId, params Guid[] billboardIds) => new(
        billboardIds,
        new PlacementGroupTemplateDto(
            videoId,
            new DateOnly(2026, 11, 10),
            new DateOnly(2026, 11, 14),
            new[] { "Monday", "Tuesday", "Wednesday", "Thursday", "Friday" },
            new TimeOnly(9, 0),
            new TimeOnly(18, 0),
            6));

    [Fact]
    public async Task Validation_returns_result_for_each_billboard()
    {
        using var t = new TestDb();
        await t.SeedDefaultTariffAsync();
        var user = await t.AddUserAsync();
        var (first, videoId) = await t.AddBillboardWithVideoAsync(user, "Первый");
        var (second, _) = await t.AddBillboardWithVideoAsync(user, "Второй");

        var result = await Groups(t).ValidateAsync(user, Request(videoId, first, second));

        Assert.True(result.CanPublish);
        Assert.Equal(2, result.RequestedBillboardCount);
        Assert.Equal(2, result.AvailableBillboardCount);
        Assert.All(result.Results, r => Assert.True(r.IsAvailable));
        Assert.Equal(new[] { first, second }, result.Results.Select(r => r.BillboardId).ToArray());
    }

    [Fact]
    public async Task Validation_marks_billboard_without_capacity()
    {
        using var t = new TestDb();
        await t.SeedDefaultTariffAsync();
        var user = await t.AddUserAsync();
        var (small, videoId) = await t.AddBillboardWithVideoAsync(user, "Маленький", capacitySecondsPerHour: 50);
        var (normal, _) = await t.AddBillboardWithVideoAsync(user, "Обычный");

        var result = await Groups(t).ValidateAsync(user, Request(videoId, small, normal));

        Assert.False(result.CanPublish);
        Assert.Equal(1, result.AvailableBillboardCount);
        var conflict = Assert.Single(result.Results.Single(r => r.BillboardId == small).Conflicts);
        Assert.Equal("INSUFFICIENT_ADVERTISING_CAPACITY", conflict.Code);
    }

    [Fact]
    public async Task Drafts_create_placements_and_reserve_budget()
    {
        using var t = new TestDb();
        await t.SeedDefaultTariffAsync();
        var user = await t.AddUserAsync();
        var (first, videoId) = await t.AddBillboardWithVideoAsync(user, "Первый");
        var (second, _) = await t.AddBillboardWithVideoAsync(user, "Второй");
        await Wallets(t).TopUpAsync(user, new TopUpRequest(50m, "top"));

        var result = await Groups(t).CreateDraftsAsync(user, Request(videoId, first, second));

        Assert.True(result.Success);
        Assert.Equal(2, result.Placements.Count);
        Assert.All(result.Placements, p => Assert.Equal("Draft", p.Status));

        await using var db = t.New();
        Assert.Equal(2, await db.AdPlacements.CountAsync());
        Assert.Equal(2, await db.BudgetReservations.CountAsync());

        var wallet = await Wallets(t).GetWalletAsync(user);
        Assert.Equal(21.60m, wallet.ReservedBalance); // 2 * (4 дня * 9 ч * 6 * 0.05)
        Assert.Equal(28.40m, wallet.Balance);
    }

    [Fact]
    public async Task Drafts_are_rolled_back_when_second_billboard_has_no_money()
    {
        using var t = new TestDb();
        await t.SeedDefaultTariffAsync();
        var user = await t.AddUserAsync();
        var (first, videoId) = await t.AddBillboardWithVideoAsync(user, "Первый");
        var (second, _) = await t.AddBillboardWithVideoAsync(user, "Второй");
        await Wallets(t).TopUpAsync(user, new TopUpRequest(20m, "top"));

        var ex = await Assert.ThrowsAsync<AppException>(() =>
            Groups(t).CreateDraftsAsync(user, Request(videoId, first, second)));

        Assert.Equal("INSUFFICIENT_FUNDS", ex.Code);
        await using var db = t.New();
        Assert.Equal(0, await db.AdPlacements.CountAsync());
        Assert.Equal(0, await db.BudgetReservations.CountAsync());

        var wallet = await Wallets(t).GetWalletAsync(user);
        Assert.Equal(20m, wallet.Balance);
        Assert.Equal(0m, wallet.ReservedBalance);
    }

    [Fact]
    public async Task Empty_group_is_rejected()
    {
        using var t = new TestDb();
        await t.SeedDefaultTariffAsync();
        var user = await t.AddUserAsync();
        var (_, videoId) = await t.AddBillboardWithVideoAsync(user, "Первый");

        var ex = await Assert.ThrowsAsync<AppException>(() =>
            Groups(t).ValidateAsync(user, Request(videoId)));

        Assert.Equal("GROUP_IS_EMPTY", ex.Code);
    }
}
