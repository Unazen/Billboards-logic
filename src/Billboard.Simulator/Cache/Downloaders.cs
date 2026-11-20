using Billboard.Scheduling;

namespace Billboard.Simulator.Cache;

/// <summary>Скачивает видео по HTTP(S) или копирует локальный файл (удобно для manifest-файла).</summary>
public sealed class HttpVideoDownloader : IVideoDownloader
{
    private const int BufferSize = 81920;
    private readonly HttpClient _httpClient;

    public HttpVideoDownloader(HttpClient httpClient)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    }

    public async Task DownloadAsync(
        ManifestItem item,
        Stream destination,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(destination);

        if (TryGetLocalPath(item.DownloadUrl, out var localPath))
        {
            await using var source = new FileStream(
                localPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                BufferSize,
                useAsync: true);

            await source.CopyToAsync(destination, BufferSize, cancellationToken);
            return;
        }

        if (!Uri.TryCreate(item.DownloadUrl, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException(
                $"Неподдерживаемый DownloadUrl: {item.DownloadUrl}. Ожидается http(s) или локальный файл.");
        }

        using var response = await _httpClient.GetAsync(
            uri,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        response.EnsureSuccessStatusCode();

        await using var content = await response.Content.ReadAsStreamAsync(cancellationToken);
        await content.CopyToAsync(destination, BufferSize, cancellationToken);
    }

    private static bool TryGetLocalPath(string url, out string path)
    {
        path = string.Empty;

        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.IsFile)
        {
            path = uri.LocalPath;
            return true;
        }

        if (!string.IsNullOrWhiteSpace(url) && File.Exists(url))
        {
            path = Path.GetFullPath(url);
            return true;
        }

        return false;
    }
}

/// <summary>Генерирует файл нужного размера для автономной демонстрации.</summary>
public sealed class DemoVideoDownloader : IVideoDownloader
{
    private const int BufferSize = 64 * 1024;

    public async Task DownloadAsync(
        ManifestItem item,
        Stream destination,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(destination);

        var buffer = new byte[BufferSize];
        var remaining = item.SizeBytes;
        long offset = 0;

        while (remaining > 0)
        {
            var count = (int)Math.Min(buffer.Length, remaining);
            for (var index = 0; index < count; index++)
            {
                buffer[index] = (byte)((offset + index) % 251);
            }

            await destination.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
            offset += count;
            remaining -= count;
        }
    }
}
