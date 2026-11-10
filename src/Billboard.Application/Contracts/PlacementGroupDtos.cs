using System.ComponentModel.DataAnnotations;

namespace Billboard.Application.Contracts;

/// <summary>
/// Один шаблон расписания для группы билбордов (контракт P2).
/// <see cref="VideoDurationSeconds"/> необязателен: длительность берётся из VideoAsset.
/// </summary>
public record PlacementGroupTemplateDto(
    Guid VideoAssetId,
    DateOnly StartDate,
    DateOnly EndDate,
    [Required, MinLength(1), MaxLength(7)] IReadOnlyList<string> DaysOfWeek,
    TimeOnly StartTime,
    TimeOnly EndTime,
    [Range(1, 600)] int MinimumPlaysPerHour,
    int VideoDurationSeconds = 0);

public record PlacementGroupRequestDto(
    [Required, MinLength(1), MaxLength(100)] IReadOnlyList<Guid> BillboardIds,
    [Required] PlacementGroupTemplateDto Template);

public record GroupScheduleConflictDto(string Code, string Message);

/// <summary>Результат проверки одного устройства группы.</summary>
public record BillboardScheduleValidationDto(
    Guid BillboardId,
    string BillboardName,
    bool IsAvailable,
    IReadOnlyList<GroupScheduleConflictDto> Conflicts,
    IReadOnlyList<AlternativeWindowDto> Alternatives);

/// <summary>Результат проверки группы: публиковать можно только если прошли все устройства.</summary>
public record GroupScheduleValidationDto(
    bool CanPublish,
    int RequestedBillboardCount,
    int AvailableBillboardCount,
    IReadOnlyList<BillboardScheduleValidationDto> Results);

/// <summary>Результат атомарного создания черновиков для группы.</summary>
public record GroupPlacementOperationDto(
    bool Success,
    IReadOnlyList<PlacementDto> Placements,
    GroupScheduleValidationDto? Validation,
    string? ErrorCode,
    string? Message);
