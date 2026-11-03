namespace Billboard.Scheduling;

public static class PlacementErrorCodes
{
    public const string NotFound = "PLACEMENT_NOT_FOUND";
    public const string InvalidStatus = "INVALID_PLACEMENT_STATUS";
    public const string ValidationFailed = "PLACEMENT_VALIDATION_FAILED";
    public const string ConcurrencyConflict = "PLACEMENT_CONCURRENCY_CONFLICT";
}

/// <summary>Результат операции над размещением.</summary>
public sealed record PlacementOperationResult
{
    public bool Success { get; init; }

    public Placement? Placement { get; init; }

    public ScheduleValidationResult? Validation { get; init; }

    public string? ErrorCode { get; init; }

    public string? Message { get; init; }

    public static PlacementOperationResult Ok(Placement placement, ScheduleValidationResult? validation = null) =>
        new()
        {
            Success = true,
            Placement = placement,
            Validation = validation,
        };

    public static PlacementOperationResult Fail(
        string errorCode,
        string message,
        ScheduleValidationResult? validation = null,
        Placement? placement = null) =>
        new()
        {
            Success = false,
            ErrorCode = errorCode,
            Message = message,
            Validation = validation,
            Placement = placement,
        };
}
