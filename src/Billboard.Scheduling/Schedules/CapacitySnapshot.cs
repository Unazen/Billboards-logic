namespace Billboard.Scheduling;

/// <summary>
/// Снимок занятости рекламной ёмкости билборда. Строится один раз по существующим
/// размещениям, после чего позволяет быстро проверять кандидатов и перебирать
/// альтернативные окна без повторного обхода всех размещений.
/// </summary>
public sealed class CapacitySnapshot
{
    private readonly Dictionary<(DateOnly Date, int Hour), int> _demand;
    private readonly IReadOnlyList<Placement> _placements;

    private CapacitySnapshot(
        Billboard billboard,
        Dictionary<(DateOnly Date, int Hour), int> demand,
        IReadOnlyList<Placement> placements)
    {
        Billboard = billboard;
        _demand = demand;
        _placements = placements;
    }

    public Billboard Billboard { get; }

    public static CapacitySnapshot Create(Billboard billboard, IEnumerable<Placement> existingPlacements)
    {
        ArgumentNullException.ThrowIfNull(billboard);
        ArgumentNullException.ThrowIfNull(existingPlacements);

        var relevant = existingPlacements
            .Where(placement => placement.BillboardId == billboard.Id)
            .Where(placement => placement.Status.OccupiesCapacity())
            .ToList();

        var demand = new Dictionary<(DateOnly Date, int Hour), int>();

        foreach (var placement in relevant)
        {
            if (placement.RequiredSecondsPerHour <= 0)
            {
                continue;
            }

            foreach (var (date, hour) in ScheduleWindow.GetBuckets(placement))
            {
                demand.TryGetValue((date, hour), out var current);
                demand[(date, hour)] = current + placement.RequiredSecondsPerHour;
            }
        }

        return new CapacitySnapshot(billboard, demand, relevant);
    }

    /// <summary>Проверяет кандидата и возвращает сводку по ёмкости.</summary>
    public CapacitySummary Evaluate(Placement candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        var capacity = Billboard.AdvertCapacitySecondsPerHour;
        var candidateSeconds = candidate.RequiredSecondsPerHour;
        var buckets = ScheduleWindow.GetBuckets(candidate);

        var peakRequired = 0;
        var peakExisting = 0;
        var overflows = new List<CapacityBucket>();

        foreach (var (date, hour) in buckets)
        {
            _demand.TryGetValue((date, hour), out var existingSeconds);
            var required = existingSeconds + candidateSeconds;

            peakRequired = Math.Max(peakRequired, required);
            peakExisting = Math.Max(peakExisting, existingSeconds);

            if (required > capacity)
            {
                overflows.Add(new CapacityBucket
                {
                    Date = date,
                    Hour = hour,
                    RequiredSeconds = required,
                    CapacitySeconds = capacity,
                });
            }
        }

        return new CapacitySummary
        {
            CapacitySecondsPerHour = capacity,
            CandidateSecondsPerHour = candidateSeconds,
            PeakRequiredSecondsPerHour = peakRequired,
            PeakExistingSecondsPerHour = peakExisting,
            ApplicableHourBuckets = buckets.Count,
            OverflowBuckets = overflows.Count,
            OverflowDetails = overflows,
        };
    }

    /// <summary>Занятость без кандидата (для диагностики и UI).</summary>
    public int GetExistingDemand(DateOnly date, int hour) =>
        _demand.TryGetValue((date, hour), out var seconds) ? seconds : 0;

    /// <summary>Размещения, из-за которых в слоте уже занята ёмкость.</summary>
    public IReadOnlyList<Guid> GetBlockingPlacementIds(DateOnly date, int hour)
    {
        var result = new List<Guid>();

        foreach (var placement in _placements)
        {
            if (ScheduleWindow.Covers(placement, date, hour) &&
                !result.Contains(placement.Id))
            {
                result.Add(placement.Id);
            }
        }

        return result;
    }
}
