using Billboard.Scheduling;

namespace Billboard.Simulator.Runtime;

/// <summary>
/// Ротация роликов с весами по квоте показов. Использует stride scheduling:
/// ролик с большей квотой появляется чаще, но показы распределены равномерно.
/// </summary>
public sealed class PlaybackRotator
{
    private readonly object _gate = new();
    private readonly List<RotationState> _states = new();

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _states.Count;
            }
        }
    }

    public IReadOnlyList<ManifestItem> Items
    {
        get
        {
            lock (_gate)
            {
                return _states.Select(state => state.Item).ToArray();
            }
        }
    }

    public void SetItems(IEnumerable<ManifestItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        var normalized = items
            .Where(item => item.VideoId != Guid.Empty)
            .Where(item => item.DurationSeconds > 0)
            .Where(item => item.PlaysPerHour > 0)
            .GroupBy(item => item.VideoId)
            .Select(group => group.Last())
            .OrderBy(item => item.VideoId)
            .ToArray();

        lock (_gate)
        {
            _states.Clear();
            foreach (var item in normalized)
            {
                _states.Add(new RotationState(
                    item,
                    NextAt: 0,
                    Step: global::Billboard.Scheduling.Billboard.FullHourSeconds / (double)item.PlaysPerHour));
            }
        }
    }

    public ManifestItem? Next()
    {
        lock (_gate)
        {
            if (_states.Count == 0)
            {
                return null;
            }

            var state = _states
                .OrderBy(item => item.NextAt)
                .ThenBy(item => item.Item.VideoId)
                .First();

            state.NextAt += state.Step;
            return state.Item;
        }
    }

    public bool Remove(Guid videoId)
    {
        lock (_gate)
        {
            return _states.RemoveAll(state => state.Item.VideoId == videoId) > 0;
        }
    }

    private sealed class RotationState
    {
        public RotationState(ManifestItem item, double NextAt, double Step)
        {
            Item = item;
            this.NextAt = NextAt;
            this.Step = Step;
        }

        public ManifestItem Item { get; }

        public double NextAt { get; set; }

        public double Step { get; }
    }
}
