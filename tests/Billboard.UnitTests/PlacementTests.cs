using Billboard.Application.Common;
using Billboard.Application.Contracts;
using Billboard.Application.Scheduling;
using Billboard.Domain.Entities;
using Billboard.Domain.Enums;
using Billboard.Infrastructure.Media;
using Billboard.Infrastructure.Payments;
using Billboard.Infrastructure.Scheduling;
using Billboard.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Billboard.UnitTests;

public class WeekMaskTests
{
    [Fact]
    public void Parses_english_and_russian_names()
    {
        Assert.True(WeekMask.TryParse(new[] { "Monday", "Friday" }, out var mask));
        Assert.Equal((int)(WeekDays.Monday | WeekDays.Friday), mask);

        Assert.True(WeekMask.TryParse(new[] { "Пн", "пятница" }, out var ru));
        Assert.Equal(mask, ru);
    }

    [Fact]
    public void Rejects_unknown_day()
    {
        Assert.False(WeekMask.TryParse(new[] { "Monday", "Funday" }, out var mask));
        Assert.Equal(0, mask);
        Assert.False(WeekMask.TryParse(Array.Empty<string>(), out _));
    }

    [Fact]
    public void Formats_days_in_week_order()
    {
        var mask = (int)(WeekDays.Monday | WeekDays.Wednesday | WeekDays.Sunday);
        Assert.Equal(new[] { "Monday", "Wednesday", "Sunday" }, WeekMask.Format(mask));
        Assert.True(WeekMask.Includes(mask, DayOfWeek.Wednesday));
        Assert.False(WeekMask.Includes(mask, DayOfWeek.Tuesday));
    }
}

public class ScheduleValidationTests
{
    private static PlacementService Service(TestDb t)
    {
        var db = t.New();
        return new PlacementService(db, new CapacityScheduleValidator(db),
            new BillingService(db, new WalletLedger(db), new TariffService(db)));
    }

    private static PlacementUpsertRequest Request(Guid videoId, Guid billboardId, int playsPerHour = 6) =>
        new(videoId, billboardId,
            new DateTimeOffset(2026, 11, 2, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 11, 2, 1, 0, 0, TimeSpan.Zero),
            new[] { "Monday" }, new TimeOnly(9, 0), new TimeOnly(18, 0), playsPerHour);

    [Fact]
    public async Task Two_placements_can_share_hours_when_capacity_allows()
    {
        using var t = new TestDb();
        await t.SeedDefaultTariffAsync();
        var user = await t.AddUserAsync();
        var (billboardId, videoId, _) = await t.AddPlacementAsync(user);

        // 6 показов * 15 с = 90 с/ч у существующего и столько же у нового; ёмкость 1800 с/ч.
        var result = await Service(t).ValidateAsync(user, Request(videoId, billboardId));

        Assert.True(result.Available);
        Assert.Empty(result.Conflicts);
        Assert.NotNull(result.Quote);
    }

    [Fact]
    public async Task Exceeding_capacity_produces_conflict_and_alternatives()
    {
        using var t = new TestDb();
        await t.SeedDefaultTariffAsync();
        var user = await t.AddUserAsync();
        var (billboardId, videoId, _) = await t.AddPlacementAsync(user, status: PlacementStatus.Scheduled, capacitySecondsPerHour: 100);

        var result = await Service(t).ValidateAsync(user, Request(videoId, billboardId));

        Assert.False(result.Available);
        Assert.Contains(result.Conflicts, c => c.Reason == "INSUFFICIENT_ADVERTISING_CAPACITY");
    }

    [Fact]
    public async Task Video_without_duration_cannot_be_published()
    {
        using var t = new TestDb();
        await t.SeedDefaultTariffAsync();
        var user = await t.AddUserAsync();
        var (billboardId, videoId, _) = await t.AddPlacementAsync(user, durationSeconds: null);

        var result = await Service(t).ValidateAsync(user, Request(videoId, billboardId));

        Assert.False(result.Available);
        Assert.Contains(result.Conflicts, c => c.Reason == "VIDEO_DURATION_UNKNOWN");
    }
}

public class PlacementLifecycleTests
{
    private static PlacementService Service(TestDb t)
    {
        var db = t.New();
        return new PlacementService(db, new CapacityScheduleValidator(db),
            new BillingService(db, new WalletLedger(db), new TariffService(db)));
    }

    private static WalletService Wallets(TestDb t)
    {
        var db = t.New();
        return new WalletService(db, new WalletLedger(db), new MockPaymentService());
    }

