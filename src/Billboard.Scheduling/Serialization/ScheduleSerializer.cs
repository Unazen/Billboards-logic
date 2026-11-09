using System.Text.Json;
using System.Text.Json.Serialization;

namespace Billboard.Scheduling;

/// <summary>Версионированный формат обмена расписанием (JSON).</summary>
public sealed record ScheduleExport
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public DateTimeOffset ExportedAt { get; init; } = DateTimeOffset.UtcNow;

    public IReadOnlyList<Placement> Placements { get; init; } = Array.Empty<Placement>();

    public IReadOnlyList<PlacementGroup> Groups { get; init; } = Array.Empty<PlacementGroup>();
}

public sealed record ScheduleImportResult
{
    public bool Success => Errors.Count == 0;

    public ScheduleExport? Data { get; init; }

    public IReadOnlyList<string> Errors { get; init; } = Array.Empty<string>();
}

public sealed class ScheduleImportException : Exception
{
    public ScheduleImportException(IReadOnlyList<string> errors)
        : base("Не удалось импортировать расписание: " + string.Join("; ", errors))
    {
        Errors = errors;
    }

    public IReadOnlyList<string> Errors { get; }
}

/// <summary>
/// Экспорт и импорт расписания. Проверяет структуру и форму записей, но не
/// занятость билбордов: для этого нужен каталог и <see cref="ScheduleValidator"/>.
/// </summary>
public sealed class ScheduleSerializer
{
    public static JsonSerializerOptions JsonOptions { get; } = CreateOptions();

    public string Export(ScheduleExport data)
    {
        ArgumentNullException.ThrowIfNull(data);
        return JsonSerializer.Serialize(data, JsonOptions);
    }

    public void ExportTo(Stream stream, ScheduleExport data)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(data);
        JsonSerializer.Serialize(stream, data, JsonOptions);
    }

    public ScheduleExport Import(string json)
    {
        var result = TryImport(json);
        if (!result.Success)
        {
            throw new ScheduleImportException(result.Errors);
        }

        return result.Data!;
    }

    public ScheduleImportResult TryImport(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new ScheduleImportResult { Errors = ["Пустой JSON."] };
        }

        ScheduleExport? export;
        try
        {
            export = JsonSerializer.Deserialize<ScheduleExport>(json, JsonOptions);
        }
        catch (JsonException exception)
        {
            return new ScheduleImportResult
            {
                Errors = [$"Некорректный JSON: {exception.Message}"],
            };
        }

        if (export is null)
        {
            return new ScheduleImportResult { Errors = ["JSON не содержит объект расписания."] };
        }

        if (export.SchemaVersion != ScheduleExport.CurrentSchemaVersion)
        {
            return new ScheduleImportResult
            {
                Errors =
                [
                    $"Неподдерживаемая версия схемы {export.SchemaVersion}; " +
                    $"ожидается {ScheduleExport.CurrentSchemaVersion}.",
                ],
            };
        }

        var errors = new List<string>();
        var placements = new List<Placement>();
        var placementIds = new HashSet<Guid>();

        foreach (var placement in export.Placements ?? Array.Empty<Placement>())
        {
            var normalized = placement.Id == Guid.Empty
                ? placement with { Id = Guid.NewGuid() }
                : placement;

            if (!placementIds.Add(normalized.Id))
            {
                errors.Add($"Повторяющийся Id размещения: {normalized.Id}.");
                continue;
            }

            ValidatePlacement(normalized, errors);
            placements.Add(normalized);
        }

        var groups = new List<PlacementGroup>();
        var groupIds = new HashSet<Guid>();

        foreach (var group in export.Groups ?? Array.Empty<PlacementGroup>())
        {
            var normalized = group.Id == Guid.Empty
                ? group with { Id = Guid.NewGuid() }
                : group;

            if (!groupIds.Add(normalized.Id))
            {
                errors.Add($"Повторяющийся Id группы: {normalized.Id}.");
                continue;
            }

            if (normalized.BillboardIds.Count == 0)
            {
                errors.Add($"Группа {normalized.Id}: не указан ни один билборд.");
            }

            groups.Add(normalized);
        }

        if (errors.Count > 0)
        {
            return new ScheduleImportResult { Errors = errors };
        }

        return new ScheduleImportResult
        {
            Data = export with
            {
                Placements = placements,
                Groups = groups,
            },
        };
    }

    private static void ValidatePlacement(Placement placement, List<string> errors)
    {
        if (placement.BillboardId == Guid.Empty)
        {
            errors.Add($"Размещение {placement.Id}: не указан BillboardId.");
        }

        if (placement.VideoAssetId == Guid.Empty)
        {
            errors.Add($"Размещение {placement.Id}: не указан VideoAssetId.");
        }

        if (placement.StartDate > placement.EndDate)
        {
            errors.Add($"Размещение {placement.Id}: дата начала позже даты окончания.");
        }

        if (placement.DaysOfWeek.Count == 0)
        {
            errors.Add($"Размещение {placement.Id}: не выбраны дни недели.");
        }

        if (placement.StartTime == placement.EndTime)
        {
            errors.Add($"Размещение {placement.Id}: пустое временное окно.");
        }

        if (placement.VideoDurationSeconds <= 0)
        {
            errors.Add($"Размещение {placement.Id}: длительность ролика должна быть > 0.");
        }

        if (placement.MinimumPlaysPerHour <= 0)
        {
            errors.Add($"Размещение {placement.Id}: квота показов должна быть > 0.");
        }

        if (!Enum.IsDefined(placement.Status))
        {
            errors.Add($"Размещение {placement.Id}: неизвестный статус {placement.Status}.");
        }
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };

        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }
}
