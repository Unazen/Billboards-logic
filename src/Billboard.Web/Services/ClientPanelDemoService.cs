namespace Billboard.Web.Services;

public record ClientMediaItem(
    Guid Id,
    string Name,
    string Extension,
    long Size,
    DateTime AddedAt,
    string Status,
    int OwnerId = 1
);

public record PlaybackStatistic(
    string Title,
    int Impressions,
    int MinutesPlayed,
    double CompletionRate,
    int OwnerId = 1
);

public record ClientStream(
    BillboardDevice Device,
    string CurrentCreative,
    DateTime LastUpdated
);

public interface IClientPanelService
{
    int UserId { get; }
    string UserName { get; }
    IReadOnlyList<BillboardDevice> GetBillboards();
    IReadOnlyList<BillboardGroup> GetGroups();
    IReadOnlyList<BillboardDevice> GetGroupDevices(int groupId);
    IReadOnlyList<ScheduleItem> GetSchedules();
    IReadOnlyList<ClientMediaItem> GetMedia();
    IReadOnlyList<PlaybackStatistic> GetPlaybackStatistics();
    IReadOnlyList<ClientStream> GetStreams();
    void AddMedia(string name, string extension, long size);
    void RemoveMedia(Guid id);
    void ClearMedia();
}

public sealed class ClientPanelDemoService(
    AdminDemoStore adminStore,
    ScheduleDemoStore scheduleStore,
    DemoUserSession session) : IClientPanelService
{
    public int UserId => session.CurrentUserId ?? 0;

    public string UserName =>
        session.CurrentName;

    private readonly List<ClientMediaItem> _media =
    [
        new(Guid.Parse("10000000-0000-0000-0000-000000000001"), "Реклама автомобиля", ".mp4", 28_400_000, DateTime.Today.AddDays(-12), "Готово"),
        new(Guid.Parse("10000000-0000-0000-0000-000000000002"), "Реклама магазина", ".mp4", 16_200_000, DateTime.Today.AddDays(-8), "Готово"),
        new(Guid.Parse("10000000-0000-0000-0000-000000000003"), "Реклама смартфона", ".webm", 22_700_000, DateTime.Today.AddDays(-4), "Готово"),
        new(Guid.Parse("10000000-0000-0000-0000-000000000004"), "Реклама ресторана", ".mp4", 31_500_000, DateTime.Today.AddDays(-1), "Готово"),
        new(Guid.Parse("20000000-0000-0000-0000-000000000001"), "Осенняя коллекция", ".mp4", 18_600_000, DateTime.Today.AddDays(-6), "Готово", 2),
        new(Guid.Parse("20000000-0000-0000-0000-000000000002"), "Скидки недели", ".webm", 12_300_000, DateTime.Today.AddDays(-2), "Готово", 2),
        new(Guid.Parse("30000000-0000-0000-0000-000000000001"), "Городской фестиваль", ".mp4", 20_100_000, DateTime.Today.AddDays(-3), "Готово", 3)
    ];

    private readonly List<PlaybackStatistic> _playbackStatistics =
    [
        new("Реклама автомобиля", 12840, 6420, 98.4),
        new("Реклама магазина", 9870, 2468, 96.8),
        new("Реклама смартфона", 7350, 2450, 97.2),
        new("Реклама ресторана", 4120, 1717, 95.6),
        new("Осенняя коллекция", 6380, 3190, 97.8, 2),
        new("Скидки недели", 4260, 1065, 96.1, 2),
        new("Городской фестиваль", 2840, 1183, 98.1, 3)
    ];

    public IReadOnlyList<BillboardDevice> GetBillboards() =>
        adminStore.Devices
            .Where(device => device.OwnerId == UserId)
            .OrderBy(device => device.Id)
            .ToList();

    public IReadOnlyList<BillboardGroup> GetGroups() =>
        adminStore.Groups
            .Where(group => group.OwnerId == UserId)
            .OrderBy(group => group.Name)
            .ToList();

    public IReadOnlyList<BillboardDevice> GetGroupDevices(int groupId)
    {
        var group = GetGroups().FirstOrDefault(item => item.Id == groupId);
        return group is null ? [] : adminStore.GetDevicesForGroup(groupId);
    }

    public IReadOnlyList<ScheduleItem> GetSchedules() =>
        scheduleStore.Schedules
            .Where(schedule => schedule.OwnerId == UserId)
            .OrderBy(schedule => schedule.StartAt)
            .ToList();

    public IReadOnlyList<ClientMediaItem> GetMedia() =>
        _media
            .Where(item => item.OwnerId == UserId)
            .OrderByDescending(item => item.AddedAt)
            .ToList();

    public IReadOnlyList<PlaybackStatistic> GetPlaybackStatistics() =>
        _playbackStatistics
            .Where(item => item.OwnerId == UserId)
            .ToList();

    public IReadOnlyList<ClientStream> GetStreams() =>
        GetBillboards()
            .Select(device => new ClientStream(
                device,
                device.IsOnline
                    ? scheduleStore.Schedules.FirstOrDefault(schedule =>
                        schedule.OwnerId == UserId &&
                        schedule.IsActive &&
                        schedule.TargetName == device.Name)?.Name ?? "Демо-трансляция"
                    : "Нет сигнала",
                DateTime.Now.AddMinutes(device.IsOnline ? -2 : -37)))
            .ToList();

    public void AddMedia(string name, string extension, long size)
    {
        _media.Add(new ClientMediaItem(
            Guid.NewGuid(),
            name,
            extension,
            size,
            DateTime.Now,
            "В демо-библиотеке",
            UserId
        ));
    }

    public void RemoveMedia(Guid id) =>
        _media.RemoveAll(item => item.Id == id && item.OwnerId == UserId);

    public void ClearMedia() =>
        _media.RemoveAll(item => item.OwnerId == UserId);
}
