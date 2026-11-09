namespace Billboard.Scheduling;

/// <summary>Простое потокобезопасное хранилище для локальной разработки, тестов и демо.</summary>
public sealed class InMemoryPlacementRepository : IPlacementRepository
{
    private readonly Dictionary<Guid, Placement> _items = new();
    private readonly object _gate = new();

    public Task<Placement?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult(_items.TryGetValue(id, out var placement) ? placement : null);
        }
    }

    public Task<IReadOnlyList<Placement>> ListAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            IReadOnlyList<Placement> result = _items.Values
                .OrderBy(placement => placement.CreatedAt)
                .ToArray();
            return Task.FromResult(result);
        }
    }

    public Task<IReadOnlyList<Placement>> GetByBillboardAsync(
        Guid billboardId,
        DateOnly from,
        DateOnly to,
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            IReadOnlyList<Placement> result = _items.Values
                .Where(placement => placement.BillboardId == billboardId)
                .Where(placement => placement.StartDate <= to && placement.EndDate >= from)
                .OrderBy(placement => placement.StartDate)
                .ToArray();
            return Task.FromResult(result);
        }
    }

    public Task AddAsync(Placement placement, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(placement);

        lock (_gate)
        {
            if (!_items.TryAdd(placement.Id, placement))
            {
                throw new InvalidOperationException($"Размещение {placement.Id} уже существует.");
            }
        }

        return Task.CompletedTask;
    }

    public Task<bool> UpdateAsync(
        Placement placement,
        long expectedVersion,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(placement);

        lock (_gate)
        {
            if (!_items.TryGetValue(placement.Id, out var current) || current.Version != expectedVersion)
            {
                return Task.FromResult(false);
            }

            _items[placement.Id] = placement;
            return Task.FromResult(true);
        }
    }

    public Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult(_items.Remove(id));
        }
    }
}

/// <summary>Каталог билбордов в памяти.</summary>
public sealed class InMemoryBillboardCatalog : IBillboardCatalog
{
    private readonly Dictionary<Guid, Billboard> _items;
    private readonly object _gate = new();

    public InMemoryBillboardCatalog(IEnumerable<Billboard> billboards)
    {
        ArgumentNullException.ThrowIfNull(billboards);
        _items = billboards.ToDictionary(billboard => billboard.Id);
    }

    public InMemoryBillboardCatalog(params Billboard[] billboards)
        : this((IEnumerable<Billboard>)billboards)
    {
    }

    public Task<Billboard?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult(_items.TryGetValue(id, out var billboard) ? billboard : null);
        }
    }

    public Task<IReadOnlyList<Billboard>> ListAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            IReadOnlyList<Billboard> result = _items.Values.OrderBy(billboard => billboard.Name).ToArray();
            return Task.FromResult(result);
        }
    }

    public Task<IReadOnlyList<Billboard>> GetManyAsync(
        IEnumerable<Guid> ids,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ids);

        lock (_gate)
        {
            IReadOnlyList<Billboard> result = ids
                .Distinct()
                .Where(_items.ContainsKey)
                .Select(id => _items[id])
                .ToArray();
            return Task.FromResult(result);
        }
    }
}
