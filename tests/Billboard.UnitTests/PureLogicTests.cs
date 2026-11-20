using System.Net;
using Billboard.Application.Billing;
using Billboard.Application.Common;
using Billboard.Application.Media;
using Billboard.Domain.Entities;
using Billboard.Domain.Enums;

namespace Billboard.UnitTests;

public class QuoteCalculatorTests
{
    // 2026-11-02 — понедельник.
    private static readonly DateTimeOffset Monday = new(2026, 11, 2, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Weekday_window_counts_nine_hours_and_plays()
    {
        var r = QuoteCalculator.Calculate(Monday, Monday.AddDays(1), (int)WeekDays.Weekdays,
            new TimeOnly(9, 0), new TimeOnly(18, 0), 6, 0.05m, 0m, TimeZoneInfo.Utc);

        Assert.Equal(9m, r.BillableHours);
        Assert.Equal(54, r.ExpectedPlays);
        Assert.Equal(2.70m, r.TotalAmount);
    }

    [Fact]
    public void Discount_reduces_price_per_play()
    {
        var r = QuoteCalculator.Calculate(Monday, Monday.AddDays(1), (int)WeekDays.Weekdays,
            new TimeOnly(9, 0), new TimeOnly(18, 0), 6, 0.05m, 10m, TimeZoneInfo.Utc);

        Assert.Equal(0.045m, r.PricePerPlayAfterDiscount);
        Assert.Equal(2.43m, r.TotalAmount);
    }

    [Fact]
    public void Days_outside_mask_are_not_billed()
    {
        var r = QuoteCalculator.Calculate(Monday, Monday.AddDays(1), (int)WeekDays.Sunday,
            new TimeOnly(9, 0), new TimeOnly(18, 0), 6, 0.05m, 0m, TimeZoneInfo.Utc);

        Assert.Equal(0m, r.BillableHours);
        Assert.Equal(0m, r.TotalAmount);
    }

    [Fact]
    public void Total_is_rounded_up_so_reserve_covers_all_charges()
    {
        var r = QuoteCalculator.Calculate(Monday, Monday.AddHours(10), (int)WeekDays.All,
            new TimeOnly(0, 0), new TimeOnly(23, 59), 1, 0.333m, 0m, TimeZoneInfo.Utc);

        Assert.True(r.TotalAmount >= r.ExpectedPlays * r.PricePerPlayAfterDiscount);
    }
}

public class VideoPolicyTests
{
    private static TariffPlan Start => new()
    {
        Name = "Старт", MaxVideoCount = 5, MaxVideoSizeBytes = 100L * 1024 * 1024, MaxVideoDurationSeconds = 15
    };

    [Fact]
    public void Unsupported_extension_is_rejected()
    {
        var ex = Assert.Throws<AppException>(() => VideoPolicy.ValidateUploadRequest(Start, "clip.avi", 1000, 0));
        Assert.Equal("UNSUPPORTED_FORMAT", ex.Code);
    }

    [Fact]
    public void Library_limit_is_enforced_with_conflict()
    {
        var ex = Assert.Throws<AppException>(() => VideoPolicy.ValidateUploadRequest(Start, "a.mp4", 1000, 5));
        Assert.Equal("VIDEO_LIMIT_REACHED", ex.Code);
        Assert.Equal(409, ex.StatusCode);
    }

    [Fact]
    public void Oversized_file_is_rejected_with_clear_code()
    {
        var ex = Assert.Throws<AppException>(() =>
            VideoPolicy.ValidateUploadRequest(Start, "a.mp4", 101L * 1024 * 1024, 0));
        Assert.Equal("VIDEO_TOO_LARGE", ex.Code);
    }

    [Fact]
    public void Too_long_video_is_rejected_after_probe()
    {
        var ex = Assert.Throws<AppException>(() => VideoPolicy.ValidateProbed(Start, 1000, 16m));
        Assert.Equal("VIDEO_TOO_LONG", ex.Code);
    }

    [Fact]
    public void Valid_video_passes()
    {
        VideoPolicy.ValidateUploadRequest(Start, "a.MP4", 1000, 4);
        VideoPolicy.ValidateProbed(Start, 1000, 15m);
    }
}

public class UrlGuardTests
{
    [Theory]
    [InlineData("10.0.0.5")]
    [InlineData("192.168.1.1")]
    [InlineData("172.16.0.1")]
    [InlineData("127.0.0.1")]
    [InlineData("169.254.169.254")]
    [InlineData("::1")]
    [InlineData("fc00::1")]
    public void Internal_addresses_are_blocked(string ip) =>
        Assert.False(UrlGuard.IsPublicAddress(IPAddress.Parse(ip)));

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("1.1.1.1")]
    public void Public_addresses_are_allowed(string ip) =>
        Assert.True(UrlGuard.IsPublicAddress(IPAddress.Parse(ip)));

    [Theory]
    [InlineData("file:///etc/passwd")]
    [InlineData("ftp://example.com/a.mp4")]
    [InlineData("http://user:pass@example.com/a.mp4")]
    [InlineData("not a url")]
    public void Bad_urls_are_rejected(string url) =>
        Assert.Throws<AppException>(() => UrlGuard.ParseAndValidate(url));
}
