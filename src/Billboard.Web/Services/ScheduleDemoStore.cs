namespace Billboard.Web.Services;

public enum ScheduleTargetType
{
    Device,
    Group
}

public record AdVideo(
    int Id,
    string Title,
    TimeSpan Duration,
    int OwnerId = 1
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
    bool IsActive,
    int OwnerId = 1
);

public class ScheduleDemoStore
{
    private readonly List<AdVideo> _videos =
    [
        new(1, "Реклама автомобиля", TimeSpan.FromSeconds(30)),
        new(2, "Реклама магазина", TimeSpan.FromSeconds(15), 2),
        new(3, "Реклама смартфона", TimeSpan.FromSeconds(20)),
        new(4, "Реклама ресторана", TimeSpan.FromSeconds(25)),
        new(5, "Осенняя коллекция", TimeSpan.FromSeconds(20), 2),
        new(6, "Скидки недели", TimeSpan.FromSeconds(15), 2),
        new(7, "Городской фестиваль", TimeSpan.FromSeconds(30), 3)
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
            true,
            2
        ),
        new(
            3,
            "Смартфон — группа №1",
            3,
            "Центр города",
            ScheduleTargetType.Group,
            DateTime.Today.AddDays(-5),
            DateTime.Today.AddDays(5),
            10,
            false
        )
    ];

    private int _nextScheduleId = 4;

    public IReadOnlyList<AdVideo> Videos => _videos;

    public IReadOnlyList<AdVideo> GetVideosForOwner(int ownerId) =>
        _videos.Where(video => video.OwnerId == ownerId).ToList();

    public IReadOnlyList<ScheduleItem> Schedules => _schedules;

    public event Action? Changed;

    public AdVideo? GetVideo(int videoId)
    {
        return _videos.FirstOrDefault(video => video.Id == videoId);
    }

    public AdVideo? GetVideo(int videoId, int ownerId) =>
        _videos.FirstOrDefault(video =>
            video.Id == videoId && video.OwnerId == ownerId);

    public void ToggleActive(int id, int? ownerId = null)
    {
        var index = _schedules.FindIndex(schedule =>
            schedule.Id == id && (ownerId is null || schedule.OwnerId == ownerId));

        if (index < 0)
            return;

        var schedule = _schedules[index];

        _schedules[index] = schedule with
        {
            IsActive = !schedule.IsActive
        };

        NotifyChanged();
    }

    public void RemoveSchedule(int id, int? ownerId = null)
    {
        var schedule = _schedules.FirstOrDefault(s =>
            s.Id == id && (ownerId is null || s.OwnerId == ownerId));

        if (schedule is null)
            return;

        _schedules.Remove(schedule);
        NotifyChanged();
    }

    public bool TryAddSchedule(
        string name,
        int videoId,
        string targetName,
        ScheduleTargetType targetType,
        DateTime startAt,
        DateTime endAt,
        int playsPerHour,
        out string error,
        int ownerId = 1)
    {
        name = name?.Trim() ?? string.Empty;
        targetName = targetName?.Trim() ?? string.Empty;

        if (string.IsNullOrWhiteSpace(name))
        {
            error = "Укажите название расписания.";
            return false;
        }

        if (name.Length > 100)
        {
            error = "Название не должно превышать 100 символов.";
            return false;
        }

        if (GetVideo(videoId, ownerId) is null)
        {
            error = "Выбранный видеоролик не найден.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(targetName))
        {
            error = "Укажите название устройства или группы.";
            return false;
        }

        if (targetName.Length > 100)
        {
            error = "Название устройства или группы не должно превышать 100 символов.";
            return false;
        }

        if (startAt.Date > endAt.Date)
        {
            error = "Дата окончания не может быть раньше даты начала.";
            return false;
        }

        if (playsPerHour <= 0)
        {
            error = "Количество показов в час должно быть больше нуля.";
            return false;
        }

        if (!Enum.IsDefined(targetType))
        {
            error = "Выбран неизвестный тип назначения.";
            return false;
        }

        _schedules.Add(new ScheduleItem(
            _nextScheduleId++,
            name,
            videoId,
            targetName,
            targetType,
            startAt.Date,
            endAt.Date,
            playsPerHour,
            true,
            ownerId
        ));

        error = string.Empty;
        NotifyChanged();

        return true;
    }

    // Оставляем прежний метод для совместимости с возможными вызовами.
    public bool TryUpdateSchedule(
    int id,
    string name,
    int videoId,
    string targetName,
    ScheduleTargetType targetType,
    DateTime startAt,
    DateTime endAt,
    int playsPerHour,
    out string error,
    int ownerId = 1)
{
    var index = _schedules.FindIndex(s => s.Id == id && s.OwnerId == ownerId);

    if (index < 0)
    {
        error = "Расписание не найдено.";
        return false;
    }

    name = name?.Trim() ?? string.Empty;
    targetName = targetName?.Trim() ?? string.Empty;

    if (string.IsNullOrWhiteSpace(name))
    {
        error = "Укажите название расписания.";
        return false;
    }

    if (name.Length > 100)
    {
        error = "Название не должно превышать 100 символов.";
        return false;
    }

    if (GetVideo(videoId, ownerId) is null)
    {
        error = "Выбранный видеоролик не найден.";
        return false;
    }

    if (string.IsNullOrWhiteSpace(targetName))
    {
        error = "Укажите название устройства или группы.";
        return false;
    }

    if (targetName.Length > 100)
    {
        error = "Название устройства или группы не должно превышать 100 символов.";
        return false;
    }

    if (startAt.Date > endAt.Date)
    {
        error = "Дата окончания не может быть раньше даты начала.";
        return false;
    }

    if (playsPerHour <= 0)
    {
        error = "Количество показов в час должно быть больше нуля.";
        return false;
    }

    if (!Enum.IsDefined(targetType))
    {
        error = "Выбран неизвестный тип назначения.";
        return false;
    }

    var oldSchedule = _schedules[index];

    _schedules[index] = oldSchedule with
    {
        Name = name,
        VideoId = videoId,
        TargetName = targetName,
        TargetType = targetType,
        StartAt = startAt.Date,
        EndAt = endAt.Date,
        PlaysPerHour = playsPerHour
    };

    error = string.Empty;
    NotifyChanged();

    return true;
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
        if (!TryAddSchedule(
                name,
                videoId,
                targetName,
                targetType,
                startAt,
                endAt,
                playsPerHour,
                out var error))
        {
            throw new ArgumentException(error);
        }
    }

    private void NotifyChanged()
    {
        Changed?.Invoke();
    }
}