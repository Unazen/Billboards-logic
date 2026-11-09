using Billboard.Domain.Interfaces;
using Microsoft.Extensions.Options;
using Minio;
using Minio.DataModel.Args;
using Minio.Exceptions;

namespace Billboard.Infrastructure.Storage;

public class StorageOptions
{
    public const string Section = "Storage";

    /// <summary>"Minio" (по умолчанию) или "Local" (папка на диске, удобно для тестов без Docker).</summary>
    public string Provider { get; set; } = "Minio";
    public string Endpoint { get; set; } = "localhost:9000";
    public string AccessKey { get; set; } = "";
    public string SecretKey { get; set; } = "";
    public string Bucket { get; set; } = "billboard-videos";
    public bool UseSsl { get; set; }
    public string LocalPath { get; set; } = "./storage-data";
}

public sealed class MinioVideoStorage : IVideoStorage
{
    private readonly IMinioClient _client;
    private readonly string _bucket;

    public MinioVideoStorage(IOptions<StorageOptions> options)
    {
        var o = options.Value;
        _bucket = o.Bucket;
        _client = new MinioClient()
            .WithEndpoint(o.Endpoint)
            .WithCredentials(o.AccessKey, o.SecretKey)
            .WithSSL(o.UseSsl)
            .Build();
    }

    /// <summary>Создаёт бакет, если его ещё нет. Вызывается при старте приложения.</summary>
    public async Task EnsureBucketAsync(CancellationToken ct = default)
    {
        var exists = await _client.BucketExistsAsync(new BucketExistsArgs().WithBucket(_bucket), ct);
        if (!exists)
            await _client.MakeBucketAsync(new MakeBucketArgs().WithBucket(_bucket), ct);
    }

    public async Task PutAsync(string key, Stream content, long size, string contentType, CancellationToken ct = default)
    {
        await _client.PutObjectAsync(new PutObjectArgs()
            .WithBucket(_bucket)
            .WithObject(key)
            .WithStreamData(content)
            .WithObjectSize(size)
            .WithContentType(contentType), ct);
    }

    public async Task<Stream> OpenReadAsync(string key, CancellationToken ct = default)
    {
        // Объект копируется во временный файл, который удаляется при закрытии потока.
        var path = Path.Combine(Path.GetTempPath(), $"billboard-{Guid.NewGuid():N}.tmp");
        var file = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 81920,
            FileOptions.Asynchronous | FileOptions.DeleteOnClose);
        try
        {
            await _client.GetObjectAsync(new GetObjectArgs()
                .WithBucket(_bucket)
                .WithObject(key)
                .WithCallbackStream(async (stream, token) => await stream.CopyToAsync(file, token)), ct);
            file.Position = 0;
            return file;
        }
        catch
        {
            await file.DisposeAsync();
            throw;
        }
    }

    public async Task DeleteAsync(string key, CancellationToken ct = default) =>
        await _client.RemoveObjectAsync(new RemoveObjectArgs().WithBucket(_bucket).WithObject(key), ct);

    public async Task<long?> GetSizeAsync(string key, CancellationToken ct = default)
    {
        try
        {
            var stat = await _client.StatObjectAsync(new StatObjectArgs().WithBucket(_bucket).WithObject(key), ct);
            return stat.Size;
        }
        catch (ObjectNotFoundException)
        {
            return null;
        }
    }

    public async Task<Uri> GetDownloadUrlAsync(string key, TimeSpan ttl, CancellationToken ct = default)
    {
        var url = await _client.PresignedGetObjectAsync(new PresignedGetObjectArgs()
            .WithBucket(_bucket)
            .WithObject(key)
            .WithExpiry((int)ttl.TotalSeconds));
        return new Uri(url);
    }
}

/// <summary>Хранилище на локальном диске для разработки и тестов. Ссылки отдаются как file://.</summary>
public sealed class LocalVideoStorage : IVideoStorage
{
    private readonly string _root;

    public LocalVideoStorage(IOptions<StorageOptions> options)
    {
        _root = Path.GetFullPath(options.Value.LocalPath);
        Directory.CreateDirectory(_root);
    }

    private string PathFor(string key)
    {
        var full = Path.GetFullPath(Path.Combine(_root, key.Replace('/', Path.DirectorySeparatorChar)));
        // Защита от выхода за пределы корня через "../" в ключе.
        if (!full.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new ArgumentException("Недопустимый ключ хранилища.", nameof(key));
        return full;
    }

    public async Task PutAsync(string key, Stream content, long size, string contentType, CancellationToken ct = default)
    {
        var path = PathFor(key);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);
        await content.CopyToAsync(file, ct);
    }

    public Task<Stream> OpenReadAsync(string key, CancellationToken ct = default) =>
        Task.FromResult<Stream>(new FileStream(PathFor(key), FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true));

    public Task DeleteAsync(string key, CancellationToken ct = default)
    {
        var path = PathFor(key);
        if (File.Exists(path)) File.Delete(path);
        return Task.CompletedTask;
    }

    public Task<long?> GetSizeAsync(string key, CancellationToken ct = default)
    {
        var info = new FileInfo(PathFor(key));
        return Task.FromResult<long?>(info.Exists ? info.Length : null);
    }

    public Task<Uri> GetDownloadUrlAsync(string key, TimeSpan ttl, CancellationToken ct = default) =>
        Task.FromResult(new Uri(PathFor(key)));
}
