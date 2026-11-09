namespace Billboard.Scheduling;

/// <summary>Хранилище размещений. Реализацию на EF Core добавляет участник 3.</summary>
public interface IPlacementRepository
{
    Task<Placement?> GetAsync(Guid id, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Placement>> ListAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Placement>> GetByBillboardAsync(
        Guid billboardId,
        DateOnly from,
        DateOnly to,
        CancellationToken cancellationToken = default);

    Task AddAsync(Placement placement, CancellationToken cancellationToken = default);

    /// <summary>Обновляет запись, если версия совпадает. Возвращает false при конфликте версий.</summary>
    Task<bool> UpdateAsync(
        Placement placement,
        long expectedVersion,
        CancellationToken cancellationToken = default);

    Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default);
}

/// <summary>Каталог билбордов. Нужен планировщику для проверки ёмкости и состояния.</summary>
public interface IBillboardCatalog
{
    Task<Billboard?> GetAsync(Guid id, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Billboard>> ListAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Billboard>> GetManyAsync(
        IEnumerable<Guid> ids,
        CancellationToken cancellationToken = default);
}
