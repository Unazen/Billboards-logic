namespace Billboard.Scheduling;

/// <summary>
/// Строит очередь показов на час. Каждое активное размещение должно получить
/// минимум <see cref="Placement.MinimumPlaysPerHour"/> показов; ролики
/// перемешиваются пропорционально квотам (stride scheduling), а фактические
/// старты считаются по длительностям.
/// </summary>
public sealed class PlaybackQueueBuilder
{
    public PlaybackPlan Build(
        Billboard billboard,
        DateOnly date,
        int hour,
        IEnumerable<Placement> placements)
    {
        ArgumentNullException.ThrowIfNull(billboard);
        ArgumentNullException.ThrowIfNull(placements);

        if (hour is < 0 or > 23)
        {
            throw new ArgumentOutOfRangeException(nameof(hour), "Час должен быть в диапазоне 0..23.");
        }

        var active = placements
            .Where(placement => placement.BillboardId == billboard.Id)
            .Where(placement => placement.Status.OccupiesCapacity())
            .Where(placement => placement.MinimumPlaysPerHour > 0)
            .Where(placement => placement.VideoDurationSeconds > 0)
            .Where(placement => ScheduleWindow.Covers(placement, date, hour))
            .OrderBy(placement => placement.Id)
            .ToArray();

        var capacity = billboard.AdvertCapacitySecondsPerHour;
        var requiredSeconds = active.Sum(placement => placement.RequiredSecondsPerHour);
        var requiredPlays = active.Sum(placement => placement.MinimumPlaysPerHour);

        if (active.Length == 0)
        {
            return new PlaybackPlan
            {
                BillboardId = billboard.Id,
                Date = date,
                Hour = hour,
                CapacitySeconds = capacity,
            };
        }

        var warnings = new List<string>();
        var overCapacity = requiredSeconds > capacity;
        if (overCapacity)
        {
            warnings.Add(PlaybackPlanWarnings.CapacityExceeded);
        }

        var states = active
            .Select(placement => new StrideState(
                placement,
                NextAt: 0,
                Step: Billboard.FullHourSeconds / (double)placement.MinimumPlaysPerHour))
            .ToList();

        var slots = new List<PlaybackSlot>(requiredPlays);
        var elapsed = 0;
        var truncated = false;

        for (var index = 0; index < requiredPlays; index++)
        {
            var state = states
                .OrderBy(item => item.NextAt)
                .ThenBy(item => item.Placement.Id)
                .First();

            var idealOffset = state.NextAt;
            state.NextAt += state.Step;

            var duration = state.Placement.VideoDurationSeconds;
            if (elapsed + duration > capacity)
            {
                truncated = true;
                break;
            }

            slots.Add(new PlaybackSlot
            {
                Sequence = slots.Count,
                PlacementId = state.Placement.Id,
                VideoAssetId = state.Placement.VideoAssetId,
                BillboardId = state.Placement.BillboardId,
                DurationSeconds = duration,
                IdealOffsetSeconds = Math.Round(idealOffset, 2),
                StartOffsetSeconds = elapsed,
            });

            elapsed += duration;
        }

        if (truncated)
        {
            warnings.Add(PlaybackPlanWarnings.PlanTruncated);
        }

        return new PlaybackPlan
        {
            BillboardId = billboard.Id,
            Date = date,
            Hour = hour,
            Slots = slots,
            CapacitySeconds = capacity,
            TotalDurationSeconds = elapsed,
            RequiredPlays = requiredPlays,
            IsOverCapacity = overCapacity,
            Warnings = warnings,
        };
    }

    private sealed class StrideState
    {
        public StrideState(Placement placement, double NextAt, double Step)
        {
            Placement = placement;
            this.NextAt = NextAt;
            this.Step = Step;
        }

        public Placement Placement { get; }

        public double NextAt { get; set; }

        public double Step { get; }
    }
}
