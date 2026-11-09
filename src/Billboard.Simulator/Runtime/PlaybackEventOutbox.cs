using System.Text.Json;
using Billboard.Scheduling;

namespace Billboard.Simulator.Runtime;

public sealed record OutboxItem
{
    public PlaybackEvent Event { get; init; } = new();

    public int Attempts { get; init; }

    public DateTimeOffset EnqueuedAt { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Надёжная очередь событий показов: события сохраняются на диск до отправки и
/// переживают перезапуск. Повторная отправка использует тот же IdempotencyKey.
/// </summary>
public sealed class PlaybackEventOutbox
{
    private readonly string _path;
    private readonly TextWriter? _log;
    private readonly object _gate = new();
    private readonly List<OutboxItem> _items = new();

    public PlaybackEventOutbox(string path, TextWriter? log = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = System.IO.Path.GetFullPath(path);
        _log = log;
        Load();
    }

    public string FilePath => _path;

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _items.Count;
            }
        }
    }

    public IReadOnlyList<OutboxItem> Snapshot()
    {
        lock (_gate)
        {
            return _items.OrderBy(item => item.EnqueuedAt).ToArray();
        }
    }

    public bool Enqueue(PlaybackEvent playbackEvent)
    {
        ArgumentNullException.ThrowIfNull(playbackEvent);

        if (playbackEvent.EventId == Guid.Empty)
        {
            throw new ArgumentException("У события должен быть непустой EventId.", nameof(playbackEvent));
        }

        lock (_gate)
        {
            if (_items.Any(item => item.Event.EventId == playbackEvent.EventId))
            {
                return false;
            }

            var normalized = string.IsNullOrWhiteSpace(playbackEvent.IdempotencyKey)
                ? playbackEvent with { IdempotencyKey = playbackEvent.EventId.ToString("N") }
                : playbackEvent;

            _items.Add(new OutboxItem
            {
                Event = normalized,
                EnqueuedAt = DateTimeOffset.UtcNow,
            });

            Save();
            return true;
        }
    }

    public IReadOnlyList<PlaybackEvent> Take(int maximum)
    {
        if (maximum <= 0)
        {
            return Array.Empty<PlaybackEvent>();
        }

        lock (_gate)
        {
            return _items
                .OrderBy(item => item.EnqueuedAt)
                .Take(maximum)
                .Select(item => item.Event)
                .ToArray();
        }
    }

    public void Acknowledge(IEnumerable<Guid> eventIds)
    {
        ArgumentNullException.ThrowIfNull(eventIds);

        var ids = eventIds.ToHashSet();
        if (ids.Count == 0)
        {
            return;
        }

        lock (_gate)
        {
            var removed = _items.RemoveAll(item => ids.Contains(item.Event.EventId));
            if (removed > 0)
            {
                Save();
            }
        }
    }

    public void RecordAttempt(IEnumerable<Guid> eventIds)
    {
        ArgumentNullException.ThrowIfNull(eventIds);

        var ids = eventIds.ToHashSet();
        if (ids.Count == 0)
        {
            return;
        }

        lock (_gate)
        {
            for (var index = 0; index < _items.Count; index++)
            {
                if (ids.Contains(_items[index].Event.EventId))
                {
                    _items[index] = _items[index] with { Attempts = _items[index].Attempts + 1 };
                }
            }

            Save();
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _items.Clear();
            Save();
        }
    }

    private void Load()
    {
        if (!File.Exists(_path))
        {
            return;
        }

        try
        {
            var json = File.ReadAllText(_path);
            var items = JsonSerializer.Deserialize<List<OutboxItem>>(json, SimulatorJson.Options) ??
                        new List<OutboxItem>();

            var seen = new HashSet<Guid>();
            foreach (var item in items)
            {
                if (item.Event.EventId == Guid.Empty || !seen.Add(item.Event.EventId))
                {
                    continue;
                }

                var normalized = string.IsNullOrWhiteSpace(item.Event.IdempotencyKey)
                    ? item with
                    {
                        Event = item.Event with { IdempotencyKey = item.Event.EventId.ToString("N") },
                    }
                    : item;

                _items.Add(normalized);
            }

            Log($"Outbox загружен: {_items.Count} событие(й).");
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            Log($"Outbox повреждён, начинаем с пустого: {exception.Message}");
            _items.Clear();
            TryArchiveCorrupted();
        }
    }

    private void Save()
    {
        var json = JsonSerializer.Serialize(_items.OrderBy(item => item.EnqueuedAt).ToArray(), SimulatorJson.Options);
        AtomicFile.WriteAllText(_path, json);
    }

    private void TryArchiveCorrupted()
    {
        try
        {
            var archive = _path + ".corrupt." + DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmss");
            File.Move(_path, archive, overwrite: true);
            Log($"Повреждённый outbox сохранён как {System.IO.Path.GetFileName(archive)}.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Log($"Не удалось архивировать повреждённый outbox: {exception.Message}");
        }
    }

    private void Log(string message) => _log?.WriteLine($"[OUTBOX] {message}");
}
