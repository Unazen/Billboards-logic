using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using Billboard.Application.Common;
using Billboard.Application.Contracts;
using Billboard.Application.Media;
using Microsoft.Extensions.Options;

namespace Billboard.Infrastructure.Media;

/// <summary>
/// Скачивание по URL с защитой от SSRF. Адрес проверяется в момент подключения
/// (ConnectCallback), поэтому обходы через DNS rebinding и редиректы не работают.
/// </summary>
public sealed class SafeUrlFetcher : IUrlFetcher, IDisposable
{
    private readonly HttpClient _http;

    public SafeUrlFetcher(IOptions<MediaOptions> options)
    {
        var allowPrivate = options.Value.AllowPrivateUrlImport;

        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 3,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            ConnectCallback = async (context, ct) =>
            {
                var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, ct);
                if (addresses.Length == 0)
                    throw new HttpRequestException("Хост не найден.");

                if (!allowPrivate && addresses.Any(a => !UrlGuard.IsPublicAddress(a)))
                    throw new HttpRequestException("Обращение к внутренним адресам запрещено.");

                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    await socket.ConnectAsync(new IPEndPoint(addresses[0], context.DnsEndPoint.Port), ct);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            }
        };

        _http = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = TimeSpan.FromSeconds(Math.Max(10, options.Value.ImportTimeoutSeconds))
        };
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("BillboardSystem", "1.0"));
    }

    public async Task<FetchResult> DownloadAsync(Uri url, string destinationPath, long maxBytes, CancellationToken ct = default)
    {
        try
        {
            using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode)
                throw AppException.Validation("URL_UNAVAILABLE", $"Источник вернул статус {(int)response.StatusCode}.");

            var contentType = response.Content.Headers.ContentType?.MediaType;
            if (contentType is not null
                && !contentType.StartsWith("video/", StringComparison.OrdinalIgnoreCase)
                && !contentType.Equals("application/octet-stream", StringComparison.OrdinalIgnoreCase))
                throw AppException.Validation("INVALID_CONTENT_TYPE", $"По ссылке находится не видео ({contentType}).");

            if (response.Content.Headers.ContentLength is { } length && length > maxBytes)
                throw AppException.Validation("VIDEO_TOO_LARGE",
                    $"Размер файла {length / 1024 / 1024} МБ превышает лимит {maxBytes / 1024 / 1024} МБ.");

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            var (size, sha) = await BoundedCopy.CopyToFileAsync(stream, destinationPath, maxBytes, ct);

            var fileName = response.Content.Headers.ContentDisposition?.FileNameStar
                           ?? response.Content.Headers.ContentDisposition?.FileName?.Trim('"')
                           ?? Path.GetFileName(response.RequestMessage?.RequestUri?.AbsolutePath ?? url.AbsolutePath);
            return new FetchResult(Path.GetFileName(fileName ?? ""), contentType, size, sha);
        }
        catch (HttpRequestException ex)
        {
            throw AppException.Validation("URL_UNAVAILABLE", $"Не удалось скачать файл: {ex.Message}");
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw AppException.Validation("URL_TIMEOUT", "Источник не ответил вовремя.");
        }
    }

    public void Dispose() => _http.Dispose();
}
