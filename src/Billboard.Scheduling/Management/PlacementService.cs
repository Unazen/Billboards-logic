namespace Billboard.Scheduling;

/// <summary>
/// Операции жизненного цикла размещения: черновик, публикация, изменение,
/// пауза, возобновление и отмена. Все изменения расписания проходят повторную
/// проверку доступности, чтобы нельзя было опубликовать конфликт.
/// </summary>
public sealed class PlacementService
{
    private readonly IPlacementRepository _repository;
    private readonly IBillboardCatalog _billboards;
    private readonly ScheduleValidator _validator;
    private readonly ScheduleValidationOptions _options;
    private readonly Func<DateTimeOffset> _clock;

    public PlacementService(
        IPlacementRepository repository,
        IBillboardCatalog billboards,
        ScheduleValidator? validator = null,
        ScheduleValidationOptions? options = null,
        Func<DateTimeOffset>? clock = null)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _billboards = billboards ?? throw new ArgumentNullException(nameof(billboards));
        _validator = validator ?? new ScheduleValidator();
        _options = options ?? ScheduleValidationOptions.Default;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public async Task<PlacementOperationResult> CreateDraftAsync(
        PlacementRequest request,
        Guid ownerId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var billboard = await _billboards.GetAsync(request.BillboardId, cancellationToken);
        if (billboard is null)
        {
            return PlacementOperationResult.Fail(
                ConflictCodes.BillboardNotFound,
                "Билборд не найден.");
        }

        var shapeConflicts = _validator.ValidateShape(request, billboard, _options);
        if (shapeConflicts.Count > 0)
        {
            return PlacementOperationResult.Fail(
                PlacementErrorCodes.ValidationFailed,
                "Расписание содержит некорректные данные.",
                new ScheduleValidationResult { Conflicts = shapeConflicts });
        }

        var placement = request.ToPlacement(ownerId, PlacementStatus.Draft);
        await _repository.AddAsync(placement, cancellationToken);
        return PlacementOperationResult.Ok(placement);
    }

    public async Task<PlacementOperationResult> UpdateScheduleAsync(
        Guid id,
        PlacementRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var placement = await _repository.GetAsync(id, cancellationToken);
        if (placement is null)
        {
            return PlacementOperationResult.Fail(
                PlacementErrorCodes.NotFound,
                "Размещение не найдено.");
        }

        if (!placement.Status.CanEditSchedule())
        {
            return PlacementOperationResult.Fail(
                PlacementErrorCodes.InvalidStatus,
                $"Нельзя изменить расписание в статусе {placement.Status}.",
                placement: placement);
        }

        var billboard = await _billboards.GetAsync(request.BillboardId, cancellationToken);
        if (billboard is null)
        {
            return PlacementOperationResult.Fail(
                ConflictCodes.BillboardNotFound,
                "Билборд не найден.",
                placement: placement);
        }

        var shapeConflicts = _validator.ValidateShape(request, billboard, _options);
        if (shapeConflicts.Count > 0)
        {
            return PlacementOperationResult.Fail(
                PlacementErrorCodes.ValidationFailed,
                "Расписание содержит некорректные данные.",
                new ScheduleValidationResult { Conflicts = shapeConflicts },
                placement);
        }

        var existing = await GetExistingAsync(
            request.BillboardId,
            request.StartDate,
            request.EndDate,
            excludedPlacementId: id,
            cancellationToken);

        var validation = _validator.Validate(request, billboard, existing, _options);
        if (!validation.IsAvailable)
        {
            return PlacementOperationResult.Fail(
                PlacementErrorCodes.ValidationFailed,
                "Новое расписание конфликтует с существующими размещениями.",
                validation,
                placement);
        }

        var updated = placement with
        {
            BillboardId = request.BillboardId,
            VideoAssetId = request.VideoAssetId,
            StartDate = request.StartDate,
            EndDate = request.EndDate,
            DaysOfWeek = request.DaysOfWeek,
            StartTime = request.StartTime,
            EndTime = request.EndTime,
            MinimumPlaysPerHour = request.MinimumPlaysPerHour,
            VideoDurationSeconds = request.VideoDurationSeconds,
            EstimatedCost = request.EstimatedCost,
            UpdatedAt = _clock(),
            Version = placement.Version + 1,
        };

        var saved = await _repository.UpdateAsync(updated, placement.Version, cancellationToken);
        if (!saved)
        {
            return PlacementOperationResult.Fail(
                PlacementErrorCodes.ConcurrencyConflict,
                "Размещение уже изменено другим запросом. Обновите данные и повторите.",
                placement: placement);
        }

        return PlacementOperationResult.Ok(updated, validation);
    }

