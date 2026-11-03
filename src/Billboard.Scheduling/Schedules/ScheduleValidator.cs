namespace Billboard.Scheduling;

/// <summary>
/// Проверка доступности размещения: форма расписания, техническое состояние
/// билборда, бюджет и рекламная ёмкость по часовым слотам.
/// </summary>
public sealed class ScheduleValidator
{
    public ScheduleValidationResult Validate(
        PlacementRequest request,
        Billboard billboard,
        IEnumerable<Placement> existingPlacements,
        ScheduleValidationOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(billboard);
        ArgumentNullException.ThrowIfNull(existingPlacements);

        options ??= ScheduleValidationOptions.Default;

        var shapeConflicts = ValidateShape(request, billboard, options).ToList();
        if (shapeConflicts.Count > 0)
        {
            return new ScheduleValidationResult { Conflicts = shapeConflicts };
        }

        var conflicts = new List<ScheduleConflict>();
        var candidate = request.ToPlacement(Guid.Empty, PlacementStatus.Scheduled);

        if (billboard.Status == BillboardStatus.Offline)
        {
            conflicts.Add(new ScheduleConflict
            {
                Code = ConflictCodes.BillboardOffline,
                Message = "Билборд отключён от сети: публикация расписания невозможна.",
                BillboardId = billboard.Id,
            });
        }
        else if (!billboard.IsOnline)
        {
            conflicts.Add(new ScheduleConflict
            {
                Code = ConflictCodes.BillboardOffline,
                Message = $"Билборд находится в состоянии {billboard.Status}: публикация невозможна.",
                BillboardId = billboard.Id,
            });
        }

        if (options.CheckBudget &&
            request.AvailableBudget.HasValue &&
            request.EstimatedCost > request.AvailableBudget.Value)
        {
            conflicts.Add(new ScheduleConflict
            {
                Code = ConflictCodes.InsufficientBudget,
                Message =
                    $"Недостаточно средств: требуется {request.EstimatedCost:0.##}, доступно {request.AvailableBudget.Value:0.##}.",
                BillboardId = billboard.Id,
                RequiredSeconds = null,
                AvailableSeconds = null,
            });
        }

        var snapshot = CapacitySnapshot.Create(billboard, existingPlacements);
        var summary = snapshot.Evaluate(candidate);

        if (candidate.RequiredSecondsPerHour > billboard.AdvertCapacitySecondsPerHour)
        {
            conflicts.Add(new ScheduleConflict
            {
                Code = ConflictCodes.QuotaExceedsCapacity,
                Message =
                    $"Квота {candidate.MinimumPlaysPerHour} показов/ч по {candidate.VideoDurationSeconds} с " +
                    $"требует {candidate.RequiredSecondsPerHour} с/ч, а ёмкость билборда — " +
                    $"{billboard.AdvertCapacitySecondsPerHour} с/ч.",
                BillboardId = billboard.Id,
                RequiredSeconds = candidate.RequiredSecondsPerHour,
                AvailableSeconds = billboard.AdvertCapacitySecondsPerHour,
            });
        }
        else
        {
            conflicts.AddRange(BuildCapacityConflicts(snapshot, summary));
        }

        IReadOnlyList<AlternativeSlot> alternatives = Array.Empty<AlternativeSlot>();
        var hasCapacityConflict = conflicts.Any(conflict =>
            conflict.Code == ConflictCodes.InsufficientAdvertisingCapacity);

        if (hasCapacityConflict &&
            options.SuggestAlternatives &&
            options.MaxAlternatives > 0 &&
            !conflicts.Any(conflict => conflict.Code is ConflictCodes.BillboardOffline or ConflictCodes.InsufficientBudget))
        {
            alternatives = new AlternativeSlotFinder().Find(request, snapshot, options);
        }

        return new ScheduleValidationResult
        {
            Conflicts = conflicts,
            Alternatives = alternatives,
            Summary = summary,
        };
    }

    /// <summary>Проверка размещения, уже сохранённого в БД.</summary>
    public ScheduleValidationResult Validate(
        Placement placement,
        Billboard billboard,
        IEnumerable<Placement> existingPlacements,
        ScheduleValidationOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(placement);
        return Validate(placement.ToRequest(), billboard, existingPlacements, options);
    }