    [Fact]
    public async Task Publish_reserves_budget_and_schedules_placement()
    {
        using var t = new TestDb();
        await t.SeedDefaultTariffAsync();
        var user = await t.AddUserAsync();
        var (_, _, placementId) = await t.AddPlacementAsync(user);
        await Wallets(t).TopUpAsync(user, new TopUpRequest(10m, "top"));

        var published = await Service(t).PublishAsync(user, placementId, 1);

        Assert.Equal("Scheduled", published.Status);
        Assert.Equal(2.70m, published.ReservedAmount); // 9 ч * 6 показов * 0.05
        var wallet = await Wallets(t).GetWalletAsync(user);
        Assert.Equal(7.30m, wallet.Balance);
        Assert.Equal(2.70m, wallet.ReservedBalance);
    }

    [Fact]
    public async Task Publish_without_money_leaves_placement_awaiting_payment()
    {
        using var t = new TestDb();
        await t.SeedDefaultTariffAsync();
        var user = await t.AddUserAsync();
        var (_, _, placementId) = await t.AddPlacementAsync(user);
        await Wallets(t).TopUpAsync(user, new TopUpRequest(1m, "top"));

        var ex = await Assert.ThrowsAsync<AppException>(() => Service(t).PublishAsync(user, placementId, 1));

        Assert.Equal("INSUFFICIENT_FUNDS", ex.Code);
        await using var db = t.New();
        Assert.Equal(PlacementStatus.PendingPayment, (await db.AdPlacements.SingleAsync()).Status);
        Assert.Equal(1m, (await Wallets(t).GetWalletAsync(user)).Balance);
    }

    [Fact]
    public async Task Cancel_returns_unused_reserve()
    {
        using var t = new TestDb();
        await t.SeedDefaultTariffAsync();
        var user = await t.AddUserAsync();
        var (_, _, placementId) = await t.AddPlacementAsync(user);
        await Wallets(t).TopUpAsync(user, new TopUpRequest(10m, "top"));
        await Service(t).PublishAsync(user, placementId, 1);

        var cancelled = await Service(t).CancelAsync(user, placementId);

        Assert.Equal("Cancelled", cancelled.Status);
        var wallet = await Wallets(t).GetWalletAsync(user);
        Assert.Equal(10m, wallet.Balance);
        Assert.Equal(0m, wallet.ReservedBalance);
    }

    [Fact]
    public async Task Published_placement_cannot_be_edited()
    {
        using var t = new TestDb();
        await t.SeedDefaultTariffAsync();
        var user = await t.AddUserAsync();
        var (billboardId, videoId, placementId) = await t.AddPlacementAsync(user);
        await Wallets(t).TopUpAsync(user, new TopUpRequest(10m, "top"));
        await Service(t).PublishAsync(user, placementId, 1);

        var request = new PlacementUpsertRequest(videoId, billboardId,
            new DateTimeOffset(2026, 11, 2, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 11, 2, 1, 0, 0, TimeSpan.Zero),
            new[] { "Monday" }, new TimeOnly(9, 0), new TimeOnly(18, 0), 6);

        var ex = await Assert.ThrowsAsync<AppException>(() => Service(t).UpdateAsync(user, placementId, request));

        Assert.Equal("INVALID_PLACEMENT_STATE", ex.Code);
    }

    [Fact]
    public async Task Pause_and_resume_keep_reservation()
    {
        using var t = new TestDb();
        await t.SeedDefaultTariffAsync();
        var user = await t.AddUserAsync();
        var (_, _, placementId) = await t.AddPlacementAsync(user);
        await Wallets(t).TopUpAsync(user, new TopUpRequest(10m, "top"));
        await Service(t).PublishAsync(user, placementId, 1);

        var paused = await Service(t).PauseAsync(user, placementId);
        Assert.Equal("Paused", paused.Status);
        Assert.Equal(PlacementService.ManualPause, paused.PauseReason);

        var resumed = await Service(t).ResumeAsync(user, placementId);
        Assert.Equal("Scheduled", resumed.Status);
        Assert.Equal(2.70m, (await Wallets(t).GetWalletAsync(user)).ReservedBalance);
    }

    [Fact]
    public async Task Foreign_user_cannot_publish_someone_elses_placement()
    {
        using var t = new TestDb();
        await t.SeedDefaultTariffAsync();
        var owner = await t.AddUserAsync();
        var stranger = await t.AddUserAsync();
        var (_, _, placementId) = await t.AddPlacementAsync(owner);

        var ex = await Assert.ThrowsAsync<AppException>(() => Service(t).PublishAsync(stranger, placementId, 1));

        Assert.Equal(404, ex.StatusCode);
    }
}