    public async Task<PlacementOperationResult> PublishAsync(
        Guid id,
        decimal? availableBudget = null,
        CancellationToken cancellationToken = default)
    {
        var placement = await _repository.GetAsync(id, cancellationToken);
        if (placement is null)
        {
            return PlacementOperationResult.Fail(
                PlacementErrorCodes.NotFound,
                "Размещение не найдено.");
        }

        if (placement.Status is PlacementStatus.Scheduled or
            PlacementStatus.Downloading or
            PlacementStatus.Ready or
            PlacementStatus.Active)
        {
            return PlacementOperationResult.Ok(placement);
        }

        if (placement.Status is PlacementStatus.Cancelled or PlacementStatus.Completed)
        {
            return PlacementOperationResult.Fail(
                PlacementErrorCodes.InvalidStatus,
                $"Нельзя опубликовать размещение в статусе {placement.Status}.",
                placement: placement);
        }

        if (placement.Status == PlacementStatus.Paused)
        {
            return PlacementOperationResult.Fail(
                PlacementErrorCodes.InvalidStatus,
                "Размещение приостановлено: используйте возобновление, а не публикацию.",
                placement: placement);
        }

        var billboard = await _billboards.GetAsync(placement.BillboardId, cancellationToken);
        if (billboard is null)
        {
            return PlacementOperationResult.Fail(
                ConflictCodes.BillboardNotFound,
                "Билборд не найден.",
                placement: placement);
        }

        var existing = await GetExistingAsync(
            placement.BillboardId,
            placement.StartDate,
            placement.EndDate,
            excludedPlacementId: placement.Id,
            cancellationToken);

        var request = placement.ToRequest() with { AvailableBudget = availableBudget };
        var validation = _validator.Validate(request, billboard, existing, _options);
        if (!validation.IsAvailable)
        {
            return PlacementOperationResult.Fail(
                PlacementErrorCodes.ValidationFailed,
                "Публикация невозможна: расписание конфликтует с существующими размещениями.",
                validation,
                placement);
        }

        var published = placement with
        {
            Status = PlacementStatus.Scheduled,
            StatusBeforePause = null,
            UpdatedAt = _clock(),
            Version = placement.Version + 1,
        };

        var saved = await _repository.UpdateAsync(published, placement.Version, cancellationToken);
        if (!saved)
        {
            return PlacementOperationResult.Fail(
                PlacementErrorCodes.ConcurrencyConflict,
                "Размещение уже изменено другим запросом. Обновите данные и повторите.",
                placement: placement);
        }

        return PlacementOperationResult.Ok(published, validation);
    }

    public async Task<PlacementOperationResult> PauseAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        var placement = await _repository.GetAsync(id, cancellationToken);
        if (placement is null)
        {
            return PlacementOperationResult.Fail(
                PlacementErrorCodes.NotFound,
                "Размещение не найдено.");
        }

        if (placement.Status == PlacementStatus.Paused)
        {
            return PlacementOperationResult.Ok(placement);
        }

        if (placement.Status is not (PlacementStatus.Scheduled or
            PlacementStatus.Downloading or
            PlacementStatus.Ready or
            PlacementStatus.Active))
        {
            return PlacementOperationResult.Fail(
                PlacementErrorCodes.InvalidStatus,
                $"Нельзя приостановить размещение в статусе {placement.Status}.",
                placement: placement);
        }

        var paused = placement with
        {
            Status = PlacementStatus.Paused,
            StatusBeforePause = placement.Status,
            UpdatedAt = _clock(),
            Version = placement.Version + 1,
        };