    /// <summary>
    /// Проверка формы расписания без учёта занятости. Используется при создании
    /// черновика, чтобы в БД не попадали заведомо некорректные данные.
    /// </summary>
    public IReadOnlyList<ScheduleConflict> ValidateShape(
        PlacementRequest request,
        Billboard billboard,
        ScheduleValidationOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(billboard);

        options ??= ScheduleValidationOptions.Default;
        var conflicts = new List<ScheduleConflict>();

        if (request.StartDate > request.EndDate)
        {
            conflicts.Add(new ScheduleConflict
            {
                Code = ConflictCodes.InvalidDateRange,
                Message = "Дата начала позже даты окончания.",
                BillboardId = billboard.Id,
            });
        }
        else
        {
            var horizonDays = request.EndDate.DayNumber - request.StartDate.DayNumber + 1;
            if (horizonDays > options.MaxHorizonDays)
            {
                conflicts.Add(new ScheduleConflict
                {
                    Code = ConflictCodes.DateRangeTooLong,
                    Message =
                        $"Расписание длиной {horizonDays} дн. превышает допустимые {options.MaxHorizonDays} дн.",
                    BillboardId = billboard.Id,
                });
            }
        }

        var days = request.DaysOfWeek.Distinct().ToArray();
        if (days.Length == 0)
        {
            conflicts.Add(new ScheduleConflict
            {
                Code = ConflictCodes.EmptyDaysOfWeek,
                Message = "Не выбран ни один день недели.",
                BillboardId = billboard.Id,
            });
        }
        else if (days.Any(day => !Enum.IsDefined(day)))
        {
            conflicts.Add(new ScheduleConflict
            {
                Code = ConflictCodes.EmptyDaysOfWeek,
                Message = "Список дней недели содержит недопустимое значение.",
                BillboardId = billboard.Id,
            });
        }

        if (request.StartTime == request.EndTime)
        {
            conflicts.Add(new ScheduleConflict
            {
                Code = ConflictCodes.InvalidTimeWindow,
                Message = "Начало и конец временного окна совпадают.",
                BillboardId = billboard.Id,
            });
        }

        if (request.VideoDurationSeconds <= 0)
        {
            conflicts.Add(new ScheduleConflict
            {
                Code = ConflictCodes.InvalidDuration,
                Message = "Длительность ролика должна быть больше нуля.",
                BillboardId = billboard.Id,
            });
        }

        if (request.MinimumPlaysPerHour <= 0)
        {
            conflicts.Add(new ScheduleConflict
            {
                Code = ConflictCodes.InvalidPlaysPerHour,
                Message = "Минимальная квота показов в час должна быть больше нуля.",
                BillboardId = billboard.Id,
            });
        }

        if (request.EstimatedCost < 0)
        {
            conflicts.Add(new ScheduleConflict
            {
                Code = ConflictCodes.InvalidCost,
                Message = "Стоимость размещения не может быть отрицательной.",
                BillboardId = billboard.Id,
            });
        }

        if (!billboard.HasValidCapacity)
        {
            conflicts.Add(new ScheduleConflict
            {
                Code = ConflictCodes.InvalidBillboardCapacity,
                Message =
                    $"Ёмкость билборда должна быть в диапазоне 1..{Billboard.FullHourSeconds} с/ч.",
                BillboardId = billboard.Id,
            });
        }

        return conflicts;
    }

    /// <summary>Проверка одного расписания для группы билбордов.</summary>
    public GroupScheduleValidationResult ValidateGroup(
        PlacementGroupRequest group,
        IReadOnlyDictionary<Guid, Billboard> billboards,
        IReadOnlyDictionary<Guid, IReadOnlyList<Placement>> existingByBillboard,
        ScheduleValidationOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(group);
        ArgumentNullException.ThrowIfNull(billboards);
        ArgumentNullException.ThrowIfNull(existingByBillboard);

        var results = new List<BillboardValidation>();
        var seen = new HashSet<Guid>();

        foreach (var billboardId in group.BillboardIds)
        {
            if (!seen.Add(billboardId))
            {
                continue;
            }

            if (!billboards.TryGetValue(billboardId, out var billboard))
            {
                results.Add(new BillboardValidation
                {
                    BillboardId = billboardId,
                    Validation = new ScheduleValidationResult
                    {
                        Conflicts =
                        [
                            new ScheduleConflict
                            {
                                Code = ConflictCodes.BillboardNotFound,
                                Message = "Билборд не найден.",
                                BillboardId = billboardId,
                            },
                        ],
                    },
                });
                continue;
            }

            existingByBillboard.TryGetValue(billboardId, out var existing);
            var request = group.Template with { BillboardId = billboardId };

            results.Add(new BillboardValidation
            {
                BillboardId = billboardId,
                BillboardName = billboard.Name,
                Validation = Validate(request, billboard, existing ?? Array.Empty<Placement>(), options),
            });
        }

        return new GroupScheduleValidationResult { Results = results };
    }

    private static IEnumerable<ScheduleConflict> BuildCapacityConflicts(
        CapacitySnapshot snapshot,
        CapacitySummary summary)
    {
        var byHour = summary.OverflowDetails
            .GroupBy(bucket => bucket.Hour)
            .OrderBy(group => group.Key);

        foreach (var group in byHour)
        {
            var required = group.Max(bucket => bucket.RequiredSeconds);
            var dates = group
                .Select(bucket => bucket.Date)
                .Distinct()
                .OrderBy(date => date)
                .ToArray();

            var blockers = new List<Guid>();
            foreach (var bucket in group)
            {
                foreach (var id in snapshot.GetBlockingPlacementIds(bucket.Date, bucket.Hour))
                {
                    if (!blockers.Contains(id))
                    {
                        blockers.Add(id);
                    }
                }
            }

            var dateHint = dates.Length <= 3
                ? string.Join(", ", dates.Select(date => date.ToString("dd.MM.yyyy")))
                : $"{dates.Take(3).Select(date => date.ToString("dd.MM.yyyy")).Aggregate((a, b) => a + ", " + b)} и ещё {dates.Length - 3}";

            yield return new ScheduleConflict
            {
                Code = ConflictCodes.InsufficientAdvertisingCapacity,
                Message =
                    $"Недостаточно рекламной ёмкости в {group.Key:00}:00–{(group.Key + 1) % 24:00}:00: " +
                    $"требуется {required} с/ч, доступно {summary.CapacitySecondsPerHour} с/ч ({dateHint}).",
                BillboardId = snapshot.Billboard.Id,
                Hour = group.Key,
                RequiredSeconds = required,
                AvailableSeconds = summary.CapacitySecondsPerHour,
                Dates = dates.Take(10).ToArray(),
                BlockingPlacementIds = blockers.Take(10).ToArray(),
            };
        }
    }
}
