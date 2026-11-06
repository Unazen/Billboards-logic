using System.ComponentModel.DataAnnotations;

namespace Billboard.Application.Contracts;

// ---------- Видео ----------
public record CreateUploadRequest(
    [property: Required, StringLength(255)] string FileName,
    string? ContentType,
    long? SizeBytes);

public record ImportUrlRequest([property: Required] string Url);

public record UploadInitDto(Guid VideoId, string Status, long MaxSizeBytes);

public record VideoDto(
    Guid Id,
    Guid OwnerId,
    string FileName,
    string ContentType,
    long SizeBytes,
    decimal? DurationSeconds,
    string Status,
    string? FailureReason,
    DateTimeOffset CreatedAt);

/// <summary>Источник для устройства: куда идти за файлом. Используется при сборке manifest.</summary>
public record VideoSourceDto(Guid VideoId, Uri Url, long SizeBytes, string? Checksum);

// ---------- Тарифы ----------
public record TariffDto(
    Guid Id,
    string Name,
    int MaxVideoCount,
    long MaxVideoSizeBytes,
    int MaxVideoDurationSeconds,
    string CompressionLevel,
    bool AdvancedStatistics,
    bool PriorityProcessing,
    decimal MultiBillboardDiscountPercent,
    decimal MonthlyPrice,
    bool IsDefault,
    bool IsActive);

public record TariffUpsertRequest(
    [property: Required, StringLength(100)] string Name,
    [property: Range(1, 100000)] int MaxVideoCount,
    [property: Range(1, long.MaxValue)] long MaxVideoSizeBytes,
    [property: Range(1, 3600)] int MaxVideoDurationSeconds,
    string CompressionLevel,
    bool AdvancedStatistics,
    bool PriorityProcessing,
    [property: Range(0, 100)] decimal MultiBillboardDiscountPercent,
    [property: Range(0, 1000000)] decimal MonthlyPrice,
    bool IsDefault,
    bool IsActive);

// ---------- Деньги ----------
public record WalletDto(Guid Id, decimal Balance, decimal ReservedBalance, string Currency);

public record TopUpRequest(
    [property: Range(0.01, 100000)] decimal Amount,
    [property: Required, StringLength(100)] string IdempotencyKey,
    bool SimulateFailure = false);

public record WalletTransactionDto(
    Guid Id,
    string Type,
    decimal Amount,
    decimal BalanceAfter,
    Guid? RelatedEntityId,
    string? Description,
    DateTimeOffset CreatedAt);

public record QuoteDto(
    Guid PlacementId,
    decimal BillableHours,
    int ExpectedPlays,
    decimal PricePerPlay,
    decimal DiscountPercent,
    decimal PricePerPlayAfterDiscount,
    decimal TotalAmount,
    string Currency);

public record ReservationDto(
    Guid PlacementId,
    decimal ReservedAmount,
    decimal ChargedAmount,
    decimal ReleasedAmount,
    decimal Remaining);

public record SubscribeRequest(
    Guid TariffPlanId,
    [property: Required, StringLength(100)] string IdempotencyKey);

public record SubscriptionDto(Guid Id, Guid TariffPlanId, string TariffName, DateTimeOffset StartAt, DateTimeOffset EndAt, string Status);

public record EarningDto(Guid Id, Guid BillboardId, Guid PlacementId, decimal Amount, DateTimeOffset CreatedAt);

public record EarningsSummaryDto(decimal Total, int Count, IReadOnlyList<EarningDto> Items);

public record PlacementUsageDto(Guid PlacementId, Guid BillboardId, int Plays, decimal Spent, decimal Reserved);

// ---------- Билборды ----------
public record BillboardDto(
    Guid Id,
    string Name,
    string Address,
    decimal? Latitude,
    decimal? Longitude,
    long CapacityBytes,
    int AdvertCapacitySecondsPerHour,
    string Status,
    DateTimeOffset? LastSeenAt,
    decimal PricePerPlay,
    string TimeZoneId);

public record BillboardUpsertRequest(
    [property: Required, StringLength(200)] string Name,
    [property: Required, StringLength(500)] string Address,
    decimal? Latitude,
    decimal? Longitude,
    long? CapacityBytes,
    int? AdvertCapacitySecondsPerHour,
    [property: Range(0, 100000)] decimal PricePerPlay,
    string? TimeZoneId);

/// <summary>Токен возвращается один раз при создании или ротации.</summary>
public record BillboardCreatedDto(BillboardDto Billboard, string DeviceToken);

// ---------- Устройства ----------
public record HeartbeatRequest(string? SimulatorVersion, string? Status);

public record PlaybackEventDto(
    [property: Required, StringLength(200)] string IdempotencyKey,
    Guid PlacementId,
    Guid VideoAssetId,
    DateTimeOffset StartedAt,
    [property: Range(0, 3600)] int DurationSeconds,
    string Result);

public record PlaybackEventsRequest([property: Required, MinLength(1), MaxLength(500)] List<PlaybackEventDto> Events);

public record PlaybackEventsResultDto(int Accepted, int Duplicates, int Rejected, IReadOnlyList<string> Errors);

// ---------- Аккаунт ----------
public record RegisterRequest(
    [property: Required, EmailAddress] string Email,
    [property: Required, MinLength(6)] string Password,
    [property: Required, StringLength(100)] string DisplayName);

public record LoginRequest(
    [property: Required, EmailAddress] string Email,
    [property: Required] string Password);

public record AccountDto(Guid Id, string Email, string DisplayName, IReadOnlyList<string> Roles, string Tariff);

// ---------- Администрирование ----------
public record AdminUserDto(
    Guid Id,
    string Email,
    string DisplayName,
    bool IsActive,
    IReadOnlyList<string> Roles,
    DateTimeOffset CreatedAt);

public record SetUserActiveRequest(bool IsActive);

public record SetUserRoleRequest([property: Required] string Role);

public record SystemLogDto(
    Guid Id,
    Guid? UserId,
    string Action,
    string? EntityType,
    string? EntityId,
    string Result,
    string? Details,
    DateTimeOffset CreatedAt);

public record SystemLogQuery(
    string? Action = null,
    Guid? UserId = null,
    DateTimeOffset? From = null,
    DateTimeOffset? To = null,
    int Skip = 0,
    int Take = 100);

/// <summary>Состояние устройства для административного мониторинга.</summary>
public record AdminDeviceDto(
    Guid Id,
    string Name,
    string Status,
    DateTimeOffset? LastSeenAt,
    string? SimulatorVersion,
    long CacheUsedBytes,
    long CapacityBytes);