        var saved = await _repository.UpdateAsync(paused, placement.Version, cancellationToken);
        return saved
            ? PlacementOperationResult.Ok(paused)
            : PlacementOperationResult.Fail(
                PlacementErrorCodes.ConcurrencyConflict,
                "Размещение уже изменено другим запросом.",
                placement: placement);
    }

    public async Task<PlacementOperationResult> ResumeAsync(
        Guid id,
        decimal? availableBudget = null,
        CancellationToken cancellationToken = default)
    {
        var placement = await _repository.GetAsync(id, cancellationToken);
        if (placement is null)
        {
            return PlacementOperationResult.Fail(
                PlacementErrorCodes.NotFound,
                "Размещение не найдено.");
        }

        if (placement.Status != PlacementStatus.Paused)
        {
            return PlacementOperationResult.Fail(
                PlacementErrorCodes.InvalidStatus,
                "Возобновить можно только приостановленное размещение.",
                placement: placement);
        }

        var billboard = await _billboards.GetAsync(placement.BillboardId, cancellationToken);
        if (billboard is null)
        {
            return PlacementOperationResult.Fail(
                ConflictCodes.BillboardNotFound,
                "Билборд не найден.",
                placement: placement);
        }

        var existing = await GetExistingAsync(
            placement.BillboardId,
            placement.StartDate,
            placement.EndDate,
            excludedPlacementId: placement.Id,
            cancellationToken);

        var request = placement.ToRequest() with { AvailableBudget = availableBudget };
        var validation = _validator.Validate(request, billboard, existing, _options);
        if (!validation.IsAvailable)
        {
            return PlacementOperationResult.Fail(
                PlacementErrorCodes.ValidationFailed,
                "Возобновление невозможно: расписание конфликтует с существующими размещениями.",
                validation,
                placement);
        }

        var restore = placement.StatusBeforePause ?? PlacementStatus.Scheduled;
        if (restore is not (PlacementStatus.Scheduled or
            PlacementStatus.Downloading or
            PlacementStatus.Ready or
            PlacementStatus.Active))
        {
            restore = PlacementStatus.Scheduled;
        }

        var resumed = placement with
        {
            Status = restore,
            StatusBeforePause = null,
            UpdatedAt = _clock(),
            Version = placement.Version + 1,
        };

        var saved = await _repository.UpdateAsync(resumed, placement.Version, cancellationToken);
        return saved
            ? PlacementOperationResult.Ok(resumed, validation)
            : PlacementOperationResult.Fail(
                PlacementErrorCodes.ConcurrencyConflict,
                "Размещение уже изменено другим запросом.",
                placement: placement);
    }

    public async Task<PlacementOperationResult> CancelAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        var placement = await _repository.GetAsync(id, cancellationToken);
        if (placement is null)
        {
            return PlacementOperationResult.Fail(
                PlacementErrorCodes.NotFound,
                "Размещение не найдено.");
        }

        if (placement.Status == PlacementStatus.Cancelled)
        {
            return PlacementOperationResult.Ok(placement);
        }

        if (placement.Status == PlacementStatus.Completed)
        {
            return PlacementOperationResult.Fail(
                PlacementErrorCodes.InvalidStatus,
                "Завершённое размещение нельзя отменить.",
                placement: placement);
        }

        var cancelled = placement with
        {
            Status = PlacementStatus.Cancelled,
            StatusBeforePause = null,
            UpdatedAt = _clock(),
            Version = placement.Version + 1,
        };

        var saved = await _repository.UpdateAsync(cancelled, placement.Version, cancellationToken);
        return saved
            ? PlacementOperationResult.Ok(cancelled)
            : PlacementOperationResult.Fail(
                PlacementErrorCodes.ConcurrencyConflict,
                "Размещение уже изменено другим запросом.",
                placement: placement);
    }

    public async Task<PlacementOperationResult> CompleteAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        var placement = await _repository.GetAsync(id, cancellationToken);
        if (placement is null)
        {
            return PlacementOperationResult.Fail(
                PlacementErrorCodes.NotFound,
                "Размещение не найдено.");
        }

        if (placement.Status == PlacementStatus.Completed)
        {
            return PlacementOperationResult.Ok(placement);
        }

        if (placement.Status == PlacementStatus.Cancelled)
        {
            return PlacementOperationResult.Fail(
                PlacementErrorCodes.InvalidStatus,
                "Отменённое размещение нельзя завершить.",
                placement: placement);
        }

        var completed = placement with
        {
            Status = PlacementStatus.Completed,
            StatusBeforePause = null,
            UpdatedAt = _clock(),
            Version = placement.Version + 1,
        };

        var saved = await _repository.UpdateAsync(completed, placement.Version, cancellationToken);
        return saved
            ? PlacementOperationResult.Ok(completed)
            : PlacementOperationResult.Fail(
                PlacementErrorCodes.ConcurrencyConflict,
                "Размещение уже изменено другим запросом.",
                placement: placement);
    }

    public Task<Placement?> GetAsync(Guid id, CancellationToken cancellationToken = default) =>
        _repository.GetAsync(id, cancellationToken);

    public async Task<IReadOnlyList<Placement>> ListAsync(
        Guid? ownerId = null,
        Guid? billboardId = null,
        CancellationToken cancellationToken = default)
    {
        var placements = await _repository.ListAsync(cancellationToken);

        return placements
            .Where(placement => ownerId is null || placement.OwnerId == ownerId)
            .Where(placement => billboardId is null || placement.BillboardId == billboardId)
            .OrderBy(placement => placement.StartDate)
            .ThenBy(placement => placement.StartTime)
            .ToArray();
    }

    private async Task<IReadOnlyList<Placement>> GetExistingAsync(
        Guid billboardId,
        DateOnly from,
        DateOnly to,
        Guid excludedPlacementId,
        CancellationToken cancellationToken)
    {
        var existing = await _repository.GetByBillboardAsync(billboardId, from, to, cancellationToken);
        return existing
            .Where(placement => placement.Id != excludedPlacementId)
            .ToArray();
    }
}