public class DeviceManifestTests
{
    private sealed class FakeVideoService : IVideoService
    {
        public Task<VideoSourceDto> GetPlaybackSourceAsync(Guid videoId, CancellationToken ct = default) =>
            Task.FromResult(new VideoSourceDto(videoId,
                new Uri($"https://storage.local/{videoId:N}.mp4"), 2048, "abc"));

        public Task<UploadInitDto> CreateUploadAsync(Guid userId, CreateUploadRequest request, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<VideoDto> UploadContentAsync(Guid userId, bool isAdmin, Guid videoId, Stream content, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<VideoDto> ImportFromUrlAsync(Guid userId, string url, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<VideoDto>> ListAsync(Guid userId, bool all, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<VideoDto> GetAsync(Guid userId, bool isAdmin, Guid videoId, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task DeleteAsync(Guid userId, bool isAdmin, Guid videoId, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<Uri> GetDownloadUrlAsync(Guid userId, bool isAdmin, Guid videoId, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private static DeviceService Service(TestDb t) =>
        new(t.New(), new FakeVideoService(), Options.Create(new MediaOptions()));

    [Fact]
    public async Task Manifest_contains_file_for_active_placement()
    {
        using var t = new TestDb();
        var user = await t.AddUserAsync();
        var (billboardId, videoId, _) = await t.AddActivePlacementAsync(user);

        var manifest = await Service(t).GetManifestAsync(billboardId);

        var file = Assert.Single(manifest.Files);
        Assert.Equal(videoId, file.VideoAssetId);
        Assert.Equal(2048, file.SizeBytes);
        Assert.Equal("abc", file.Checksum);
        Assert.True(file.DownloadUrl.IsAbsoluteUri);
    }

    [Fact]
    public async Task Manifest_skips_placements_outside_their_window()
    {
        using var t = new TestDb();
        var user = await t.AddUserAsync();
        var (billboardId, _, placementId) = await t.AddActivePlacementAsync(user);

        await using (var db = t.New())
        {
            var placement = await db.AdPlacements.SingleAsync(p => p.Id == placementId);
            placement.StartAt = DateTimeOffset.UtcNow.AddDays(1);
            placement.EndAt = DateTimeOffset.UtcNow.AddDays(2);
            await db.SaveChangesAsync();
        }

        var manifest = await Service(t).GetManifestAsync(billboardId);

        Assert.Empty(manifest.Files);
    }

    [Fact]
    public async Task Cache_report_updates_used_space()
    {
        using var t = new TestDb();
        var user = await t.AddUserAsync();
        var (billboardId, videoId, _) = await t.AddActivePlacementAsync(user);

        var result = await Service(t).ReportCacheAsync(billboardId,
            new[] { new CacheReportItemDto(videoId, 1024, "Ready", true) });

        Assert.Equal(1024, result.CacheUsedBytes);
        var manifest = await Service(t).GetManifestAsync(billboardId);
        Assert.Equal(1024, manifest.CacheUsedBytes);
    }

    [Fact]
    public void Version_changes_when_placement_set_changes()
    {
        var first = new AdPlacement { Id = Guid.NewGuid(), CreatedAt = DateTimeOffset.UtcNow };
        var second = new AdPlacement { Id = Guid.NewGuid(), CreatedAt = DateTimeOffset.UtcNow };

        var one = DeviceService.ComputeVersion(new[] { first });
        var two = DeviceService.ComputeVersion(new[] { first, second });

        Assert.NotEqual(one, two);
        Assert.Equal(one, DeviceService.ComputeVersion(new[] { first }));
    }

    [Fact]
    public void Overnight_window_matches_after_midnight()
    {
        var placement = new AdPlacement
        {
            DaysOfWeekMask = (int)WeekDays.All,
            StartTimeOfDay = new TimeOnly(22, 0),
            EndTimeOfDay = new TimeOnly(6, 0)
        };

        var at23 = new DateTimeOffset(2026, 11, 6, 23, 30, 0, TimeSpan.Zero);
        var at3 = new DateTimeOffset(2026, 11, 7, 3, 0, 0, TimeSpan.Zero);
        var at12 = new DateTimeOffset(2026, 11, 7, 12, 0, 0, TimeSpan.Zero);

        Assert.True(DeviceService.IsWithinSchedule(placement, at23));
        Assert.True(DeviceService.IsWithinSchedule(placement, at3));
        Assert.False(DeviceService.IsWithinSchedule(placement, at12));
    }
}
