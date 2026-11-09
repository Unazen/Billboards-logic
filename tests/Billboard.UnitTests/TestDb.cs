using Billboard.Domain.Entities;
using Billboard.Domain.Enums;
using Billboard.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Billboard.UnitTests;

/// <summary>
/// SQLite в памяти: одна БД на тест, схема создаётся из модели EF.
/// Используется shared-cache in-memory база, а не одно общее соединение:
/// каждый DbContext открывает своё соединение к той же базе, поэтому нет гонок
/// при параллельных тестах и на разных ОС.
/// </summary>
public sealed class TestDb : IDisposable
{
    private readonly string _connectionString =
        $"DataSource=file:memdb-{Guid.NewGuid():N}?mode=memory&cache=shared";
    private readonly SqliteConnection _keepAlive;
    private readonly DbContextOptions<AppDbContext> _options;

    public TestDb()
    {
        _keepAlive = new SqliteConnection(_connectionString);
        _keepAlive.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connectionString).Options;
        using var db = New();
        db.Database.EnsureCreated();
    }

    public AppDbContext New() => new(_options);

    public async Task<Guid> AddUserAsync()
    {
        await using var db = New();
        var id = Guid.NewGuid();
        db.Users.Add(new ApplicationUser { Id = id, UserName = $"{id:N}@t.local", Email = $"{id:N}@t.local", DisplayName = "t" });
        await db.SaveChangesAsync();
        return id;
    }

    public async Task SeedDefaultTariffAsync()
    {
        await using var db = New();
        db.TariffPlans.Add(new TariffPlan
        {
            Id = Guid.NewGuid(), Name = "Старт", IsDefault = true, MaxVideoCount = 5,
            MaxVideoSizeBytes = 100L * 1024 * 1024, MaxVideoDurationSeconds = 15
        });
        await db.SaveChangesAsync();
    }

    /// <summary>Билборд + видео + черновик размещения на 9 часов (понедельник 09:00–18:00, 6 показов/час).</summary>
    public async Task<(Guid BillboardId, Guid VideoId, Guid PlacementId)> AddPlacementAsync(
        Guid ownerId, decimal pricePerPlay = 0.05m, PlacementStatus status = PlacementStatus.Draft,
        int playsPerHour = 6, decimal? durationSeconds = 15m, int capacitySecondsPerHour = 1800)
    {
        await using var db = New();
        var billboard = new BillboardDevice
        {
            Id = Guid.NewGuid(), Name = "B", Address = "A", PricePerPlay = pricePerPlay,
            AdvertCapacitySecondsPerHour = capacitySecondsPerHour
        };
        var video = new VideoAsset
        {
            Id = Guid.NewGuid(), OwnerId = ownerId, OriginalFileName = "v.mp4", StorageKey = "k",
            ContentType = "video/mp4", SizeBytes = 10, Status = VideoStatus.Ready, DurationSeconds = durationSeconds
        };
        var monday = new DateTimeOffset(2026, 11, 2, 0, 0, 0, TimeSpan.Zero);
        var placement = new AdPlacement
        {
            Id = Guid.NewGuid(), OwnerId = ownerId, VideoAssetId = video.Id, BillboardId = billboard.Id,
            StartAt = monday, EndAt = monday.AddDays(1), DaysOfWeekMask = (int)WeekDays.Weekdays,
            StartTimeOfDay = new TimeOnly(9, 0), EndTimeOfDay = new TimeOnly(18, 0),
            MinimumPlaysPerHour = playsPerHour, Status = status, CreatedAt = monday
        };
        db.AddRange(billboard, video, placement);
        await db.SaveChangesAsync();
        return (billboard.Id, video.Id, placement.Id);
    }

    /// <summary>Размещение, которое действует прямо сейчас (для манифеста устройства).</summary>
    public async Task<(Guid BillboardId, Guid VideoId, Guid PlacementId)> AddActivePlacementAsync(
        Guid ownerId, PlacementStatus status = PlacementStatus.Active, decimal pricePerPlay = 0.05m)
    {
        await using var db = New();
        var now = DateTimeOffset.UtcNow;
        var billboard = new BillboardDevice
        {
            Id = Guid.NewGuid(), Name = "Active", Address = "A", PricePerPlay = pricePerPlay,
            CapacityBytes = 32L * 1024 * 1024 * 1024, TimeZoneId = "UTC"
        };
        var video = new VideoAsset
        {
            Id = Guid.NewGuid(), OwnerId = ownerId, OriginalFileName = "now.mp4", StorageKey = "now",
            ContentType = "video/mp4", SizeBytes = 2048, Status = VideoStatus.Ready,
            DurationSeconds = 15m, Checksum = "abc", CreatedAt = now
        };
        var placement = new AdPlacement
        {
            Id = Guid.NewGuid(), OwnerId = ownerId, VideoAssetId = video.Id, BillboardId = billboard.Id,
            StartAt = now.AddHours(-1), EndAt = now.AddHours(2), DaysOfWeekMask = (int)WeekDays.All,
            StartTimeOfDay = new TimeOnly(0, 0), EndTimeOfDay = new TimeOnly(23, 59, 59),
            MinimumPlaysPerHour = 6, Status = status, CreatedAt = now
        };
        db.AddRange(billboard, video, placement);
        await db.SaveChangesAsync();
        return (billboard.Id, video.Id, placement.Id);
    }

    public void Dispose() => _keepAlive.Dispose();

    /// <summary>Билборд без размещений (для тестов групп).</summary>
    public async Task<Guid> AddBillboardAsync(string name, BillboardStatus status = BillboardStatus.Online)
    {
        await using var db = New();
        var billboard = new BillboardDevice { Id = Guid.NewGuid(), Name = name, Address = "A", Status = status };
        db.Billboards.Add(billboard);
        await db.SaveChangesAsync();
        return billboard.Id;
    }

    /// <summary>Второе размещение того же ролика на том же билборде (проверка манифеста).</summary>
    public async Task<Guid> AddPlacementForExistingAsync(
        Guid billboardId, Guid videoId, Guid ownerId, PlacementStatus status = PlacementStatus.Active,
        int playsPerHour = 4)
    {
        await using var db = New();
        var now = DateTimeOffset.UtcNow;
        var placement = new AdPlacement
        {
            Id = Guid.NewGuid(), OwnerId = ownerId, VideoAssetId = videoId, BillboardId = billboardId,
            StartAt = now.AddHours(-1), EndAt = now.AddHours(2), DaysOfWeekMask = (int)WeekDays.All,
            StartTimeOfDay = new TimeOnly(0, 0), EndTimeOfDay = new TimeOnly(23, 59, 59),
            MinimumPlaysPerHour = playsPerHour, Status = status, CreatedAt = now
        };
        db.AdPlacements.Add(placement);
        await db.SaveChangesAsync();
        return placement.Id;
    }
}
