using Billboard.Application.Common;
using Billboard.Application.Contracts;
using Billboard.Domain.Enums;
using Billboard.Infrastructure.Payments;
using Billboard.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace Billboard.UnitTests;

public class WalletTests
{
    private static WalletService Wallets(TestDb t)
    {
        // Сервис и журнал должны работать с одним DbContext, иначе транзакции не сохраняются.
        var db = t.New();
        return new(db, new WalletLedger(db), new MockPaymentService());
    }

    [Fact]
    public async Task TopUp_with_same_idempotency_key_credits_balance_once()
    {
        using var t = new TestDb();
        var user = await t.AddUserAsync();

        var first = await Wallets(t).TopUpAsync(user, new TopUpRequest(100m, "k1"));
        var second = await Wallets(t).TopUpAsync(user, new TopUpRequest(100m, "k1"));

        Assert.Equal(first.Id, second.Id);
        var wallet = await Wallets(t).GetWalletAsync(user);
        Assert.Equal(100m, wallet.Balance);
        await using var db = t.New();
        Assert.Equal(1, await db.WalletTransactions.CountAsync());
    }

    [Fact]
    public async Task Failed_payment_does_not_change_balance()
    {
        using var t = new TestDb();
        var user = await t.AddUserAsync();

        var ex = await Assert.ThrowsAsync<AppException>(() =>
            Wallets(t).TopUpAsync(user, new TopUpRequest(50m, "k1", SimulateFailure: true)));

        Assert.Equal("PAYMENT_FAILED", ex.Code);
        Assert.Equal(0m, (await Wallets(t).GetWalletAsync(user)).Balance);
    }
}

public class BillingFlowTests
{
    private static (BillingService Billing, WalletService Wallets, PlaybackService Playback) Services(TestDb t)
    {
        var db = t.New();
        var ledger = new WalletLedger(db);
        return (new BillingService(db, ledger, new TariffService(db)),
                new WalletService(db, ledger, new MockPaymentService()),
                new PlaybackService(db, ledger));
    }

    private static PlaybackEventDto Event(Guid placementId, Guid videoId, string key) =>
        new(key, placementId, videoId, DateTimeOffset.UtcNow, 15, "Completed");

    [Fact]
    public async Task Reserve_moves_money_from_balance_to_reserve_and_is_idempotent()
    {
        using var t = new TestDb();
        await t.SeedDefaultTariffAsync();
        var user = await t.AddUserAsync();
        var (_, _, placementId) = await t.AddPlacementAsync(user, status: PlacementStatus.PendingPayment);
        await Services(t).Wallets.TopUpAsync(user, new TopUpRequest(10m, "top"));

        var first = await Services(t).Billing.ReserveAsync(user, placementId, 1);
        var again = await Services(t).Billing.ReserveAsync(user, placementId, 1);

        Assert.Equal(2.70m, first.ReservedAmount); // 9 ч * 6 показов * 0.05
        Assert.Equal(first.ReservedAmount, again.ReservedAmount);
        var wallet = await Services(t).Wallets.GetWalletAsync(user);
        Assert.Equal(7.30m, wallet.Balance);
        Assert.Equal(2.70m, wallet.ReservedBalance);
    }

    [Fact]
    public async Task Reserve_with_insufficient_funds_fails_and_keeps_balance()
    {
        using var t = new TestDb();
        await t.SeedDefaultTariffAsync();
        var user = await t.AddUserAsync();
        var (_, _, placementId) = await t.AddPlacementAsync(user);
        await Services(t).Wallets.TopUpAsync(user, new TopUpRequest(1m, "top"));

        var ex = await Assert.ThrowsAsync<AppException>(() => Services(t).Billing.ReserveAsync(user, placementId, 1));

        Assert.Equal("INSUFFICIENT_FUNDS", ex.Code);
        var wallet = await Services(t).Wallets.GetWalletAsync(user);
        Assert.Equal(1m, wallet.Balance);
        Assert.Equal(0m, wallet.ReservedBalance);
    }

    [Fact]
    public async Task Duplicate_playback_event_is_charged_exactly_once()
    {
        using var t = new TestDb();
        await t.SeedDefaultTariffAsync();
        var user = await t.AddUserAsync();
        var (billboardId, videoId, placementId) = await t.AddPlacementAsync(user, status: PlacementStatus.Scheduled);
        await Services(t).Wallets.TopUpAsync(user, new TopUpRequest(10m, "top"));
        await Services(t).Billing.ReserveAsync(user, placementId, 1);

        var ev = Event(placementId, videoId, "e-1");
        var r1 = await Services(t).Playback.IngestAsync(billboardId, new[] { ev });
        var r2 = await Services(t).Playback.IngestAsync(billboardId, new[] { ev });

        Assert.Equal(1, r1.Accepted);
        Assert.Equal(1, r2.Duplicates);
        await using var db = t.New();
        Assert.Equal(1, await db.PlaybackEvents.CountAsync());
        Assert.Equal(1, await db.OwnerEarnings.CountAsync());
        var reservation = await db.BudgetReservations.SingleAsync();
        Assert.Equal(0.05m, reservation.ChargedAmount);
    }

