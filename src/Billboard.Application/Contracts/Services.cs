using Billboard.Domain.Entities;

namespace Billboard.Application.Contracts;

public interface ITariffService
{
    /// <summary>Действующий тариф: активная подписка или тариф по умолчанию.</summary>
    Task<TariffPlan> GetEffectivePlanAsync(Guid userId, CancellationToken ct = default);
    Task<IReadOnlyList<TariffDto>> ListAsync(bool onlyActive, CancellationToken ct = default);
    Task<TariffDto> CreateAsync(TariffUpsertRequest request, CancellationToken ct = default);
    Task<TariffDto> UpdateAsync(Guid id, TariffUpsertRequest request, CancellationToken ct = default);
}

public interface IVideoService
{
    /// <summary>Шаг 1: создать задачу загрузки, проверив тариф.</summary>
    Task<UploadInitDto> CreateUploadAsync(Guid userId, CreateUploadRequest request, CancellationToken ct = default);

    /// <summary>Шаг 2: передать содержимое файла потоком.</summary>
    Task<VideoDto> UploadContentAsync(Guid userId, bool isAdmin, Guid videoId, Stream content, CancellationToken ct = default);

    Task<VideoDto> ImportFromUrlAsync(Guid userId, string url, CancellationToken ct = default);
    Task<IReadOnlyList<VideoDto>> ListAsync(Guid userId, bool all, CancellationToken ct = default);
    Task<VideoDto> GetAsync(Guid userId, bool isAdmin, Guid videoId, CancellationToken ct = default);
    Task DeleteAsync(Guid userId, bool isAdmin, Guid videoId, CancellationToken ct = default);
    Task<Uri> GetDownloadUrlAsync(Guid userId, bool isAdmin, Guid videoId, CancellationToken ct = default);

    /// <summary>Для manifest устройства: файл (предпочтительно сжатый вариант) и ссылка на него.</summary>
    Task<VideoSourceDto> GetPlaybackSourceAsync(Guid videoId, CancellationToken ct = default);
}

public interface IWalletService
{
    Task<WalletDto> GetWalletAsync(Guid userId, CancellationToken ct = default);
    Task<WalletTransactionDto> TopUpAsync(Guid userId, TopUpRequest request, CancellationToken ct = default);
    Task<IReadOnlyList<WalletTransactionDto>> GetTransactionsAsync(Guid userId, int skip, int take, CancellationToken ct = default);
}

public interface IBillingService
{
    Task<QuoteDto> QuoteAsync(Guid userId, bool isAdmin, Guid placementId, int billboardCount, CancellationToken ct = default);

    /// <summary>
    /// Резервирует бюджет под размещение (идемпотентно). Вызывается планировщиком
    /// при публикации после успешной проверки коллизий. Статус размещения не меняет.
    /// </summary>
    Task<ReservationDto> ReserveAsync(Guid userId, Guid placementId, int billboardCount, CancellationToken ct = default);

    /// <summary>Добавляет средства к резерву; снимает паузу по исчерпанию бюджета.</summary>
    Task<ReservationDto> AddToReservationAsync(Guid userId, Guid placementId, decimal amount, string idempotencyKey, CancellationToken ct = default);

    /// <summary>Освобождает неиспользованный остаток резерва (отмена или завершение).</summary>
    Task<ReservationDto?> ReleaseAsync(Guid placementId, CancellationToken ct = default);

    Task<SubscriptionDto> SubscribeAsync(Guid userId, SubscribeRequest request, CancellationToken ct = default);
    Task CancelSubscriptionAsync(Guid userId, Guid subscriptionId, CancellationToken ct = default);

    Task<EarningsSummaryDto> GetEarningsAsync(Guid? billboardId, int take, CancellationToken ct = default);
    Task<IReadOnlyList<PlacementUsageDto>> GetUsageAsync(Guid? userId, CancellationToken ct = default);
}

public interface IPlaybackService
{
    /// <summary>Принимает события показов от устройства; повторы игнорирует, списывает деньги один раз.</summary>
    Task<PlaybackEventsResultDto> IngestAsync(Guid billboardId, IReadOnlyList<PlaybackEventDto> events, CancellationToken ct = default);
}

public interface IBillboardService
{
    Task<IReadOnlyList<BillboardDto>> ListAsync(bool includeDisabled, CancellationToken ct = default);
    Task<BillboardDto> GetAsync(Guid id, CancellationToken ct = default);
    Task<BillboardCreatedDto> CreateAsync(BillboardUpsertRequest request, CancellationToken ct = default);
    Task<BillboardDto> UpdateAsync(Guid id, BillboardUpsertRequest request, CancellationToken ct = default);
    Task<BillboardDto> DisableAsync(Guid id, CancellationToken ct = default);
    Task<BillboardCreatedDto> RotateDeviceTokenAsync(Guid id, CancellationToken ct = default);
    Task HeartbeatAsync(Guid billboardId, HeartbeatRequest request, CancellationToken ct = default);
}

public interface IAdminService
{
    Task<IReadOnlyList<AdminUserDto>> ListUsersAsync(int skip, int take, CancellationToken ct = default);

    /// <summary>Блокирует или разблокирует пользователя. Себя заблокировать нельзя.</summary>
    Task<AdminUserDto> SetUserActiveAsync(Guid actorId, Guid userId, bool isActive, CancellationToken ct = default);

    /// <summary>Назначает единственную роль (Admin или Client). Лишить себя роли Admin нельзя.</summary>
    Task<AdminUserDto> SetUserRoleAsync(Guid actorId, Guid userId, string role, CancellationToken ct = default);

    Task<IReadOnlyList<SystemLogDto>> QueryLogsAsync(SystemLogQuery query, CancellationToken ct = default);

    /// <summary>Экспорт логов в CSV (UTF-8, с защитой от формульной инъекции).</summary>
    Task<string> ExportLogsCsvAsync(SystemLogQuery query, CancellationToken ct = default);

    Task<IReadOnlyList<AdminDeviceDto>> ListDevicesAsync(CancellationToken ct = default);
}

public interface IPaymentGateway
{
    Task<PaymentResult> ChargeAsync(PaymentRequest request, CancellationToken ct = default);
}

public record PaymentRequest(Guid UserId, decimal Amount, string IdempotencyKey, bool ForceFailure);
public record PaymentResult(bool Success, string? ExternalId, string? Error);

// ---------- Медиа ----------
public record VideoProbeResult(decimal? DurationSeconds, string? Format, string? VideoCodec);

public interface IVideoProbe
{
    /// <summary>Метаданные файла; null, если средство анализа недоступно. Бросает AppException для битого файла.</summary>
    Task<VideoProbeResult?> ProbeAsync(string filePath, CancellationToken ct = default);
}

public interface IVideoTranscoder
{
    /// <summary>Сжимает видео; false, если не удалось или ffmpeg недоступен.</summary>
    Task<bool> TranscodeAsync(string inputPath, string outputPath, Billboard.Domain.Enums.CompressionLevel level, CancellationToken ct = default);
}

public record FetchResult(string FileName, string? ContentType, long SizeBytes, string Sha256);

public interface IUrlFetcher
{
    /// <summary>Скачивает файл по URL в destinationPath с защитой от SSRF и лимитом размера.</summary>
    Task<FetchResult> DownloadAsync(Uri url, string destinationPath, long maxBytes, CancellationToken ct = default);
}
