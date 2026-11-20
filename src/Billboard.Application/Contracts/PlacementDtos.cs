using System.ComponentModel.DataAnnotations;

namespace Billboard.Application.Contracts;

/// <summary>Размещение для интерфейса клиента и администратора.</summary>
public record PlacementDto(
    Guid Id,
    Guid OwnerId,
    Guid VideoAssetId,
    string VideoFileName,
    Guid BillboardId,
    string BillboardName,
    DateTimeOffset StartAt,
    DateTimeOffset EndAt,
    int DaysOfWeekMask,
    IReadOnlyList<string> DaysOfWeek,
    TimeOnly StartTimeOfDay,
    TimeOnly EndTimeOfDay,
    int MinimumPlaysPerHour,
    string Status,
    string? PauseReason,
    decimal ReservedAmount,
    decimal ChargedAmount,
    int Plays,
    DateTimeOffset CreatedAt);

/// <summary>
/// Создание и изменение размещения. Владелец всегда берётся из токена,
/// поэтому OwnerId в запросе отсутствует.
/// </summary>
public record PlacementUpsertRequest(
    Guid VideoAssetId,
    Guid BillboardId,
    DateTimeOffset StartAt,
    DateTimeOffset EndAt,
    [Required, MinLength(1), MaxLength(7)] IReadOnlyList<string> DaysOfWeek,
    TimeOnly StartTimeOfDay,
    TimeOnly EndTimeOfDay,
    [Range(1, 600)] int MinimumPlaysPerHour);

/// <summary>Результат проверки размещения: доступно ли время и сколько это стоит.</summary>
public record PlacementValidationResultDto(
    bool Available,
    QuoteDto? Quote,
    IReadOnlyList<PlacementConflictDto> Conflicts,
    IReadOnlyList<AlternativeWindowDto> Alternatives);

public record PlacementConflictDto(Guid BillboardId, string Reason, string? Message);

public record AlternativeWindowDto(DateTimeOffset StartAt, DateTimeOffset EndAt);

public record PublishPlacementResultDto(Guid PlacementId, string Status, decimal ReservedAmount, QuoteDto Quote);
