namespace Billboard.Scheduling;

public static class GroupPlacementErrorCodes
{
    public const string EmptyGroup = "GROUP_IS_EMPTY";
    public const string ValidationFailed = "GROUP_PLACEMENT_VALIDATION_FAILED";
    public const string SaveFailed = "GROUP_PLACEMENT_SAVE_FAILED";
}

/// <summary>Результат назначения одного расписания группе билбордов.</summary>
public sealed record GroupPlacementOperationResult
{
    public bool Success { get; init; }

    public IReadOnlyList<Placement> Placements { get; init; } = Array.Empty<Placement>();

    public GroupScheduleValidationResult? Validation { get; init; }

    public string? ErrorCode { get; init; }

    public string? Message { get; init; }

    public static GroupPlacementOperationResult Ok(
        IReadOnlyList<Placement> placements,
        GroupScheduleValidationResult validation) =>
        new()
        {
            Success = true,
            Placements = placements,
            Validation = validation,
        };

    public static GroupPlacementOperationResult Fail(
        string errorCode,
        string message,
        GroupScheduleValidationResult? validation = null) =>
        new()
        {
            Success = false,
            ErrorCode = errorCode,
            Message = message,
            Validation = validation,
        };
}

/// <summary>
/// Назначение одного расписания группе билбордов. Сначала проверяются все
/// устройства; если хотя бы одно не может принять расписание, не создаётся ни
/// одного размещения. Это закрывает сценарий «группа без частичной публикации».
/// </summary>
public sealed class GroupPlacementService
{
    private readonly IPlacementRepository _repository;
    private readonly IBillboardCatalog _billboards;
    private readonly ScheduleValidator _validator;
    private readonly ScheduleValidationOptions _options;
    private readonly Func<DateTimeOffset> _clock;

    public GroupPlacementService(
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

    /// <summary>Проверяет все билборды группы, не создавая размещений.</summary>
    public async Task<GroupScheduleValidationResult> ValidateAsync(
        PlacementGroupRequest group,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(group);

        var billboardIds = Normalize(group.BillboardIds);
        if (billboardIds.Length == 0)
        {
            return new GroupScheduleValidationResult();
        }

        var billboards = await _billboards.GetManyAsync(billboardIds, cancellationToken);
        var billboardMap = billboards.ToDictionary(billboard => billboard.Id);

        var existingByBillboard = new Dictionary<Guid, IReadOnlyList<Placement>>();
        foreach (var billboardId in billboardIds)
        {
            if (!billboardMap.ContainsKey(billboardId))
            {
                continue;
            }

            existingByBillboard[billboardId] = await _repository.GetByBillboardAsync(
                billboardId,
                group.Template.StartDate,
                group.Template.EndDate,
                cancellationToken);
        }

        var normalized = group with { BillboardIds = billboardIds };
        return _validator.ValidateGroup(normalized, billboardMap, existingByBillboard, _options);
    }

    /// <summary>
    /// Создаёт черновики размещений для всех билбордов группы. При ошибке
    /// сохранения уже добавленные черновики удаляются (rollback в памяти).
    /// В EF Core-реализации P3 эту операцию нужно выполнять в одной транзакции.
    /// </summary>
    public async Task<GroupPlacementOperationResult> CreateDraftsAsync(
        PlacementGroupRequest group,
        Guid ownerId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(group);

        var billboardIds = Normalize(group.BillboardIds);
        if (billboardIds.Length == 0)
        {
            return GroupPlacementOperationResult.Fail(
                GroupPlacementErrorCodes.EmptyGroup,
                "В группе не выбран ни один билборд.");
        }

        var normalized = group with { BillboardIds = billboardIds };
        var validation = await ValidateAsync(normalized, cancellationToken);

        if (!validation.CanPublish)
        {
            return GroupPlacementOperationResult.Fail(
                GroupPlacementErrorCodes.ValidationFailed,
                "Группа не может быть назначена: хотя бы один билборд не прошёл проверку.",
                validation);
        }

        var created = new List<Placement>(billboardIds.Length);

        try
        {
            foreach (var billboardId in billboardIds)
            {
                var placement = normalized.Template
                    .With(billboardId)
                    .ToPlacement(ownerId, PlacementStatus.Draft) with
                {
                    CreatedAt = _clock(),
                };

                await _repository.AddAsync(placement, cancellationToken);
                created.Add(placement);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            foreach (var placement in created)
            {
                await _repository.DeleteAsync(placement.Id, CancellationToken.None);
            }

            return GroupPlacementOperationResult.Fail(
                GroupPlacementErrorCodes.SaveFailed,
                $"Не удалось сохранить размещения группы: {exception.Message}",
                validation);
        }

        return GroupPlacementOperationResult.Ok(created, validation);
    }

    private static Guid[] Normalize(IEnumerable<Guid> billboardIds) =>
        billboardIds
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToArray();
}

internal static class PlacementGroupRequestExtensions
{
    public static PlacementRequest With(this PlacementRequest template, Guid billboardId) =>
        template with { BillboardId = billboardId };
}
