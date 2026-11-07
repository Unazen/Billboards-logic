using System.Text;

namespace Billboard.Web.Services;

public sealed record AdminLogEntry(
    DateTime Timestamp,
    string Level,
    string Action,
    string Actor,
    string Target,
    string Details
);

public sealed class AdminLogDemoStore
{
    private readonly IReadOnlyList<AdminLogEntry> _entries =
    [
        new(DateTime.Now.AddMinutes(-4), "Информация", "Устройство подключено", "Система", "Билборд №1", "Устройство восстановило соединение."),
        new(DateTime.Now.AddMinutes(-18), "Информация", "Расписание изменено", "Иван Петров", "Автомобиль — Москва", "Обновлены параметры размещения."),
        new(DateTime.Now.AddMinutes(-36), "Предупреждение", "Устройство отключено", "Система", "Билборд №3", "Нет heartbeat от устройства."),
        new(DateTime.Now.AddHours(-2), "Информация", "Видеоролик добавлен", "Анна Смирнова", "Осенняя коллекция", "Добавлен тестовый рекламный материал."),
        new(DateTime.Now.AddHours(-4), "Ошибка", "Загрузка не завершена", "Алексей Иванов", "Билборд №4", "Демо-событие: загрузка прервана.")
    ];

    public IReadOnlyList<AdminLogEntry> GetLogs(string? search = null, string? level = null)
    {
        var query = _entries.AsEnumerable();

        if (!string.IsNullOrWhiteSpace(level) && level != "Все уровни")
            query = query.Where(entry => entry.Level == level);

        if (!string.IsNullOrWhiteSpace(search))
        {
            query = query.Where(entry =>
                entry.Action.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                entry.Actor.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                entry.Target.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                entry.Details.Contains(search, StringComparison.OrdinalIgnoreCase));
        }

        return query.OrderByDescending(entry => entry.Timestamp).ToList();
    }

    public string CreateCsvDataUrl(string? search = null, string? level = null)
    {
        var lines = new List<string>
        {
            "Время;Уровень;Событие;Пользователь;Объект;Подробности"
        };

        lines.AddRange(GetLogs(search, level).Select(entry => string.Join(';',
            Csv(entry.Timestamp.ToString("yyyy-MM-dd HH:mm:ss")),
            Csv(entry.Level),
            Csv(entry.Action),
            Csv(entry.Actor),
            Csv(entry.Target),
            Csv(entry.Details))));

        var content = Encoding.UTF8.GetPreamble()
            .Concat(Encoding.UTF8.GetBytes(string.Join("\r\n", lines)))
            .ToArray();
        return $"data:text/csv;charset=utf-8;base64,{Convert.ToBase64String(content)}";
    }

    private static string Csv(string value) =>
        $"\"{value.Replace("\"", "\"\"")}\"";
}
