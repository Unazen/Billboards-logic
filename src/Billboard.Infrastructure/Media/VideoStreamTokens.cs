using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;

namespace Billboard.Infrastructure.Media;

/// <summary>
/// Подписанная ссылка на скачивание видео через API. Нужна для локального хранилища:
/// presigned URL S3 здесь недоступен, а отдавать файл по произвольному id нельзя.
/// </summary>
public sealed class VideoStreamTokens(IDataProtectionProvider provider)
{
    private readonly IDataProtector _protector = provider.CreateProtector("Billboard.VideoStream.v1");

    public string Create(Guid videoId, DateTimeOffset expiresAt) =>
        _protector.Protect($"{videoId:N}|{expiresAt.ToUnixTimeSeconds()}");

    public bool TryValidate(string? token, Guid videoId, out DateTimeOffset expiresAt)
    {
        expiresAt = default;
        if (string.IsNullOrWhiteSpace(token))
            return false;

        try
        {
            var payload = _protector.Unprotect(token);
            var separator = payload.IndexOf('|');
            if (separator <= 0)
                return false;

            if (!Guid.TryParseExact(payload[..separator], "N", out var tokenVideoId) || tokenVideoId != videoId)
                return false;

            if (!long.TryParse(payload[(separator + 1)..], out var unix))
                return false;

            expiresAt = DateTimeOffset.FromUnixTimeSeconds(unix);
            return expiresAt > DateTimeOffset.UtcNow;
        }
        catch (CryptographicException)
        {
            return false;
        }
        catch (FormatException)
        {
            return false;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }
}
