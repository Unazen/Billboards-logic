namespace Billboard.Domain.Interfaces;

/// <summary>
/// Хранилище видеофайлов (MinIO / S3 / локальный диск). Бизнес-логика работает
/// только с этим интерфейсом, поэтому реализацию можно менять без правок логики.
/// </summary>
public interface IVideoStorage
{
    Task PutAsync(string key, Stream content, long size, string contentType, CancellationToken ct = default);

    /// <summary>Открывает объект для чтения. Вызывающий обязан освободить поток.</summary>
    Task<Stream> OpenReadAsync(string key, CancellationToken ct = default);

    Task DeleteAsync(string key, CancellationToken ct = default);

    /// <summary>Размер объекта в байтах или null, если объекта нет.</summary>
    Task<long?> GetSizeAsync(string key, CancellationToken ct = default);

    /// <summary>Временная ссылка для скачивания напрямую из хранилища.</summary>
    Task<Uri> GetDownloadUrlAsync(string key, TimeSpan ttl, CancellationToken ct = default);
}
