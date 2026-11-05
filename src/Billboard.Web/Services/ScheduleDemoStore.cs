
namespace Billboard.Web.Services;

public enum ScheduleTargetType
{
    Device,
    Group
}

public record AdVideo(
    int Id,
    string Title,
    TimeSpan Duration
);

public record ScheduleItem(
    int Id,
    string Name,
    int VideoId,
    string TargetName,
    ScheduleTargetType TargetType,
    DateTime StartAt,
    DateTime EndAt,
    int PlaysPerHour,
    bool IsActive
);

public class ScheduleDemoStore
{
    private readonly List<AdVideo> _videos =
    [
        new(1, "Реклама автомобиля", TimeSpan.FromSeconds(30)),
        new(2, "Реклама магазина", TimeSpan.FromSeconds(15)),
        new(3, "Реклама смартфона", TimeSpan.FromSeconds(20)),
        new(4, "Реклама ресторана", TimeSpan.FromSeconds(25))
    ];

    private readonly List<ScheduleItem> _schedules =
    [
        new(
            1,
            "Автомобиль — Москва",
            1,
            "Билборд №1",
            ScheduleTargetType.Device,
            DateTime.Today,
            DateTime.Today.AddDays(7),
            12,
            true
        ),
        new(
            2,
            "Магазин — Санкт-Петербург",
            2,
            "Билборд №3",
            ScheduleTargetType.Device,
            DateTime.Today,
            DateTime.Today.AddDays(14),
            8,
            true
        ),
        new(
            3,
            "Смартфон — группа №1",
            3,
            "Центральные билборды",
            ScheduleTargetType.Group,
            DateTime.Today.AddDays(-5),
            DateTime.Today.AddDays(5),
            10,
            false
        )
    ];

    private int _nextScheduleId = 4;

    public IReadOnlyList<AdVideo> Videos => _videos;

    public IReadOnlyList<ScheduleItem> Schedules => _schedules;

    public event Action? Changed;

    public AdVideo? GetVideo(int videoId)
    {
        return _videos.FirstOrDefault(video => video.Id == videoId);
    }

    public void ToggleActive(int id)
    {
        var index = _schedules.FindIndex(schedule => schedule.Id == id);

        if (index < 0)
            return;

        var schedule = _schedules[index];

        _schedules[index] = schedule with
        {
            IsActive = !schedule.IsActive
        };

        NotifyChanged();
    }

    public void RemoveSchedule(int id)
    {
        var schedule = _schedules.FirstOrDefault(s => s.Id == id);

        if (schedule is null)
            return;

        _schedules.Remove(schedule);
        NotifyChanged();
    }

    public void AddSchedule(
        string name,
        int videoId,
        string targetName,
        ScheduleTargetType targetType,
        DateTime startAt,
        DateTime endAt,
        int playsPerHour)
    {
        _schedules.Add(new ScheduleItem(
            _nextScheduleId++,
            name,
            videoId,
            targetName,
            targetType,
            startAt,
            endAt,
            playsPerHour,
            true
        ));

        NotifyChanged();
    }

    private void NotifyChanged()
    {
        Changed?.Invoke();
    }
}