    [Fact]
    public async Task Non_completed_playback_is_not_charged()
    {
        using var t = new TestDb();
        await t.SeedDefaultTariffAsync();
        var user = await t.AddUserAsync();
        var (billboardId, videoId, placementId) = await t.AddPlacementAsync(user, status: PlacementStatus.Scheduled);
        await Services(t).Wallets.TopUpAsync(user, new TopUpRequest(10m, "top"));
        await Services(t).Billing.ReserveAsync(user, placementId, 1);

        var ev = new PlaybackEventDto("e-err", placementId, videoId, DateTimeOffset.UtcNow, 0, "Error");
        await Services(t).Playback.IngestAsync(billboardId, new[] { ev });

        await using var db = t.New();
        Assert.Equal(0m, (await db.BudgetReservations.SingleAsync()).ChargedAmount);
        Assert.Empty(db.OwnerEarnings);
    }

    [Fact]
    public async Task Event_for_foreign_billboard_is_rejected()
    {
        using var t = new TestDb();
        await t.SeedDefaultTariffAsync();
        var user = await t.AddUserAsync();
        var (_, videoId, placementId) = await t.AddPlacementAsync(user, status: PlacementStatus.Scheduled);

        var result = await Services(t).Playback.IngestAsync(Guid.NewGuid(), new[] { Event(placementId, videoId, "x") });

        Assert.Equal(1, result.Rejected);
    }

    [Fact]
    public async Task Budget_exhaustion_pauses_placement_and_topup_resumes_it()
    {
        using var t = new TestDb();
        await t.SeedDefaultTariffAsync();
        var user = await t.AddUserAsync();
        // 1 показ в час * 9 часов * 1.00 = 9.00 резерва, ровно 9 показов.
        var (billboardId, videoId, placementId) =
            await t.AddPlacementAsync(user, pricePerPlay: 1m, status: PlacementStatus.Scheduled, playsPerHour: 1);
        await Services(t).Wallets.TopUpAsync(user, new TopUpRequest(20m, "top"));
        await Services(t).Billing.ReserveAsync(user, placementId, 1);

        var events = Enumerable.Range(1, 9).Select(i => Event(placementId, videoId, $"e-{i}")).ToList();
        await Services(t).Playback.IngestAsync(billboardId, events);

        await using (var db = t.New())
        {
            var placement = await db.AdPlacements.SingleAsync();
            Assert.Equal(PlacementStatus.Paused, placement.Status);
            Assert.Equal(PlaybackService.BudgetExhausted, placement.PauseReason);
        }

        await Services(t).Billing.AddToReservationAsync(user, placementId, 5m, "more");

        await using var db2 = t.New();
        Assert.Equal(PlacementStatus.Scheduled, (await db2.AdPlacements.SingleAsync()).Status);
    }

    [Fact]
    public async Task Release_returns_unused_reserve_to_balance()
    {
        using var t = new TestDb();
        await t.SeedDefaultTariffAsync();
        var user = await t.AddUserAsync();
        var (billboardId, videoId, placementId) = await t.AddPlacementAsync(user, status: PlacementStatus.Scheduled);
        await Services(t).Wallets.TopUpAsync(user, new TopUpRequest(10m, "top"));
        await Services(t).Billing.ReserveAsync(user, placementId, 1);
        await Services(t).Playback.IngestAsync(billboardId, new[] { Event(placementId, videoId, "e-1") });

        var released = await Services(t).Billing.ReleaseAsync(placementId);

        Assert.NotNull(released);
        Assert.Equal(0m, released!.Remaining);
        var wallet = await Services(t).Wallets.GetWalletAsync(user);
        Assert.Equal(0m, wallet.ReservedBalance);
        Assert.Equal(9.95m, wallet.Balance); // 10 - 0.05 за один показ
    }

    [Fact]
    public async Task Foreign_user_cannot_quote_someone_elses_placement()
    {
        using var t = new TestDb();
        await t.SeedDefaultTariffAsync();
        var owner = await t.AddUserAsync();
        var stranger = await t.AddUserAsync();
        var (_, _, placementId) = await t.AddPlacementAsync(owner);

        var ex = await Assert.ThrowsAsync<AppException>(() =>
            Services(t).Billing.QuoteAsync(stranger, false, placementId, 1));

        Assert.Equal(404, ex.StatusCode);
    }
}
